using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Testing;

namespace Codalus.Shell.Editor;

/// <summary>
/// Coverage coloring (ADR 0011): a bar in the gutter and a faint tint behind each coverable line, blue for covered,
/// amber for partly covered (some branches not taken) and red for uncovered lines. Once the document changes after the
/// coverage was collected, the colors fade (the line numbers may no longer match). Hovering the bar shows the hits.
/// </summary>
public sealed class CoverageMargin : AbstractMargin, IBackgroundRenderer
{
    private static readonly Color s_covered = Color.FromRgb(0x3A, 0x8E, 0xE6);
    private static readonly Color s_partial = Color.FromRgb(0xE0, 0xA0, 0x30);
    private static readonly Color s_uncovered = Color.FromRgb(0xD8, 0x4B, 0x4B);

    private readonly Func<string> _path;
    private readonly CoverageStore _coverage;
    private Dictionary<int, CoverageLine> _lines = [];
    private bool _stale;
    private bool _refreshQueued;

    public CoverageMargin(Func<string> path, CoverageStore coverage)
    {
        _path = path;
        _coverage = coverage;
        coverage.Changed += OnCoverageChanged;
        Refresh();
    }

    /// <summary>The colored lines by one-based line number (tests).</summary>
    internal IReadOnlyDictionary<int, CoverageLine> Lines => _lines;

    /// <summary>The document changed since the coverage was collected (tests).</summary>
    internal bool IsStale => _stale;

    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>Stops listening to the coverage (the editor closed).</summary>
    public void Detach() => _coverage.Changed -= OnCoverageChanged;

    /// <summary>The document was edited: its coverage no longer lines up for sure.</summary>
    public void MarkEdited()
    {
        if (!_stale && _lines.Count > 0)
        {
            _stale = true;
            Redraw();
        }
    }

    private void OnCoverageChanged(object? sender, EventArgs e)
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        Ui.Post(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    public void Refresh()
    {
        var path = _path();
        var lines = new Dictionary<int, CoverageLine>();
        if (_coverage.IsVisible)
        {
            foreach (var line in _coverage.LinesOf(DocumentUris.FromPath(path)))
            {
                lines[line.Line + 1] = line;
            }
        }

        _lines = lines;
        _stale = lines.Count > 0 && _coverage.CollectedAt is { } collected && File.Exists(path) && File.GetLastWriteTimeUtc(path) > collected.UtcDateTime;
        InvalidateMeasure();
        Redraw();
    }

    private void Redraw()
    {
        InvalidateVisual();
        TextView?.InvalidateLayer(KnownLayer.Background);
    }

    protected override Size MeasureOverride(Size availableSize) => new(_lines.Count == 0 ? 0 : 5, 0);

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null)
        {
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
            oldTextView.BackgroundRenderers.Remove(this);
        }

        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView is not null)
        {
            newTextView.VisualLinesChanged += OnVisualLinesChanged;
            newTextView.BackgroundRenderers.Add(this);
        }

        InvalidateVisual();
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    internal static Color ColorOf(LineCoverageState state) => state switch
    {
        LineCoverageState.Covered => s_covered,
        LineCoverageState.Partial => s_partial,
        _ => s_uncovered,
    };

    /// <summary>The hover text of a line.</summary>
    internal static string Describe(CoverageLine line)
    {
        var text = line.State switch
        {
            LineCoverageState.Covered => string.Format(CultureInfo.CurrentCulture, "Covered ({0} {1})", line.Hits, line.Hits == 1 ? "hit" : "hits"),
            LineCoverageState.Partial => string.Format(CultureInfo.CurrentCulture, "Partly covered ({0} {1})", line.Hits, line.Hits == 1 ? "hit" : "hits"),
            _ => "Not covered",
        };
        return line.BranchesTotal > 0
            ? text + string.Format(CultureInfo.CurrentCulture, ", {0} of {1} branches", line.BranchesCovered, line.BranchesTotal)
            : text;
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (TextView is not { VisualLinesValid: true } view || _lines.Count == 0)
        {
            return;
        }

        var opacity = _stale ? 0.35 : 1.0;
        foreach (var line in view.VisualLines)
        {
            if (_lines.TryGetValue(line.FirstDocumentLine.LineNumber, out var coverage))
            {
                var top = line.VisualTop - view.VerticalOffset;
                context.FillRectangle(new ImmutableSolidColorBrush(ColorOf(coverage.State), opacity), new Rect(1, top, 3, line.Height));
            }
        }
    }

    /// <summary>The faint tint behind coverable lines.</summary>
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid || _lines.Count == 0)
        {
            return;
        }

        var alpha = _stale ? 0.05 : 0.12;
        foreach (var line in textView.VisualLines)
        {
            if (_lines.TryGetValue(line.FirstDocumentLine.LineNumber, out var coverage))
            {
                var top = line.VisualTop - textView.VerticalOffset;
                drawingContext.FillRectangle(new ImmutableSolidColorBrush(ColorOf(coverage.State), alpha), new Rect(0, top, textView.Bounds.Width, line.Height));
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        string? tip = null;
        if (TextView is { } view && view.GetVisualLineFromVisualTop(e.GetPosition(view).Y + view.VerticalOffset) is { } line
            && _lines.TryGetValue(line.FirstDocumentLine.LineNumber, out var coverage))
        {
            tip = Describe(coverage) + (_stale ? " (the file changed since)" : string.Empty);
        }

        ToolTip.SetTip(this, tip);
        ToolTip.SetIsOpen(this, tip is not null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ToolTip.SetIsOpen(this, false);
    }
}
