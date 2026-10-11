using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using DaedalusStudio.Diagnostics.Protocol;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Diagnostics;

namespace DaedalusStudio.Shell.Editor;

/// <summary>
/// Hot-line attribution (M18, ADR 0045): the share of CPU samples on each line of the profiling report the user is
/// looking at, in the gutter: the percentage of samples with the line on the stack (total), over a bar that is redder
/// the more of them ran on the line itself (self). Hovering shows both counts. The margin is empty, and takes no
/// room, for files without samples.
/// </summary>
public sealed class HotLineMargin : AbstractMargin
{
    public const double MarginWidth = 46;

    private static readonly Typeface s_typeface = new(FontFamily.Default);
    private readonly Func<string> _path;
    private readonly HotLineStore _store;
    private Dictionary<int, CpuLine> _lines = [];
    private int _total;
    private bool _refreshQueued;

    public HotLineMargin(Func<string> path, HotLineStore store)
    {
        _path = path;
        _store = store;
        store.Changed += OnChanged;
        Refresh();
    }

    /// <summary>The lines with samples by one-based number (tests).</summary>
    internal IReadOnlyDictionary<int, CpuLine> Lines => _lines;

    public void Detach() => _store.Changed -= OnChanged;

    private void OnChanged(object? sender, EventArgs e)
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
        _lines = _store.LinesOf(_path()).ToDictionary(l => l.Line);
        _total = Math.Max(1, _store.Total);
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(_lines.Count == 0 ? 0 : MarginWidth, 0);

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

        InvalidateVisual();
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>Red for lines that run hot themselves, amber for lines that only call hot code.</summary>
    internal static Color ColorOf(CpuLine line)
    {
        var self = line.Inclusive == 0 ? 0 : (double)line.Exclusive / line.Inclusive;
        return Color.FromRgb(0xE0, (byte)(0xA0 - (0x60 * self)), (byte)(0x30 - (0x10 * self)));
    }

    internal static string Describe(CpuLine line, int total) =>
        string.Format(CultureInfo.CurrentCulture, "{0} of the CPU samples on the stack ({1:N0}), {2} running on this line ({3:N0})", Share(line.Inclusive, total), line.Inclusive, Share(line.Exclusive, total), line.Exclusive);

    internal static string Share(int part, int total) =>
        (100.0 * part / Math.Max(1, total)).ToString(part * 1000 < total ? "0.##" : "0.#", CultureInfo.CurrentCulture) + "%";

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (TextView is not { VisualLinesValid: true } view || _lines.Count == 0)
        {
            return;
        }

        var foreground = view.TryFindResource("Shell.Editor.LineNumbers", view.ActualThemeVariant, out var brush) && brush is IBrush b ? b : Brushes.Gray;
        foreach (var visual in view.VisualLines)
        {
            if (!_lines.TryGetValue(visual.FirstDocumentLine.LineNumber, out var line))
            {
                continue;
            }

            var top = visual.VisualTop - view.VerticalOffset;
            var share = (double)line.Inclusive / _total;
            var width = Math.Max(2, share * (MarginWidth - 4));
            context.FillRectangle(new ImmutableSolidColorBrush(ColorOf(line), 0.35 + (0.5 * share)), new Rect(2, top + 1, width, visual.Height - 2));
            var text = new FormattedText(Share(line.Inclusive, _total), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, s_typeface, 10, foreground);
            context.DrawText(text, new Point(MarginWidth - 4 - text.Width, top + ((visual.Height - text.Height) / 2)));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        string? tip = null;
        if (TextView is { } view && view.GetVisualLineFromVisualTop(e.GetPosition(view).Y + view.VerticalOffset) is { } visual
            && _lines.TryGetValue(visual.FirstDocumentLine.LineNumber, out var line))
        {
            tip = Describe(line, _total) + (_store.Source is { } source ? "\n" + source : string.Empty);
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
