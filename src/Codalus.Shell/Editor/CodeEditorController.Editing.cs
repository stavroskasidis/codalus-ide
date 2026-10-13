using Avalonia.Input;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Codalus.LanguageServices.Protocol;

namespace Codalus.Shell.Editor;

/// <summary>Editing commands (M12b): duplicate and move lines, expand and shrink selection, documentation comments, zoom.</summary>
public sealed partial class CodeEditorController
{
    private readonly Stack<(int Start, int Length)> _selectionHistory = new();
    private (int Start, int Length)? _expandedTo;
    private double _zoom = 1;

    /// <summary>Raised with the text the user copies or cuts in this editor (the clipboard ring).</summary>
    public event EventHandler<string>? Copied;

    /// <summary>Ctrl+D: duplicates the selection, or the caret's line(s), below.</summary>
    public void DuplicateLines()
    {
        if (Model.IsReadOnly)
        {
            return;
        }

        var document = Model.Text;
        var area = Editor.TextArea;
        if (area.Selection.Length > 0 && !area.Selection.IsMultiline)
        {
            var start = Editor.SelectionStart;
            var text = Editor.SelectedText;
            document.Insert(start + text.Length, text);
            Editor.Select(start + text.Length, text.Length);
            return;
        }

        var (first, last) = SelectedLines();
        var block = document.GetText(first.Offset, last.EndOffset - first.Offset);
        var newLine = TextUtilities.GetNewLineFromDocument(document, first.LineNumber);
        var caretColumn = Editor.CaretOffset - document.GetLineByOffset(Editor.CaretOffset).Offset;
        var caretLine = document.GetLineByOffset(Editor.CaretOffset).LineNumber - first.LineNumber;
        document.Insert(last.EndOffset, newLine + block);
        var target = document.GetLineByNumber(last.LineNumber + 1 + caretLine);
        Editor.TextArea.ClearSelection();
        Editor.CaretOffset = Math.Min(target.EndOffset, target.Offset + caretColumn);
    }

    /// <summary>Alt+Up / Alt+Down: moves the caret's line(s) up or down, keeping the selection.</summary>
    public void MoveLines(int direction)
    {
        if (Model.IsReadOnly)
        {
            return;
        }

        var document = Model.Text;
        var (first, last) = SelectedLines();
        if ((direction < 0 && first.PreviousLine is null) || (direction > 0 && last.NextLine is null))
        {
            return;
        }

        var selectionStart = Editor.SelectionStart - first.Offset;
        var selectionLength = Editor.SelectionLength;
        var caret = Editor.CaretOffset - first.Offset;
        var block = document.GetText(first.Offset, last.EndOffset - first.Offset);
        using (document.RunUpdate())
        {
            if (direction < 0)
            {
                var previous = first.PreviousLine!;
                var above = document.GetText(previous.Offset, previous.Length);
                var newLine = document.GetText(previous.EndOffset, previous.DelimiterLength);
                document.Replace(previous.Offset, last.EndOffset - previous.Offset, block + newLine + above);
                ApplyMoved(previous.Offset, selectionStart, selectionLength, caret);
            }
            else
            {
                var next = last.NextLine!;
                var below = document.GetText(next.Offset, next.Length);
                var newLine = document.GetText(last.EndOffset, last.DelimiterLength);
                document.Replace(first.Offset, next.EndOffset - first.Offset, below + newLine + block);
                ApplyMoved(first.Offset + below.Length + newLine.Length, selectionStart, selectionLength, caret);
            }
        }
    }

    private void ApplyMoved(int blockStart, int selectionStart, int selectionLength, int caret)
    {
        if (selectionLength > 0)
        {
            Editor.Select(blockStart + selectionStart, selectionLength);
        }
        else
        {
            Editor.TextArea.ClearSelection();
        }

        Editor.CaretOffset = blockStart + caret;
    }

    /// <summary>The first and last line touched by the selection (a selection ending at a line start excludes that line).</summary>
    private (DocumentLine First, DocumentLine Last) SelectedLines()
    {
        var document = Model.Text;
        if (Editor.SelectionLength == 0)
        {
            var line = document.GetLineByOffset(Editor.CaretOffset);
            return (line, line);
        }

        var first = document.GetLineByOffset(Editor.SelectionStart);
        var endOffset = Editor.SelectionStart + Editor.SelectionLength;
        var last = document.GetLineByOffset(endOffset);
        if (last.Offset == endOffset && last != first)
        {
            last = last.PreviousLine!;
        }

        return (first, last);
    }

