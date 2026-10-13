using System.Text.Json;
using System.Text.RegularExpressions;

namespace Codalus.Workbench.Languages;

/// <summary>
/// A language (ADR 0016): which files it covers and how to edit them. Files match, in this order, by exact file name,
/// file-name pattern (<c>*</c> and <c>?</c>), the longest extension, then a regular expression on the first line.
/// </summary>
public sealed record LanguageDefinition(string Id, string Name)
{
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>Extensions with the dot (<c>.json</c>); compound ones (<c>.d.ts</c>) are allowed.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    public IReadOnlyList<string> FileNames { get; init; } = [];

    public IReadOnlyList<string> FileNamePatterns { get; init; } = [];

    public string? FirstLine { get; init; }

    public LanguageConfiguration? Configuration { get; init; }

    /// <summary>Who registered it: <c>shell</c> or a plugin id (unregistered with the plugin).</summary>
    public string Source { get; init; } = "shell";
}

/// <summary>A TextMate grammar: a file (JSON or plist) or, with no <see cref="Path"/>, one bundled with the shell.</summary>
public sealed record GrammarDefinition(string ScopeName, string? Language, string? Path)
{
    /// <summary>Scopes inside the grammar that are other languages (for embedded code).</summary>
    public IReadOnlyDictionary<string, string>? EmbeddedLanguages { get; init; }

    public string Source { get; init; } = "shell";
}

/// <summary>An auto-closing pair; it doesn't close inside the scopes in <see cref="NotIn"/> (<c>string</c>, <c>comment</c>).</summary>
public sealed record AutoClosingPair(string Open, string Close, IReadOnlyList<string> NotIn);

/// <summary>
/// VS Code's language configuration (<c>language-configuration.json</c>): comments, brackets, auto-closing and
/// surrounding pairs, folding markers, indentation rules and the word pattern.
/// </summary>
public sealed record LanguageConfiguration
{
    public string? LineComment { get; init; }

    public (string Start, string End)? BlockComment { get; init; }

    public IReadOnlyList<(string Open, string Close)> Brackets { get; init; } = [];

    public IReadOnlyList<AutoClosingPair> AutoClosingPairs { get; init; } = [];

    public IReadOnlyList<(string Open, string Close)> SurroundingPairs { get; init; } = [];

    /// <summary>Folding regions start and end at lines matching these (<c>#region</c>, <c>&lt;!-- #region --&gt;</c>).</summary>
    public string? FoldingStart { get; init; }

    public string? FoldingEnd { get; init; }

    /// <summary>Indentation-based languages (YAML, Python): a block ends at the first line indented no deeper.</summary>
    public bool OffSide { get; init; }

    public string? WordPattern { get; init; }

    public string? IncreaseIndentPattern { get; init; }

    public string? DecreaseIndentPattern { get; init; }

    /// <summary>Parses a <c>language-configuration.json</c> (comments and trailing commas allowed).</summary>
    public static LanguageConfiguration Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = document.RootElement;
        string? lineComment = null;
        (string, string)? blockComment = null;
        if (root.TryGetProperty("comments", out var comments) && comments.ValueKind == JsonValueKind.Object)
        {
            lineComment = comments.TryGetProperty("lineComment", out var line) && line.ValueKind == JsonValueKind.String ? line.GetString() : null;
            if (comments.TryGetProperty("blockComment", out var block) && Pair(block) is { } pair)
            {
                blockComment = pair;
            }
        }

        string? foldingStart = null, foldingEnd = null;
        var offSide = false;
        if (root.TryGetProperty("folding", out var folding) && folding.ValueKind == JsonValueKind.Object)
        {
            offSide = folding.TryGetProperty("offSide", out var off) && off.ValueKind == JsonValueKind.True;
            if (folding.TryGetProperty("markers", out var markers) && markers.ValueKind == JsonValueKind.Object)
            {
                foldingStart = Pattern(markers, "start");
                foldingEnd = Pattern(markers, "end");
            }
        }

