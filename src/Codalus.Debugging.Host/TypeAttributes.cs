using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Codalus.Debugging.Companion;

namespace Codalus.Debugging.Host;

/// <summary>
/// The debugger attributes on the debuggee's types (M14b): <c>[DebuggerDisplay]</c>, <c>[DebuggerTypeProxy]</c> and the
/// members <c>[DebuggerBrowsable]</c> hides or flattens, read from the assemblies' metadata (nothing is loaded or run).
/// netcoredbg doesn't apply them; the shell does, with these strings and its own evaluations.
/// </summary>
internal sealed class TypeAttributes : IDisposable
{
    private readonly Dictionary<string, Module?> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TypeDisplayResult> _cache = new(StringComparer.Ordinal);

    public TypeDisplayResult Find(string typeName, IReadOnlyList<string> modules)
    {
        var key = typeName + "|" + string.Join('|', modules);
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var result = FindCore(typeName, modules, depth: 0) ?? new TypeDisplayResult(null, null, null, [], [], null);
        lock (_cache)
        {
            _cache[key] = result;
        }

        return result;
    }

    private TypeDisplayResult? FindCore(string typeName, IReadOnlyList<string> modules, int depth)
    {
        foreach (var candidate in MetadataNames(typeName))
        {
            foreach (var path in modules)
            {
                if (Load(path) is not { } module || !module.Types.TryGetValue(candidate, out var handle))
                {
                    continue;
                }

                var reader = module.Reader;
                var type = reader.GetTypeDefinition(handle);
                var (display, name, proxy) = TypeLevel(reader, type.GetCustomAttributes());
                var hidden = new List<string>();
                var rootHidden = new List<string>();
                Members(reader, type, hidden, rootHidden);

                // The attributes are inherited: a type without them shows its base type's (within the given modules).
                if (display is null && proxy is null && depth < 8 && BaseTypeName(reader, type.BaseType) is { } baseName
                    && baseName is not ("System.Object" or "System.ValueType" or "System.Enum")
                    && FindCore(baseName, modules, depth + 1) is { } inherited)
                {
                    return inherited with { Hidden = [.. hidden, .. inherited.Hidden], RootHidden = [.. rootHidden, .. inherited.RootHidden] };
                }

                return new TypeDisplayResult(display, name, proxy, hidden, rootHidden, path);
            }
        }

        return null;
    }

    /// <summary>
    /// The metadata names a debugger's type name can mean: <c>List&lt;int&gt;</c> is <c>List`1</c>, and a nested
    /// <c>Outer.Inner</c> may be <c>Outer/Inner</c>.
    /// </summary>
    internal static IEnumerable<string> MetadataNames(string typeName)
    {
        var name = typeName.Replace('+', '.');
        var arity = 0;
        var generic = name.IndexOf('<', StringComparison.Ordinal);
        if (generic >= 0)
        {
            var arguments = name[(generic + 1)..name.LastIndexOf('>')];
            var level = 0;
            arity = 1;
            foreach (var c in arguments)
            {
                level += c switch { '<' => 1, '>' => -1, _ => 0 };
                if (c == ',' && level == 0)
                {
                    arity++;
                }
            }

            name = name[..generic];
        }

        if (name.EndsWith("[]", StringComparison.Ordinal))
        {
            yield break; // arrays have no attributes of their own
        }

        var full = arity > 0 ? $"{name}`{arity}" : name;
        yield return full;

        // Nested types: the last dots may separate declaring types rather than namespaces.
        var parts = full.Split('.');
        for (var nested = 1; nested < parts.Length - 1; nested++)
        {
            yield return string.Join('.', parts[..^(nested + 1)]) + "." + string.Join('/', parts[^(nested + 1)..]);
        }
    }

    private static (string? Display, string? Name, string? Proxy) TypeLevel(MetadataReader reader, CustomAttributeHandleCollection attributes)
    {
        string? display = null, name = null, proxy = null;
        foreach (var handle in attributes)
        {
            var attribute = reader.GetCustomAttribute(handle);
            switch (AttributeName(reader, attribute))
            {
                case "System.Diagnostics.DebuggerDisplayAttribute":
                    var value = attribute.DecodeValue(StringTypes.Instance);
                    display = value.FixedArguments is [{ Value: string text }, ..] ? text : null;
                    name = value.NamedArguments.FirstOrDefault(a => a.Name == "Name").Value as string;
                    break;
                case "System.Diagnostics.DebuggerTypeProxyAttribute":
                    proxy = attribute.DecodeValue(StringTypes.Instance).FixedArguments is [{ Value: string type }, ..] ? type : null;
                    break;
            }
        }

        return (display, name, proxy);
    }

