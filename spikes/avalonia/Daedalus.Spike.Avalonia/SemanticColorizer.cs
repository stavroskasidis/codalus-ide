using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Daedalus.LanguageServices.Protocol;

namespace Daedalus.Spike.Avalonia;

/// <summary>
/// Applies host-computed semantic tokens to visible lines. Only the requested window of lines is kept,
/// so memory stays proportional to the viewport even for 100k-line files.
/// </summary>
public sealed class SemanticColorizer : DocumentColorizingTransformer
{
    private Dictionary<int, LineToken[]> _lines = [];
    private (int Start, int End) _covered = (0, 0);

    /// <summary>True if every line in the zero-based, end-exclusive range has tokens.</summary>
    public bool Covers((int First, int Last) range) => range.First >= _covered.Start && range.Last <= _covered.End;

    public int TokenCount => _lines.Values.Sum(t => t.Length);

    /// <summary>Replaces the token window with LSP-encoded <paramref name="data"/> for lines [start, end).</summary>
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

    /// <summary>Keeps tokens roughly aligned while an edit is in flight: drops the edited lines, shifts later ones.</summary>
    public void OnDocumentChanged(DocumentChangeEventArgs e, TextDocument document)
    {
        var editedLine = document.GetLineByOffset(Math.Min(e.Offset, document.TextLength)).LineNumber - 1;
        var delta = CountNewlines(e.InsertedText.Text) - CountNewlines(e.RemovedText.Text);
        var shifted = new Dictionary<int, LineToken[]>(_lines.Count);
        foreach (var (line, tokens) in _lines)
        {
            if (line < editedLine)
            {
                shifted[line] = tokens;
            }
            else if (line > editedLine + Math.Max(0, -delta))
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
            if (start >= end || TokenTheme.Brush(token.Type) is not { } brush)
            {
                continue;
            }

            ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }

    private static int CountNewlines(string text) => text.AsSpan().Count('\n');

    private readonly record struct LineToken(int Start, int Length, int Type);
}

/// <summary>Dark theme colors for semantic token types.</summary>
public static class TokenTheme
{
    private static readonly IBrush?[] s_brushes = Build();

    public static IBrush? Brush(int tokenType) => (uint)tokenType < (uint)s_brushes.Length ? s_brushes[tokenType] : null;

    private static IBrush?[] Build()
    {
        var colors = new Dictionary<string, uint>
        {
            ["namespace"] = 0xFFC8C8C8,
            ["class"] = 0xFF4EC9B0, ["record"] = 0xFF4EC9B0, ["type"] = 0xFF4EC9B0, ["delegate"] = 0xFF4EC9B0,
            ["struct"] = 0xFF86C691, ["recordStruct"] = 0xFF86C691, ["enum"] = 0xFFB8D7A3, ["interface"] = 0xFFB8D7A3,
            ["typeParameter"] = 0xFFB8D7A3,
            ["parameter"] = 0xFF9CDCFE, ["local"] = 0xFF9CDCFE, ["variable"] = 0xFF9CDCFE,
            ["field"] = 0xFFDCDCAA, ["property"] = 0xFFDCDCAA, ["event"] = 0xFFDCDCAA, ["constant"] = 0xFF4FC1FF,
            ["enumMember"] = 0xFF4FC1FF,
            ["method"] = 0xFFDCDCAA, ["extensionMethod"] = 0xFFDCDCAA, ["function"] = 0xFFDCDCAA,
            ["keyword"] = 0xFF569CD6, ["modifier"] = 0xFF569CD6, ["controlKeyword"] = 0xFFC586C0,
            ["comment"] = 0xFF6A9955, ["xmlDocComment"] = 0xFF608B4E,
            ["string"] = 0xFFCE9178, ["stringVerbatim"] = 0xFFCE9178, ["stringEscape"] = 0xFFFFD68F,
            ["number"] = 0xFFB5CEA8, ["regexp"] = 0xFFD16969,
            ["operator"] = 0xFFD4D4D4, ["punctuation"] = 0xFFD4D4D4,
            ["preprocessor"] = 0xFF9B9B9B, ["excludedCode"] = 0xFF767676, ["label"] = 0xFFC8C8C8,
        };

        return [.. TokenTypes.All.Select(name => colors.TryGetValue(name, out var argb) ? new ImmutableSolidColorBrush(argb) : (IBrush?)null)];
    }
}