        string? increase = null, decrease = null;
        if (root.TryGetProperty("indentationRules", out var indentation) && indentation.ValueKind == JsonValueKind.Object)
        {
            increase = Pattern(indentation, "increaseIndentPattern");
            decrease = Pattern(indentation, "decreaseIndentPattern");
        }

        return new LanguageConfiguration
        {
            LineComment = lineComment,
            BlockComment = blockComment,
            Brackets = Pairs(root, "brackets"),
            AutoClosingPairs = AutoClosing(root),
            SurroundingPairs = Pairs(root, "surroundingPairs"),
            FoldingStart = foldingStart,
            FoldingEnd = foldingEnd,
            OffSide = offSide,
            WordPattern = Pattern(root, "wordPattern"),
            IncreaseIndentPattern = increase,
            DecreaseIndentPattern = decrease,
        };
    }

    private static (string, string)? Pair(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() == 2
            && element[0].ValueKind == JsonValueKind.String && element[1].ValueKind == JsonValueKind.String)
        {
            return (element[0].GetString()!, element[1].GetString()!);
        }

        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("open", out var open) && open.ValueKind == JsonValueKind.String
            && element.TryGetProperty("close", out var close) && close.ValueKind == JsonValueKind.String)
        {
            return (open.GetString()!, close.GetString()!);
        }

        return null;
    }

    private static List<(string, string)> Pairs(JsonElement root, string name) =>
        root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray().Select(Pair).OfType<(string, string)>()]
            : [];

    private static List<AutoClosingPair> AutoClosing(JsonElement root)
    {
        if (!root.TryGetProperty("autoClosingPairs", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var pairs = new List<AutoClosingPair>();
        foreach (var item in array.EnumerateArray())
        {
            if (Pair(item) is not var (open, close))
            {
                continue;
            }

            var notIn = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("notIn", out var scopes) && scopes.ValueKind == JsonValueKind.Array
                ? scopes.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!).ToArray()
                : [];
            pairs.Add(new AutoClosingPair(open, close, notIn));
        }

        return pairs;
    }

    /// <summary>A regular expression given as a string or as <c>{ "pattern": ... }</c>.</summary>
    private static string? Pattern(JsonElement owner, string name) =>
        !owner.TryGetProperty(name, out var value) ? null
        : value.ValueKind == JsonValueKind.String ? value.GetString()
        : value.ValueKind == JsonValueKind.Object && value.TryGetProperty("pattern", out var pattern) && pattern.ValueKind == JsonValueKind.String ? pattern.GetString()
        : null;
}

/// <summary>
/// The languages and TextMate grammars the editor knows (ADR 0016): the shell's built-ins and those plugins contribute
/// (<c>contributes.languages</c>, <c>contributes.grammars</c>). A later registration of the same id or scope wins.
/// </summary>
public sealed class LanguageRegistry
{
    public const string PlainText = "plaintext";

    private readonly Lock _gate = new();
    private readonly List<LanguageDefinition> _languages = [];
    private readonly List<GrammarDefinition> _grammars = [];

    public event EventHandler? Changed;

    public IReadOnlyList<LanguageDefinition> Languages
    {
        get
        {
            lock (_gate)
            {
                return [.. _languages];
            }
        }
    }

    public IReadOnlyList<GrammarDefinition> Grammars
    {
        get
        {
            lock (_gate)
            {
                return [.. _grammars];
            }
        }
    }

