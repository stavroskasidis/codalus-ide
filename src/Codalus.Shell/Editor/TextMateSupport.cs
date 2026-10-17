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

    /// <summary>The TextMate theme for the shell's theme, by its kind (<c>dark</c>, <c>light</c>, <c>highContrast</c>; a family's variant too).</summary>
    public IRawTheme ThemeFor(string themeId)
    {
        _theme = Theming.ThemeService.KindOf(themeId) switch
        {
            "light" => ThemeName.LightPlus,
            "highContrast" => ThemeName.HighContrastDark,
            _ => ThemeName.DarkPlus,
        };
        return _bundled.Value.LoadTheme(_theme);
    }

    // ------------------------------------------------------------------ IRegistryOptions

    public IRawTheme GetTheme(string scopeName) => _bundled.Value.GetTheme(scopeName);

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

    public IRawTheme GetDefaultTheme() => _bundled.Value.LoadTheme(_theme);
}
