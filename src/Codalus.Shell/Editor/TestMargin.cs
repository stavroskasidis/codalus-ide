using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Controls;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Testing;

namespace Codalus.Shell.Editor;

/// <summary>
/// The test margin (ADR 0010): a glyph on the first line of each test and test class in the document, showing its last
/// outcome (shape and color: passed ✓, failed ✗, skipped –, running, not run ○). Clicking a glyph offers Run, Debug,
/// Run with Coverage and Show in Test Explorer. Tests that only a runner knows (no source location) have no glyph.
/// </summary>
public sealed class TestMargin : AbstractMargin
{
    private const double Size = 16;

    private readonly Func<string> _path;
    private readonly TestService _tests;
    private readonly Action<TestNode, Control> _onClick;
    private Dictionary<int, TestNode> _byLine = [];
    private bool _refreshQueued;
    private long _version = -1; // the document's version (TestService.DocumentVersion) the glyphs show
    private bool _changedWhileHidden;

    public TestMargin(Func<string> path, TestService tests, Action<TestNode, Control> onClick)
    {
        _path = path;
        _tests = tests;
        _onClick = onClick;
        Cursor = new Cursor(StandardCursorType.Hand);
        tests.Changed += OnTestsChanged;
        Refresh();
    }

    /// <summary>The glyphs by one-based line (tests).</summary>
    internal IReadOnlyDictionary<int, TestNode> Glyphs => _byLine;

    /// <summary>How many times the glyphs were found again (tests).</summary>
    internal int Refreshes { get; private set; }

    /// <summary>Stops listening to the tests (the editor closed).</summary>
    public void Detach() => _tests.Changed -= OnTestsChanged;

    private void OnTestsChanged(object? sender, EventArgs e)
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        Ui.Post(() =>
        {
            _refreshQueued = false;
            RefreshIfChanged();
        });
    }

    /// <summary>
    /// The glyphs are found again when the document's tests changed, and only while the editor shows: a hidden one
    /// catches up when it's shown (its visual lines are built again). Every editor, hidden tabs too, looked through every
    /// test of the solution at each change of any (M56).
    /// </summary>
    private void RefreshIfChanged()
    {
        if (TextView is not { IsEffectivelyVisible: true } view || TopLevel.GetTopLevel(view) is null)
        {
            _changedWhileHidden = true;
            return;
        }

        _changedWhileHidden = false;
        if (_tests.DocumentVersion(DocumentUris.FromPath(_path())) != _version)
        {
            Refresh();
        }
    }

    public void Refresh()
    {
        var uri = DocumentUris.FromPath(_path());
        _version = _tests.DocumentVersion(uri);
        Refreshes++;
        var byLine = new Dictionary<int, TestNode>();
        foreach (var node in _tests.InDocument(uri).OrderBy(n => n.Range!.EndLine - n.Range.StartLine))
        {
            // The innermost item wins a line (a method over its class when both start there).
            byLine.TryAdd(node.Range!.StartLine + 1, node);
        }

        _byLine = byLine;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(_byLine.Count == 0 ? 0 : Size, 0);

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

    private void OnVisualLinesChanged(object? sender, EventArgs e)
    {
        if (_changedWhileHidden && !_refreshQueued)
        {
            // Shown again: after this layout pass (the glyphs can change the margin's width).
            _refreshQueued = true;
            Ui.Post(() =>
            {
                _refreshQueued = false;
                RefreshIfChanged();
            });
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (TextView is not { VisualLinesValid: true } view || _byLine.Count == 0)
        {
            return;
        }

        foreach (var line in view.VisualLines)
        {
            if (!_byLine.TryGetValue(line.FirstDocumentLine.LineNumber, out var node))
            {
                continue;
            }

            var outcome = _tests.Read(_ => node.IsLeaf ? node.Outcome : node.AggregateOutcome());
            if (IconSet.Get(ViewModels.TestRowViewModel.Icons(outcome)) is not { } geometry)
            {
                continue;
            }

            var top = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineTop) - view.VerticalOffset;
            var height = line.TextLines[0].Height;
            var scale = Math.Min(14, height) / 16;
            var y = top + ((height - (16 * scale)) / 2);
            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(1, y)))
            {
                context.DrawGeometry(null, new Pen(Brush(outcome), 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
            }
        }
    }

    private IBrush Brush(TestOutcome outcome)
    {
        var key = outcome switch
        {
            TestOutcome.Passed => "Shell.Diff.Added",
            TestOutcome.Failed or TestOutcome.Errored => "Shell.Error",
            TestOutcome.Skipped => "Shell.Warning",
            TestOutcome.Running or TestOutcome.Queued => "Shell.Info",
            _ => "Shell.MutedForeground",
        };
        return this.TryFindResource(key, ActualThemeVariant, out var resource) && resource is IBrush brush ? brush : Brushes.Gray;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (TextView is not { } view || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var position = e.GetPosition(view);
        if (view.GetVisualLineFromVisualTop(position.Y + view.VerticalOffset) is { } line
            && _byLine.TryGetValue(line.FirstDocumentLine.LineNumber, out var node))
        {
            e.Handled = true;
            _onClick(node, this);
        }
    }
}
