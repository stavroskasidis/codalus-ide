using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Codalus.LanguageServices.Protocol;

namespace Codalus.LanguageServices.Host.WinForms;

/// <summary>
/// A form's <c>.resx</c> file as text (M41b, ADR 0073): its <c>data</c>, <c>metadata</c> and <c>assembly</c> elements
/// found by a light scan, and edits that touch only the entries they change (a value replaced, an entry inserted or
/// removed with its lines), so the rest of the file (its schema, comments, other resources) stays byte for byte, as the
/// designer file does. New entries go where Visual Studio's designer orders them: with their component's (metadata
/// first, then its properties by name, then Visual Studio's records of the control, <c>&gt;&gt;name.Name</c>,
/// <c>Type</c>, <c>Parent</c>, <c>ZOrder</c>), a new component's before the next one by name, the form's
/// (<c>$this</c>) last.
/// </summary>
internal sealed partial class ResxText
{
    /// <summary>An empty resource file, as the classic IDE's designer starts a form's culture's (<see cref="DesignerResx.Empty"/>).</summary>
    public const string Empty = DesignerResx.Empty;

    private static readonly string[] s_records = ["Name", "Type", "Parent", "ZOrder"];

    private string _text;

    public ResxText(string text)
    {
        _text = text;
        NewLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }

    public string Text => _text;

    public string NewLine { get; }

    /// <summary>An entry: its element (<c>data</c>, <c>metadata</c>, <c>assembly</c>), name (the alias for an assembly), whole lines, and its value's text.</summary>
    public sealed record Entry(string Element, string Name, int Start, int End, int ValueStart, int ValueEnd, IReadOnlyDictionary<string, string> Attributes);

    [GeneratedRegex(@"(?<name>[\w:]+)\s*=\s*""(?<value>[^""]*)""")]
    private static partial Regex AttributePattern();

    /// <summary>A culture's name as resource files are named (<c>fr</c>, <c>fr-CA</c>, <c>zh-Hans</c>).</summary>
    [GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$")]
    public static partial Regex CulturePattern();

    /// <summary>The entries, in file order (comments skipped).</summary>
    public IReadOnlyList<Entry> Entries()
    {
        var entries = new List<Entry>();
        var i = 0;
        while (i < _text.Length)
        {
            var open = _text.IndexOf('<', i);
            if (open < 0)
            {
                break;
            }

            if (string.CompareOrdinal(_text, open, "<!--", 0, 4) == 0)
            {
                var close = _text.IndexOf("-->", open + 4, StringComparison.Ordinal);
                i = close < 0 ? _text.Length : close + 3;
                continue;
            }

            var element = ElementAt(open);
            if (element is null)
            {
                i = open + 1;
                continue;
            }

            var tagEnd = _text.IndexOf('>', open);
            if (tagEnd < 0)
            {
                break;
            }

            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match match in AttributePattern().Matches(_text[(open + element.Length + 1)..tagEnd]))
            {
                attributes[match.Groups["name"].Value] = WebUtility.HtmlDecode(match.Groups["value"].Value);
            }

            int end;
            int valueStart = -1, valueEnd = -1;
            if (_text[tagEnd - 1] == '/')
            {
                end = tagEnd + 1;
            }
            else
            {
                var closing = _text.IndexOf("</" + element + ">", tagEnd, StringComparison.Ordinal);
                if (closing < 0)
                {
                    break;
                }

                end = closing + element.Length + 3;
                var valueOpen = _text.IndexOf("<value", tagEnd, closing - tagEnd, StringComparison.Ordinal);
                if (valueOpen >= 0)
                {
                    var valueTagEnd = _text.IndexOf('>', valueOpen);
                    if (_text[valueTagEnd - 1] == '/')
                    {
                        valueStart = valueEnd = valueTagEnd + 1;
                    }
                    else
                    {
                        valueStart = valueTagEnd + 1;
                        valueEnd = _text.IndexOf("</value>", valueStart, StringComparison.Ordinal);
                    }
                }
            }

            var name = element == "assembly" ? attributes.GetValueOrDefault("alias") ?? string.Empty : attributes.GetValueOrDefault("name") ?? string.Empty;
            entries.Add(new Entry(element, name, LineStart(open), LineEnd(end), valueStart, valueEnd, attributes));
            i = end;
        }

        return entries;
    }

