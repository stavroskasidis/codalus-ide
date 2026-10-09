using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Daedalus.Shell.Editor;

/// <summary>
/// Applies language-service semantic tokens to visible lines. Only a window of lines around the viewport is kept, so
/// memory stays proportional to the viewport even for 100k-line files. While an edit is in flight, tokens of the edited
/// lines are dropped and later lines shift, so colors stay roughly aligned until fresh tokens arrive.
/// </summary>
public sealed class SemanticColorizer : DocumentColorizingTransformer
{
    private Dictionary<int, LineToken[]> _lines = [];
    private (int Start, int End) _covered = (0, 0);

    public EditorTheme Theme { get; set; } = EditorTheme.Dark;

    /// <summary>True if every line in the zero-based, end-exclusive range has tokens.</summary>
    public bool Covers((int First, int Last) range) => range.First >= _covered.Start && range.Last <= _covered.End;

    public bool HasTokens => _lines.Count > 0;

    /// <summary>The token type at a zero-based line and character, or -1.</summary>
    public int TokenAt(int line, int character) =>
        _lines.TryGetValue(line, out var tokens) && tokens.FirstOrDefault(t => character >= t.Start && character < t.Start + t.Length) is { Length: > 0 } token
            ? token.Type
            : -1;

    /// <summary>Replaces the token window with LSP-encoded <paramref name="data"/> covering lines [start, end).</summary>
    public void Apply(int start, int end, int[] data)
    {
        var lines = new Dictionary<int, List<LineToken>>();
        int line = 0, character = 0;
        for (var i = 0; i + 4 < data.Length; i += 5)
        {
            line += data[i];
            character = data[i] == 0 ? character + data[i + 1] : data[i + 1];
            if (!lines.TryGetValue(line, out var list))
            {
                lines[line] = list = [];
            }

            list.Add(new LineToken(character, data[i + 2], data[i + 3]));
        }

        _lines = lines.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        _covered = (start, end);
    }

    public void Clear()
    {
        _lines = [];
        _covered = (0, 0);
    }

    /// <summary>Called before the document changes: drops tokens of the edited lines, shifts later ones.</summary>
    public void OnDocumentChanging(DocumentChangeEventArgs e, TextDocument document)
    {
        var editedLine = document.GetLineByOffset(Math.Min(e.Offset, document.TextLength)).LineNumber - 1;
        var removedLines = CountNewlines(e.RemovedText.Text);
        var delta = CountNewlines(e.InsertedText.Text) - removedLines;
        if (delta == 0 && removedLines == 0)
        {
            _lines.Remove(editedLine);
            return;
        }

        var shifted = new Dictionary<int, LineToken[]>(_lines.Count);
        foreach (var (line, tokens) in _lines)
        {
            if (line < editedLine)
            {
                shifted[line] = tokens;
            }
            else if (line > editedLine + removedLines)
            {
                shifted[line + delta] = tokens;
            }
        }

        _lines = shifted;
        _covered = (_covered.Start, Math.Max(_covered.Start, _covered.End + delta));
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (!_lines.TryGetValue(line.LineNumber - 1, out var tokens))
        {
            return;
        }

        foreach (var token in tokens)
        {
            var start = line.Offset + token.Start;
            var end = Math.Min(line.EndOffset, start + token.Length);
            if (start >= end || Theme.Token(token.Type) is not { } brush)
            {
                continue;
            }

            ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }

    private static int CountNewlines(string text) => text.AsSpan().Count('\n');

    private readonly record struct LineToken(int Start, int Length, int Type);
}
