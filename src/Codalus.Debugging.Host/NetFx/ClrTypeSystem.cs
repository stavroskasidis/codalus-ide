using System.Globalization;
using System.Text;

namespace Codalus.Debugging.Host.NetFx;

/// <summary>
/// The debuggee's types as the session sees them (M43b): each loaded module's metadata (<see cref="ClrModuleInfo"/>),
/// type references resolved across modules (by assembly name, then by full name anywhere, for type forwarders), base
/// type chains, C# display names, and values formatted as the Variables window shows them.
/// </summary>
internal sealed class ClrTypeSystem(Func<IReadOnlyList<(IClrModule Module, ClrModuleInfo Info)>> modules)
{
    public const int MaxString = 1000;

    private static readonly Dictionary<string, string> s_keywords = ExpressionParser.Keywords.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

    public IReadOnlyList<(IClrModule Module, ClrModuleInfo Info)> Modules => modules();

    public ClrModuleInfo? Info(IClrModule? module) => module is null ? null : Modules.FirstOrDefault(m => ReferenceEquals(m.Module, module)).Info;

    /// <summary>mscorlib (where System.Object, System.String and the primitives are).</summary>
    public (IClrModule Module, ClrModuleInfo Info)? CoreLibrary
    {
        get
        {
            foreach (var entry in Modules)
            {
                if (string.Equals(entry.Info.AssemblyName, "mscorlib", StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }

            return null;
        }
    }

    /// <summary>A type a module names, as a module and a type definition token; null when its assembly isn't loaded.</summary>
    public (IClrModule Module, int Token)? Resolve(IClrModule from, ClrTypeName name)
    {
        if (name.Assembly is null && name.Token != 0)
        {
            return (from, name.Token);
        }

        var all = Modules;
        if (name.Assembly is { } assembly)
        {
            foreach (var (module, info) in all)
            {
                if (string.Equals(info.AssemblyName, assembly, StringComparison.OrdinalIgnoreCase) && info.FindType(name.FullName) is var token and not 0)
                {
                    return (module, token);
                }
            }
        }

        // Forwarded (netstandard, System.Runtime facades) or in the same module by name: anywhere it's defined.
        return FindType(name.FullName);
    }

    /// <summary>A type by its full name in any loaded module, mscorlib first.</summary>
    public (IClrModule Module, int Token)? FindType(string fullName)
    {
        var all = Modules;
        foreach (var (module, info) in all.OrderBy(m => string.Equals(m.Info.AssemblyName, "mscorlib", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            if (info.FindType(fullName) is var token and not 0)
            {
                return (module, token);
            }
        }

        return null;
    }

    /// <summary>
    /// A type as an expression names it: a keyword, a full name, a name in <paramref name="contextNamespace"/> or one
    /// of its parents, or the only type with that name (types of loaded modules).
    /// </summary>
    public (IClrModule Module, int Token)? FindTypeByName(string name, string? contextNamespace)
    {
        if (ExpressionParser.Keywords.TryGetValue(name, out var keyword))
        {
            return FindType(keyword);
        }

        if (FindType(name) is { } full)
        {
            return full;
        }

        for (var ns = contextNamespace; !string.IsNullOrEmpty(ns); ns = ns.LastIndexOf('.') is var dot and > 0 ? ns[..dot] : null)
        {
            if (FindType(ns + "." + name) is { } inNamespace)
            {
                return inNamespace;
            }
        }

        if (FindType("System." + name) is { } inSystem)
        {
            return inSystem;
        }

        var matches = Modules.SelectMany(m => m.Info.FindTypesBySuffix(name).Select(t => (m.Module, Token: t))).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>A type definition as a runtime type (no type arguments).</summary>
    public static ClrType TypeOf(IClrModule module, int token) => new(module, token);

    /// <summary>The module and token of a runtime type: its own, or mscorlib's for primitives, string and object.</summary>
    public (IClrModule Module, int Token)? Definition(ClrType type)
    {
        if (type.Module is { } module && type.Token != 0)
        {
            return (module, type.Token);
        }

        return type.IsArray ? FindType("System.Array") : type.Name is { } name ? FindType(name) : null;
    }

    /// <summary>A type and its base types, most derived first (each a module, a token and its metadata).</summary>
    public IEnumerable<(IClrModule Module, int Token, ClrModuleInfo Info)> Chain(ClrType type)
    {
        var current = Definition(type);
        for (var depth = 0; current is { } c && depth < 32; depth++)
        {
            if (Info(c.Module) is not { } info)
            {
                yield break;
            }

            yield return (c.Module, c.Token, info);
            current = info.BaseType(c.Token) is { } baseName ? Resolve(c.Module, baseName) : null;
        }
    }

    /// <summary>A type's metadata full name (<c>System.Collections.Generic.List`1</c>), or its primitive name.</summary>
    public string FullName(ClrType type) =>
        type.IsArray ? FullName(type.Element!) + "[]" : Definition(type) is { } d && Info(d.Module) is { } info ? info.TypeFullName(d.Token) : type.Name ?? "?";

    /// <summary>A type as C# shows it: <c>int</c>, <c>string[]</c>, <c>System.Collections.Generic.List&lt;int&gt;</c>.</summary>
    public string Name(ClrType? type)
    {
        if (type is null)
        {
            return "?";
        }

        if (type.IsArray)
        {
            return Name(type.Element) + "[" + new string(',', Math.Max(0, type.Rank - 1)) + "]";
        }

        string name;
        if (type.Module is { } module && type.Token != 0 && Info(module) is { } info)
        {
            var full = info.TypeFullName(type.Token);
            if (s_keywords.TryGetValue(full, out var keyword) && type.TypeArguments.Count == 0)
            {
                return keyword;
            }

            if (full == "System.Nullable`1" && type.TypeArguments.Count == 1)
            {
                return Name(type.TypeArguments[0]) + "?";
            }

            name = ClrModuleInfo.DisplayName(full);
        }
        else if (type.Name is { } primitive)
        {
            return s_keywords.TryGetValue(primitive, out var keyword) ? keyword : ClrModuleInfo.DisplayName(primitive);
        }
        else
        {
            return "?";
        }

        return type.TypeArguments.Count == 0 ? name : name + "<" + string.Join(", ", type.TypeArguments.Select(Name)) + ">";
    }

    /// <summary>Whether a value's type is, or derives from, the type named <paramref name="fullName"/>.</summary>
    public bool IsA(ClrType type, string fullName) => Chain(type).Any(c => c.Info.TypeFullName(c.Token) == fullName);

    // ------------------------------------------------------------------ formatting

    /// <summary>A value as the Variables window shows it.</summary>
    public string Format(IClrValue? value)
    {
        if (value is null)
        {
            return "(unavailable)";
        }

        try
        {
            switch (value.Kind)
            {
                case ClrValueKind.Null:
                    return "null";
                case ClrValueKind.Primitive:
                    return FormatPrimitive(value.Primitive);
                case ClrValueKind.String:
                    return Quote(value.Text ?? string.Empty);
                case ClrValueKind.Array:
                    var dimensions = value.Dimensions.Count > 1 ? string.Join(", ", value.Dimensions.Select(d => d.ToString(CultureInfo.InvariantCulture))) : value.Length.ToString(CultureInfo.InvariantCulture);
                    return "{" + Name(value.Type.Element) + "[" + dimensions + "]}";
                default:
                    return FormatObject(value);
            }
        }
        catch (InvalidOperationException ex)
        {
            return "(" + ex.Message + ")";
        }
    }

    /// <summary>mscorlib's primitive structs: a boxed primitive may come as one, with its value in <c>m_value</c>.</summary>
    private static readonly HashSet<string> s_primitiveStructs = new(StringComparer.Ordinal)
    {
        "System.Boolean", "System.Char", "System.SByte", "System.Byte", "System.Int16", "System.UInt16", "System.Int32", "System.UInt32",
        "System.Int64", "System.UInt64", "System.Single", "System.Double", "System.IntPtr", "System.UIntPtr",
    };

    /// <summary>The primitive inside a boxed primitive that came as an object (<c>m_value</c>); null for anything else.</summary>
    public IClrValue? Unboxed(IClrValue value) =>
        value.Kind == ClrValueKind.Object && s_primitiveStructs.Contains(FullName(value.Type)) && Field(value, "m_value") is { Kind: ClrValueKind.Primitive } inner ? inner : null;

    private string FormatObject(IClrValue value)
    {
        if (Unboxed(value) is { } primitive)
        {
            return FormatPrimitive(primitive.Primitive);
        }

        var full = FullName(value.Type);
        switch (full)
        {
            case "System.Decimal" when ReadDecimal(value) is { } d:
                return d.ToString(CultureInfo.InvariantCulture);
            case "System.DateTime" when Field(value, "dateData") is { Primitive: ulong data }:
                return "{" + new DateTime((long)(data & 0x3FFFFFFFFFFFFFFF)).ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.') + "}";
            case "System.TimeSpan" when Field(value, "_ticks") is { Primitive: long ticks }:
                return "{" + TimeSpan.FromTicks(ticks).ToString("c", CultureInfo.InvariantCulture) + "}";
            case "System.Nullable`1":
                return Field(value, "hasValue") is { Primitive: true } ? Format(Field(value, "value")) : "null";
        }

        if (Definition(value.Type) is { } definition && Info(definition.Module) is { } info && info.IsEnum(definition.Token))
        {
            return FormatEnum(value, info, definition.Token);
        }

        return "{" + Name(value.Type) + "}";
    }

    private string FormatEnum(IClrValue value, ClrModuleInfo info, int token)
    {
        var fields = info.Fields(token);
        var raw = fields.FirstOrDefault(f => !f.IsStatic) is { } valueField ? value.Field(Definition(value.Type)!.Value.Module, token, valueField.Token).Primitive : null;
        if (raw is null)
        {
            return "{" + Name(value.Type) + "}";
        }

        var number = Convert.ToInt64(raw is ulong u ? unchecked((long)u) : raw, CultureInfo.InvariantCulture);
        var constants = fields.Where(f => f.IsLiteral && f.Constant is not null).Select(f => (f.Name, Value: Convert.ToInt64(f.Constant is ulong cu ? unchecked((long)cu) : f.Constant, CultureInfo.InvariantCulture))).ToList();
        if (constants.FirstOrDefault(c => c.Value == number) is { Name: { } exact })
        {
            return exact;
        }

        if (info.TypeHasAttribute(token, "System.FlagsAttribute") && number != 0)
        {
            var parts = constants.Where(c => c.Value != 0 && (number & c.Value) == c.Value).ToList();
            if (parts.Aggregate(0L, (all, c) => all | c.Value) == number)
            {
                return string.Join(" | ", parts.Select(c => c.Name));
            }
        }

        return number.ToString(CultureInfo.InvariantCulture);
    }

    private decimal? ReadDecimal(IClrValue value)
    {
        if (Field(value, "flags") is { Primitive: int flags } && Field(value, "hi") is { Primitive: int hi } && Field(value, "lo") is { Primitive: int lo } && Field(value, "mid") is { Primitive: int mid })
        {
            var scale = (byte)((flags >> 16) & 0xFF);
            return scale > 28 ? null : new decimal(lo, mid, hi, flags < 0, scale);
        }

        return null;
    }

    /// <summary>An instance field of a value by name, in its type or a base type; null when there's none.</summary>
    public IClrValue? Field(IClrValue value, string name)
    {
        foreach (var (module, token, info) in Chain(value.Type))
        {
            if (info.Fields(token).FirstOrDefault(f => !f.IsStatic && f.Name == name) is { } field)
            {
                return value.Field(module, token, field.Token);
            }
        }

        return null;
    }

    public static string FormatPrimitive(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        char c => FormatChar(c),
        string s => Quote(s),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string FormatChar(char c) => ((int)c).ToString(CultureInfo.InvariantCulture) + " '" + Escape(c, '\'') + "'";

    public static string Quote(string text)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in text.Length > MaxString ? text[..MaxString] : text)
        {
            builder.Append(Escape(c, '"'));
        }

        if (text.Length > MaxString)
        {
            builder.Append('…');
        }

        return builder.Append('"').ToString();
    }

    private static string Escape(char c, char quote) => c switch
    {
        '\\' => "\\\\",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        '\0' => "\\0",
        _ when c == quote => "\\" + c,
        _ => c.ToString(),
    };

    /// <summary>A field's name as shown: an auto-property's backing field (<c>&lt;Name&gt;k__BackingField</c>) is <c>Name</c>; other compiler fields are hidden (null).</summary>
    public static string? DisplayFieldName(string field)
    {
        if (field.StartsWith('<'))
        {
            var close = field.IndexOf('>', StringComparison.Ordinal);
            return close > 1 && field.EndsWith("k__BackingField", StringComparison.Ordinal) ? field[1..close] : null;
        }

        return field;
    }
}
