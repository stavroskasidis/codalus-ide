using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host;

/// <summary>
/// Encodes token spans into the LSP relative integer format. LSP tokens must be single-line and must
/// not overlap, while Roslyn spans can be multi-line (block comments, raw strings) and nested
/// (an escape sequence inside a string). Nested spans win over their container.
/// </summary>
public static class SemanticTokenEncoder
{
    public static int[] Encode(SourceText text, IReadOnlyList<TokenSpan> spans)
    {
        var flat = Flatten(spans);
        var data = new List<int>(flat.Count * 5);
        int prevLine = 0, prevChar = 0;

        foreach (var token in flat)
        {
            var position = token.Start;
            var lineIndex = text.Lines.GetLineFromPosition(position).LineNumber;
            while (position < token.End && lineIndex < text.Lines.Count)
            {
                var line = text.Lines[lineIndex];
                var segmentEnd = Math.Min(token.End, line.End);
                if (segmentEnd > position)
                {
                    var character = position - line.Start;
                    var deltaLine = lineIndex - prevLine;
                    data.Add(deltaLine);
                    data.Add(deltaLine == 0 ? character - prevChar : character);
                    data.Add(segmentEnd - position);
                    data.Add(token.Type);
                    data.Add(token.Modifiers);
                    prevLine = lineIndex;
                    prevChar = character;
                }

                position = line.EndIncludingLineBreak;
                lineIndex++;
            }
        }

        return [.. data];
    }

    /// <summary>Splits containers around nested spans so that the result is sorted and non-overlapping.</summary>
    public static List<TokenSpan> Flatten(IReadOnlyList<TokenSpan> spans)
    {
        var sorted = spans.OrderBy(s => s.Start).ThenByDescending(s => s.Length).ToList();
        var result = new List<TokenSpan>(sorted.Count);
        var open = new Stack<TokenSpan>();
        var position = 0;

        void Emit(TokenSpan owner, int start, int end)
        {
            if (end > start)
            {
                result.Add(owner with { Start = start, Length = end - start });
            }
        }

        foreach (var span in sorted)
        {
            while (open.Count > 0 && open.Peek().End <= span.Start)
            {
                var closed = open.Pop();
                Emit(closed, Math.Max(position, closed.Start), closed.End);
                position = Math.Max(position, closed.End);
            }

            if (open.Count > 0)
            {
                var container = open.Peek();
                Emit(container, Math.Max(position, container.Start), span.Start);
            }

            position = Math.Max(position, span.Start);
            open.Push(span);
        }

        while (open.Count > 0)
        {
            var closed = open.Pop();
            Emit(closed, Math.Max(position, closed.Start), closed.End);
            position = Math.Max(position, closed.End);
        }

        return result;
    }
}
