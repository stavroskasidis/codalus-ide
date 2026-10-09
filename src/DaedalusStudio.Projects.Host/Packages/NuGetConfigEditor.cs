using System.Text;
using System.Xml;
using System.Xml.Linq;
using DaedalusStudio.Projects.Protocol;

namespace DaedalusStudio.Projects.Host.Packages;

/// <summary>
/// Saves package sources and package source mapping into the solution's <c>NuGet.Config</c> hierarchy with exact
/// control over which file changes (ADR 0012). NuGet's settings API may add new items to the user-wide file, which
/// would change every other solution; here:
/// <list type="bullet">
/// <item>a source is updated, reordered or removed in the file that defines it;</item>
/// <item>new sources and new mappings go to the most specific config file under the solution folder (a
/// <c>nuget.config</c> is created there when there is none);</item>
/// <item>a source's mapping is changed where it is defined;</item>
/// <item>machine-wide files are never written.</item>
/// </list>
/// Formatting and comments are kept; only the changed elements are touched.
/// </summary>
internal sealed class NuGetConfigEditor
{
    private static readonly StringComparer s_keys = StringComparer.OrdinalIgnoreCase;
    private readonly Dictionary<string, (XDocument Document, bool Bom)> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="configFiles">The effective config files, most specific first.</param>
    /// <param name="root">The solution folder: new items go to a config file under it.</param>
    /// <param name="readOnly">Machine-wide files.</param>
    public static IReadOnlyList<string> Save(
        IReadOnlyList<string> configFiles,
        string root,
        ISet<string> readOnly,
        IReadOnlyList<PackageSourceInfo> current,
        IReadOnlyList<PackageSourceMapping> currentMappings,
        IReadOnlyList<PackageSourceInfo> wanted,
        IReadOnlyList<PackageSourceMapping> wantedMappings)
    {
        var editor = new NuGetConfigEditor();
        var writable = configFiles.Where(f => !readOnly.Contains(f) && File.Exists(f)).ToList();
        var target = writable.FirstOrDefault(f => ProjectFileEditor.IsSameOrUnder(Path.GetFullPath(f), Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))));
        string Target() => target ??= editor.Create(Path.Combine(root, "nuget.config"));

        // ---------------------------------------------------------------- sources
        var definedIn = new Dictionary<string, string>(s_keys);
        foreach (var source in current.Where(s => s.ConfigFile is not null && !s.IsMachineWide))
        {
            definedIn[source.Name] = source.ConfigFile!;
        }

        var wantedByName = wanted.ToDictionary(s => s.Name, s_keys);
        foreach (var source in current.Where(s => !s.IsMachineWide && !wantedByName.ContainsKey(s.Name) && s.ConfigFile is { } file && writable.Contains(file, StringComparer.OrdinalIgnoreCase)))
        {
            editor.RemoveSource(source.ConfigFile!, source.Name);
        }

        var currentByName = current.ToDictionary(s => s.Name, s_keys);
        foreach (var source in wanted.Where(s => !s.IsMachineWide))
        {
            if (definedIn.TryGetValue(source.Name, out var file))
            {
                if (writable.Contains(file, StringComparer.OrdinalIgnoreCase) && currentByName[source.Name].Url != source.Url)
                {
                    editor.SetSourceUrl(file, source.Name, source.Url);
                }
            }
            else if (!currentByName.ContainsKey(source.Name))
            {
                editor.AddSource(Target(), source.Name, source.Url);
                definedIn[source.Name] = Target();
            }
        }

        // The order within each file follows the wanted order (NuGet can't order across files).
        foreach (var file in definedIn.Values.Distinct(StringComparer.OrdinalIgnoreCase).Where(f => writable.Contains(f, StringComparer.OrdinalIgnoreCase) || f == target))
        {
            editor.OrderSources(file, [.. wanted.Where(s => definedIn.TryGetValue(s.Name, out var f) && string.Equals(f, file, StringComparison.OrdinalIgnoreCase)).Select(s => s.Name)]);
        }

        // Enabled state: enabling removes every disabled entry; disabling adds one where the source is defined.
        foreach (var source in wanted)
        {
            var wasEnabled = currentByName.TryGetValue(source.Name, out var old) ? old.Enabled : true;
            if (wasEnabled == source.Enabled)
            {
                continue;
            }

            if (source.Enabled)
            {
                foreach (var file in writable.Append(target).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    editor.SetDisabled(file, source.Name, disabled: false);
                }
            }
            else
            {
                var file = definedIn.TryGetValue(source.Name, out var f) && writable.Contains(f, StringComparer.OrdinalIgnoreCase) ? f : Target();
                editor.SetDisabled(file, source.Name, disabled: true);
            }
        }

        // ---------------------------------------------------------------- mappings
        var currentPatterns = currentMappings.ToDictionary(m => m.Source, m => m.Patterns, s_keys);
        var wantedPatterns = wantedMappings.Where(m => m.Patterns.Length > 0).ToDictionary(m => m.Source, m => m.Patterns, s_keys);
        foreach (var key in currentPatterns.Keys.Union(wantedPatterns.Keys, s_keys).ToList())
        {
            var before = currentPatterns.GetValueOrDefault(key) ?? [];
            var after = wantedPatterns.GetValueOrDefault(key) ?? [];
            if (before.Order(s_keys).SequenceEqual(after.Order(s_keys), s_keys))
            {
                continue;
            }

            var files = writable.Where(f => editor.HasMapping(f, key)).ToList();
            if (files.Count == 0)
            {
                if (after.Length > 0)
                {
                    editor.SetMapping(Target(), key, after);
                }

                continue;
            }

            // Changed in the most specific file that maps the source; other files' entries for it are removed.
            editor.SetMapping(files[0], key, after);
            foreach (var other in files.Skip(1))
            {
                editor.SetMapping(other, key, []);
            }
        }

        return editor.SaveAll();
    }

    // ------------------------------------------------------------------ documents

    private XDocument Document(string path)
    {
        if (!_documents.TryGetValue(path, out var entry))
        {
            var bytes = File.ReadAllBytes(path);
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            using var stream = new MemoryStream(bytes);
            entry = (XDocument.Load(stream, LoadOptions.PreserveWhitespace), bom);
            _documents[path] = entry;
        }

        return entry.Document;
    }

    private string Create(string path)
    {
        var document = XDocument.Parse("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <packageSources>\n  </packageSources>\n</configuration>\n", LoadOptions.PreserveWhitespace);
        _documents[path] = (document, false);
        _dirty.Add(path);
        return path;
    }

    private List<string> SaveAll()
    {
        var saved = new List<string>();
        foreach (var path in _dirty)
        {
            var (document, bom) = _documents[path];
            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(bom),
                OmitXmlDeclaration = document.Declaration is null,
                NewLineHandling = NewLineHandling.None,
            };
            using (var writer = XmlWriter.Create(path, settings))
            {
                document.Save(writer);
            }

            saved.Add(path);
        }

        return saved;
    }

    private XElement Section(string path, string name, bool create)
    {
        var root = Document(path).Root ?? throw new InvalidOperationException($"{path} has no root element.");
        if (root.Element(name) is { } section)
        {
            return section;
        }

        if (!create)
        {
            return new XElement(name);
        }

        section = new XElement(name);
        AppendIndented(root, section);
        _dirty.Add(path);
        return section;
    }

    private static IEnumerable<XElement> Adds(XElement section, string key) =>
        section.Elements("add").Where(e => s_keys.Equals((string?)e.Attribute("key"), key));

    // ------------------------------------------------------------------ edits

    private void RemoveSource(string path, string name)
    {
        foreach (var add in Adds(Section(path, "packageSources", create: false), name).ToList())
        {
            RemoveWithIndentation(add);
            _dirty.Add(path);
        }

        SetDisabled(path, name, disabled: false);
        SetMapping(path, name, []);
    }

    private void SetSourceUrl(string path, string name, string url)
    {
        foreach (var add in Adds(Section(path, "packageSources", create: false), name))
        {
            add.SetAttributeValue("value", url);
            _dirty.Add(path);
        }
    }

    private void AddSource(string path, string name, string url)
    {
        AppendIndented(Section(path, "packageSources", create: true), new XElement("add", new XAttribute("key", name), new XAttribute("value", url)));
        _dirty.Add(path);
    }

    /// <summary>Puts the file's <c>add</c> elements in <paramref name="names"/> order (only when it differs).</summary>
    private void OrderSources(string path, IReadOnlyList<string> names)
    {
        var section = Section(path, "packageSources", create: false);
        var adds = section.Elements("add").ToList();
        var ordered = adds.OrderBy(a => names.ToList().FindIndex(n => s_keys.Equals(n, (string?)a.Attribute("key"))) is var i && i < 0 ? int.MaxValue : i).ToList();
        if (adds.SequenceEqual(ordered))
        {
            return;
        }

        // Swap the elements into the existing slots, so whitespace and comments stay where they were.
        var copies = ordered.Select(a => new XElement(a)).ToList();
        for (var i = 0; i < adds.Count; i++)
        {
            adds[i].ReplaceWith(copies[i]);
        }

        _dirty.Add(path);
    }

    private void SetDisabled(string path, string name, bool disabled)
    {
        var section = Section(path, "disabledPackageSources", create: disabled);
        var existing = Adds(section, name).ToList();
        if (disabled && existing.Count == 0)
        {
            AppendIndented(section, new XElement("add", new XAttribute("key", name), new XAttribute("value", "true")));
            _dirty.Add(path);
        }
        else if (!disabled && existing.Count > 0)
        {
            foreach (var add in existing)
            {
                RemoveWithIndentation(add);
            }

            _dirty.Add(path);
        }
    }

    private bool HasMapping(string path, string key) =>
        Section(path, "packageSourceMapping", create: false).Elements("packageSource").Any(e => s_keys.Equals((string?)e.Attribute("key"), key));

    /// <summary>Sets a source's patterns (none: removes its entry).</summary>
    private void SetMapping(string path, string key, string[] patterns)
    {
        var section = Section(path, "packageSourceMapping", create: patterns.Length > 0);
        var existing = section.Elements("packageSource").Where(e => s_keys.Equals((string?)e.Attribute("key"), key)).ToList();
        if (patterns.Length == 0)
        {
            if (existing.Count > 0)
            {
                existing.ForEach(RemoveWithIndentation);
                _dirty.Add(path);
            }

            return;
        }

        var element = existing.FirstOrDefault();
        if (element is null)
        {
            element = new XElement("packageSource", new XAttribute("key", key));
            AppendIndented(section, element);
        }
        else
        {
            foreach (var package in element.Elements("package").ToList())
            {
                RemoveWithIndentation(package);
            }
        }

        foreach (var pattern in patterns)
        {
            AppendIndented(element, new XElement("package", new XAttribute("pattern", pattern)));
        }

        _dirty.Add(path);
    }

    // ------------------------------------------------------------------ whitespace

    private static string LineBreak(XElement element) =>
        element.Document?.Root?.DescendantNodes().OfType<XText>().FirstOrDefault(t => t.Value.Contains('\n', StringComparison.Ordinal))?.Value.Contains("\r\n", StringComparison.Ordinal) == true ? "\r\n" : "\n";

    /// <summary>The whitespace before the element on its line.</summary>
    private static string Indentation(XElement element)
    {
        if (element.PreviousNode is XText { Value: var text } && text.LastIndexOf('\n') is var at && at >= 0)
        {
            return text[(at + 1)..];
        }

        return element.Parent is null ? string.Empty : Indentation(element.Parent) + "  ";
    }

    /// <summary>Adds <paramref name="child"/> as the last child on its own line, indented like its siblings.</summary>
    private static void AppendIndented(XElement parent, XElement child)
    {
        var lineBreak = LineBreak(parent);
        var childIndentation = parent.Elements().LastOrDefault() is { } sibling ? Indentation(sibling) : Indentation(parent) + "  ";
        if (parent.LastNode is XText last && string.IsNullOrWhiteSpace(last.Value))
        {
            last.Remove();
        }

        parent.Add(new XText(lineBreak + childIndentation), child, new XText(lineBreak + Indentation(parent)));
    }

    private static void RemoveWithIndentation(XElement element)
    {
        if (element.PreviousNode is XText { Value: var text } previous && string.IsNullOrWhiteSpace(text))
        {
            previous.Remove();
        }

        element.Remove();
    }
}
