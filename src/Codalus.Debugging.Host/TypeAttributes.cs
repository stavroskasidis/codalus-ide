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

        var result = (FindCore(typeName, modules, depth: 0) ?? new TypeDisplayResult(null, null, null, [], [], null)) with { Members = MembersOf(typeName, modules) };
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
    /// The members of <paramref name="typeName"/> and of its base types, most derived first (D44): each field, property
    /// and event with its kind, access, static-ness, declaring type and module, whether it can be set, and its declared
    /// type. Generic base types (<c>Dictionary&lt;string, object&gt;</c>) are followed through their definitions. Null
    /// when the type isn't in the modules (a remote program's, whose files aren't here).
    /// </summary>
    internal List<TypeMember>? MembersOf(string typeName, IReadOnlyList<string> modules)
    {
        var located = MetadataNames(typeName).Select(name => Locate(name, modules)).FirstOrDefault(l => l is not null);
        if (located is null)
        {
            return null;
        }

        var members = new List<TypeMember>();
        for (var depth = 0; located is { } at && depth < 16; depth++)
        {
            var (reader, type, path) = at;
            var declaring = FullName(reader, type).Replace('/', '.');
            var events = new HashSet<string>(type.GetEvents().Select(e => reader.GetString(reader.GetEventDefinition(e).Name)), StringComparer.Ordinal);
            foreach (var handle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(handle);
                var name = reader.GetString(field.Name);
                var attributes = field.Attributes;
                var literal = (attributes & FieldAttributes.Literal) != 0;
                members.Add(new TypeMember(
                    name,
                    literal ? "constant" : events.Contains(name) ? "event" : "field",
                    Access((int)(attributes & FieldAttributes.FieldAccessMask)),
                    (attributes & FieldAttributes.Static) != 0,
                    declaring,
                    path,
                    !literal && (attributes & FieldAttributes.InitOnly) == 0,
                    Decode(() => field.DecodeSignature(SignatureNames.Instance, null))));
            }

            foreach (var handle in type.GetProperties())
            {
                var property = reader.GetPropertyDefinition(handle);
                var accessors = property.GetAccessors();
                var getter = accessors.Getter.IsNil ? (MethodDefinition?)null : reader.GetMethodDefinition(accessors.Getter);
                var setter = accessors.Setter.IsNil ? (MethodDefinition?)null : reader.GetMethodDefinition(accessors.Setter);
                if (getter is null && setter is null)
                {
                    continue;
                }

                // An indexer: netcoredbg lists it by its name (Item) with the exception its call without arguments throws.
                var indexer = (getter ?? setter)!.Value.GetParameters().Count > (getter is null ? 1 : 0);
                var access = Math.Max(getter is { } g ? (int)(g.Attributes & MethodAttributes.MemberAccessMask) : 0, setter is { } st ? (int)(st.Attributes & MethodAttributes.MemberAccessMask) : 0);
                members.Add(new TypeMember(
                    reader.GetString(property.Name),
                    indexer ? "indexer" : "property",
                    Access(access),
                    ((getter ?? setter)!.Value.Attributes & MethodAttributes.Static) != 0,
                    declaring,
                    path,
                    setter is not null,
                    Decode(() => property.DecodeSignature(SignatureNames.Instance, null).ReturnType)));
            }

            located = BaseTypeMetadataName(reader, type.BaseType) is { } baseName && baseName is not ("System.Object" or "System.ValueType" or "System.Enum")
                ? Locate(baseName, modules)
                : null;
        }

        return members;
    }

    /// <summary>The access of a field's or a method's access bits (FieldAttributes and MethodAttributes share them).</summary>
    private static string Access(int bits) => bits switch
    {
        6 => "public",
        5 => "protected internal",
        4 => "protected",
        3 => "internal",
        2 => "private protected",
        _ => "private",
    };

    private static string? Decode(Func<string> decode)
    {
        try
        {
            return decode();
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>A type definition by its metadata name (<c>Namespace.Name`1</c>, <c>Outer/Inner</c>), in the first module that has it.</summary>
    private (MetadataReader Reader, TypeDefinition Type, string Path)? Locate(string metadataName, IReadOnlyList<string> modules)
    {
        foreach (var path in modules)
        {
            if (Load(path) is { } module && module.Types.TryGetValue(metadataName, out var handle))
            {
                return (module.Reader, module.Reader.GetTypeDefinition(handle), path);
            }
        }

        return null;
    }

    /// <summary>A base type's metadata name, a generic instantiation's (<c>Dictionary&lt;string, object&gt;</c>) by its definition's (<c>Dictionary`2</c>).</summary>
    private static string? BaseTypeMetadataName(MetadataReader reader, EntityHandle handle)
    {
        if (handle.Kind != HandleKind.TypeSpecification)
        {
            return BaseTypeName(reader, handle);
        }

        var blob = reader.GetBlobReader(reader.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
        {
            return null;
        }

        blob.ReadSignatureTypeCode(); // class or value type
        return BaseTypeName(reader, blob.ReadTypeHandle());
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
                    // The metadata read into memory and the file closed: a handle held for the companion's lifetime keeps the
                    // program from being rebuilt on Windows ("being used by another process") after it was debugged.
                    var pe = new PEReader(File.OpenRead(path), PEStreamOptions.PrefetchMetadata);
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

    /// <summary>A signature's types as C# names (<c>int</c>, <c>System.Collections.Generic.List&lt;string&gt;</c>), enough to tell an exception type.</summary>
    private sealed class SignatureNames : ISignatureTypeProvider<string, object?>
    {
        public static readonly SignatureNames Instance = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Boolean => "bool",
            PrimitiveTypeCode.Byte => "byte",
            PrimitiveTypeCode.SByte => "sbyte",
            PrimitiveTypeCode.Char => "char",
            PrimitiveTypeCode.Int16 => "short",
            PrimitiveTypeCode.UInt16 => "ushort",
            PrimitiveTypeCode.Int32 => "int",
            PrimitiveTypeCode.UInt32 => "uint",
            PrimitiveTypeCode.Int64 => "long",
            PrimitiveTypeCode.UInt64 => "ulong",
            PrimitiveTypeCode.Single => "float",
            PrimitiveTypeCode.Double => "double",
            PrimitiveTypeCode.String => "string",
            PrimitiveTypeCode.Object => "object",
            PrimitiveTypeCode.IntPtr => "nint",
            PrimitiveTypeCode.UIntPtr => "nuint",
            PrimitiveTypeCode.Void => "void",
            _ => typeCode.ToString(),
        };

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => FullName(reader, reader.GetTypeDefinition(handle)).Replace('/', '.');

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => FullName(reader, reader.GetTypeReference(handle));

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";

        public string GetByReferenceType(string elementType) => elementType;

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetPinnedType(string elementType) => elementType;

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        {
            var tick = genericType.IndexOf('`', StringComparison.Ordinal);
            return (tick >= 0 ? genericType[..tick] : genericType) + "<" + string.Join(", ", typeArguments) + ">";
        }

        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public string GetFunctionPointerType(MethodSignature<string> signature) => "delegate*";

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    }

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
