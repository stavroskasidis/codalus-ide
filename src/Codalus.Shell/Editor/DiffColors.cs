using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace Codalus.Shell.Editor;

/// <summary>A colored range of a line: a token type the editor theme colors, or a TextMate theme's color.</summary>
public readonly record struct SourceToken(int Start, int Length, int Type, uint Argb);

/// <summary>
/// The colored tokens of a whole text, line by line (#86): what the grammar gives the old and the new file of a diff,
/// or what the C# host gives the open document. Made from the real file text, never from a diff pane's composed text,
/// so a block comment or a raw string carries its state from line to line.
/// </summary>
public sealed class SourceTokens(SourceToken[][] lines)
{
    public int LineCount => lines.Length;

    /// <summary>The tokens of a 0-based line (none past the end).</summary>
    public ReadOnlySpan<SourceToken> Line(int index) => (uint)index < (uint)lines.Length ? lines[index] : [];

    /// <summary>The token type at a 0-based line and character, or -1 (tests).</summary>
    public int TypeAt(int line, int character)
    {
        foreach (var token in Line(line))
        {
            if (character >= token.Start && character < token.Start + token.Length)
            {
                return token.Type;
            }
        }

        return -1;
    }

    /// <summary>Tokens from a language service's LSP-encoded semantic tokens (already in the shell's legend).</summary>
    public static SourceTokens FromSemantic(int[] data, int lineCount)
    {
        var lines = new List<SourceToken>[Math.Max(0, lineCount)];
        int line = 0, character = 0;
        for (var i = 0; i + 4 < data.Length; i += 5)
        {
            line += data[i];
            character = data[i] == 0 ? character + data[i + 1] : data[i + 1];
            if ((uint)line >= (uint)lines.Length || data[i + 3] < 0)
            {
                continue;
            }

            (lines[line] ??= []).Add(new SourceToken(character, data[i + 2], data[i + 3], 0));
        }

        return new SourceTokens([.. lines.Select(l => l?.ToArray() ?? [])]);
    }
}

/// <summary>
/// Tokenizes whole texts with their language's TextMate grammar off the UI thread (#86: the diff document's sides and
/// the merge editor's panes). C# and Visual Basic take the editor's token types (<see cref="GrammarColorizer.TypeOf"/>,
/// drawn in <see cref="EditorTheme"/>, as Add New Item's preview colors them); other languages take their TextMate
/// theme's colors, as the editor colors them. It has a registry of its own, since the shared one is the UI thread's.
/// </summary>
public sealed class GrammarTokenizer(TextMateSupport textMate)
{
    /// <summary>Longer lines are left plain, as the editor leaves them.</summary>
    private const int MaxLineLength = 5000;

    private readonly Lock _sync = new();
    private Registry? _registry;
    private IRawTheme? _registryTheme;

    /// <summary>Whether a language is colored with the editor's token types (C# and Visual Basic) rather than a TextMate theme.</summary>
    public static bool UsesEditorColors(string? languageId) => languageId is "csharp" or "vb";

