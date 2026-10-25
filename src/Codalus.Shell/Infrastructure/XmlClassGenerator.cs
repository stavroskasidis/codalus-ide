using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// Paste XML as Classes (deferred from M12b): C# classes for an XML document that <c>XmlSerializer</c> reads and writes,
/// as the classic IDE generates them. Every element with attributes or child elements becomes a class: attributes become
/// <c>[XmlAttribute]</c> properties, child elements <c>[XmlElement]</c> properties (a <c>List&lt;T&gt;</c> when one repeats
/// in any instance), and the text of an element that also has attributes an <c>[XmlText]</c> <c>Value</c>. Values are
/// typed from every occurrence: <c>bool</c>, <c>int</c>, <c>long</c>, <c>decimal</c>, <c>DateTime</c>, else <c>string</c>.
/// </summary>
public static class XmlClassGenerator
{
    private sealed class Shape(string name)
    {
        public string Name { get; } = name;

        public XNamespace Namespace { get; set; } = XNamespace.None;

        /// <summary>Attribute names in first-seen order, with every value seen.</summary>
        public List<(XName Name, List<string> Values)> Attributes { get; } = [];

        /// <summary>Child element names in first-seen order: every occurrence, and whether one repeats under a parent.</summary>
        public List<(XName Name, List<XElement> Elements, bool Repeats)> Children { get; } = [];

        public List<string> Texts { get; } = [];
    }

    public static string Generate(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.None);
        var root = document.Root ?? throw new XmlException("The XML has no root element.");
        var classes = new List<(string ClassName, Shape Shape)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        ClassOf(root.Name, [root], classes, names);

        var builder = new StringBuilder();
        for (var i = 0; i < classes.Count; i++)
        {
            var (className, shape) = classes[i];
            if (i > 0)
            {
                builder.AppendLine();
            }

            if (i == 0)
            {
                builder.AppendLine(shape.Namespace == XNamespace.None
                    ? $"[System.Xml.Serialization.XmlRoot(\"{shape.Name}\")]"
                    : $"[System.Xml.Serialization.XmlRoot(\"{shape.Name}\", Namespace = \"{shape.Namespace.NamespaceName}\")]");
            }

            builder.AppendLine(CultureInfo.InvariantCulture, $"public class {className}");
            builder.AppendLine("{");
            var members = new List<string>();
            var used = new HashSet<string>(StringComparer.Ordinal) { className };
            foreach (var (name, values) in shape.Attributes)
            {
                var property = Unique(Pascal(name.LocalName), used);
                members.Add($"    [System.Xml.Serialization.XmlAttribute(\"{name.LocalName}\"{NamespaceArgument(name.Namespace, shape.Namespace, attribute: true)})]\n    public {ValueType(values)} {property} {{ get; set; }}");
            }

            foreach (var (name, elements, repeats) in shape.Children)
            {
                var type = IsLeaf(elements) ? ValueType([.. elements.Select(e => e.Value)]) : ClassOf(name, elements, classes, names);
                var property = Unique(Pascal(repeats ? Plural(name.LocalName) : name.LocalName), used);
                var attribute = $"    [System.Xml.Serialization.XmlElement(\"{name.LocalName}\"{NamespaceArgument(name.Namespace, shape.Namespace, attribute: false)})]";
                members.Add(repeats
                    ? $"{attribute}\n    public List<{type}> {property} {{ get; set; }} = [];"
                    : $"{attribute}\n    public {type} {property} {{ get; set; }}{(type == "string" ? " = string.Empty;" : string.Empty)}");
            }

            if (shape.Texts.Any(t => !string.IsNullOrWhiteSpace(t)))
            {
                members.Add($"    [System.Xml.Serialization.XmlText]\n    public {ValueType(shape.Texts)} {Unique("Value", used)} {{ get; set; }}");
            }

            builder.AppendLine(string.Join("\n\n", members));
            builder.AppendLine("}");
        }

        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>The class of the elements named <paramref name="name"/> (all their instances merged), added once per name.</summary>
    private static string ClassOf(XName name, IReadOnlyList<XElement> elements, List<(string ClassName, Shape Shape)> classes, HashSet<string> names)
    {
        var existing = classes.FindIndex(c => c.Shape.Name == name.LocalName && c.Shape.Namespace == name.Namespace);
        if (existing >= 0)
        {
            return classes[existing].ClassName;
        }

        var className = Pascal(name.LocalName);
        for (var n = 2; !names.Add(className); n++)
        {
            className = Pascal(name.LocalName) + n.ToString(CultureInfo.InvariantCulture);
        }

        var shape = new Shape(name.LocalName) { Namespace = name.Namespace };
        classes.Add((className, shape));
        foreach (var element in elements)
        {
            foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
            {
                var index = shape.Attributes.FindIndex(a => a.Name == attribute.Name);
                if (index < 0)
                {
                    shape.Attributes.Add((attribute.Name, [attribute.Value]));
                }
                else
                {
                    shape.Attributes[index].Values.Add(attribute.Value);
                }
            }

            foreach (var group in element.Elements().GroupBy(e => e.Name))
            {
                var index = shape.Children.FindIndex(c => c.Name == group.Key);
                var repeats = group.Count() > 1;
                if (index < 0)
                {
                    shape.Children.Add((group.Key, [.. group], repeats));
                }
                else
                {
                    var (childName, list, wasRepeated) = shape.Children[index];
                    list.AddRange(group);
                    shape.Children[index] = (childName, list, wasRepeated || repeats);
                }
            }

            if (element.Nodes().OfType<XText>().Any())
            {
                shape.Texts.Add(string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value)));
            }
        }

        return className;
    }

    /// <summary>Elements with only text (no attributes or children) are values, not classes.</summary>
    private static bool IsLeaf(IReadOnlyList<XElement> elements) => elements.All(e => !e.HasElements && !e.Attributes().Any(a => !a.IsNamespaceDeclaration));

    /// <summary>The narrowest type every value parses as.</summary>
    private static string ValueType(IReadOnlyList<string> values)
    {
        var present = values.Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
        if (present.Count == 0)
        {
            return "string";
        }

        if (present.All(v => v is "true" or "false"))
        {
            return "bool";
        }

        if (present.All(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
        {
            return "int";
        }

        if (present.All(v => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
        {
            return "long";
        }

        if (present.All(v => decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            return "decimal";
        }

        return present.All(v => v.Length >= 10 && DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)) ? "DateTime" : "string";
    }

    private static string NamespaceArgument(XNamespace name, XNamespace owner, bool attribute) =>
        name == XNamespace.None || !attribute && name == owner ? string.Empty : $", Namespace = \"{name.NamespaceName}\"";

    private static string Unique(string name, HashSet<string> used)
    {
        var unique = name;
        for (var n = 2; !used.Add(unique); n++)
        {
            unique = name + n.ToString(CultureInfo.InvariantCulture);
        }

        return unique;
    }

    private static string Pascal(string name)
    {
        var builder = new StringBuilder();
        var upper = true;
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c))
            {
                upper = true;
                continue;
            }

            builder.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        var result = builder.ToString();
        return result.Length == 0 ? "Item" : char.IsDigit(result[0]) ? "_" + result : result;
    }

    private static string Plural(string name) =>
        name.EndsWith('y') && name.Length > 1 && !"aeiou".Contains(char.ToLowerInvariant(name[^2]), StringComparison.Ordinal) ? name[..^1] + "ies"
        : name.EndsWith('s') || name.EndsWith('x') || name.EndsWith("ch", StringComparison.Ordinal) || name.EndsWith("sh", StringComparison.Ordinal) ? name + "es"
        : name + "s";
}
