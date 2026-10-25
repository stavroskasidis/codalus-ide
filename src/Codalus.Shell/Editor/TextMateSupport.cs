using System.Collections.Concurrent;
using Codalus.Workbench.Languages;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;
using LanguageConfiguration = Codalus.Workbench.Languages.LanguageConfiguration;

namespace Codalus.Shell.Editor;

/// <summary>
/// TextMate grammars and themes for the editor (ADR 0016): plugin grammars from <see cref="LanguageRegistry"/>, then the
/// VS Code grammars bundled in TextMateSharp. Tokenizing runs off the UI thread in AvaloniaEdit.TextMate's model.
/// The bundled registry loads on first use (the first non-C# file), not at startup.
/// </summary>
public sealed class TextMateSupport : IRegistryOptions
{
    /// <summary>Languages whose grammar is a bundled one under another id.</summary>
    private static readonly Dictionary<string, string> s_aliases = new(StringComparer.Ordinal)
    {
        ["msbuild"] = "xml",
        ["axaml"] = "xml",
        ["xaml"] = "xml",
        ["editorconfig"] = "ini",
        ["properties"] = "ini",
        ["javascriptreact"] = "javascript",
        ["typescriptreact"] = "typescript",
    };

    private readonly LanguageRegistry _languages;
    private readonly Lazy<RegistryOptions> _bundled = new(() => new RegistryOptions(ThemeName.DarkPlus), LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly ConcurrentDictionary<string, IRawGrammar?> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LanguageConfiguration?> _configurations = new(StringComparer.Ordinal);
    private ThemeName _theme = ThemeName.DarkPlus;
    private TextMateSharp.Registry.Registry? _registry;

    public TextMateSupport(LanguageRegistry languages)
    {
        _languages = languages;
        languages.Changed += (_, _) =>
        {
            _files.Clear();
            _configurations.Clear();
        };
    }

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

        try
        {
            return _bundled.Value.GetScopeByLanguageId(s_aliases.GetValueOrDefault(languageId, languageId));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log?.Invoke($"TextMate: no grammar for '{languageId}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// A language's grammar loaded for tokenizing outside an editor (Add New Item's preview, M34h), or null when it has
    /// none or it can't be read.
    /// </summary>
    public IGrammar? GrammarFor(string? languageId)
    {
        if (GrammarScopeFor(languageId) is not { } scope)
        {
            return null;
        }

        try
        {
            _registry ??= new TextMateSharp.Registry.Registry(this);
            return _registry.LoadGrammar(scope);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log?.Invoke($"TextMate: the grammar {scope} could not be loaded: {ex.Message}");
            return null;
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
            var id = s_aliases.GetValueOrDefault(languageId, languageId);
            var bundled = _bundled.Value.GetAvailableLanguages().FirstOrDefault(l => l.Id == id)?.Configuration;
            return bundled is null ? null : Convert(bundled);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log?.Invoke($"TextMate: no language configuration for '{languageId}': {ex.Message}");
            return null;
        }
    }

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

    private static LanguageConfiguration Convert(TextMateSharp.Grammars.LanguageConfiguration source)
    {
        static (string, string)? Pair(IList<string>? pair) => pair is { Count: 2 } ? (pair[0], pair[1]) : null;
        return new LanguageConfiguration
        {
            LineComment = source.Comments?.LineComment,
            BlockComment = Pair(source.Comments?.BlockComment),
            Brackets = [.. (source.Brackets ?? []).Select(b => Pair(b?.Select(x => x?.ToString() ?? string.Empty).ToList())).OfType<(string, string)>()],
            AutoClosingPairs = [.. (source.AutoClosingPairs?.AutoPairs ?? []).Where(p => p?.Open is not null && p.Close is not null)
                .Select(p => new AutoClosingPair(p.Open, p.Close, [.. p.NotIn ?? []]))],
            SurroundingPairs = [.. (source.SurroundingPairs ?? []).Select(b => Pair(b?.Select(x => x.ToString()).ToList())).OfType<(string, string)>()],
            FoldingStart = source.Folding?.Markers?.Start,
            FoldingEnd = source.Folding?.Markers?.End,
            OffSide = source.Folding?.OffSide == true,
            IncreaseIndentPattern = source.IndentationRules?.Increase,
            DecreaseIndentPattern = source.IndentationRules?.Decrease,
        };
    }

    /// <summary>
    /// The TextMate theme for the shell's theme, by its kind (<c>dark</c>, <c>light</c>, <c>highContrast</c>; a family's
    /// variant too). Dark+ draws plain text (embedded code, operators) in VS Code's #D4D4D4; it takes the editor's plain
    /// text color instead, the classic IDE's #DCDCDC, as the C# around it does. In Classic, grammar-colored text also takes
    /// the classic IDE's control keywords, comments and strings (#83, D47), as the semantic colors do.
    /// </summary>
    public IRawTheme ThemeFor(string themeId)
    {
        _theme = Theming.ThemeService.KindOf(themeId) switch
        {
            "light" => ThemeName.LightPlus,
            "highContrast" => ThemeName.HighContrastDark,
            _ => ThemeName.DarkPlus,
        };
        _recolors = RecolorsFor(themeId);
        return Recolor(_bundled.Value.LoadTheme(_theme));
    }

    private IReadOnlyDictionary<string, string> _recolors = RecolorsFor("dark");

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

    private IRawTheme Recolor(IRawTheme theme) => theme is null || _recolors.Count == 0 ? theme! : new RecoloredTheme(theme, _recolors);

    /// <summary>A theme (and the themes it includes) with some of its colors replaced (<see cref="RecolorsFor"/>).</summary>
    private sealed class RecoloredTheme(IRawTheme inner, IReadOnlyDictionary<string, string> colors) : IRawTheme
    {
        public string GetName() => inner.GetName();

        public string GetInclude() => inner.GetInclude();

        public ICollection<IRawThemeSetting> GetSettings() => Recolor(inner.GetSettings());

        public ICollection<IRawThemeSetting> GetTokenColors() => Recolor(inner.GetTokenColors());

        public ICollection<KeyValuePair<string, object>> GetGuiColors() =>
            inner.GetGuiColors()?.Select(c => c.Value is string color ? new KeyValuePair<string, object>(c.Key, Replace(colors, color)!) : c).ToList()!;

        private List<IRawThemeSetting> Recolor(ICollection<IRawThemeSetting>? settings) =>
            settings?.Select(s => (IRawThemeSetting)new RecoloredSetting(s, colors)).ToList()!;
    }

    private static string? Replace(IReadOnlyDictionary<string, string> colors, string? color) =>
        color is not null && colors.TryGetValue(color, out var replacement) ? replacement : color;

    private sealed class RecoloredSetting(IRawThemeSetting inner, IReadOnlyDictionary<string, string> colors) : IRawThemeSetting, IThemeSetting
    {
        public string GetName() => inner.GetName();

        public object GetScope() => inner.GetScope();

        public IThemeSetting GetSetting() => inner.GetSetting() is null ? null! : this;

        public object GetFontStyle() => inner.GetSetting().GetFontStyle();

        public string GetBackground() => inner.GetSetting().GetBackground();

        public string GetForeground() => Replace(colors, inner.GetSetting().GetForeground())!;
    }

    // ------------------------------------------------------------------ IRegistryOptions

    public IRawTheme GetTheme(string scopeName) => Recolor(_bundled.Value.GetTheme(scopeName));

    public IRawGrammar GetGrammar(string scopeName)
    {
        if (_languages.GrammarByScope(scopeName) is { Path: { } path })
        {
            var grammar = _files.GetOrAdd(scopeName, _ =>
            {
                try
                {
                    using var reader = new StreamReader(path);
                    return GrammarReader.ReadGrammarSync(reader);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log?.Invoke($"TextMate: grammar {path} could not be read: {ex.Message}");
                    return null;
                }
            });
            if (grammar is not null)
            {
                return grammar;
            }
        }

        return _bundled.Value.GetGrammar(scopeName);
    }

    public ICollection<string> GetInjections(string scopeName) => _bundled.Value.GetInjections(scopeName);

    public IRawTheme GetDefaultTheme() => Recolor(_bundled.Value.LoadTheme(_theme));
}
