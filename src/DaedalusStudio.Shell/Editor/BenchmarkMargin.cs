using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Diagnostics;

namespace DaedalusStudio.Shell.Editor;

/// <summary>
/// The benchmark glyphs (M18b, ADR 0046): in a source file of a project that references BenchmarkDotNet, a stopwatch on
/// each <c>[Benchmark]</c> method's line and on its class's; clicking one runs that benchmark (or the class's) and shows
/// the results in the Benchmarks window. The benchmarks are found in the editor's text, so they follow edits.
/// </summary>
public sealed class BenchmarkMargin : AbstractMargin
{
    private const double Size = 16;

    private readonly Func<string> _path;
    private readonly BenchmarkService _benchmarks;
    private readonly Func<string, BenchmarkMethod, Task> _run;
    private TextDocument? _document;
    private IReadOnlyList<BenchmarkMethod> _found = [];
    private bool _dirty = true;
    private bool _refreshQueued;

    public BenchmarkMargin(Func<string> path, BenchmarkService benchmarks, Func<string, BenchmarkMethod, Task> run)
    {
        _path = path;
        _benchmarks = benchmarks;
        _run = run;
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "Run benchmark");
        benchmarks.Changed += OnBenchmarksChanged;
    }

    private bool Enabled => _benchmarks.ProjectOf(_path()) is not null;

    /// <summary>The glyphs of the document (tests): methods and classes with benchmarks, by line.</summary>
    public IReadOnlyList<BenchmarkMethod> Glyphs
    {
        get
        {
            if (!Enabled || _document is null)
            {
                return [];
            }

            if (_dirty)
            {
                _found = BenchmarkFinder.Find(_document.Text);
                _dirty = false;
            }

            return _found;
        }
    }

    public void Detach() => _benchmarks.Changed -= OnBenchmarksChanged;

    private void OnBenchmarksChanged(object? sender, EventArgs e)
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        Ui.Post(() =>
        {
            _refreshQueued = false;
            InvalidateMeasure();
            InvalidateVisual();
        });
    }

    protected override void OnDocumentChanged(TextDocument? oldDocument, TextDocument? newDocument)
    {
        if (oldDocument is not null)
        {
            oldDocument.TextChanged -= OnTextChanged;
        }

        base.OnDocumentChanged(oldDocument, newDocument);
        _document = newDocument;
        _dirty = true;
        if (newDocument is not null)
        {
            newDocument.TextChanged += OnTextChanged;
        }

        InvalidateMeasure();
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        _dirty = true;
        InvalidateVisual();
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

        InvalidateVisual();
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize) => new(Enabled ? Size : 0, 0);

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (TextView is not { VisualLinesValid: true } view)
        {
            return;
        }

        var glyphs = Glyphs;
        if (glyphs.Count == 0)
        {
            return;
        }

        var brush = this.TryFindResource(_benchmarks.IsRunning ? "Shell.Info" : "Shell.Warning", ActualThemeVariant, out var resource) && resource is IBrush b ? b : Brushes.DarkOrange;
        var pen = new Pen(brush, 1.5);
        var lines = glyphs.Select(g => g.Line).ToHashSet();
        foreach (var line in view.VisualLines)
        {
            if (!lines.Contains(line.FirstDocumentLine.LineNumber))
            {
                continue;
            }

            // A stopwatch: a dial with a hand and a button on top.
            var top = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineTop) - view.VerticalOffset;
            var height = line.TextLines[0].Height;
            var radius = Math.Min(10, height) / 2 - 1;
            var center = new Point(Size / 2, top + (height / 2) + 1);
            context.DrawEllipse(null, pen, center, radius, radius);
            context.DrawLine(pen, center, new Point(center.X + (radius * 0.6), center.Y - (radius * 0.6)));
            context.DrawLine(pen, new Point(center.X, center.Y - radius), new Point(center.X, center.Y - radius - 2));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (TextView is not { } view || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var y = e.GetPosition(view).Y + view.VerticalOffset;
        if (view.GetVisualLineFromVisualTop(y) is { } line && Glyphs.FirstOrDefault(g => g.Line == line.FirstDocumentLine.LineNumber) is { } benchmark)
        {
            e.Handled = true;
            _ = _run(_path(), benchmark);
        }
    }
}