    private string? ElementAt(int open)
    {
        foreach (var element in new[] { "data", "metadata", "assembly" })
        {
            if (string.CompareOrdinal(_text, open + 1, element, 0, element.Length) == 0 && open + 1 + element.Length < _text.Length
                && _text[open + 1 + element.Length] is ' ' or '\t' or '\r' or '\n' or '/' or '>')
            {
                return element;
            }
        }

        return null;
    }

    private int LineStart(int position)
    {
        var start = position;
        while (start > 0 && _text[start - 1] is ' ' or '\t')
        {
            start--;
        }

        return start > 0 && _text[start - 1] != '\n' ? position : start;
    }

    private int LineEnd(int position)
    {
        var end = position;
        while (end < _text.Length && _text[end] is ' ' or '\t')
        {
            end++;
        }

        if (end < _text.Length && _text[end] == '\r')
        {
            end++;
        }

        if (end < _text.Length && _text[end] == '\n')
        {
            return end + 1;
        }

        return position;
    }

    public Entry? Find(string key, bool? metadata = null) =>
        Entries().FirstOrDefault(e => e.Name == key && (metadata is null ? e.Element is "data" or "metadata" : e.Element == (metadata.Value ? "metadata" : "data")));

    /// <summary>A data entry's value as text (decoded), or null.</summary>
    public string? Value(string key) => Find(key) is { ValueStart: >= 0 } entry ? WebUtility.HtmlDecode(_text[entry.ValueStart..entry.ValueEnd]) : null;

    /// <summary>The component a key is about: <c>okButton</c> for <c>okButton.Text</c> and <c>&gt;&gt;okButton.Name</c>, <c>$this</c> for the form's.</summary>
    public static string ComponentOf(string key)
    {
        var name = key.StartsWith(">>", StringComparison.Ordinal) ? key[2..] : key;
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? name : name[..dot];
    }

    /// <summary>Where a key sorts among its component's: metadata, then properties by name, then the records.</summary>
    private static (int, int, string) Rank(string key, bool metadata)
    {
        if (key.StartsWith(">>", StringComparison.Ordinal))
        {
            var record = key[(key.LastIndexOf('.') + 1)..];
            var index = Array.IndexOf(s_records, record);
            return (2, index < 0 ? s_records.Length : index, record);
        }

        var dot = key.IndexOf('.', StringComparison.Ordinal);
        return (metadata ? 0 : 1, 0, dot < 0 ? key : key[(dot + 1)..]);
    }

    private static int CompareRank((int, int, string) a, (int, int, string) b) =>
        a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2 != b.Item2 ? a.Item2.CompareTo(b.Item2) : StringComparer.OrdinalIgnoreCase.Compare(a.Item3, b.Item3);

    /// <summary>Components by name, the form's (<c>$this</c>) last.</summary>
    private static int CompareComponents(string a, string b) =>
        a == b ? 0 : a == "$this" ? 1 : b == "$this" ? -1 : StringComparer.OrdinalIgnoreCase.Compare(a, b);

    /// <summary>Sets <paramref name="key"/> to <paramref name="resource"/> (null removes it).</summary>
    public void Set(string key, DesignerResource? resource, bool metadata = false)
    {
        var existing = Find(key, metadata);
        if (resource is null)
        {
            if (existing is not null)
            {
                Replace(existing.Start, existing.End, string.Empty);
                RemoveUnusedAssembly(existing);
            }

            return;
        }

        if (existing is not null)
        {
            var sameShape = existing.Attributes.GetValueOrDefault("type") == resource.Type && existing.Attributes.GetValueOrDefault("mimetype") == resource.MimeType
                && existing.ValueStart >= 0 && existing.ValueEnd >= existing.ValueStart;
            if (sameShape)
            {
                var encoded = Encode(resource);
                if (_text[existing.ValueStart..existing.ValueEnd] != encoded)
                {
                    Replace(existing.ValueStart, existing.ValueEnd, encoded);
                }

                return;
            }

            Replace(existing.Start, existing.End, AssemblyFor(resource) + Element(key, resource, metadata));
            RemoveUnusedAssembly(existing);
            return;
        }

        var at = InsertionPoint(key, metadata);
        var element = Element(key, resource, metadata);

        // The assembly alias the value uses is declared before its first use: moved up when it was declared below.
        if (resource.AssemblyAlias is { } alias && Entries().FirstOrDefault(e => e.Element == "assembly" && e.Name == alias) is { } declared && declared.Start >= at)
        {
            var line = _text[declared.Start..declared.End];
            Replace(declared.Start, declared.End, string.Empty);
            Replace(at, at, line + element);
            return;
        }

        Replace(at, at, AssemblyFor(resource) + element);
    }

