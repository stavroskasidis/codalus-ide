using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Tint;

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
/// theme's colors, as the editor colors them. Each text gets a tokenizer of its own from the shared registry (which
/// compiles each grammar once): a tokenizer isn't thread-safe, the registry's tokenizers can be made from any thread.
/// </summary>
public sealed class GrammarTokenizer(TextMateSupport textMate)
{
    /// <summary>Longer lines are left plain, as the editor leaves them (tests give one that runs out of its time).</summary>
    internal int MaxLineLength { get; init; } = 5000;

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
        return await Task.Run(() => Tokenize(text, scope, theme, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private SourceTokens? Tokenize(string text, string scope, Theme? theme, CancellationToken cancellationToken)
    {
        // Without a theme (C# and Visual Basic), the tokens' scopes give the editor's token types.
        if (textMate.TokenizerForScope(scope, theme) is not { } tokenizer)
        {
            return null;
        }

        tokenizer.TimeBudget = TimeSpan.FromMilliseconds(200);
        var lines = Workbench.Git.LineDiff.Lines(text);
        var result = new SourceToken[lines.Length][];
        var state = StateStack.Initial;
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

            var lineStart = state;
            try
            {
                foreach (var token in tokenizer.TokenizeLine(line, lineStart, out state))
                {
                    var start = Math.Min(token.Start, line.Length);
                    var end = Math.Min(token.End, line.Length);
                    if (end <= start)
                    {
                        continue;
                    }

                    if (theme is null)
                    {
                        if (GrammarColorizer.TypeOf(token.Scopes.GetScopeNames()) is { } type)
                        {
                            tokens.Add(new SourceToken(start, end - start, type, 0));
                        }
                    }
                    else if (token.Foreground > 0 && token.Foreground != theme.Defaults.ForegroundId && Parse(theme.ColorMap.GetColor(token.Foreground)) is { } argb)
                    {
                        tokens.Add(new SourceToken(start, end - start, -1, argb));
                    }
                }

                // A line cut by its budget ends wherever the tokenizer stopped (inside a string, say): the next line starts
                // as this one did, as the editor's TintColorizer does, rather than taking the rest of the text with it.
                if (tokenizer.StoppedEarly)
                {
                    state = lineStart;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                // A grammar that fails on a line leaves it plain and starts the next one afresh.
                tokens.Clear();
                state = StateStack.Initial;
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

    /// <summary>The brush the character at a 1-based pane line and 0-based character is drawn with, or null (the text color).</summary>
    public IBrush? BrushAt(int paneLine, int character)
    {
        var (tokens, source) = map(paneLine);
        if (tokens is null || source < 1)
        {
            return null;
        }

        foreach (var token in tokens.Line(source - 1))
        {
            if (character >= token.Start && character < token.Start + token.Length)
            {
                return token.Argb != 0 ? BrushOf(token.Argb) : Theme.Token(token.Type);
            }
        }

        return null;
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
            if (token.Start >= line.Length || token.Start >= LongLineCutter.MaxDrawnToCaret)
            {
                continue; // past the line, or past where a long line is cut
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

/// <summary>
/// What a diff pane's minimap draws: the pane's words in its grammar's colors, and its changed lines marked. A side's
/// filler line stands for the other side's line, so the new side marks the old side's removed lines where they were,
/// and its minimap maps the whole diff.
/// </summary>
public sealed class DiffMinimapColors(Func<Codalus.Workbench.Git.DiffPane> pane, SourceLineColorizer colors) : IMinimapColors
{
    public int Version { get; private set; }

    /// <summary>The pane's colors changed (its tokens arrived, or the theme changed): the words are read again.</summary>
    public void Invalidate() => Version++;

    public IBrush? WordBrush(int line, int character) => colors.BrushAt(line + 1, character);

    public MinimapMark MarkOf(int line)
    {
        var lines = pane();
        if ((uint)line >= (uint)lines.Lines.Count)
        {
            return MinimapMark.None;
        }

        return lines.Lines[line].Kind switch
        {
            '+' => MinimapMark.Added,
            '-' => MinimapMark.Removed,
            'f' => lines.Side == Codalus.Workbench.Git.DiffPaneSide.Old ? MinimapMark.Added : MinimapMark.Removed,
            _ => MinimapMark.None,
        };
    }
}
