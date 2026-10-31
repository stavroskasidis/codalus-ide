using System.Text;
using AvaloniaEdit.Document;

namespace Codalus.Shell.Editor;

/// <summary>
/// Applies many edits to a document as few changes (M56): edits near each other become one replace whose offset change
/// map has an entry per edit. Anchors (bookmarks, breakpoints, pinned data tips), markers, foldings, the caret and the
/// selection follow each edit through the map, as when the edits are applied one by one, while every handler of the
/// document's changes (the text views, colorizers, margins, the language service's change list) runs once per group
/// instead of once per edit: Format Document's 50,000 edits ran all of them 50,000 times on the UI thread, and typing
/// with 20,000 carets as many times per key. Edits far apart stay separate changes, so a group never rewrites a long
/// unchanged stretch between them.
/// </summary>
internal static class DocumentEdits
{
    /// <summary>
    /// The longest unchanged text between two edits of one group, about 20 lines: rewriting it costs less than another
    /// change, whose handlers walk the visible lines.
    /// </summary>
    internal const int MaxGap = 1024;

    /// <summary>
    /// The fewest lines for which a group over nearly all of the document replaces all of it (<see cref="ApplyGroup"/>):
    /// below it rebuilding the line tree saves next to nothing, and a change of the edited part keeps the measured line
    /// heights and the lines around it.
    /// </summary>
    internal const int WholeDocumentLines = 5_000;

    /// <summary>
    /// Applies <paramref name="edits"/> (offsets in the current text, ascending, not overlapping; edits at the same offset
    /// in their order, as LSP orders inserts) as one undoable update. Returns the number of document changes it made.
    /// </summary>
    public static int Apply(TextDocument document, IReadOnlyList<(int Offset, int Length, string Text)> edits)
    {
        var changes = 0;
        using (document.RunUpdate())
        {
            // Groups last to first: the offsets of a group stay valid while the text after it changes.
            var end = edits.Count;
            while (end > 0)
            {
                var start = end - 1;
                while (start > 0 && edits[start].Offset - (edits[start - 1].Offset + edits[start - 1].Length) <= MaxGap)
                {
                    start--;
                }

                ApplyGroup(document, edits, start, end);
                changes++;
                end = start;
            }
        }

        return changes;
    }

    /// <summary>
    /// The part of the text a change edited, in the text before it (offset, removal length) and after it (offset,
    /// insertion length): for a group, from its first edit to its last, which is less than the change's own range when
    /// the group replaced the whole document (<see cref="ApplyGroup"/>), or its undo. Handlers that work on the edited
    /// lines (the last edit location, semantic tokens, bracket states, the language service's change list) use it.
    /// </summary>
    public static (int Offset, int RemovalLength, int InsertionLength) EditedSpan(DocumentChangeEventArgs e)
    {
        if (e.OffsetChangeMap is not { Count: > 1 } map) // a plain change: one entry
        {
            return (e.Offset, e.RemovalLength, e.InsertionLength);
        }

        // An ascending map (as Apply builds it): the first entry's offset stands, the last entry's end is in the text
        // after. A descending one (its undo): each entry's offset is in the text before, the first entry ends last.
        var delta = e.InsertionLength - e.RemovalLength;
        int start, end;
        if (IsAscending(map))
        {
            (start, end) = (map[0].Offset, map[^1].Offset + map[^1].InsertionLength);
        }
        else if (IsDescending(map))
        {
            (start, end) = (map[^1].Offset, map[0].Offset + map[0].RemovalLength + delta);
        }
        else
        {
            return (e.Offset, e.RemovalLength, e.InsertionLength);
        }

        var removal = end - delta - start;
        var insertion = end - start;
        return start >= e.Offset && removal >= 0 && insertion >= 0 && start + removal <= e.Offset + e.RemovalLength && start + insertion <= e.Offset + e.InsertionLength
            ? (start, removal, insertion)
            : (e.Offset, e.RemovalLength, e.InsertionLength);
    }

    /// <summary>
    /// Moves <paramref name="offsets"/> through a document change as <see cref="DocumentChangeEventArgs.GetNewOffset"/>
    /// moves each, in one pass over the change's map: each GetNewOffset walks the whole map, so a format's 50,000 entries
    /// for each of 20,000 carets would take minutes.
    /// </summary>
    public static void Map(DocumentChangeEventArgs e, int[] offsets)
    {
        IReadOnlyList<OffsetChangeMapEntry> map = e.OffsetChangeMap;
        if (map.Count > 8 && !IsAscending(map) && IsDescending(map))
        {
            map = Ascending(map); // an undo's map: the entries last to first
        }

        if (map.Count <= 8 || !IsAscending(map))
        {
            for (var i = 0; i < offsets.Length; i++)
            {
                offsets[i] = e.GetNewOffset(offsets[i]);
            }

            return;
        }

        // In order of offset: the entries wholly before an offset are before every later one too. Carets come in order.
        var order = new int[offsets.Length];
        var ascending = true;
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
            ascending &= i == 0 || offsets[i - 1] <= offsets[i];
        }