    /// <summary>Removes every key about <paramref name="component"/> (its properties, records and metadata).</summary>
    public void RemoveComponent(string component)
    {
        var removed = Entries().Where(e => e.Element is "data" or "metadata" && ComponentOf(e.Name) == component).Reverse().ToList();
        foreach (var entry in removed)
        {
            Replace(entry.Start, entry.End, string.Empty);
        }

        foreach (var entry in removed)
        {
            RemoveUnusedAssembly(entry);
        }
    }

    /// <summary>
    /// The assembly alias an entry's type names (<c>mscorlib</c> for <c>System.Boolean, mscorlib</c>, as the resource
    /// writer shortens a type whose assembly it declared), or null for a fully qualified or bare type.
    /// </summary>
    private static string? AliasOf(Entry entry) =>
        entry.Attributes.GetValueOrDefault("type")?.Split(',', 2) is [_, var rest] && !rest.Contains(',', StringComparison.Ordinal) ? rest.Trim() : null;

    /// <summary>
    /// The <c>assembly</c> declaration a removed entry used, removed with it when no entry uses it any more (#67: Lock
    /// Controls' <c>System.Boolean, mscorlib</c> brings <c>mscorlib</c>'s, so unlocking takes it away again and the file
    /// is as it was), as the resource writer declares only the aliases its entries use.
    /// </summary>
    private void RemoveUnusedAssembly(Entry removed)
    {
        if (AliasOf(removed) is not { } alias)
        {
            return;
        }

        var entries = Entries();
        if (entries.Any(e => e.Element is "data" or "metadata" && AliasOf(e) == alias)
            || entries.FirstOrDefault(e => e.Element == "assembly" && e.Name == alias) is not { } declared)
        {
            return;
        }

        Replace(declared.Start, declared.End, string.Empty);
    }

    /// <summary>Renames a component's keys (<c>okButton.Text</c> → <c>closeButton.Text</c>) and the records that name it (its <c>Name</c>, its controls' <c>Parent</c>).</summary>
    public void RenameComponent(string component, string newName)
    {
        foreach (var entry in Entries().Where(e => e.Element is "data" or "metadata").Reverse())
        {
            if (entry.Name.EndsWith(".Parent", StringComparison.Ordinal) && entry.Name.StartsWith(">>", StringComparison.Ordinal) && entry.ValueStart >= 0
                && WebUtility.HtmlDecode(_text[entry.ValueStart..entry.ValueEnd]) == component)
            {
                Replace(entry.ValueStart, entry.ValueEnd, Escape(newName));
            }

            if (ComponentOf(entry.Name) != component)
            {
                continue;
            }

            if (entry.Name == ">>" + component + ".Name" && entry.ValueStart >= 0 && WebUtility.HtmlDecode(_text[entry.ValueStart..entry.ValueEnd]) == component)
            {
                Replace(entry.ValueStart, entry.ValueEnd, Escape(newName));
            }

            // The name attribute: the first name="…" of the start tag.
            var tag = _text.IndexOf("name=\"", entry.Start, StringComparison.Ordinal);
            var valueStart = tag + "name=\"".Length;
            var valueEnd = _text.IndexOf('"', valueStart);
            var prefix = entry.Name.StartsWith(">>", StringComparison.Ordinal) ? ">>" : string.Empty;
            var renamed = prefix + newName + entry.Name[(prefix.Length + component.Length)..];
            Replace(valueStart, valueEnd, EscapeAttribute(renamed));
        }
    }