    /// <summary>Shift+Alt+=: selects the next larger syntactic unit (the language service's selection ranges, else word, line, document).</summary>
    public async Task ExpandSelectionAsync()
    {
        var current = (Editor.SelectionStart, Editor.SelectionLength);
        if (_expandedTo != current)
        {
            _selectionHistory.Clear();
        }

        var candidates = new List<(int Start, int Length)>();
        if (Client is { Capabilities.SelectionRangeProvider: true } client)
        {
            try
            {
                var ranges = await client.SelectionRangesAsync(Model.Uri, [Model.PositionAt(Editor.CaretOffset)], CancellationToken.None).ConfigureAwait(true);
                for (var range = ranges.FirstOrDefault(); range is not null; range = range.Parent)
                {
                    var start = Model.OffsetAt(range.Range.Start);
                    candidates.Add((start, Model.OffsetAt(range.Range.End) - start));
                }
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                Report(ex);
            }
        }

        if (candidates.Count == 0)
        {
            // Word, line, whole document.
            var offset = Editor.CaretOffset;
            var document = Model.Text;
            var wordStart = TextUtilities.GetNextCaretPosition(document, Math.Min(offset + 1, document.TextLength), LogicalDirection.Backward, CaretPositioningMode.WordStart);
            var wordEnd = TextUtilities.GetNextCaretPosition(document, Math.Max(0, wordStart), LogicalDirection.Forward, CaretPositioningMode.WordBorder);
            if (wordStart >= 0 && wordEnd > wordStart)
            {
                candidates.Add((wordStart, wordEnd - wordStart));
            }

            var line = document.GetLineByOffset(offset);
            candidates.Add((line.Offset, line.Length));
            candidates.Add((0, document.TextLength));
        }

        var selectionEnd = current.SelectionStart + current.SelectionLength;
        var next = candidates
            .Where(c => c.Start <= current.SelectionStart && c.Start + c.Length >= selectionEnd && c.Length > current.SelectionLength)
            .OrderBy(c => c.Length)
            .Cast<(int Start, int Length)?>()
            .FirstOrDefault();
        if (next is not { } target)
        {
            return;
        }

        _selectionHistory.Push(current);
        Editor.Select(target.Start, target.Length);
        _expandedTo = target;
    }

    /// <summary>Shift+Alt+-: goes back to the selection before the last Expand Selection.</summary>
    public void ShrinkSelection()
    {
        if (_expandedTo != (Editor.SelectionStart, Editor.SelectionLength) || _selectionHistory.Count == 0)
        {
            _selectionHistory.Clear();
            return;
        }

        var previous = _selectionHistory.Pop();
        Editor.Select(previous.Start, previous.Length);
        _expandedTo = previous;
    }

    /// <summary>After <c>///</c> is typed on an empty line above a C# member: the documentation comment template.</summary>
    private async Task DocCommentAsync()
    {
        if (Client is not { } client || Model.LanguageId != ShellHost.CSharpLanguageId || Model.IsReadOnly)
        {
            return;
        }

        try
        {
            var version = Model.Version;
            var template = await client.DocCommentTemplateAsync(Model.Uri, Model.PositionAt(Editor.CaretOffset), CancellationToken.None).ConfigureAwait(true);
            if (template is null || version != Model.Version || _disposed)
            {
                return;
            }

            Model.ApplyEdits([template.Edit]);
            Editor.CaretOffset = Model.OffsetAt(template.Caret);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            Report(ex);
        }
    }

    /// <summary>Whether the caret's line is just <c>///</c> with the caret at its end (after typing the third slash).</summary>
    private bool AtDocCommentStart()
    {
        var line = Model.Text.GetLineByOffset(Editor.CaretOffset);
        return Editor.CaretOffset == line.EndOffset && Model.Text.GetText(line).Trim() == "///";
    }

    /// <summary>Records copies and cuts for the clipboard ring (Ctrl+C, Ctrl+X, Ctrl+Insert, Shift+Delete).</summary>
    private void TrackCopy(KeyEventArgs e)
    {
        var primary = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var copies = (primary && e.Key is Key.C or Key.X or Key.Insert) || (e.KeyModifiers == KeyModifiers.Shift && e.Key == Key.Delete);
        if (!copies)
        {
            return;
        }

        var text = Editor.SelectionLength > 0
            ? Editor.SelectedText
            : Model.Text.GetText(Model.Text.GetLineByOffset(Editor.CaretOffset)) + Environment.NewLine; // a line copy
        if (text.Length > 0)
        {
            Copied?.Invoke(this, text);
        }
    }

    /// <summary>This editor's zoom (Ctrl+Shift+. and Ctrl+Shift+,; Ctrl+mouse wheel), 50%–400%, on top of the font size.</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            _zoom = Math.Clamp(Math.Round(value, 2), 0.5, 4);
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ZoomChanged;

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Delta.Y != 0)
        {
            Zoom *= e.Delta.Y > 0 ? 1.1 : 1 / 1.1;
            e.Handled = true;
        }
    }
}
