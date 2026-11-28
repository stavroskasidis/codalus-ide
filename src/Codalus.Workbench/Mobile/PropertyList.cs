using System.Globalization;
using System.Xml.Linq;

namespace Codalus.Workbench.Mobile;

/// <summary>
/// Apple's XML property lists (ADR 0114): read into plain values (<see cref="string"/>, <see cref="long"/>,
/// <see cref="bool"/>, <see cref="DateTimeOffset"/>, <see cref="IReadOnlyList{T}"/> of values and
/// <see cref="IReadOnlyDictionary{TKey, TValue}"/> by key), and written from the same. An Xcode archive's
/// <c>Info.plist</c> is read; <c>xcodebuild -exportArchive</c>'s export options are written. Binary property lists
/// aren't read (Xcode and the .NET SDK write XML ones).
/// </summary>
public static class PropertyList
{
    /// <summary>The top-level dictionary of <paramref name="xml"/>, or null when it isn't an XML property list with one.</summary>
    public static IReadOnlyDictionary<string, object>? ParseDictionary(string xml)
    {
        try
        {
            var document = XDocument.Parse(xml, LoadOptions.None);
            return document.Root?.Elements().FirstOrDefault() is { } top && Value(top) is IReadOnlyDictionary<string, object> dict ? dict : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>The value of one plist element (<c>string</c>, <c>integer</c>, <c>true</c>, <c>dict</c>…), or null for a kind it doesn't read.</summary>
    public static object? Value(XElement element) => element.Name.LocalName switch
    {
        "string" => element.Value,
        "integer" => long.TryParse(element.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null,
        "real" => double.TryParse(element.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) ? real : null,
        "true" => true,
        "false" => false,
        "date" => DateTimeOffset.TryParse(element.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null,
        "array" => element.Elements().Select(Value).OfType<object>().ToList(),
        "dict" => Dictionary(element),
        _ => null,
    };

    private static Dictionary<string, object> Dictionary(XElement dict)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        string? key = null;
        foreach (var element in dict.Elements())
        {
            if (element.Name.LocalName == "key")
            {
                key = element.Value;
            }
            else if (key is not null)
            {
                if (Value(element) is { } value)
                {
                    result[key] = value;
                }

                key = null;
            }
        }

        return result;
    }

    /// <summary>A string at <paramref name="path"/> (keys through nested dictionaries), or null.</summary>
    public static string? String(IReadOnlyDictionary<string, object>? dict, params string[] path)
    {
        object? value = dict;
        foreach (var key in path)
        {
            value = value is IReadOnlyDictionary<string, object> d ? d.GetValueOrDefault(key) : null;
        }

        return value as string;
    }

    /// <summary>The XML of a property list whose top level is <paramref name="values"/> (keys in the order given).</summary>
    public static string Write(IEnumerable<KeyValuePair<string, object>> values)
    {
        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), Element(values)));
        return document.Declaration + "\n" + document.ToString() + "\n";
    }

    private static XElement Element(object value) => value switch
    {
        string text => new XElement("string", text),
        bool flag => new XElement(flag ? "true" : "false"),
        int or long => new XElement("integer", Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)),
        DateTimeOffset date => new XElement("date", date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
        IEnumerable<KeyValuePair<string, object>> dict => new XElement("dict", dict.SelectMany(p => new object[] { new XElement("key", p.Key), Element(p.Value) })),
        IEnumerable<object> list => new XElement("array", list.Select(Element)),
        _ => throw new ArgumentException($"A property list has no value of type {value.GetType().Name}.", nameof(value)),
    };
}