    private int InsertionPoint(string key, bool metadata)
    {
        var entries = Entries().Where(e => e.Element is "data" or "metadata").ToList();
        var component = ComponentOf(key);
        var rank = Rank(key, metadata);
        var group = entries.Where(e => ComponentOf(e.Name) == component).ToList();
        if (group.Count > 0)
        {
            foreach (var entry in group)
            {
                if (CompareRank(Rank(entry.Name, entry.Element == "metadata"), rank) > 0)
                {
                    return AssemblyStart(entry);
                }
            }

            return group[^1].End;
        }

        foreach (var entry in entries)
        {
            if (CompareComponents(ComponentOf(entry.Name), component) > 0)
            {
                return AssemblyStart(entry);
            }
        }

        var close = _text.LastIndexOf("</root>", StringComparison.Ordinal);
        return close < 0 ? _text.Length : LineStart(close);
    }

    /// <summary>Before an entry, and before the <c>assembly</c> element just above it that it brought.</summary>
    private int AssemblyStart(Entry entry)
    {
        var previous = Entries().LastOrDefault(e => e.End == entry.Start);
        return previous is { Element: "assembly" } ? previous.Start : entry.Start;
    }

    private string Indent()
    {
        var resheader = _text.IndexOf("<resheader", StringComparison.Ordinal);
        if (resheader > 0)
        {
            var start = resheader;
            while (start > 0 && _text[start - 1] is ' ' or '\t')
            {
                start--;
            }

            return _text[start..resheader];
        }

        return "  ";
    }

    private string AssemblyFor(DesignerResource resource)
    {
        if (resource.AssemblyAlias is not { } alias || resource.AssemblyName is not { } name || Entries().Any(e => e.Element == "assembly" && e.Name == alias))
        {
            return string.Empty;
        }

        return $"{Indent()}<assembly alias=\"{EscapeAttribute(alias)}\" name=\"{EscapeAttribute(name)}\" />{NewLine}";
    }

    private string Element(string key, DesignerResource resource, bool metadata)
    {
        var indent = Indent();
        var element = metadata ? "metadata" : "data";
        var builder = new StringBuilder();
        builder.Append(indent).Append('<').Append(element).Append(" name=\"").Append(EscapeAttribute(key)).Append('"');
        if (resource.Type is { } type)
        {
            builder.Append(" type=\"").Append(EscapeAttribute(type)).Append('"');
        }
        else
        {
            builder.Append(" xml:space=\"preserve\"");
        }

        if (resource.MimeType is { } mime)
        {
            builder.Append(" mimetype=\"").Append(EscapeAttribute(mime)).Append('"');
        }

        builder.Append('>').Append(NewLine)
            .Append(indent).Append(indent).Append("<value>").Append(Encode(resource)).Append("</value>").Append(NewLine)
            .Append(indent).Append("</").Append(element).Append('>').Append(NewLine);
        return builder.ToString();
    }

    /// <summary>A value's text as the file holds it: escaped, with the file's line breaks (a base64 value's lines as the writer wrapped them).</summary>
    private string Encode(DesignerResource resource)
    {
        var text = resource.Text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (NewLine != "\n")
        {
            text = text.Replace("\n", NewLine, StringComparison.Ordinal);
        }

        return resource.MimeType is null ? Escape(text) : text;
    }

