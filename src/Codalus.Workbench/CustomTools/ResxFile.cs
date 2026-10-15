using System.Xml;
using System.Xml.Linq;

namespace Codalus.Workbench.CustomTools;

/// <summary>A resource of a <c>.resx</c> file: strings are edited, others (images, files, typed values) are shown as they are.</summary>
public sealed record ResxEntry(string Name, string? Value, string? Comment, string? Type)
{
    /// <summary>A string resource (no type, or <c>System.String</c>, and not a file reference).</summary>
    public bool IsString => Type is null || Type.StartsWith("System.String,", StringComparison.Ordinal) || Type == "System.String";
}

/// <summary>
/// The <c>.resx</c> resource editor's model (deferred from M11): the <c>data</c> elements of a resource file, with edits
/// to its strings (set, rename, remove, add) applied to the XML itself, so the schema, the <c>resheader</c>s, other
/// resources, comments and order stay as they were. New strings go after the last <c>data</c> element.
/// </summary>
public sealed class ResxFile
{
    private readonly XDocument _document;

    private ResxFile(XDocument document) => _document = document;

    /// <summary>Parses a resource file. Throws <see cref="XmlException"/> when it isn't one.</summary>
    public static ResxFile Parse(string text)
    {
        var document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        if (document.Root?.Name.LocalName != "root")
        {
            throw new XmlException("A .resx file's root element is <root>.");
        }

        return new ResxFile(document);
    }

    /// <summary>An empty resource file, as the .NET templates write one.</summary>
    public static ResxFile Empty() => Parse("""
        <?xml version="1.0" encoding="utf-8"?>
        <root>
          <resheader name="resmimetype">
            <value>text/microsoft-resx</value>
          </resheader>
          <resheader name="version">
            <value>2.0</value>
          </resheader>
          <resheader name="reader">
            <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
          </resheader>
          <resheader name="writer">
            <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
          </resheader>
        </root>
        """);

    /// <summary>Every resource, in file order.</summary>
    public IReadOnlyList<ResxEntry> Entries =>
    [
        .. Data().Select(d => new ResxEntry(
            (string?)d.Attribute("name") ?? string.Empty,
            d.Element("value")?.Value,
            d.Element("comment")?.Value,
            (string?)d.Attribute("type"))),
    ];

    /// <summary>Sets a string's value and comment (null removes the comment), adding the string when there is none.</summary>
    public void Set(string name, string value, string? comment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        if (Find(name) is not { } data)
        {
            data = new XElement("data", new XAttribute("name", name), new XAttribute(XNamespace.Xml + "space", "preserve"));
            var indent = Indentation();
            if (Data().LastOrDefault() is { } last)
            {
                last.AddAfterSelf(new XText("\n" + indent), data);
            }
            else
            {
                var root = _document.Root!;
                if (root.LastNode is XText trailing && string.IsNullOrWhiteSpace(trailing.Value))
                {
                    trailing.AddBeforeSelf(new XText("\n" + indent), data);
                }
                else
                {
                    root.Add(new XText("\n" + indent), data, new XText("\n"));
                }
            }

            data.Add(new XText("\n" + indent + indent), new XElement("value", value), new XText("\n" + indent));
        }
        else if (!new ResxEntry(name, null, null, (string?)data.Attribute("type")).IsString)
        {
            throw new InvalidOperationException($"{name} isn't a string resource.");
        }
        else if (data.Element("value") is { } existing)
        {
            existing.Value = value;
        }
        else
        {
            data.AddFirst(new XElement("value", value));
        }

        var commentElement = data.Element("comment");
        if (comment is null or "")
        {
            if (commentElement is not null)
            {
                RemoveWithIndent(commentElement);
            }
        }
        else if (commentElement is not null)
        {
            commentElement.Value = comment;
        }
        else
        {
            var valueElement = data.Element("value")!;
            var indent = valueElement.PreviousNode is XText before ? before.Value : string.Empty;
            valueElement.AddAfterSelf(new XText(indent), new XElement("comment", comment));
        }
    }

    /// <summary>Renames a resource. Throws when the new name is taken.</summary>
    public void Rename(string name, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        if (name == newName)
        {
            return;
        }

        if (Find(newName) is not null)
        {
            throw new InvalidOperationException($"There is already a resource named {newName}.");
        }

        (Find(name) ?? throw new InvalidOperationException($"There is no resource named {name}.")).SetAttributeValue("name", newName);
    }

    /// <summary>Removes a resource (and the line it was on).</summary>
    public void Remove(string name)
    {
        if (Find(name) is { } data)
        {
            RemoveWithIndent(data);
        }
    }

    /// <summary>The file's text, with its XML declaration.</summary>
    public string Save()
    {
        var declaration = _document.Declaration is { } d ? d + "\n" : string.Empty;
        return declaration + _document.Root!.ToString(SaveOptions.DisableFormatting) + "\n";
    }

    /// <summary>Why a set of names can't be saved (empty or duplicate), or null.</summary>
    public static string? Validate(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "Every resource needs a name.";
            }

            if (!seen.Add(name))
            {
                return $"There are two resources named {name}.";
            }
        }

        return null;
    }

    private IEnumerable<XElement> Data() => _document.Root!.Elements("data");

    private XElement? Find(string name) => Data().FirstOrDefault(d => (string?)d.Attribute("name") == name);

    /// <summary>The indentation of the root's children (two spaces when there are none).</summary>
    private string Indentation() =>
        _document.Root!.Elements().FirstOrDefault()?.PreviousNode is XText text && text.Value.LastIndexOf('\n') is var newline and >= 0
            ? text.Value[(newline + 1)..]
            : "  ";

    private static void RemoveWithIndent(XElement element)
    {
        if (element.PreviousNode is XText before && string.IsNullOrWhiteSpace(before.Value))
        {
            before.Remove();
        }

        element.Remove();
    }
}
