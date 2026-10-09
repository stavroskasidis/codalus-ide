using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Daedalus.Workbench.Git;

namespace Daedalus.Shell.Editor;

/// <summary>Gutter change markers (ADR 0008): added (green), modified (blue) and deleted (red wedge) lines against the index.</summary>
public sealed class GitChangeMargin : AbstractMargin
{
    private static readonly IBrush s_added = new ImmutableSolidColorBrush(Color.FromRgb(0x48, 0x9E, 0x50));
    private static readonly IBrush s_modified = new ImmutableSolidColorBrush(Color.FromRgb(0x2F, 0x7F, 0xD8));
    private static readonly IBrush s_deleted = new ImmutableSolidColorBrush(Color.FromRgb(0xD8, 0x3B, 0x3B));
    private IReadOnlyList<LineChange> _changes = [];

    public IReadOnlyList<LineChange> Changes
    {
        get => _changes;
        set
        {
            _changes = value;
            InvalidateVisual();
        }
    }

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

    protected override Size MeasureOverride(Size availableSize) => new(4, 0);

    public override void Render(DrawingContext context)
    {
        if (TextView is not { VisualLinesValid: true } view || _changes.Count == 0)
        {
            return;
        }

        var first = view.VisualLines[0].FirstDocumentLine.LineNumber;
        var last = view.VisualLines[^1].LastDocumentLine.LineNumber;
        foreach (var change in _changes)
        {
            if (change.Line + Math.Max(change.Count, 1) - 1 < first || change.Line > last + 1)
            {
                continue;
            }

            if (change.Kind == LineChangeKind.Deleted)
            {
                var y = LineTop(view, Math.Min(change.Line, last + 1));
                var wedge = new PolylineGeometry([new Point(0, y - 4), new Point(4, y), new Point(0, y + 4)], isFilled: true);
                context.DrawGeometry(s_deleted, null, wedge);
                continue;
            }

            var top = LineTop(view, Math.Max(change.Line, first));
            var bottom = LineTop(view, Math.Min(change.Line + change.Count, last + 1));
            context.FillRectangle(change.Kind == LineChangeKind.Added ? s_added : s_modified, new Rect(0, top, 3, Math.Max(2, bottom - top)));
        }
    }

    /// <summary>The top of a document line in this margin's coordinates (the bottom of the last line for one past it).</summary>
    private static double LineTop(TextView view, int line)
    {
        foreach (var visual in view.VisualLines)
        {
            if (visual.FirstDocumentLine.LineNumber == line)
            {
                return visual.VisualTop - view.VerticalOffset;
            }
        }

        var lastLine = view.VisualLines[^1];
        return lastLine.VisualTop + lastLine.Height - view.VerticalOffset;
    }
}

/// <summary>Blame annotations (ADR 0008): the commit, author and date of each run of lines, in a margin left of the text.</summary>
public sealed class BlameMargin : AbstractMargin
{
    private const double MarginWidth = 260;
    private IReadOnlyList<BlameRange> _ranges = [];

    public IReadOnlyList<BlameRange> Ranges
    {
        get => _ranges;
        set
        {
            _ranges = value;
            InvalidateVisual();
        }
    }

    /// <summary>The annotation text for a range (also used by tests and the accessible description).</summary>
    public static string Describe(BlameRange range) =>
        range.IsUncommitted
            ? "Not committed yet"
            : $"{range.Commit[..7]}  {range.Author}  {range.Date.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}  {range.Summary}";

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

    protected override Size MeasureOverride(Size availableSize) => new(MarginWidth, 0);

    public override void Render(DrawingContext context)
    {
        var foreground = (this.TryFindResource("Shell.Editor.Foreground", ActualThemeVariant, out var resource) ? resource : null) as IBrush ?? Brushes.Gray;
        var band = new ImmutableSolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80));
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (TextView is not { VisualLinesValid: true } view || _ranges.Count == 0)
        {
            return;
        }

        var typeface = new Typeface(GetValue(Avalonia.Controls.Documents.TextElement.FontFamilyProperty));
        var size = Math.Max(9, GetValue(Avalonia.Controls.Documents.TextElement.FontSizeProperty) - 2);
        var index = 0;
        foreach (var line in view.VisualLines)
        {
            var number = line.FirstDocumentLine.LineNumber;
            while (index < _ranges.Count && _ranges[index].StartLine + _ranges[index].LineCount - 1 < number)
            {
                index++;
            }

            if (index >= _ranges.Count || _ranges[index].StartLine > number)
            {
                continue;
            }

            var range = _ranges[index];
            var top = line.VisualTop - view.VerticalOffset;
            if (index % 2 == 1)
            {
                context.FillRectangle(band, new Rect(0, top, MarginWidth - 4, line.Height));
            }

            if (number == range.StartLine || line == view.VisualLines[0])
            {
                var text = new FormattedText(Describe(range), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, foreground)
                {
                    MaxTextWidth = MarginWidth - 12,
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                context.DrawText(text, new Point(4, top + ((line.Height - text.Height) / 2)));
            }
        }
    }
}