    private static string Escape(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static string EscapeAttribute(string text) => Escape(text).Replace("\"", "&quot;", StringComparison.Ordinal);

    private void Replace(int start, int end, string text) => _text = string.Concat(_text.AsSpan(0, start), text, _text.AsSpan(end));

    /// <summary>Whether the file says the form is localizable (<c>$this.Localizable</c> metadata, True).</summary>
    public bool IsLocalizable =>
        Find("$this.Localizable", metadata: true) is { ValueStart: >= 0 } entry
        && bool.TryParse(_text[entry.ValueStart..entry.ValueEnd].Trim(), out var localizable) && localizable;

    /// <summary>The non-visual components' tray locations (<c>name.TrayLocation</c> metadata), by name.</summary>
    public IReadOnlyDictionary<string, (int X, int Y)> TrayLocations()
    {
        var locations = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        foreach (var entry in Entries().Where(e => e.Element == "metadata" && e.Name.EndsWith(".TrayLocation", StringComparison.Ordinal) && e.ValueStart >= 0))
        {
            var parts = _text[entry.ValueStart..entry.ValueEnd].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
            {
                locations[entry.Name[..^".TrayLocation".Length]] = (x, y);
            }
        }

        return locations;
    }
}

/// <summary>
/// A form's <c>.resx</c> files (M41b): the default one (<c>OrderForm.resx</c>) and one per culture
/// (<c>OrderForm.fr.resx</c>), as text, edited by the designer's writer. A culture's file the form doesn't have yet is
/// created when a key is set in it.
/// </summary>
internal sealed class DesignerResourceSet
{
    private readonly Dictionary<string, (string Path, string? Original, ResxText Text)> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The resource files of the form whose code file is <paramref name="codePath"/>, read through <paramref name="read"/>
    /// (the designer's or the editor's text, else the file's), with <paramref name="unsaved"/> files that aren't on disk yet.
    /// </summary>
    public DesignerResourceSet(string codePath, Func<string, string?> read, IEnumerable<string>? unsaved = null)
    {
        CodePath = codePath;
        Read = read;
        var folder = Path.GetDirectoryName(codePath)!;
        var stem = Path.GetFileNameWithoutExtension(codePath);
        Add(string.Empty, Path.Combine(folder, stem + ".resx"));
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder, stem + ".*.resx"))
            {
                if (CultureOf(stem, Path.GetFileName(file)) is { } culture)
                {
                    Add(culture, file);
                }
            }
        }

        foreach (var file in unsaved ?? [])
        {
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)
                && CultureOf(stem, Path.GetFileName(file)) is { } culture && !_files.ContainsKey(culture))
            {
                Add(culture, file);
            }
        }
    }

    public string CodePath { get; }

    private Func<string, string?> Read { get; }

    /// <summary>The culture of a form's resource file (<c>fr</c> for <c>OrderForm.fr.resx</c>), or null when it isn't one.</summary>
    public static string? CultureOf(string stem, string fileName)
    {
        if (fileName.Length <= stem.Length + 1 + ".resx".Length || !fileName.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase) || !fileName.EndsWith(".resx", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = fileName[(stem.Length + 1)..^".resx".Length];
        return ResxText.CulturePattern().IsMatch(name) ? name : null;
    }

    private void Add(string culture, string path)
    {
        if (Read(path) is { } text)
        {
            _files[culture] = (path, text, new ResxText(text));
        }
    }

    /// <summary>The cultures besides the default that have a file, sorted.</summary>
    public IReadOnlyList<string> Cultures => [.. _files.Keys.Where(k => k.Length > 0).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>A culture's file (empty: the default), or null when the form has none.</summary>
    public ResxText? Get(string? culture) => _files.TryGetValue(culture ?? string.Empty, out var file) ? file.Text : null;

    /// <summary>A culture's file, created (empty) when the form has none.</summary>
    public ResxText GetOrCreate(string? culture)
    {
        culture ??= string.Empty;
        if (!_files.TryGetValue(culture, out var file))
        {
            var folder = Path.GetDirectoryName(CodePath)!;
            var stem = Path.GetFileNameWithoutExtension(CodePath);
            var path = Path.Combine(folder, culture.Length == 0 ? stem + ".resx" : $"{stem}.{culture}.resx");
            // In the line breaks of the form's other resource files (else of its code file).
            var newLine = _files.Values.Select(f => f.Text.NewLine).FirstOrDefault() ?? (Read(CodePath)?.Contains("\r\n", StringComparison.Ordinal) == true ? "\r\n" : "\n");
            var empty = ResxText.Empty.Replace("\r\n", "\n", StringComparison.Ordinal);
            file = (path, null, new ResxText(newLine == "\n" ? empty : empty.Replace("\n", newLine, StringComparison.Ordinal)));
            _files[culture] = file;
        }

        return file.Text;
    }

    public IEnumerable<ResxText> All => _files.Values.Select(f => f.Text);

    /// <summary>The files that changed: their paths, their text before (null: new) and after.</summary>
    public IEnumerable<(string Path, string? Before, string After)> Changes =>
        _files.Values.Where(f => f.Original != f.Text.Text).Select(f => (f.Path, f.Original, f.Text.Text));
}
