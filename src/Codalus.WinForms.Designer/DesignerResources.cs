using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Xml.Linq;
using Codalus.LanguageServices.Protocol;
using Codalus.Workbench.WinForms;

namespace Codalus.WinForms.Designer;

/// <summary>
/// A form's resources from the <c>.resx</c> texts the IDE sends (M41b, ADR 0073): a <c>ComponentResourceManager</c>
/// whose resource sets are those texts', per culture, with the usual fallback to the parent culture. The designer reads
/// what the editor has, so what it wrote (a localizable property, an image) shows without a build.
/// </summary>
internal sealed class TextResourceManager : ComponentResourceManager
{
    private readonly Dictionary<string, string> _texts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResourceSet?> _sets = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _basePath;

    public TextResourceManager(IEnumerable<DesignerResourceText> texts, string? basePath)
    {
        foreach (var text in texts)
        {
            _texts[text.Culture] = text.Text;
        }

        _basePath = basePath;
    }

    public bool IsEmpty => _texts.Count == 0;

    private IReadOnlyDictionary<string, object?>? _metadata;

    /// <summary>
    /// The default resources' design-time metadata (#67: <c>name.Locked</c>), read from its text once; empty without one.
    /// A build's compiled resources have none.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Metadata()
    {
        if (_metadata is not null)
        {
            return _metadata;
        }

        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (_texts.TryGetValue(string.Empty, out var text))
        {
            using var reader = new ResXResourceReader(new StringReader(text)) { BasePath = _basePath };
            var entries = reader.GetMetadataEnumerator();
            while (entries.MoveNext())
            {
                if (entries.Key is string key)
                {
                    metadata[key] = entries.Value;
                }
            }
        }

        return _metadata = metadata;
    }

    /// <summary>A culture's resource set (empty name: the default resources), or null when the form has none for it.</summary>
    public ResourceSet? Set(string culture)
    {
        if (_sets.TryGetValue(culture, out var set))
        {
            return set;
        }

        set = null;
        if (_texts.TryGetValue(culture, out var text))
        {
            using var reader = new ResXResourceReader(new StringReader(text)) { BasePath = _basePath };
            set = new ResourceSet(reader);
        }

        _sets[culture] = set;
        return set;
    }

    protected override ResourceSet? InternalGetResourceSet(CultureInfo culture, bool createIfNotExists, bool tryParents)
    {
        for (var current = culture; ; current = current.Parent)
        {
            if (Set(current.Name) is { } set)
            {
                return set;
            }

            if (!tryParents || current.Name.Length == 0)
            {
                return null;
            }
        }
    }

    /// <summary>The value a key has in <paramref name="culture"/>'s parents (what the culture inherits), or null.</summary>
    public object? InheritedValue(string key, CultureInfo culture)
    {
        if (culture.Name.Length == 0)
        {
            return null;
        }

        for (var current = culture.Parent; ; current = current.Parent)
        {
            if (Set(current.Name)?.GetObject(key) is { } value)
            {
                return value;
            }

            if (current.Name.Length == 0)
            {
                return null;
            }
        }
    }
}

/// <summary>Values as a <c>.resx</c> file stores them, written by Windows Forms' own resource writer with Visual Studio's type names.</summary>
internal static class ResourceValues
{
    private const string DrawingIdentity = "System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a";
    private const string FormsIdentity = "System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";
    private const string CoreIdentity = "mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";

    /// <summary>
    /// A type's name as Visual Studio's designer writes it in a <c>.resx</c>: the .NET Framework names of the framework's
    /// assemblies (<c>System.Drawing.Point, System.Drawing, Version=4.0.0.0, …</c>), else the assembly's own.
    /// </summary>
    public static string TypeName(Type? type)
    {
        if (type is null)
        {
            return string.Empty;
        }

        var full = type.FullName ?? type.Name;
        if (type.Assembly == typeof(object).Assembly)
        {
            return $"{full}, {CoreIdentity}";
        }

        if (type.Namespace?.StartsWith("System.Drawing", StringComparison.Ordinal) == true)
        {
            return $"{full}, {DrawingIdentity}";
        }

        if (type.Namespace?.StartsWith("System.Windows.Forms", StringComparison.Ordinal) == true || type.Namespace?.StartsWith("System.Resources", StringComparison.Ordinal) == true)
        {
            return $"{full}, {FormsIdentity}";
        }

        return type.AssemblyQualifiedName ?? full;
    }

    /// <summary><paramref name="value"/> as the resource writer stores it under <paramref name="key"/> (its type, text or base64 bytes, and the assembly alias it uses).</summary>
    public static DesignerResource Of(string key, object? value)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        using (var resx = new ResXResourceWriter(writer, TypeName))
        {
            resx.AddResource(new ResXDataNode(key, value, TypeName));
            resx.Generate();
        }

        var document = XDocument.Parse(writer.ToString(), LoadOptions.PreserveWhitespace);
        var data = document.Root!.Elements("data").Single(d => (string?)d.Attribute("name") == key);
        var type = (string?)data.Attribute("type");
        var alias = type?.Split(',', 2) is [_, var rest] && !rest.Contains(',', StringComparison.Ordinal) ? rest.Trim() : null;
        var assembly = alias is null ? null : document.Root.Elements("assembly").FirstOrDefault(e => (string?)e.Attribute("alias") == alias);
        return new DesignerResource
        {
            Type = type,
            Text = data.Element("value")?.Value ?? string.Empty,
            MimeType = (string?)data.Attribute("mimetype"),
            AssemblyAlias = assembly is null ? null : alias,
            AssemblyName = (string?)assembly?.Attribute("name"),
        };
    }

    /// <summary>A string as the resources hold it.</summary>
    public static DesignerResource Text(string text) => DesignerResource.String(text);

    /// <summary>Whether a value can only be in the resources (an image, an icon, an image list's stream): the code reads it with <c>GetObject</c>.</summary>
    public static bool IsResourceOnly(object? value) => value is Image or Icon or ImageListStreamer;

    /// <summary>A component's key in the resources: <c>okButton.Text</c>, <c>$this.Text</c> for the root.</summary>
    public static string Key(string component, string property) => (component.Length == 0 ? "$this" : component) + "." + property;
}
