using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Codalus.Shell.Controls;

/// <summary>
/// Keeps a log view at its end while new output arrives (M34a): the Output window, the Debug Console, C# Interactive
/// and the container and Aspire logs. It follows while the user is at the end; scrolling up stops it, and scrolling
/// back to the end resumes it. It works on the control's own scroll viewer (a text editor's or a text box's), and
/// scrolls after layout, when the new extent is known (scrolling right after appending text reached the old end).
/// </summary>
public sealed class FollowTail
{
    private readonly Control _control;
    private ScrollViewer? _viewer;
    private bool _following = true;
    private bool _enabled = true;

    private FollowTail(Control control)
    {
        _control = control;
        control.LayoutUpdated += OnLayoutUpdated;
        if (control is AvaloniaEdit.TextEditor editor)
        {
            // A log's end is its last line at the bottom: scrolling below the document showed an empty view.
            editor.Options.AllowScrollBelowDocument = false;
        }

        if (control is TextBox box)
        {
            // A text box scrolls its caret into view after its text changes: keep the caret at the end too.
            box.TextChanged += (_, _) =>
            {
                if (_following && _enabled)
                {
                    box.CaretIndex = box.Text?.Length ?? 0;
                }
            };
        }
    }

    /// <summary>Follows the end of <paramref name="control"/>'s scroll viewer, once its template has one.</summary>
    public static FollowTail Attach(Control control) => new(control);

    /// <summary>Whether the view sticks to the end now (the user hasn't scrolled away).</summary>
    public bool IsFollowing => _following;

    /// <summary>False pauses following (Scroll Lock); true resumes it at the end.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (value)
            {
                Reset();
            }
        }
    }

    /// <summary>Follows again from the end (new content replaced the old, e.g. another Output channel).</summary>
    public void Reset()
    {
        _following = true;
        Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Loaded);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_viewer is not null || _control.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } viewer)
        {
            return;
        }

        _viewer = viewer;
        _control.LayoutUpdated -= OnLayoutUpdated;
        viewer.ScrollChanged += OnScrollChanged;
        ScrollToEnd();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)
        {
            // New output (or a resize): keep the end in view while following.
            if (_following && _enabled)
            {
                ScrollToEnd();
            }

            return;
        }

        // Moving up away from the end stops following; reaching the end (by any means) resumes it.
        if (AtEnd)
        {
            _following = true;
        }
        else if (e.OffsetDelta.Y < 0)
        {
            _following = false;
        }
    }

    private bool AtEnd => _viewer is not { } viewer || viewer.Offset.Y + viewer.Viewport.Height >= viewer.Extent.Height - 2;

    private void ScrollToEnd()
    {
        if (_viewer is { } viewer && _enabled && _following)
        {
            viewer.Offset = new Vector(viewer.Offset.X, Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height));
        }
    }
}
