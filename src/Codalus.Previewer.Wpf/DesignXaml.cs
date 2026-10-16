using System.Globalization;
using System.Text;
using System.Xml;

namespace Codalus.Previewer.Wpf;

/// <summary>A <c>d:DesignInstance</c>: the type (its XML namespace and name) and whether the designer may create it.</summary>
internal sealed record DesignInstance(string Namespace, string TypeName, bool IsDesignTimeCreatable, bool CreateList);

/// <summary>
/// What the designer host reads from the root's start tag before loading (M40b, ADR 0072): the root's element, its
/// <c>x:Class</c> (stripped from the loaded XAML, which then names the root in selection paths), the design size
/// (<c>d:DesignWidth</c>, <c>d:DesignHeight</c>) and the design-time data context (<c>d:DataContext</c> with a
/// <c>d:DesignInstance</c>). XAML's markup compatibility ignores <c>d:</c>, so the loader never sees them. No WPF here:
/// the tests run it on every OS.
/// </summary>
internal sealed record DesignXaml(
    string? RootNamespace,
    string? RootName,
    string? ClassName,
    double? DesignWidth,
    double? DesignHeight,
    DesignInstance? DataContext,
    string? DataContextProblem)
{
    public const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    public const string Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    public const string Blend = "http://schemas.microsoft.com/expression/blend/2008";

    public static readonly DesignXaml Empty = new(null, null, null, null, null, null, null);

    /// <summary>The root's type as selection paths name it: the <c>x:Class</c>'s simple name, else the element's.</summary>
    public string? RootType => ClassName is { Length: > 0 } cls ? cls[(cls.LastIndexOf('.') + 1)..].Trim() : RootName;

    /// <summary>Whether the root is WPF's <c>Application</c> (App.xaml): nothing to render.</summary>
    public bool IsApplication => RootName == "Application" && RootNamespace == Presentation;

    /// <summary>Reads the root's start tag; <see cref="Empty"/> when it isn't well-formed (the loader then says why).</summary>
    public static DesignXaml Read(string xaml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xaml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
            });
            if (reader.MoveToContent() != XmlNodeType.Element)
            {
                return Empty;
            }

            var rootNamespace = reader.NamespaceURI;
            var rootName = reader.LocalName;
            string? className = null;
            double? width = null, height = null;
            string? dataContext = null;
            while (reader.MoveToNextAttribute())
            {
                switch (reader.NamespaceURI, reader.LocalName)
                {
                    case (Xaml, "Class"):
                        className = reader.Value.Trim();
                        break;
                    case (Blend, "DesignWidth"):
                        width = Size(reader.Value);
                        break;
                    case (Blend, "DesignHeight"):
                        height = Size(reader.Value);
                        break;
                    case (Blend, "DataContext"):
                        dataContext = reader.Value;
                        break;
                }
            }

            reader.MoveToElement();
            var scope = ((IXmlNamespaceResolver)reader).GetNamespacesInScope(XmlNamespaceScope.All);
            var (instance, problem) = dataContext is null ? (null, null) : ParseDesignInstance(dataContext, scope);
            return new DesignXaml(rootNamespace, rootName, className, width, height, instance, problem);
        }
        catch (XmlException)
        {
            return Empty;
        }
    }

    private static double? Size(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0 && !double.IsInfinity(value) ? value : null;

    /// <summary>
    /// <c>{d:DesignInstance vm:Type}</c>, <c>{d:DesignInstance Type=vm:Type, IsDesignTimeCreatable=True}</c> or
    /// <c>{d:DesignInstance {x:Type vm:Type}}</c>, with the type's prefix resolved in <paramref name="namespaces"/>.
    /// </summary>
    internal static (DesignInstance? Instance, string? Problem) ParseDesignInstance(string value, IDictionary<string, string> namespaces)
    {
        var text = value.Trim();
        if (!text.StartsWith('{') || !text.EndsWith('}'))
        {
            return (null, "d:DataContext isn't a d:DesignInstance markup extension: the preview has no design-time data.");
        }

        var inner = text[1..^1].Trim();
        var space = inner.IndexOfAny([' ', '\t', '\r', '\n']);
        var extension = space < 0 ? inner : inner[..space];
        var (extensionNamespace, extensionName) = Resolve(extension, namespaces);
        if (extensionNamespace != Blend || extensionName is not ("DesignInstance" or "DesignInstanceExtension"))
        {
            return (null, $"d:DataContext uses {extension}: the preview's design-time data comes from d:DesignInstance only.");
        }

        string? type = null;
        var creatable = false;
        var list = false;
        foreach (var argument in Arguments(space < 0 ? string.Empty : inner[space..]))
        {
            var equals = TopLevelIndexOf(argument, '=');
            if (equals < 0)
            {
                type ??= argument;
                continue;
            }

            var name = argument[..equals].Trim();
            var argumentValue = argument[(equals + 1)..].Trim();
            switch (name)
            {
                case "Type":
                    type = argumentValue;
                    break;
                case "IsDesignTimeCreatable":
                    creatable = bool.TryParse(argumentValue, out var b) && b;
                    break;
                case "CreateList":
                    list = bool.TryParse(argumentValue, out var l) && l;
                    break;
            }
        }

        if (type is { } t && t.StartsWith('{') && t.EndsWith('}'))
        {
            // {x:Type vm:Type} or {x:Type TypeName=vm:Type}
            var typeExtension = t[1..^1].Trim();
            var typeSpace = typeExtension.IndexOfAny([' ', '\t', '\r', '\n']);
            type = typeSpace < 0 ? null : typeExtension[typeSpace..].Trim();
            if (type is not null && type.StartsWith("TypeName", StringComparison.Ordinal) && TopLevelIndexOf(type, '=') is var e and > 0)
            {
                type = type[(e + 1)..].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(type))
        {
            return (null, "d:DesignInstance names no type: the preview has no design-time data.");
        }

        var (typeNamespace, typeName) = Resolve(type.Trim().Trim('\'', '"'), namespaces);
        return typeNamespace is null
            ? (null, $"The prefix of {type} isn't declared: the preview has no design-time data.")
            : (new DesignInstance(typeNamespace, typeName, creatable, list), null);
    }

    /// <summary>A <c>prefix:Name</c> (or <c>Name</c>, in the default namespace) resolved to its XML namespace.</summary>
    private static (string? Namespace, string Name) Resolve(string qualified, IDictionary<string, string> namespaces)
    {
        var colon = qualified.IndexOf(':', StringComparison.Ordinal);
        var prefix = colon < 0 ? string.Empty : qualified[..colon];
        var name = colon < 0 ? qualified : qualified[(colon + 1)..];
        return (namespaces.TryGetValue(prefix, out var ns) ? ns : null, name);
    }

    /// <summary>A markup extension's arguments, split at the commas outside nested braces and quotes.</summary>
    private static IEnumerable<string> Arguments(string text)
    {
        var depth = 0;
        char? quote = null;
        var current = new StringBuilder();
        foreach (var c in text)
        {
            if (quote is { } q)
            {
                quote = c == q ? null : quote;
            }
            else if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                if (current.ToString().Trim() is { Length: > 0 } argument)
                {
                    yield return argument;
                }

                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.ToString().Trim() is { Length: > 0 } last)
        {
            yield return last;
        }
    }

    private static int TopLevelIndexOf(string text, char c)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            depth += text[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0 && text[i] == c)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// A <c>clr-namespace:</c> XML namespace's CLR namespace and assembly (null: the XAML's own assembly), or null for
    /// a URI namespace (WPF's <c>[XmlnsDefinition]</c>s).
    /// </summary>
    public static (string ClrNamespace, string? Assembly)? ClrNamespace(string xmlNamespace)
    {
        const string Scheme = "clr-namespace:";
        if (!xmlNamespace.StartsWith(Scheme, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = xmlNamespace[Scheme.Length..].Split(';');
        var assembly = parts.Skip(1).Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("assembly=", StringComparison.Ordinal))?["assembly=".Length..];
        return (parts[0].Trim(), string.IsNullOrWhiteSpace(assembly) ? null : assembly.Trim());
    }
}
