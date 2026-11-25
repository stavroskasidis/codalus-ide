using Avalonia.Styling;
using Codalus.Shell.Theming;
using Tint;

namespace Codalus.Shell.Editor;

/// <summary>
/// The editor's syntax colors for single lines of code outside an editor (M34h, the control set's views): Find Results'
/// matches and the WCF Test Client's response tree. A line is split by its language's TextMate grammar and each scope
/// takes the token type <see cref="GrammarColorizer"/> gives it, drawn in the <see cref="EditorTheme"/> of the theme the
/// control is shown in (with the user's colors over the active theme's). The shell configures it once; without it
/// (a view hosted alone in a test) lines are plain. UI thread only.
/// </summary>
public static class CodeColors
{
    private const int MaxCached = 4096;

    private static readonly Dictionary<string, string?> s_languages = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(string Language, string Text), (int Start, int Length, int Type)[]> s_tokens = [];

    /// <summary>
    /// Each language's tokenizer (null: no grammar), kept: a tokenizer builds its regular expressions as it reaches its
    /// grammar's rules, so a new one per line made Find Results' Markdown lines about ten times slower and allocated GiBs.
    /// </summary>
    private static readonly Dictionary<string, Tokenizer?> s_tokenizers = new(StringComparer.Ordinal);
    private static Func<string, string?>? s_languageOf;
    private static Func<string, Tokenizer?>? s_tokenizerOf;
    private static Func<string?, EditorTheme>? s_themeFor;

    /// <summary>The theme or the user's colors changed: lines shown should be colored again.</summary>
    public static event EventHandler? Changed;

    /// <param name="languageOf">A file's language id.</param>
    /// <param name="tokenizerOf">A new tokenizer for a language's TextMate grammar, or null (asked once per language).</param>
    /// <param name="themeFor">A built-in theme's editor colors by its id (null: the active theme's).</param>
    public static void Configure(Func<string, string?> languageOf, Func<string, Tokenizer?> tokenizerOf, Func<string?, EditorTheme> themeFor)
    {
        s_languageOf = languageOf;
        s_tokenizerOf = tokenizerOf;
        s_themeFor = themeFor;
        s_languages.Clear();
        s_tokens.Clear();
        s_tokenizers.Clear();
        NotifyChanged();
    }

    /// <summary>
    /// Forgets the configuration whose <paramref name="languageOf"/> this is (a disposed shell's), unless another
    /// replaced it: it would hold that shell after it was disposed.
    /// </summary>
    public static void Release(Func<string, string?> languageOf)
    {
        if (Equals(s_languageOf, languageOf))
        {
            s_languageOf = null;
            s_tokenizerOf = null;
            s_themeFor = null;
            s_languages.Clear();
            s_tokens.Clear();
            s_tokenizers.Clear();
        }
    }

    /// <summary>The theme or the user's colors changed.</summary>
    public static void NotifyChanged() => Changed?.Invoke(null, EventArgs.Empty);

    /// <summary>The language of a file (by its extension, cached), or null.</summary>
    public static string? LanguageOf(string? path)
    {
        if (string.IsNullOrEmpty(path) || s_languageOf is not { } languageOf)
        {
            return null;
        }

        var extension = Path.GetExtension(path);
        var key = extension.Length > 0 ? extension : Path.GetFileName(path);
        if (!s_languages.TryGetValue(key, out var language))
        {
            try
            {
                language = languageOf(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                language = null;
            }

            s_languages[key] = language;
        }

        return language;
    }

    /// <summary>The editor colors of the theme <paramref name="variant"/> stands for (the active theme's for one it doesn't know).</summary>
    public static EditorTheme ThemeFor(ThemeVariant? variant)
    {
        string? id = null;
        foreach (var (themeId, themeVariant) in ThemeService.Themes)
        {
            if (themeVariant == variant)
            {
                id = themeId;
                break;
            }
        }

        if (s_themeFor is { } themeFor)
        {
            return themeFor(id);
        }

        return id is not null ? EditorTheme.For(id)
            : ThemeService.IsHighContrast(variant) ? EditorTheme.HighContrast
            : variant == ThemeVariant.Light || variant?.InheritVariant == ThemeVariant.Light ? EditorTheme.Light : EditorTheme.Dark;
    }

    /// <summary>The colored tokens of one line (start, length, token type), or none without a grammar.</summary>
    public static IReadOnlyList<(int Start, int Length, int Type)> Tokens(string? languageId, string text)
    {
        if (languageId is null || text.Length == 0 || s_tokenizerOf is not { } tokenizerOf)
        {
            return [];
        }

        if (s_tokens.TryGetValue((languageId, text), out var cached))
        {
            return cached;
        }

        var tokens = new List<(int, int, int)>();
        try
        {
            if (!s_tokenizers.TryGetValue(languageId, out var tokenizer))
            {
                tokenizer = tokenizerOf(languageId);
                s_tokenizers[languageId] = tokenizer;
            }

            if (tokenizer is not null)
            {
                tokenizer.TimeBudget = TimeSpan.FromMilliseconds(50);
                foreach (var token in tokenizer.TokenizeLine(text, StateStack.Initial, out _))
                {
                    var start = Math.Min(token.Start, text.Length);
                    var end = Math.Min(token.End, text.Length);
                    if (end > start && GrammarColorizer.TypeOf(token.Scopes.GetScopeNames()) is { } type)
                    {
                        tokens.Add((start, end - start, type));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            tokens.Clear(); // a grammar that fails leaves the line plain
        }

        if (s_tokens.Count >= MaxCached)
        {
            s_tokens.Clear();
        }

        var array = tokens.ToArray();
        s_tokens[(languageId, text)] = array;
        return array;
    }
}
