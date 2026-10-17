using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Codalus.Debugging.Host.NetFx;

/// <summary>A sequence point: an IL offset and the source span it starts (hidden ones have line 0xFEEFEE).</summary>
internal readonly record struct ClrSequencePoint(int Offset, int StartLine, int StartColumn, int EndLine, int EndColumn, string Document)
{
    public bool IsHidden => StartLine == SequencePoint.HiddenLine;
}

/// <summary>A type named by a module's metadata: its assembly (null: the same module) and its full name (<c>Ns.Outer+Inner</c>).</summary>
internal readonly record struct ClrTypeName(string? Assembly, string FullName, int Token);

internal sealed record ClrFieldInfo(int Token, string Name, bool IsStatic, bool IsLiteral, object? Constant, string TypeName);

internal sealed record ClrPropertyInfo(string Name, int GetterToken, int SetterToken, bool IsStatic, int IndexParameters, string TypeName);

internal sealed record ClrMethodInfo(int Token, string Name, bool IsStatic, bool IsVirtual, IReadOnlyList<string> ParameterTypes, IReadOnlyList<string> ParameterNames, int GenericParameters, string ReturnType, bool IsSpecialName);

/// <summary>
/// A module's metadata and portable PDB, read from its file with System.Reflection.Metadata (M43b): type, method,
/// field and property names; sequence points (lines to IL offsets and back); local variables' names by slot. Windows
/// PDBs aren't read (<see cref="SymbolStatus"/> says so): Codalus builds .NET Framework projects with portable PDBs.
/// </summary>
internal sealed class ClrModuleInfo : IDisposable
{
    private static readonly StringComparer s_paths = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private readonly PEReader _pe;
    private readonly MetadataReaderProvider? _pdbProvider;
    private readonly Lazy<Dictionary<string, List<int>>> _methodsByDocument;
    private readonly Lazy<Dictionary<string, int>> _typesByName;
    private readonly Dictionary<int, IReadOnlyList<ClrSequencePoint>> _sequencePoints = [];

    private ClrModuleInfo(string path, PEReader pe, MetadataReaderProvider? pdbProvider, string? pdbPath, string symbolStatus)
    {
        Path = path;
        _pe = pe;
        _pdbProvider = pdbProvider;
        Metadata = pe.GetMetadataReader();
        Pdb = pdbProvider?.GetMetadataReader();
        PdbPath = pdbPath;
        SymbolStatus = symbolStatus;
        AssemblyName = Metadata.IsAssembly ? Metadata.GetString(Metadata.GetAssemblyDefinition().Name) : System.IO.Path.GetFileNameWithoutExtension(path);
        IsOptimized = ReadIsOptimized();
        _methodsByDocument = new(IndexDocuments);
        _typesByName = new(IndexTypes);
    }

    public string Path { get; }

    public string AssemblyName { get; }

    public MetadataReader Metadata { get; }

    /// <summary>The portable PDB's reader; null without symbols.</summary>
    public MetadataReader? Pdb { get; }

    public string? PdbPath { get; }

    public bool HasSymbols => Pdb is not null;

    /// <summary>DAP's <c>symbolStatus</c>: "Symbols loaded.", or why not.</summary>
    public string SymbolStatus { get; }

    /// <summary>Whether the assembly was compiled with optimizations (its <c>DebuggableAttribute</c> doesn't turn the JIT optimizer off).</summary>
    public bool IsOptimized { get; }

