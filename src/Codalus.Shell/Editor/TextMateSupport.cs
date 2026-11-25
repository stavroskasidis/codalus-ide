using System.Collections.Concurrent;
using Codalus.Workbench.Languages;
using Tint;
using Tint.Grammars;
using Tint.Loading;
using Tint.Themes;
using LanguageConfiguration = Codalus.Workbench.Languages.LanguageConfiguration;

namespace Codalus.Shell.Editor;

/// <summary>
/// TextMate grammars and themes for the editor (ADR 0016), tokenized by Tint: plugin grammars from
/// <see cref="LanguageRegistry"/>, then the VS Code grammars bundled in Tint.Grammars. One <see cref="GrammarRegistry"/>
/// is shared by every editor and view: it compiles each grammar once, on first use (the first file of its language, not
/// at startup), and hands each consumer a <see cref="Tokenizer"/> of its own (a tokenizer isn't thread-safe; creating
/// one is). Grammars and themes are read once (M56); a change of the languages (a plugin's grammars) starts afresh.
/// </summary>
public sealed class TextMateSupport
{
    /// <summary>The scope of each built-in language's bundled grammar (some languages share one, some grammars have several ids).</summary>
    private static readonly Dictionary<string, string> s_scopes = new(StringComparer.Ordinal)
    {
        ["msbuild"] = "text.xml",
        ["axaml"] = "text.xml",
        ["xaml"] = "text.xml",
        ["xml"] = "text.xml",
        ["json"] = "source.json",
        ["jsonc"] = "source.json.comments",
        ["yaml"] = "source.yaml",
        ["markdown"] = "text.html.markdown",
        ["editorconfig"] = "source.ini",
        ["ini"] = "source.ini",
        ["properties"] = "source.ini",
        ["ignore"] = "source.ignore",
        ["powershell"] = "source.powershell",
        ["shellscript"] = "source.shell",
        ["bat"] = "source.batchfile",
        ["dockerfile"] = "source.dockerfile",
        ["makefile"] = "source.makefile",
        ["html"] = "text.html.derivative",
        ["css"] = "source.css",
        ["scss"] = "source.css.scss",
        ["less"] = "source.css.less",
        ["javascript"] = "source.js",
        ["javascriptreact"] = "source.js.jsx",
        ["typescript"] = "source.ts",
        ["typescriptreact"] = "source.tsx",
        ["sql"] = "source.sql",
        ["python"] = "source.python",
        ["fsharp"] = "source.fsharp",
        ["vb"] = "source.asp.vb.net",
        ["diff"] = "source.diff",
        ["cpp"] = "source.cpp",
        ["c"] = "source.c",
        ["java"] = "source.java",
        ["go"] = "source.go",
        ["rust"] = "source.rust",
        ["lua"] = "source.lua",
        ["ruby"] = "source.ruby",
        ["php"] = "source.php",
        ["razor"] = "text.html.cshtml",
        ["csharp"] = "source.cs",
    };

    /// <summary>Languages whose bundled language configuration is another language's.</summary>
    private static readonly Dictionary<string, string> s_configurationAliases = new(StringComparer.Ordinal)
    {
        ["msbuild"] = "xml",
        ["axaml"] = "xml",
        ["xaml"] = "xml",
        ["jsonc"] = "json",
        ["editorconfig"] = "ini",
        ["properties"] = "ini",
        ["javascriptreact"] = "javascript",
        ["typescriptreact"] = "typescript",
        ["c"] = "cpp",
    };

    private readonly LanguageRegistry _languages;
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, LanguageConfiguration?> _configurations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RawTheme> _rawThemes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Theme> _themes = new(StringComparer.Ordinal);
    private volatile GrammarRegistry? _registry;
    private int _grammarReads;
    private int _themeReads;
    private string _lastThemeId = "dark";

    public TextMateSupport(LanguageRegistry languages)
    {
        _languages = languages;
        languages.Changed += (_, _) =>
        {
            lock (_gate)
            {
                _registry = null;
            }

            _rawThemes.Clear();
            _themes.Clear();
            _configurations.Clear();
        };
    }

    /// <summary>How many times a bundled grammar was read and parsed (tests: once per scope).</summary>
    internal int GrammarReads => Volatile.Read(ref _grammarReads);

    /// <summary>How many times a bundled theme was read and parsed (tests: once per theme).</summary>
    internal int ThemeReads => Volatile.Read(ref _themeReads);

    /// <summary>Logs grammar problems (Language Services output).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>The scope of a language's grammar, or null (C# has semantic highlighting from its language service instead).</summary>
    public string? ScopeFor(string? languageId) => languageId is "csharp" ? null : GrammarScopeFor(languageId);