    public void Register(LanguageDefinition language)
    {
        lock (_gate)
        {
            var existing = _languages.FindIndex(l => l.Id == language.Id);
            if (existing >= 0)
            {
                // A plugin extending a built-in language adds file patterns and keeps what it doesn't set.
                var old = _languages[existing];
                language = language with
                {
                    Extensions = [.. old.Extensions.Union(language.Extensions, StringComparer.OrdinalIgnoreCase)],
                    FileNames = [.. old.FileNames.Union(language.FileNames, StringComparer.OrdinalIgnoreCase)],
                    FileNamePatterns = [.. old.FileNamePatterns.Union(language.FileNamePatterns, StringComparer.OrdinalIgnoreCase)],
                    Aliases = [.. old.Aliases.Union(language.Aliases, StringComparer.Ordinal)],
                    FirstLine = language.FirstLine ?? old.FirstLine,
                    Configuration = language.Configuration ?? old.Configuration,
                    Name = string.IsNullOrEmpty(language.Name) || language.Name == language.Id ? old.Name : language.Name,
                };
                _languages[existing] = language;
            }
            else
            {
                _languages.Add(language);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RegisterGrammar(GrammarDefinition grammar)
    {
        lock (_gate)
        {
            _grammars.RemoveAll(g => g.ScopeName == grammar.ScopeName);
            _grammars.Add(grammar);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes everything <paramref name="source"/> (a plugin id) registered.</summary>
    public void Unregister(string source)
    {
        lock (_gate)
        {
            _languages.RemoveAll(l => l.Source == source);
            _grammars.RemoveAll(g => g.Source == source);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public LanguageDefinition? Get(string id)
    {
        lock (_gate)
        {
            return _languages.FirstOrDefault(l => l.Id == id || l.Aliases.Contains(id, StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>The grammar of a language, or null.</summary>
    public GrammarDefinition? GrammarFor(string languageId)
    {
        lock (_gate)
        {
            return _grammars.LastOrDefault(g => g.Language == languageId);
        }
    }

    public GrammarDefinition? GrammarByScope(string scopeName)
    {
        lock (_gate)
        {
            return _grammars.LastOrDefault(g => g.ScopeName == scopeName);
        }
    }

    /// <summary>The language of a file; <paramref name="firstLine"/> is read only when nothing else matches.</summary>
    public LanguageDefinition? ForFile(string path, Func<string?>? firstLine = null)
    {
        var name = Path.GetFileName(path);
        List<LanguageDefinition> languages;
        lock (_gate)
        {
            languages = [.. _languages];
        }

        // Later registrations win ties (plugins over built-ins).
        languages.Reverse();
        if (languages.FirstOrDefault(l => l.FileNames.Contains(name, StringComparer.OrdinalIgnoreCase)) is { } byName)
        {
            return byName;
        }

        if (languages.FirstOrDefault(l => l.FileNamePatterns.Any(p => Glob(p, name))) is { } byPattern)
        {
            return byPattern;
        }

        var byExtension = languages
            .SelectMany(l => l.Extensions.Where(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase) && name.Length > e.Length).Select(e => (Language: l, e.Length)))
            .OrderByDescending(m => m.Length)
            .FirstOrDefault();
        if (byExtension.Language is not null)
        {
            return byExtension.Language;
        }

        if (languages.Any(l => l.FirstLine is not null) && firstLine?.Invoke() is { Length: > 0 } line)
        {
            foreach (var language in languages.Where(l => l.FirstLine is not null))
            {
                try
                {
                    if (Regex.IsMatch(line, language.FirstLine!, RegexOptions.None, TimeSpan.FromMilliseconds(100)))
                    {
                        return language;
                    }
                }
                catch (ArgumentException)
                {
                    // An invalid pattern from a plugin: ignore it.
                }
                catch (RegexMatchTimeoutException)
                {
                }
            }
        }

        return null;
    }

    /// <summary>The first line of a text file (up to 1 KB), for <see cref="ForFile"/>.</summary>
    public static string? ReadFirstLine(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[1024];
            var read = stream.Read(buffer);
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            var end = text.IndexOfAny(['\r', '\n']);
            return end < 0 ? text : text[..end];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Glob(string pattern, string name)
    {
        // *, ? and literal text; a pattern with a path separator matches the file name only by its last segment.
        var last = pattern.Replace('\\', '/').Split('/')[^1];
        var regex = "^" + Regex.Escape(last).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    }
}