    private static void Members(MetadataReader reader, TypeDefinition type, List<string> hidden, List<string> rootHidden)
    {
        void Check(string member, CustomAttributeHandleCollection attributes)
        {
            foreach (var handle in attributes)
            {
                var attribute = reader.GetCustomAttribute(handle);
                if (AttributeName(reader, attribute) != "System.Diagnostics.DebuggerBrowsableAttribute")
                {
                    continue;
                }

                // DebuggerBrowsableState: Never = 0, Collapsed = 2, RootHidden = 3.
                switch (attribute.DecodeValue(StringTypes.Instance).FixedArguments is [{ Value: int state }, ..] ? state : -1)
                {
                    case 0:
                        hidden.Add(member);
                        break;
                    case 3:
                        rootHidden.Add(member);
                        break;
                }
            }
        }

        foreach (var field in type.GetFields())
        {
            var definition = reader.GetFieldDefinition(field);
            Check(reader.GetString(definition.Name), definition.GetCustomAttributes());
        }

        foreach (var property in type.GetProperties())
        {
            var definition = reader.GetPropertyDefinition(property);
            Check(reader.GetString(definition.Name), definition.GetCustomAttributes());
        }
    }

    private static string? AttributeName(MetadataReader reader, CustomAttribute attribute)
    {
        EntityHandle type = attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
            HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
            _ => default,
        };
        return type.Kind switch
        {
            HandleKind.TypeReference => FullName(reader, reader.GetTypeReference((TypeReferenceHandle)type)),
            HandleKind.TypeDefinition => FullName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)type)),
            _ => null,
        };
    }

    private static string? BaseTypeName(MetadataReader reader, EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeReference => FullName(reader, reader.GetTypeReference((TypeReferenceHandle)handle)),
        HandleKind.TypeDefinition => FullName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)handle)),
        _ => null, // a generic instantiation (TypeSpecification): not followed
    };

    private static string FullName(MetadataReader reader, TypeReference type) =>
        type.Namespace.IsNil ? reader.GetString(type.Name) : $"{reader.GetString(type.Namespace)}.{reader.GetString(type.Name)}";

    private static string FullName(MetadataReader reader, TypeDefinition type)
    {
        var name = reader.GetString(type.Name);
        if (type.GetDeclaringType() is { IsNil: false } declaring)
        {
            return FullName(reader, reader.GetTypeDefinition(declaring)) + "/" + name;
        }

        return type.Namespace.IsNil ? name : $"{reader.GetString(type.Namespace)}.{name}";
    }

    private Module? Load(string path)
    {
        lock (_modules)
        {
            if (_modules.TryGetValue(path, out var known))
            {
                return known;
            }

            Module? module = null;
            try
            {
                if (File.Exists(path))
                {
                    var pe = new PEReader(File.OpenRead(path));
                    if (pe.HasMetadata)
                    {
                        var reader = pe.GetMetadataReader();
                        var types = new Dictionary<string, TypeDefinitionHandle>(StringComparer.Ordinal);
                        foreach (var handle in reader.TypeDefinitions)
                        {
                            types.TryAdd(FullName(reader, reader.GetTypeDefinition(handle)), handle);
                        }

                        module = new Module(pe, reader, types);
                    }
                    else
                    {
                        pe.Dispose();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
            {
                module = null;
            }

            return _modules[path] = module;
        }
    }

    public void Dispose()
    {
        lock (_modules)
        {
            foreach (var module in _modules.Values)
            {
                module?.Pe.Dispose();
            }

            _modules.Clear();
        }
    }

    private sealed record Module(PEReader Pe, MetadataReader Reader, Dictionary<string, TypeDefinitionHandle> Types);

    /// <summary>Decodes attribute arguments with types as names (enough for the debugger attributes).</summary>
    private sealed class StringTypes : ICustomAttributeTypeProvider<object?>
    {
        public static readonly StringTypes Instance = new();

        public object? GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode;

        public object? GetSystemType() => "System.Type";

        public object? GetSZArrayType(object? elementType) => elementType;

        public object? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => FullName(reader, reader.GetTypeDefinition(handle));

        public object? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => FullName(reader, reader.GetTypeReference(handle));

        public object? GetTypeFromSerializedName(string name) => name;

        // DebuggerBrowsableState is an int-backed enum.
        public PrimitiveTypeCode GetUnderlyingEnumType(object? type) => PrimitiveTypeCode.Int32;

        public bool IsSystemType(object? type) => type is "System.Type";
    }
}