    /// <summary>
    /// The scope of a language's grammar, C# included: read-only views without a language service (Peek Definition,
    /// the Code Definition window) color C# with its TextMate grammar.
    /// </summary>
    public string? GrammarScopeFor(string? languageId)
    {
        if (languageId is null or LanguageRegistry.PlainText)
        {
            return null;
        }

        if (_languages.GrammarFor(languageId) is { } grammar)
        {
            return grammar.ScopeName;
        }

        if (s_scopes.TryGetValue(languageId, out var scope))
        {
            return scope;
        }

        foreach (var source in TintGrammars.All)
        {
            if (source.Info.LanguageId == languageId)
            {
                return source.ScopeName;
            }
        }

        return null;
    }

    /// <summary>
    /// A new tokenizer for a language's grammar, its tokens colored by <paramref name="theme"/> (null: no colors), or
    /// null when the language has none or it can't be compiled. Each consumer keeps its own: a tokenizer isn't thread-safe.
    /// </summary>
    public Tokenizer? TokenizerFor(string? languageId, Theme? theme = null) =>
        GrammarScopeFor(languageId) is { } scope ? TokenizerForScope(scope, theme) : null;

    /// <summary>A new tokenizer for a grammar by its scope, or null when it can't be compiled (logged).</summary>
    public Tokenizer? TokenizerForScope(string scope, Theme? theme = null)
    {
        try
        {
            return Registry().CreateTokenizer(scope, theme);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log?.Invoke($"TextMate: the grammar {scope} could not be loaded: {ex.Message}");
            return null;
        }
    }

    /// <summary>The shared registry: the bundled grammars, then the plugins' (a plugin's scope replaces a bundled one).</summary>
    private GrammarRegistry Registry()
    {
        if (_registry is { } registry)
        {
            return registry;
        }

        lock (_gate)
        {
            if (_registry is { } built)
            {
                return built;
            }

            registry = new GrammarRegistry();
            foreach (var source in TintGrammars.All)
            {
                registry.Add(new CountedGrammar(source, this));
            }

            foreach (var grammar in _languages.Grammars)
            {
                if (grammar.Path is { } path)
                {
                    var info = new GrammarInfo
                    {
                        LanguageId = grammar.Language,
                        EmbeddedLanguages = grammar.EmbeddedLanguages ?? new Dictionary<string, string>(),
                    };
                    registry.Add(new FileGrammar(grammar.ScopeName, info, path, this));
                }
            }

            _registry = registry;
            return registry;
        }
    }

    /// <summary>A bundled grammar, read once and counted (<see cref="GrammarReads"/>).</summary>
    private sealed class CountedGrammar(IGrammarSource inner, TextMateSupport owner) : IGrammarSource
    {
        private RawGrammar? _grammar;

        public string ScopeName => inner.ScopeName;

        public GrammarInfo Info => inner.Info;

        public RawGrammar GetGrammar()
        {
            if (Volatile.Read(ref _grammar) is { } grammar)
            {
                return grammar;
            }

            lock (this)
            {
                if (_grammar is null)
                {
                    Interlocked.Increment(ref owner._grammarReads);
                    _grammar = inner.GetGrammar();
                }

                return _grammar;
            }
        }
    }

    /// <summary>A grammar file (a plugin's, or the shell's own next to it), read when first used; a bundled one by its scope if it can't be.</summary>
    private sealed class FileGrammar(string scopeName, GrammarInfo info, string path, TextMateSupport owner) : IGrammarSource
    {
        private RawGrammar? _grammar;

        public string ScopeName => scopeName;

        public GrammarInfo Info => info;

        public RawGrammar GetGrammar()
        {
            lock (this)
            {
                if (_grammar is not null)
                {
                    return _grammar;
                }

                try
                {
                    _grammar = GrammarLoader.Load(path, info).GetGrammar();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    owner.Log?.Invoke($"TextMate: grammar {path} could not be read: {ex.Message}");
                    if (TintGrammars.Find(scopeName) is not { } bundled)
                    {
                        throw;
                    }

                    Interlocked.Increment(ref owner._grammarReads);
                    _grammar = bundled.GetGrammar();
                }

                return _grammar;
            }
        }
    }

    /// <summary>A language's configuration: the registry's (plugins), else the bundled grammar's.</summary>
    public LanguageConfiguration? ConfigurationFor(string? languageId) =>
        languageId is null ? null : _configurations.GetOrAdd(languageId, LoadConfiguration);