    /// <summary>Reads <paramref name="path"/>'s metadata and its portable PDB (next to it, where its debug directory says, or embedded).</summary>
    public static ClrModuleInfo Open(string path)
    {
        var stream = File.OpenRead(path);
        PEReader pe;
        try
        {
            pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                pe.Dispose();
                throw new BadImageFormatException($"{System.IO.Path.GetFileName(path)} isn't a .NET assembly.");
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        MetadataReaderProvider? provider = null;
        string? pdbPath = null;
        string status;
        try
        {
            if (pe.TryOpenAssociatedPortablePdb(path, p => File.Exists(p) ? File.OpenRead(p) : null, out provider, out pdbPath))
            {
                status = "Symbols loaded.";
            }
            else
            {
                status = WindowsPdb(pe, path) is { } windows
                    ? $"{System.IO.Path.GetFileName(windows)} is a Windows PDB, which this debugger doesn't read: build with portable PDBs (DebugType portable)."
                    : "No symbols: there's no PDB.";
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            status = "The PDB couldn't be read: " + ex.Message;
            provider?.Dispose();
            provider = null;
            pdbPath = null;
        }

        return new ClrModuleInfo(path, pe, provider, pdbPath, status);
    }

    /// <summary>A Windows PDB the debug directory names (next to the module or where it was built), if it's there.</summary>
    private static string? WindowsPdb(PEReader pe, string path)
    {
        foreach (var entry in pe.ReadDebugDirectory().Where(e => e.Type == DebugDirectoryEntryType.CodeView))
        {
            var codeView = pe.ReadCodeViewDebugDirectoryData(entry);
            var nextTo = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? string.Empty, System.IO.Path.GetFileName(codeView.Path.Replace('\\', System.IO.Path.DirectorySeparatorChar)));
            foreach (var candidate in new[] { nextTo, codeView.Path })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public void Dispose()
    {
        _pdbProvider?.Dispose();
        _pe.Dispose();
    }

    private bool ReadIsOptimized()
    {
        if (!Metadata.IsAssembly)
        {
            return false;
        }

        foreach (var handle in Metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = Metadata.GetCustomAttribute(handle);
            if (AttributeTypeName(attribute) != "System.Diagnostics.DebuggableAttribute")
            {
                continue;
            }

            // DebuggableAttribute(DebuggingModes): DisableOptimizations is 0x100; the (bool, bool) form's second argument says it.
            var value = attribute.DecodeValue(new AttributeArgumentProvider());
            if (value.FixedArguments.Length == 1 && value.FixedArguments[0].Value is int modes)
            {
                return (modes & 0x100) == 0;
            }

            if (value.FixedArguments.Length == 2 && value.FixedArguments[1].Value is bool disableOptimizer)
            {
                return !disableOptimizer;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------ types

    /// <summary>A type definition's full name as metadata spells it: <c>Ns.Outer+Inner`1</c>.</summary>
    public string TypeFullName(int typeToken)
    {
        var definition = Metadata.GetTypeDefinition(TypeHandle(typeToken));
        var name = Metadata.GetString(definition.Name);
        var declaring = definition.GetDeclaringType();
        if (!declaring.IsNil)
        {
            return TypeFullName(MetadataTokens.GetToken(declaring)) + "+" + name;
        }

        var ns = Metadata.GetString(definition.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    /// <summary>A type's name as C# shows it: <c>Ns.Outer.Inner</c>, without the generic arity (its arguments are the caller's).</summary>
    public string TypeDisplayName(int typeToken) => DisplayName(TypeFullName(typeToken));

    public static string DisplayName(string fullName)
    {
        var builder = new System.Text.StringBuilder(fullName.Length);
        var skipping = false;
        foreach (var c in fullName)
        {
            if (c == '`')
            {
                skipping = true;
                continue;
            }

            if (skipping && char.IsAsciiDigit(c))
            {
                continue;
            }

            skipping = false;
            builder.Append(c == '+' ? '.' : c);
        }

        return builder.ToString();
    }

    /// <summary>A type definition by its full name (<c>Ns.Outer+Inner</c>, or with dots for nesting); 0 when there's none.</summary>
    public int FindType(string fullName) =>
        _typesByName.Value.TryGetValue(fullName, out var token) || _typesByName.Value.TryGetValue(fullName.Replace('+', '.'), out token) ? token : 0;

    /// <summary>Type definitions whose display name ends with <paramref name="name"/> after a dot (or is it).</summary>
    public IEnumerable<int> FindTypesBySuffix(string name) =>
        _typesByName.Value.Where(p => DisplayName(p.Key) == name || DisplayName(p.Key).EndsWith("." + name, StringComparison.Ordinal))
            .Select(p => p.Value).Distinct();

    private Dictionary<string, int> IndexTypes()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var handle in Metadata.TypeDefinitions)
        {
            var token = MetadataTokens.GetToken(handle);
            var full = TypeFullName(token);
            result.TryAdd(full, token);
            result.TryAdd(full.Replace('+', '.'), token);
        }

        return result;
    }

    public int GenericParameterCount(int typeToken) => Metadata.GetTypeDefinition(TypeHandle(typeToken)).GetGenericParameters().Count;

    /// <summary>The base type (null for <c>System.Object</c> and interfaces).</summary>
    public ClrTypeName? BaseType(int typeToken)
    {
        var baseType = Metadata.GetTypeDefinition(TypeHandle(typeToken)).BaseType;
        return baseType.IsNil ? null : NameOf(baseType);
    }

    public bool IsValueType(int typeToken) => BaseType(typeToken)?.FullName is "System.ValueType" or "System.Enum" && TypeFullName(typeToken) != "System.Enum";

    public bool IsEnum(int typeToken) => BaseType(typeToken)?.FullName == "System.Enum";

    /// <summary>A type a handle names: a definition here, a reference to another assembly, or a generic instantiation's type.</summary>
    public ClrTypeName? NameOf(EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                var token = MetadataTokens.GetToken(handle);
                return new ClrTypeName(null, TypeFullName(token), token);
            case HandleKind.TypeReference:
                var reference = Metadata.GetTypeReference((TypeReferenceHandle)handle);
                var name = Metadata.GetString(reference.Name);
                var ns = Metadata.GetString(reference.Namespace);
                switch (reference.ResolutionScope.Kind)
                {
                    case HandleKind.TypeReference:
                        var outer = NameOf(reference.ResolutionScope);
                        return outer is { } o ? new ClrTypeName(o.Assembly, o.FullName + "+" + name, 0) : null;
                    case HandleKind.AssemblyReference:
                        var assembly = Metadata.GetString(Metadata.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name);
                        return new ClrTypeName(assembly, ns.Length == 0 ? name : ns + "." + name, 0);
                    default:
                        var local = FindType(ns.Length == 0 ? name : ns + "." + name);
                        return new ClrTypeName(null, ns.Length == 0 ? name : ns + "." + name, local);
                }

            case HandleKind.TypeSpecification:
                // A generic instantiation (class Derived : Base<int>): its generic type.
                var blob = Metadata.GetBlobReader(Metadata.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
                if (blob.ReadSignatureTypeCode() == SignatureTypeCode.GenericTypeInstance)
                {
                    blob.ReadCompressedInteger(); // class or value type
                    return NameOf(blob.ReadTypeHandle());
                }

                return null;
            default:
                return null;
        }
    }

    public IReadOnlyList<ClrFieldInfo> Fields(int typeToken)
    {
        var result = new List<ClrFieldInfo>();
        foreach (var handle in Metadata.GetTypeDefinition(TypeHandle(typeToken)).GetFields())
        {
            var field = Metadata.GetFieldDefinition(handle);
            var attributes = field.Attributes;
            object? constant = null;
            if ((attributes & FieldAttributes.Literal) != 0 && !field.GetDefaultValue().IsNil)
            {
                constant = ReadConstant(Metadata.GetConstant(field.GetDefaultValue()));
            }

            string type;
            try
            {
                type = field.DecodeSignature(SignatureNames.Instance, null);
            }
            catch (BadImageFormatException)
            {
                type = "?";
            }

            result.Add(new ClrFieldInfo(MetadataTokens.GetToken(handle), Metadata.GetString(field.Name), (attributes & FieldAttributes.Static) != 0, (attributes & FieldAttributes.Literal) != 0, constant, type));
        }

        return result;
    }

    public IReadOnlyList<ClrPropertyInfo> Properties(int typeToken)
    {
        var result = new List<ClrPropertyInfo>();
        foreach (var handle in Metadata.GetTypeDefinition(TypeHandle(typeToken)).GetProperties())
        {
            var property = Metadata.GetPropertyDefinition(handle);
            var accessors = property.GetAccessors();
            var getter = accessors.Getter.IsNil ? 0 : MetadataTokens.GetToken(accessors.Getter);
            var setter = accessors.Setter.IsNil ? 0 : MetadataTokens.GetToken(accessors.Setter);
            var any = getter != 0 ? accessors.Getter : accessors.Setter;
            var isStatic = !any.IsNil && (Metadata.GetMethodDefinition(any).Attributes & MethodAttributes.Static) != 0;
            int indexParameters;
            string type;
            try
            {
                var signature = property.DecodeSignature(SignatureNames.Instance, null);
                indexParameters = signature.ParameterTypes.Length;
                type = signature.ReturnType;
            }
            catch (BadImageFormatException)
            {
                indexParameters = 0;
                type = "?";
            }

            result.Add(new ClrPropertyInfo(Metadata.GetString(property.Name), getter, setter, isStatic, indexParameters, type));
        }

        return result;
    }

    public IReadOnlyList<ClrMethodInfo> Methods(int typeToken) =>
        [.. Metadata.GetTypeDefinition(TypeHandle(typeToken)).GetMethods().Select(h => Method(MetadataTokens.GetToken(h)))];

    public ClrMethodInfo Method(int methodToken)
    {
        var method = Metadata.GetMethodDefinition(MethodHandle(methodToken));
        IReadOnlyList<string> parameterTypes;
        string returnType;
        int generic;
        try
        {
            var signature = method.DecodeSignature(SignatureNames.Instance, null);
            parameterTypes = signature.ParameterTypes;
            returnType = signature.ReturnType;
            generic = signature.GenericParameterCount;
        }
        catch (BadImageFormatException)
        {
            parameterTypes = [];
            returnType = "?";
            generic = 0;
        }

        var names = new string[parameterTypes.Count];
        foreach (var handle in method.GetParameters())
        {
            var parameter = Metadata.GetParameter(handle);
            if (parameter.SequenceNumber > 0 && parameter.SequenceNumber <= names.Length)
            {
                names[parameter.SequenceNumber - 1] = Metadata.GetString(parameter.Name);
            }
        }

        for (var i = 0; i < names.Length; i++)
        {
            names[i] ??= "arg" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var attributes = method.Attributes;
        return new ClrMethodInfo(methodToken, Metadata.GetString(method.Name), (attributes & MethodAttributes.Static) != 0, (attributes & MethodAttributes.Virtual) != 0, parameterTypes, names, generic, returnType, (attributes & MethodAttributes.SpecialName) != 0);
    }

    public int DeclaringType(int methodToken) => MetadataTokens.GetToken(Metadata.GetMethodDefinition(MethodHandle(methodToken)).GetDeclaringType());

    /// <summary>A method as a stack frame shows it: <c>Ns.Type.Method</c>.</summary>
    public string MethodDisplayName(int methodToken)
    {
        var method = Metadata.GetMethodDefinition(MethodHandle(methodToken));
        return TypeDisplayName(MetadataTokens.GetToken(method.GetDeclaringType())) + "." + Metadata.GetString(method.Name);
    }

    /// <summary>Methods whose name is <paramref name="name"/>, or whose type's display name and name end with it (<c>Program.Main</c>).</summary>
    public IEnumerable<int> FindMethods(string name)
    {
        var dot = name.LastIndexOf('.');
        var method = dot < 0 ? name : name[(dot + 1)..];
        var type = dot < 0 ? null : name[..dot];
        foreach (var handle in Metadata.MethodDefinitions)
        {
            var definition = Metadata.GetMethodDefinition(handle);
            if (!Metadata.StringComparer.Equals(definition.Name, method))
            {
                continue;
            }

            var typeName = TypeDisplayName(MetadataTokens.GetToken(definition.GetDeclaringType()));
            if (type is null || typeName == type || typeName.EndsWith("." + type, StringComparison.Ordinal))
            {
                yield return MetadataTokens.GetToken(handle);
            }
        }
    }

    // ------------------------------------------------------------------ attributes

    public bool MethodHasAttribute(int methodToken, string attribute) =>
        Metadata.GetMethodDefinition(MethodHandle(methodToken)).GetCustomAttributes().Any(h => AttributeTypeName(Metadata.GetCustomAttribute(h)) == attribute);

    public bool TypeHasAttribute(int typeToken, string attribute) =>
        Metadata.GetTypeDefinition(TypeHandle(typeToken)).GetCustomAttributes().Any(h => AttributeTypeName(Metadata.GetCustomAttribute(h)) == attribute);

    /// <summary>Whether Just My Code treats a method as not the user's: [DebuggerHidden], [DebuggerStepThrough], [DebuggerNonUserCode] on it or its types.</summary>
    public bool IsNonUserCode(int methodToken)
    {
        if (MethodHasAttribute(methodToken, "System.Diagnostics.DebuggerHiddenAttribute") || MethodHasAttribute(methodToken, "System.Diagnostics.DebuggerStepThroughAttribute")
            || MethodHasAttribute(methodToken, "System.Diagnostics.DebuggerNonUserCodeAttribute"))
        {
            return true;
        }

        for (var type = MetadataTokens.TypeDefinitionHandle(MetadataTokens.GetRowNumber(Metadata.GetMethodDefinition(MethodHandle(methodToken)).GetDeclaringType())); !type.IsNil; type = Metadata.GetTypeDefinition(type).GetDeclaringType())
        {
            var token = MetadataTokens.GetToken(type);
            if (TypeHasAttribute(token, "System.Diagnostics.DebuggerStepThroughAttribute") || TypeHasAttribute(token, "System.Diagnostics.DebuggerNonUserCodeAttribute"))
            {
                return true;
            }
        }

        return false;
    }

    private string? AttributeTypeName(CustomAttribute attribute)
    {
        EntityHandle parent;
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MethodDefinition:
                parent = Metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
                break;
            case HandleKind.MemberReference:
                parent = Metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                break;
            default:
                return null;
        }

        return NameOf(parent)?.FullName;
    }

    // ------------------------------------------------------------------ symbols

    public IReadOnlyList<ClrSequencePoint> SequencePoints(int methodToken)
    {
        lock (_sequencePoints)
        {
            if (_sequencePoints.TryGetValue(methodToken, out var cached))
            {
                return cached;
            }
        }

        var result = new List<ClrSequencePoint>();
        if (Pdb is { } pdb)
        {
            var rid = MetadataTokens.GetRowNumber(MethodHandle(methodToken));
            if (rid <= pdb.MethodDebugInformation.Count)
            {
                try
                {
                    var information = pdb.GetMethodDebugInformation(MetadataTokens.MethodDebugInformationHandle(rid));
                    foreach (var point in information.GetSequencePoints())
                    {
                        result.Add(new ClrSequencePoint(point.Offset, point.StartLine, point.StartColumn, point.EndLine, point.EndColumn, point.Document.IsNil ? string.Empty : pdb.GetString(pdb.GetDocument(point.Document).Name)));
                    }
                }
                catch (BadImageFormatException)
                {
                }
            }
        }

        lock (_sequencePoints)
        {
            _sequencePoints[methodToken] = result;
        }

        return result;
    }

    /// <summary>The sequence point an IL offset is in (the last one at or before it); hidden ones included.</summary>
    public ClrSequencePoint? SequencePointAt(int methodToken, int ilOffset)
    {
        ClrSequencePoint? found = null;
        foreach (var point in SequencePoints(methodToken).OrderBy(p => p.Offset))
        {
            if (point.Offset > ilOffset)
            {
                break;
            }

            found = point;
        }

        return found;
    }

    /// <summary>The source line an IL offset is on: the last visible sequence point at or before it.</summary>
    public ClrSequencePoint? LineAt(int methodToken, int ilOffset) =>
        SequencePoints(methodToken).Where(p => !p.IsHidden && p.Offset <= ilOffset).OrderBy(p => p.Offset).Cast<ClrSequencePoint?>().LastOrDefault();

    /// <summary>The IL range of the sequence point an offset is in: from it to the next one (or the method's end).</summary>
    public (int Start, int End)? RangeAt(int methodToken, int ilOffset)
    {
        var points = SequencePoints(methodToken).Select(p => p.Offset).Distinct().Order().ToList();
        if (points.Count == 0)
        {
            return null;
        }

        var start = points.LastOrDefault(o => o <= ilOffset, -1);
        if (start < 0)
        {
            return null;
        }

        var next = points.FirstOrDefault(o => o > start, -1);
        return (start, next < 0 ? int.MaxValue : next);
    }

    /// <summary>Whether this module's PDB has <paramref name="path"/> (the same path, or the only document with its file name).</summary>
    public string? MatchDocument(string path)
    {
        var documents = _methodsByDocument.Value.Keys;
        if (documents.FirstOrDefault(d => SamePath(d, path)) is { } exact)
        {
            return exact;
        }

        var name = System.IO.Path.GetFileName(path);
        var byName = documents.Where(d => s_paths.Equals(System.IO.Path.GetFileName(d.Replace('\\', '/')), name)).Take(2).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    /// <summary>Where a breakpoint on <paramref name="line"/> of <paramref name="document"/> binds: each method's first offset on the line, or on the next line with code in a method that spans it.</summary>
    public IReadOnlyList<(int Method, int Offset, int Line)> Locations(string document, int line)
    {
        if (!_methodsByDocument.Value.TryGetValue(document, out var methods))
        {
            return [];
        }

        var points = methods.SelectMany(m => SequencePoints(m).Where(p => !p.IsHidden && p.Document == document).Select(p => (Method: m, Point: p))).ToList();
        var exact = points.Where(p => p.Point.StartLine == line).ToList();
        if (exact.Count == 0)
        {
            var spanning = points.GroupBy(p => p.Method).Where(g => g.Min(p => p.Point.StartLine) <= line && g.Max(p => p.Point.EndLine) >= line).Select(g => g.Key).ToHashSet();
            var next = points.Where(p => p.Point.StartLine > line && spanning.Contains(p.Method)).OrderBy(p => p.Point.StartLine).FirstOrDefault();
            if (next.Point.StartLine == 0)
            {
                return [];
            }

            exact = [.. points.Where(p => p.Point.StartLine == next.Point.StartLine && spanning.Contains(p.Method))];
        }

        // The innermost method wins when a lambda and its enclosing method share the line: one location per method.
        return [.. exact.GroupBy(p => p.Method).Select(g => g.OrderBy(p => p.Point.Offset).First()).Select(p => (p.Method, p.Point.Offset, p.Point.StartLine))];
    }

    private Dictionary<string, List<int>> IndexDocuments()
    {
        var result = new Dictionary<string, List<int>>(s_paths);
        if (Pdb is not { } pdb)
        {
            return result;
        }

        foreach (var handle in pdb.MethodDebugInformation)
        {
            var information = pdb.GetMethodDebugInformation(handle);
            if (information.SequencePointsBlob.IsNil)
            {
                continue;
            }

            var token = MetadataTokens.GetToken(MetadataTokens.MethodDefinitionHandle(MetadataTokens.GetRowNumber(handle)));
            var documents = new HashSet<string>(s_paths);
            try
            {
                foreach (var point in information.GetSequencePoints())
                {
                    if (!point.Document.IsNil)
                    {
                        documents.Add(pdb.GetString(pdb.GetDocument(point.Document).Name));
                    }
                }
            }
            catch (BadImageFormatException)
            {
                continue;
            }

            foreach (var document in documents)
            {
                if (!result.TryGetValue(document, out var list))
                {
                    result[document] = list = [];
                }

                list.Add(token);
            }
        }

        return result;
    }

    /// <summary>The local variables in scope at an IL offset, by slot (compiler-hidden ones left out); empty without symbols.</summary>
    public IReadOnlyList<(int Slot, string Name)> Locals(int methodToken, int ilOffset)
    {
        if (Pdb is not { } pdb)
        {
            return [];
        }

        var result = new List<(int, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            // Innermost scopes first, so a name shadowed in a nested block shows its inner variable.
            var scopes = pdb.GetLocalScopes(MethodHandle(methodToken)).Select(pdb.GetLocalScope)
                .Where(s => s.StartOffset <= ilOffset && ilOffset < s.EndOffset).OrderBy(s => s.Length).ToList();
            foreach (var scope in scopes)
            {
                foreach (var handle in scope.GetLocalVariables())
                {
                    var variable = pdb.GetLocalVariable(handle);
                    var name = pdb.GetString(variable.Name);
                    if ((variable.Attributes & LocalVariableAttributes.DebuggerHidden) != 0 || name.Length == 0 || !seen.Add(name))
                    {
                        continue;
                    }

                    result.Add((variable.Index, name));
                }
            }
        }
        catch (BadImageFormatException)
        {
        }

        return result;
    }

    private static bool SamePath(string a, string b) =>
        s_paths.Equals(a, b) || s_paths.Equals(Normalize(a), Normalize(b));

    private static string Normalize(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path.Replace('\\', System.IO.Path.DirectorySeparatorChar));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private object? ReadConstant(Constant constant)
    {
        var reader = Metadata.GetBlobReader(constant.Value);
        return constant.TypeCode switch
        {
            ConstantTypeCode.Boolean => reader.ReadBoolean(),
            ConstantTypeCode.Char => reader.ReadChar(),
            ConstantTypeCode.SByte => reader.ReadSByte(),
            ConstantTypeCode.Byte => reader.ReadByte(),
            ConstantTypeCode.Int16 => reader.ReadInt16(),
            ConstantTypeCode.UInt16 => reader.ReadUInt16(),
            ConstantTypeCode.Int32 => reader.ReadInt32(),
            ConstantTypeCode.UInt32 => reader.ReadUInt32(),
            ConstantTypeCode.Int64 => reader.ReadInt64(),
            ConstantTypeCode.UInt64 => reader.ReadUInt64(),
            ConstantTypeCode.Single => reader.ReadSingle(),
            ConstantTypeCode.Double => reader.ReadDouble(),
            ConstantTypeCode.String => reader.ReadUTF16(reader.Length),
            _ => null,
        };
    }

    private static TypeDefinitionHandle TypeHandle(int token) => (TypeDefinitionHandle)MetadataTokens.EntityHandle(token);

    private static MethodDefinitionHandle MethodHandle(int token) => (MethodDefinitionHandle)MetadataTokens.EntityHandle(token);

    /// <summary>Decodes the DebuggableAttribute's arguments (an enum is read as its underlying int).</summary>
    private sealed class AttributeArgumentProvider : ICustomAttributeTypeProvider<object?>
    {
        public object? GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode;

        public object? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => PrimitiveTypeCode.Int32;

        public object? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => PrimitiveTypeCode.Int32;

        public object? GetSZArrayType(object? elementType) => null;

        public object? GetSystemType() => null;

        public object? GetTypeFromSerializedName(string name) => null;

        public PrimitiveTypeCode GetUnderlyingEnumType(object? type) => PrimitiveTypeCode.Int32;

        public bool IsSystemType(object? type) => false;
    }
}

/// <summary>Signatures decoded to C#-like type names (<c>int</c>, <c>string</c>, <c>List`1&lt;T&gt;</c>), for display and overload matching.</summary>
internal sealed class SignatureNames : ISignatureTypeProvider<string, object?>
{
    public static readonly SignatureNames Instance = new();

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Byte => "byte",
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
        PrimitiveTypeCode.IntPtr => "System.IntPtr",
        PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.TypedReference => "System.TypedReference",
        _ => typeCode.ToString(),
    };

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var definition = reader.GetTypeDefinition(handle);
        var name = reader.GetString(definition.Name);
        var declaring = definition.GetDeclaringType();
        if (!declaring.IsNil)
        {
            return GetTypeFromDefinition(reader, declaring, rawTypeKind) + "." + name;
        }

        var ns = reader.GetString(definition.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Name);
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return GetTypeFromReference(reader, (TypeReferenceHandle)reference.ResolutionScope, rawTypeKind) + "." + name;
        }

        var ns = reader.GetString(reference.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";

    public string GetByReferenceType(string elementType) => "ref " + elementType;

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(", ", typeArguments) + ">";

    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

    public string GetPinnedType(string elementType) => elementType;
}