    /// <summary>
    /// The tokens of <paramref name="text"/>, or null when its language has no grammar. Call it on the UI thread; the
    /// work runs on the thread pool. <paramref name="themeId"/> picks the TextMate theme of languages other than C#.
    /// </summary>
    public async Task<SourceTokens?> TokenizeAsync(string text, string? languageId, string themeId, CancellationToken cancellationToken)
    {
        if (textMate.GrammarScopeFor(languageId) is not { } scope)
        {
            return null;
        }

        var editorColors = UsesEditorColors(languageId);
        var theme = editorColors ? null : textMate.ThemeFor(themeId);
        return await Task.Run(
            () =>
            {
                // One text at a time: the registry and its grammars aren't thread-safe.
                lock (_sync)
                {
                    return Tokenize(text, scope, theme, editorColors, cancellationToken);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private SourceTokens? Tokenize(string text, string scope, IRawTheme? theme, bool editorColors, CancellationToken cancellationToken)
    {
        IGrammar? grammar;
        Theme? colors = null;
        try
        {
            if (_registry is null || (theme is not null && !ReferenceEquals(theme, _registryTheme)))
            {
                _registry = new Registry(textMate);
                _registryTheme = null;
            }

            if (theme is not null)
            {
                if (!ReferenceEquals(theme, _registryTheme))
                {
                    _registry.SetTheme(theme);
                    _registryTheme = theme;
                }

                colors = _registry.GetTheme();
            }

            grammar = _registry.LoadGrammar(scope);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            textMate.Log?.Invoke($"TextMate: the grammar {scope} could not be loaded: {ex.Message}");
            return null;
        }

        if (grammar is null)
        {
            return null;
        }

        var lines = Workbench.Git.LineDiff.Lines(text);
        var result = new SourceToken[lines.Length][];
        IStateStack? state = null;
        var tokens = new List<SourceToken>();
        for (var i = 0; i < lines.Length; i++)
        {
            if ((i & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var line = lines[i];
            tokens.Clear();
            if (line.Length > MaxLineLength)
            {
                result[i] = [];
                continue;
            }

            try
            {
                if (editorColors)
                {
                    var tokenized = grammar.TokenizeLine(line, state, TimeSpan.FromMilliseconds(200));
                    state = tokenized.RuleStack;
                    foreach (var token in tokenized.Tokens)
                    {
                        var start = Math.Min(token.StartIndex, line.Length);
                        var end = Math.Min(token.EndIndex, line.Length);
                        if (end > start && GrammarColorizer.TypeOf(token.Scopes) is { } type)
                        {
                            tokens.Add(new SourceToken(start, end - start, type, 0));
                        }
                    }
                }
                else
                {
                    var tokenized = grammar.TokenizeLine2(line, state, TimeSpan.FromMilliseconds(200));
                    state = tokenized.RuleStack;
                    var data = tokenized.Tokens;
                    for (var k = 0; k + 1 < data.Length; k += 2)
                    {
                        var start = Math.Min(data[k], line.Length);
                        var end = k + 2 < data.Length ? Math.Min(data[k + 2], line.Length) : line.Length;
                        var foreground = TextMateSharp.Internal.Grammars.EncodedTokenAttributes.GetForeground(data[k + 1]);
                        if (end > start && foreground > 1 && colors?.GetColor(foreground) is { } hex && Parse(hex) is { } argb)
                        {
                            tokens.Add(new SourceToken(start, end - start, -1, argb));
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                // A grammar that fails on a line leaves it plain and starts the next one afresh.
                state = null;
            }

            result[i] = [.. tokens];
        }

        return new SourceTokens(result);
    }

    private static uint? Parse(string hex)
    {
        if (hex.Length == 7 && hex[0] == '#' && uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var rgb))
        {
            return 0xFF000000 | rgb;
        }

        if (hex.Length == 9 && hex[0] == '#' && uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var rgba))
        {
            return (rgba >> 8) | ((rgba & 0xFF) << 24);
        }

        return null;
    }
}

/// <summary>
/// Colors the lines of a pane whose lines show lines of one or two files (#86: a diff document's sides, the inline
/// view, the merge editor's panes): each pane line is mapped to its file line, and that line's tokens are drawn in the
/// editor theme's colors. Filler and folded lines stay plain.
/// </summary>
public sealed class SourceLineColorizer(Func<int, (SourceTokens? Tokens, int Line)> map) : DocumentColorizingTransformer
{
    private readonly Dictionary<uint, IBrush> _brushes = [];

    public EditorTheme Theme { get; set; } = EditorTheme.Dark;

    /// <summary>The colored token type at a 1-based pane line and 0-based character, or -1 (tests).</summary>
    public int TypeAt(int paneLine, int character)
    {
        var (tokens, line) = map(paneLine);
        return tokens is null || line < 1 ? -1 : tokens.TypeAt(line - 1, character);
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        var (tokens, source) = map(line.LineNumber);
        if (tokens is null || source < 1)
        {
            return;
        }

        foreach (var token in tokens.Line(source - 1))
        {
            if (token.Start >= line.Length)
            {
                continue;
            }

            var brush = token.Argb != 0 ? BrushOf(token.Argb) : Theme.Token(token.Type);
            if (brush is null)
            {
                continue;
            }

            var end = Math.Min(line.Length, token.Start + token.Length);
            ChangeLinePart(line.Offset + token.Start, line.Offset + end, element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }

    private IBrush BrushOf(uint argb)
    {
        if (!_brushes.TryGetValue(argb, out var brush))
        {
            _brushes[argb] = brush = new ImmutableSolidColorBrush(argb);
        }

        return brush;
    }
}