    private LanguageConfiguration? LoadConfiguration(string languageId)
    {
        if (_languages.Get(languageId)?.Configuration is { } configured)
        {
            return configured;
        }

        if (languageId is "csharp")
        {
            return CSharp;
        }

        try
        {
            var id = s_configurationAliases.GetValueOrDefault(languageId, languageId);
            using var stream = typeof(TextMateSupport).Assembly.GetManifestResourceStream($"Codalus.Shell.LanguageConfigurations.{id}.json");
            if (stream is null)
            {
                return null;
            }

            using var reader = new StreamReader(stream);
            return Bundled(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log?.Invoke($"TextMate: no language configuration for '{languageId}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// A bundled language configuration (VS Code's, as TextMateSharp shipped them), read as a plugin's is, without its word
    /// pattern (as the editor always read them). Its auto-closing pairs are both kinds VS Code reads: <c>["{", "}"]</c> and
    /// <c>{ "open", "close", "notIn" }</c> (TextMateSharp kept only the second, so Go, Rust, Java and Lua closed no brackets).
    /// </summary>
    internal static LanguageConfiguration Bundled(string json) => LanguageConfiguration.Parse(json) with { WordPattern = null };

    /// <summary>C#'s configuration (its grammar isn't used: Roslyn colors C#).</summary>
    private static LanguageConfiguration CSharp { get; } = new()
    {
        LineComment = "//",
        BlockComment = ("/*", "*/"),
        Brackets = [("{", "}"), ("[", "]"), ("(", ")")],
        AutoClosingPairs = [new("{", "}", []), new("[", "]", []), new("(", ")", []), new("\"", "\"", ["string", "comment"]), new("'", "'", ["string", "comment"])],
        SurroundingPairs = [("{", "}"), ("[", "]"), ("(", ")"), ("\"", "\""), ("'", "'"), ("<", ">")],
        FoldingStart = @"^\s*#region\b",
        FoldingEnd = @"^\s*#endregion\b",
    };

    /// <summary>
    /// The TextMate theme for the shell's theme, by its kind (<c>dark</c>, <c>light</c>, <c>highContrast</c>; a family's
    /// variant too). Dark+ draws plain text (embedded code, operators) in VS Code's #D4D4D4; it takes the editor's plain
    /// text color instead, the classic IDE's #DCDCDC, as the C# around it does. In Classic, grammar-colored text also takes
    /// the classic IDE's control keywords, comments and strings (#83, D47), as the semantic colors do.
    /// </summary>
    public Theme ThemeFor(string themeId)
    {
        _lastThemeId = themeId;
        var kind = Theming.ThemeService.KindOf(themeId) switch
        {
            "light" => "light",
            "highContrast" => "highContrast",
            _ => "dark",
        };
        var recolors = RecolorsFor(themeId);
        var key = kind + ":" + string.Join(',', recolors.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Select(c => c.Key + "=" + c.Value));
        return _themes.GetOrAdd(key, _ => Theme.FromSource(Recolor(LoadTheme(kind), recolors)));
    }

    /// <summary>The theme last asked for (dark until then).</summary>
    public Theme GetDefaultTheme() => ThemeFor(_lastThemeId);

    /// <summary>A bundled theme as read, before <see cref="Recolor"/> (which differs by the shell's theme).</summary>
    private RawTheme LoadTheme(string kind) => _rawThemes.GetOrAdd(kind, k =>
    {
        Interlocked.Increment(ref _themeReads);
        IThemeSource source = k switch
        {
            "light" => TintThemes.LightPlus,
            "highContrast" => TintThemes.DarkHighContrast,
            _ => TintThemes.DarkPlus,
        };
        return source.GetTheme();
    });

    /// <summary>
    /// The colors a bundled theme's are replaced with for <paramref name="themeId"/>: Dark+'s plain text with the editor's
    /// everywhere it's used, and in Classic Dark and Light (Blue too) the values <see cref="EditorTheme"/> changed (D47).
    /// </summary>
    internal static IReadOnlyDictionary<string, string> RecolorsFor(string themeId)
    {
        var classic = Theming.ThemeService.FamilyOf(themeId) == Theming.ThemeService.Classic;
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        switch (Theming.ThemeService.KindOf(themeId))
        {
            case "light":
                if (classic)
                {
                    colors["#AF00DB"] = "#8F08C4"; // control keywords
                }

                break;
            case "highContrast":
                break;
            default:
                colors["#D4D4D4"] = "#DCDCDC"; // plain text
                if (classic)
                {
                    colors["#C586C0"] = "#D8A0DF"; // control keywords
                    colors["#6A9955"] = "#57A64A"; // comments
                    colors["#CE9178"] = "#D69D85"; // strings
                }

                break;
        }

        return colors;
    }

    /// <summary>A theme with some of its colors replaced (<see cref="RecolorsFor"/>).</summary>
    private static RawTheme Recolor(RawTheme theme, IReadOnlyDictionary<string, string> colors)
    {
        if (colors.Count == 0)
        {
            return theme;
        }

        var rules = theme.Rules.Select(r => new RawThemeRule
        {
            Name = r.Name,
            Scopes = r.Scopes,
            FontStyle = r.FontStyle,
            Foreground = Replace(colors, r.Foreground),
            Background = r.Background,
        }).ToList();
        var ui = theme.UiColors.ToDictionary(c => c.Key, c => Replace(colors, c.Value)!, StringComparer.Ordinal);
        return new RawTheme(rules, theme.Name, theme.Type, ui);
    }

    private static string? Replace(IReadOnlyDictionary<string, string> colors, string? color) =>
        color is not null && colors.TryGetValue(color, out var replacement) ? replacement : color;
}
