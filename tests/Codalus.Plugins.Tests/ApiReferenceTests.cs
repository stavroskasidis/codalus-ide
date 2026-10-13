using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Codalus.Plugins.Tests;

/// <summary>
/// The plugin API reference (M22b), <c>docs/user-guide/plugin-api.md</c>, is generated from the API's assembly and its
/// XML documentation, so the site's page never drifts from the API: this fails when the page is out of date, and
/// <c>CODALUS_UPDATE_API_REFERENCE=1</c> writes it again.
/// </summary>
public sealed partial class ApiReferenceTests
{
    private static string RepoRoot => typeof(ApiReferenceTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "CodalusRepoRoot").Value!;

    private static string PageFile => Path.Combine(RepoRoot, "docs", "user-guide", "plugin-api.md");

    private static Assembly Api => typeof(ICodalusPlugin).Assembly;

    private static readonly HashSet<string> s_recordMembers = ["Equals", "GetHashCode", "ToString", "Deconstruct", "PrintMembers", "<Clone>$", "EqualityContract"];

    private const BindingFlags DeclaredPublic = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Fact]
    public void The_api_reference_page_is_up_to_date()
    {
        var page = Generate();
        if (Environment.GetEnvironmentVariable("CODALUS_UPDATE_API_REFERENCE") == "1")
        {
            File.WriteAllText(PageFile, page, new UTF8Encoding(false));
        }

        var current = File.Exists(PageFile) ? File.ReadAllText(PageFile).ReplaceLineEndings("\n") : string.Empty;
        Assert.True(current == page, "docs/user-guide/plugin-api.md is out of date with the plugin API's XML docs: run the plugin tests with CODALUS_UPDATE_API_REFERENCE=1.");
    }

    [Fact]
    public void Every_public_type_and_member_is_documented()
    {
        var docs = Docs();
        var missing = new List<string>();
        foreach (var type in Types())
        {
            if (!docs.ContainsKey(TypeId(type)))
            {
                missing.Add(TypeId(type));
            }

            foreach (var member in Members(type).Where(m => Summary(docs, m) is null))
            {
                missing.Add(MemberId(member));
            }
        }

        Assert.True(missing.Count == 0, "Undocumented plugin API members (the reference shows them without a description):\n" + string.Join('\n', missing));
    }

    internal static string Generate()
    {
        var docs = Docs();
        var types = Types().ToList();
        var page = new StringBuilder();
        page.Append("# Plugin API reference\n\n");
        page.Append(CultureInfo($"The plugin API, version {PluginApi.Version}: every public type and member of `Codalus.Plugins.Abstractions`, from its documentation comments. How to write a plugin is in the [plugin guide](../plugins.md); this page is generated from the API and checked by its tests, so it matches the version it names.\n\n"));
        foreach (var group in types.GroupBy(t => t.Namespace!).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            page.Append(CultureInfo($"## {group.Key}\n\n"));
            foreach (var type in group.OrderBy(t => DisplayName(t), StringComparer.Ordinal))
            {
                page.Append(CultureInfo($"- [`{DisplayName(type)}`](#{Anchor(type)})\n"));
            }

            page.Append('\n');
        }

        foreach (var type in types.OrderBy(t => t.Namespace, StringComparer.Ordinal).ThenBy(t => DisplayName(t), StringComparer.Ordinal))
        {
            page.Append(CultureInfo($"### `{DisplayName(type)}`\n\n"));
            page.Append(CultureInfo($"`{Kind(type)} {type.Namespace}.{DisplayName(type)}`"));
            if (BaseTypes(type) is { Length: > 0 } bases)
            {
                page.Append(CultureInfo($" : {bases}"));
            }

            page.Append("\n\n");
            if (docs.TryGetValue(TypeId(type), out var typeDoc) && Text(typeDoc.Element("summary")) is { Length: > 0 } summary)
            {
                page.Append(summary).Append("\n\n");
            }

            var members = Members(type).ToList();
            if (members.Count > 0)
            {
                page.Append("| Member | Description |\n|---|---|\n");
                foreach (var member in members)
                {
                    page.Append(CultureInfo($"| `{Escape(Signature(member))}` | {Escape(Summary(docs, member) ?? string.Empty)} |\n"));
                }

                page.Append('\n');
            }
        }

        return page.ToString();
    }

    private static string CultureInfo(FormattableString text) => FormattableString.Invariant(text);

    private static IEnumerable<Type> Types() => Api.GetExportedTypes().Where(t => !t.IsNested || t.DeclaringType!.IsPublic);

    private static IEnumerable<MemberInfo> Members(Type type)
    {
        var members = type.GetMembers(DeclaredPublic)
            .Where(m => !s_recordMembers.Contains(m.Name) && !m.Name.StartsWith('<'))
            .Where(m => m switch
            {
                MethodInfo method => !method.IsSpecialName,
                ConstructorInfo => !type.IsAbstract || !type.IsSealed,
                FieldInfo field => !type.IsEnum || field.IsStatic,
                PropertyInfo or EventInfo => true,
                _ => false,
            });
        return members.OrderBy(m => m switch { ConstructorInfo => 0, FieldInfo => 1, PropertyInfo => 2, EventInfo => 3, _ => 4 })
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .ThenBy(MemberId, StringComparer.Ordinal);
    }

    private static string Kind(Type type) =>
        type.IsInterface ? "interface" : type.IsEnum ? "enum" : type.IsValueType ? "struct" : type.IsAbstract && type.IsSealed ? "static class"
            : type.GetMethod("<Clone>$") is not null ? "record" : type.IsAbstract ? "abstract class" : type.IsSealed ? "sealed class" : "class";

    private static string BaseTypes(Type type)
    {
        var bases = new List<string>();
        if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType) && baseType != typeof(Enum))
        {
            bases.Add(Friendly(baseType, null));
        }

        bases.AddRange(type.GetInterfaces().Where(i => type.BaseType?.GetInterfaces().Contains(i) != true && !i.Name.StartsWith("IEquatable", StringComparison.Ordinal)).Select(i => Friendly(i)).Order(StringComparer.Ordinal));
        return string.Join(", ", bases);
    }

    // ------------------------------------------------------------------ signatures

    private static readonly NullabilityInfoContext s_nullability = new();

    private static string Signature(MemberInfo member) => member switch
    {
        ConstructorInfo c => $"{DisplayName(c.DeclaringType!)}({Parameters(c)})",
        MethodInfo m => $"{(m.IsStatic ? "static " : string.Empty)}{Friendly(m.ReturnType, s_nullability.Create(m.ReturnParameter))} {m.Name}{GenericArguments(m)}({Parameters(m)})",
        PropertyInfo p => $"{Friendly(p.PropertyType, s_nullability.Create(p))} {p.Name} {{{(p.GetMethod?.IsPublic == true ? " get;" : string.Empty)}{(p.SetMethod?.IsPublic == true ? (IsInit(p) ? " init;" : " set;") : string.Empty)} }}",
        FieldInfo f when f.DeclaringType!.IsEnum => $"{f.Name} = {Convert.ToInt64(f.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture)}",
        FieldInfo f => $"{(f.IsLiteral ? "const " : f.IsStatic ? "static " : string.Empty)}{Friendly(f.FieldType)} {f.Name}{(f.IsLiteral ? $" = {Literal(f.GetRawConstantValue())}" : string.Empty)}",
        EventInfo e => $"event {Friendly(e.EventHandlerType!, s_nullability.Create(e))} {e.Name}",
        _ => member.Name,
    };

    private static string Literal(object? value) => value is string text ? $"\"{text}\"" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null";

    private static bool IsInit(PropertyInfo property) =>
        property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");

    private static string GenericArguments(MethodInfo method) =>
        method.IsGenericMethodDefinition ? "<" + string.Join(", ", method.GetGenericArguments().Select(a => a.Name)) + ">" : string.Empty;

    private static string Parameters(MethodBase method) =>
        string.Join(", ", method.GetParameters().Select(p =>
            (p.GetCustomAttribute<ParamArrayAttribute>() is null ? string.Empty : "params ")
            + Friendly(p.ParameterType, s_nullability.Create(p)) + " " + p.Name
            + (p.HasDefaultValue ? " = " + Default(p) : string.Empty)));

    private static string Default(ParameterInfo parameter) => parameter.DefaultValue switch
    {
        null => parameter.ParameterType.IsValueType && Nullable.GetUnderlyingType(parameter.ParameterType) is null ? "default" : "null",
        bool value => value ? "true" : "false",
        Enum value => value.GetType().Name + "." + value,
        var value => Literal(value),
    };

    private static readonly Dictionary<Type, string> s_keywords = new()
    {
        [typeof(void)] = "void", [typeof(string)] = "string", [typeof(bool)] = "bool", [typeof(int)] = "int", [typeof(long)] = "long",
        [typeof(double)] = "double", [typeof(object)] = "object", [typeof(byte)] = "byte",
    };

    private static string Friendly(Type type, NullabilityInfo? nullability = null)
    {
        var nullable = nullability?.ReadState == NullabilityState.Nullable && !type.IsValueType ? "?" : string.Empty;
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Friendly(underlying) + "?";
        }

        if (type.IsGenericParameter)
        {
            return type.Name; // unconstrained: as declared
        }

        if (type.IsArray)
        {
            return Friendly(type.GetElementType()!, nullability?.ElementType) + "[]" + nullable;
        }

        if (s_keywords.TryGetValue(type, out var keyword))
        {
            return keyword + nullable;
        }

        var name = type.IsNested ? Friendly(type.DeclaringType!) + "." + type.Name : type.Name;
        if (type.IsGenericType)
        {
            var arguments = type.GetGenericArguments();
            var argumentNullability = nullability?.GenericTypeArguments;
            name = name[..name.IndexOf('`', StringComparison.Ordinal)] + "<"
                + string.Join(", ", arguments.Select((a, i) => Friendly(a, argumentNullability is { } n && i < n.Length ? n[i] : null))) + ">";
        }

        return name + nullable;
    }

    private static string DisplayName(Type type)
    {
        var name = type.IsNested ? DisplayName(type.DeclaringType!) + "." + type.Name : type.Name;
        return type.IsGenericTypeDefinition
            ? name[..name.IndexOf('`', StringComparison.Ordinal)] + "<" + string.Join(", ", type.GetGenericArguments().Select(a => a.Name)) + ">"
            : name;
    }

    /// <summary>The heading's id as the site (and GitHub) makes it: lower case, punctuation dropped, spaces to dashes.</summary>
    private static string Anchor(Type type) => AnchorRegex().Replace(DisplayName(type).ToLowerInvariant(), string.Empty).Replace(' ', '-');

    [GeneratedRegex(@"[^a-z0-9 _-]")]
    private static partial Regex AnchorRegex();

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);

    // ------------------------------------------------------------------ XML documentation

    private static Dictionary<string, XElement> Docs()
    {
        var file = Path.ChangeExtension(Api.Location, ".xml");
        Assert.True(File.Exists(file), $"No XML documentation next to {Api.Location}.");
        return XDocument.Load(file).Root!.Element("members")!.Elements("member").ToDictionary(m => (string)m.Attribute("name")!, StringComparer.Ordinal);
    }

    /// <summary>A member's summary; a record's property falls back to its type's <c>param</c> of the same name.</summary>
    private static string? Summary(Dictionary<string, XElement> docs, MemberInfo member)
    {
        if (docs.TryGetValue(MemberId(member), out var doc) && Text(doc.Element("summary")) is { Length: > 0 } summary)
        {
            return summary;
        }

        if (member is PropertyInfo or ConstructorInfo && docs.TryGetValue(TypeId(member.DeclaringType!), out var typeDoc))
        {
            if (member is ConstructorInfo)
            {
                return Text(typeDoc.Element("summary")) is { Length: > 0 } ? "Creates one." : null;
            }

            return Text(typeDoc.Elements("param").FirstOrDefault(p => (string?)p.Attribute("name") == member.Name)) is { Length: > 0 } param ? param : null;
        }

        return null;
    }

    /// <summary>Documentation markup as Markdown: code, references by their short names, whitespace collapsed.</summary>
    private static string? Text(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        var text = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            text.Append(node switch
            {
                XText t => t.Value.Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal),
                XElement { Name.LocalName: "c" } c => "`" + c.Value + "`",
                XElement { Name.LocalName: "see" or "seealso" } see when see.Attribute("cref") is { } cref => "`" + ShortName(cref.Value) + "`",
                XElement { Name.LocalName: "see" } see when see.Attribute("langword") is { } word => "`" + word.Value + "`",
                XElement { Name.LocalName: "paramref" or "typeparamref" } p => "`" + (string?)p.Attribute("name") + "`",
                XElement { Name.LocalName: "para" } para => " " + Text(para) + " ",
                XElement other => other.Value,
                _ => string.Empty,
            });
        }

        return WhitespaceRegex().Replace(text.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary><c>M:Codalus.Plugins.IWindow.SetView(System.String,…)</c> → <c>IWindow.SetView</c>.</summary>
    private static string ShortName(string cref)
    {
        var name = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        if (name.IndexOf('(', StringComparison.Ordinal) is var paren and >= 0)
        {
            name = name[..paren];
        }

        name = BacktickRegex().Replace(name, string.Empty);
        var parts = name.Split('.');
        return cref.StartsWith("T:", StringComparison.Ordinal) || parts.Length < 2 ? parts[^1] : parts[^2] + "." + parts[^1];
    }

    [GeneratedRegex("`+[0-9]+")]
    private static partial Regex BacktickRegex();

    private static string TypeId(Type type) => "T:" + IdName(type);

    private static string IdName(Type type) => type.IsNested ? IdName(type.DeclaringType!) + "." + type.Name : type.Namespace + "." + type.Name;

    /// <summary>The documentation id of a member (ECMA-334 annex D), as the compiler writes it in the XML file.</summary>
    internal static string MemberId(MemberInfo member)
    {
        var owner = IdName(member.DeclaringType!);
        return member switch
        {
            ConstructorInfo c => $"M:{owner}.#ctor{ParameterIds(c)}",
            MethodInfo m => $"M:{owner}.{m.Name}{(m.IsGenericMethodDefinition ? "``" + m.GetGenericArguments().Length.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty)}{ParameterIds(m)}",
            PropertyInfo p => $"P:{owner}.{p.Name}",
            FieldInfo f => $"F:{owner}.{f.Name}",
            EventInfo e => $"E:{owner}.{e.Name}",
            _ => member.Name,
        };
    }

    private static string ParameterIds(MethodBase method)
    {
        var parameters = method.GetParameters();
        return parameters.Length == 0 ? string.Empty : "(" + string.Join(",", parameters.Select(p => TypeIdOf(p.ParameterType))) + ")";
    }

    private static string TypeIdOf(Type type)
    {
        if (type.IsGenericParameter)
        {
            return (type.DeclaringMethod is not null ? "``" : "`") + type.GenericParameterPosition.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (type.IsByRef)
        {
            return TypeIdOf(type.GetElementType()!) + "@";
        }

        if (type.IsArray)
        {
            return TypeIdOf(type.GetElementType()!) + "[]";
        }

        var name = type.IsNested ? TypeIdOf(type.DeclaringType!) + "." + type.Name : type.Namespace + "." + type.Name;
        if (type.IsGenericType)
        {
            name = name[..name.IndexOf('`', StringComparison.Ordinal)] + "{" + string.Join(",", type.GetGenericArguments().Select(TypeIdOf)) + "}";
        }

        return name;
    }
}
