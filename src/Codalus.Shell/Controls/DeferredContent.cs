using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Codalus.Shell.Controls;

/// <summary>
/// A row of a long list of rows of different heights in a plain stack panel, which builds its controls from
/// <see cref="ContentTemplate"/> when it comes near the view, during that layout, and otherwise in the background soon
/// after, a few milliseconds at a time. Until then it stands in at the height its template's rows have so far.
/// </summary>
/// <remarks>
/// Options' All Settings is 177 rows from 60 to 550 px high. Making them all at once stalled the UI for seconds, and a
/// virtualizing stack panel, which makes only those in view, estimates the list's height from the rows it has made: the
/// estimate went from 16,700 to 41,200 px and back while it scrolled, so the scroll bar's thumb ran away from the pointer
/// that dragged it (issue 355). A list of these rows is its real height once its rows are made, and stays so. Each row is
/// an anchor of its scroll viewer, so a row above the view that is made, and grows or shrinks, doesn't move what's shown.
/// </remarks>
public sealed class DeferredContent : Decorator
{
    public static readonly StyledProperty<IDataTemplate?> ContentTemplateProperty =
        AvaloniaProperty.Register<DeferredContent, IDataTemplate?>(nameof(ContentTemplate));

    /// <summary>How long one background turn makes rows, so input and rendering go first.</summary>
    private static readonly TimeSpan s_slice = TimeSpan.FromMilliseconds(4);

    /// <summary>The rows waiting to be made, in the order they were attached (a list's order). UI thread only.</summary>
    private static readonly Queue<DeferredContent> s_pending = new();

    /// <summary>The height of each template's rows made so far, for the rows not made yet.</summary>
    private static readonly ConditionalWeakTable<IDataTemplate, Average> s_heights = [];

    private static bool s_scheduled;

    private IScrollAnchorProvider? _anchors;
    private bool _attached;
    private bool _counted;

    public DeferredContent() => EffectiveViewportChanged += OnEffectiveViewportChanged;

    /// <summary>The template of the row's controls, built for its data context.</summary>
    public IDataTemplate? ContentTemplate
    {
        get => GetValue(ContentTemplateProperty);
        set => SetValue(ContentTemplateProperty, value);
    }

    /// <summary>Whether the row's controls are made.</summary>
    public bool IsBuilt { get; private set; }

    /// <summary>The height a row not made yet stands in at: the average of its template's rows made so far.</summary>
    private static double EstimatedHeight(IDataTemplate? template) =>
        template is not null && s_heights.TryGetValue(template, out var average) && average.Count > 0 ? average.Total / average.Count : 100;

    private void Build()
    {
        if (IsBuilt)
        {
            return;
        }

        IsBuilt = true;
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        Child = ContentTemplate?.Build(DataContext);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!IsBuilt)
        {
            return new Size(0, EstimatedHeight(ContentTemplate));
        }

        var size = base.MeasureOverride(availableSize);
        if (!_counted && Child is not null && ContentTemplate is { } template && size.Height > 0)
        {
            _counted = true;
            var average = s_heights.GetOrCreateValue(template);
            average.Total += size.Height;
            average.Count++;
        }

        return size;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _anchors = this.FindAncestorOfType<IScrollAnchorProvider>();
        _anchors?.RegisterAnchorCandidate(this);
        if (!IsBuilt)
        {
            s_pending.Enqueue(this);
            Schedule();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _anchors?.UnregisterAnchorCandidate(this);
        _anchors = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// A row near the view is made at once, in this layout: the layout runs again before the frame is drawn, so no gap
    /// shows where it was. Near is within one view's height above or below.
    /// </summary>
    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        var view = e.EffectiveViewport;
        if (view.Width <= 0 && view.Height <= 0)
        {
            return;
        }

        var near = new Rect(view.X, view.Y - view.Height, Math.Max(view.Width, 1), view.Height * 3);
        if (near.Intersects(new Rect(0, 0, Math.Max(Bounds.Width, 1), Math.Max(Bounds.Height, 1))))
        {
            Build();
        }
    }

    private static void Schedule()
    {
        if (s_scheduled)
        {
            return;
        }

        s_scheduled = true;
        Dispatcher.UIThread.Post(BuildSome, DispatcherPriority.Background);
    }

    /// <summary>Makes the waiting rows for one slice, and comes back for the rest after input and the next frame.</summary>
    private static void BuildSome()
    {
        s_scheduled = false;
        var started = Stopwatch.GetTimestamp();
        while (s_pending.TryDequeue(out var row))
        {
            // A row taken off the list (another page, the search) is dropped; it queues again if it comes back.
            if (!row._attached || row.IsBuilt)
            {
                continue;
            }

            row.Build();
            if (Stopwatch.GetElapsedTime(started) >= s_slice)
            {
                break;
            }
        }

        if (s_pending.Count > 0)
        {
            Schedule();
        }
    }

    private sealed class Average
    {
        public double Total;
        public int Count;
    }
}