        if (!ascending)
        {
            Array.Sort((int[])offsets.Clone(), order);
        }

        var next = 0;
        var delta = 0;
        foreach (var index in order)
        {
            var offset = offsets[index];
            while (next < map.Count && map[next].Offset + map[next].RemovalLength < offset + delta)
            {
                delta += map[next].InsertionLength - map[next].RemovalLength;
                next++;
            }

            var moved = offset + delta;
            for (var i = next; i < map.Count && map[i].Offset <= moved; i++)
            {
                moved = map[i].GetNewOffset(moved);
            }

            offsets[index] = moved;
        }
    }

    /// <summary>Each entry starts at or after where the one before it left off (as <see cref="Apply"/> builds them).</summary>
    private static bool IsAscending(IReadOnlyList<OffsetChangeMapEntry> map)
    {
        for (var i = 1; i < map.Count; i++)
        {
            if (map[i].Offset < map[i - 1].Offset + map[i - 1].InsertionLength)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Each entry ends at or before where the one before it starts (an inverted map: undoing an ascending one).</summary>
    private static bool IsDescending(IReadOnlyList<OffsetChangeMapEntry> map)
    {
        for (var i = 1; i < map.Count; i++)
        {
            if (map[i].Offset + map[i].RemovalLength > map[i - 1].Offset)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A descending map's entries first to last, each in the coordinates the ones before it leave: the same change, as
    /// each entry of a descending map touches only text before the entries applied ahead of it.
    /// </summary>
    private static List<OffsetChangeMapEntry> Ascending(IReadOnlyList<OffsetChangeMapEntry> map)
    {
        var ascending = new List<OffsetChangeMapEntry>(map.Count);
        var delta = 0;
        for (var i = map.Count - 1; i >= 0; i--)
        {
            var entry = map[i];
            ascending.Add(new OffsetChangeMapEntry(entry.Offset + delta, entry.RemovalLength, entry.InsertionLength, entry.RemovalNeverCausesAnchorDeletion, entry.DefaultAnchorMovementIsBeforeInsertion));
            delta += entry.InsertionLength - entry.RemovalLength;
        }

        return ascending;
    }

    private static void ApplyGroup(TextDocument document, IReadOnlyList<(int Offset, int Length, string Text)> edits, int start, int end)
    {
        if (end - start == 1)
        {
            var (offset, length, text) = edits[start];
            document.Replace(offset, length, text);
            return;
        }

        // One replace of the group's span; the map's entries are in the coordinates left by the entries before them. A span
        // over nearly all of a long document (no more than MaxGap untouched at either end) replaces all of it, AvaloniaEdit's
        // path for setting the text: it rebuilds its line tree, and each view its line heights, in one pass (foldings fold
        // again; the map moves the rest as before), where a replace of a part removes each line of the span and inserts it
        // again through both trees: typing with 20,000 carets on 100,000 lines spent 100-150 ms of each key there against
        // 40-100 ms, and a second until the JIT had optimized it. Only nearly all: the rebuild also drops the measured
        // heights (CodeLens rows, wrapped lines), the grammar's tokens and the DocumentLines (all but the first) of the
        // lines outside the span, so a span starting below the view would move the view, and blank the grammar's colors
        // above it. Handlers of the edited lines take them from the map (EditedSpan), not the change's range.
        var first = edits[start].Offset;
        var last = edits[end - 1].Offset + edits[end - 1].Length;
        if (first <= MaxGap && document.TextLength - last <= MaxGap && document.LineCount >= WholeDocumentLines)
        {
            (first, last) = (0, document.TextLength);
        }

        var original = document.GetText(first, last - first);
        var replacement = new StringBuilder(original.Length);
        var map = new OffsetChangeMap();
        var position = first;
        var delta = 0;
        for (var i = start; i < end; i++)
        {
            var (offset, length, text) = edits[i];
            replacement.Append(original, position - first, offset - position).Append(text);
            map.Add(new OffsetChangeMapEntry(offset + delta, length, text.Length));
            delta += text.Length - length;
            position = offset + length;
        }

        replacement.Append(original, position - first, last - position);
        document.Replace(first, last - first, replacement.ToString(), map);
    }
}
