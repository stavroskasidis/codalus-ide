using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Bookmarks;

namespace DaedalusStudio.Shell.Editor;

/// <summary>
/// The bookmark glyphs of an editor (M12b). Each bookmark of the file is anchored to its line while the editor is open,
/// so bookmarks move with edits above them; the new lines go back to the bookmark service after each edit.
/// </summary>
public sealed class BookmarkMargin : AbstractMargin
{
    private const double Size = 14;
    private readonly TextDocument _document;
    private readonly Func<string> _path;
    private readonly BookmarkService _bookmarks;
    private List<(Bookmark Bookmark, TextAnchor Anchor)> _anchored = [];
    private bool _moving;

    public BookmarkMargin(TextDocument document, Func<string> path, BookmarkService bookmarks)
    {
        _document = document;
        _path = path;
        _bookmarks = bookmarks;
        bookmarks.Changed += OnBookmarksChanged;
        document.Changed += OnDocumentChanged;
        Anchor();
    }

    /// <summary>The one-based lines with a bookmark (tests).</summary>
    internal IReadOnlyList<int> Lines => [.. _anchored.Select(a => a.Anchor.IsDeleted ? a.Bookmark.Line : a.Anchor.Line)];

    public void Detach()
    {
        _bookmarks.Changed -= OnBookmarksChanged;
        _document.Changed -= OnDocumentChanged;
    }

    private void OnBookmarksChanged(object? sender, EventArgs e)
    {
        if (!_moving)
        {
            Ui.Post(Anchor);
        }
    }

    private void Anchor()
    {
        _anchored = [.. _bookmarks.For(_path())
            .Where(b => b.Line >= 1 && b.Line <= _document.LineCount)
            .Select(b => (b, _document.CreateAnchor(_document.GetLineByNumber(b.Line).Offset)))];
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_anchored.Count == 0)
        {
            return;
        }

        var moves = _anchored.Select(a => (a.Bookmark, Line: a.Anchor.IsDeleted ? a.Bookmark.Line : a.Anchor.Line)).ToList();
        if (moves.All(m => m.Bookmark.Line == m.Line))
        {
            return;
        }

        _moving = true;
        try
        {
            _bookmarks.Move(_path(), moves);
        }
        finally
        {
            _moving = false;
        }

        // The service now holds the moved bookmarks: anchor them again (and merge two that met on one line).
        Anchor();
    }

    protected override Size MeasureOverride(Size availableSize) => new(_anchored.Count == 0 ? 0 : Size, 0);

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null)
        {
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        }

        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView is not null)
        {
            newTextView.VisualLinesChanged += OnVisualLinesChanged;
        }
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (TextView is not { VisualLinesValid: true } view || _anchored.Count == 0)
        {
            return;
        }

        var brush = this.TryFindResource("Shell.Info", ActualThemeVariant, out var resource) && resource is IBrush b ? b : Brushes.SteelBlue;
        var lines = Lines.ToHashSet();
        foreach (var line in view.VisualLines)
        {
            if (!lines.Contains(line.FirstDocumentLine.LineNumber))
            {
                continue;
            }

            // A bookmark ribbon.
            var top = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineTop) - view.VerticalOffset;
            var height = Math.Min(Size, line.TextLines[0].Height) - 2;
            var x = 3;
            var geometry = new PolylineGeometry([new Point(x, top + 1), new Point(x + 8, top + 1), new Point(x + 8, top + height), new Point(x + 4, top + height - 4), new Point(x, top + height)], isFilled: true);
            context.DrawGeometry(brush, null, geometry);
        }
    }
}
