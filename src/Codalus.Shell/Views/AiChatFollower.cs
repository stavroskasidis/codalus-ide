using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Codalus.Shell.Views;

/// <summary>
/// Keeps AI Chat's conversation at its end while an answer is written and new questions come (M34h), and leaves it
/// where the user scrolled to. Unlike a log view's <c>FollowTail</c>, it can't tell from the scroll deltas alone: the
/// turns are a virtualized list whose extent changes whenever turns are built or estimated, in the same layout pass as
/// a Page Up. So only the user leaves the end (the wheel up; Page Up, Up, Home or a scroll bar, which move the view
/// up), and reaching the end again, by any means, follows again.
/// </summary>
internal sealed class AiChatFollower
{
    private readonly ScrollViewer _viewer;
    private bool _following = true;

    /// <summary>A key went to the conversation; the scroll it causes, if any, is the user's (until the layout ran).</summary>
    private bool _keyScroll;

    /// <summary>The pointer is down on the conversation's scroll bar (a track click or the thumb dragged).</summary>
    private bool _barScroll;

    public AiChatFollower(ScrollViewer viewer)
    {
        _viewer = viewer;
        viewer.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
        viewer.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        viewer.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        viewer.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        viewer.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => _barScroll = false, RoutingStrategies.Bubble, handledEventsToo: true);
        viewer.ScrollChanged += OnScrollChanged;
    }

    /// <summary>Whether the view sticks to the end now (tests).</summary>
    public bool IsFollowing => _following;

    /// <summary>Follows again from the end (a new question, a cleared chat).</summary>
    public void Reset()
    {
        _following = true;
        Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Loaded);
    }

    private bool AtEnd => _viewer.Offset.Y + _viewer.Viewport.Height >= _viewer.Extent.Height - 2;

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y > 0 && _viewer.Offset.Y > 0)
        {
            _following = false;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.PageUp or Key.Up or Key.Home)
        {
            _keyScroll = true;
            Dispatcher.UIThread.Post(() => _keyScroll = false, DispatcherPriority.Background);
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<ScrollBar>(includeSelf: true) is { } bar && bar.TemplatedParent == _viewer)
        {
            _barScroll = true;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e) => _barScroll = false;

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_barScroll || (_keyScroll && e.OffsetDelta.Y < 0))
        {
            // The user moved the view: it follows only if they took it to the end.
            _following = AtEnd;
            return;
        }

        if (AtEnd)
        {
            _following = true;
        }
        else if (_following)
        {
            // New text, turns built or estimated, a resize: the end stays in view.
            ScrollToEnd();
        }
    }

    private void ScrollToEnd()
    {
        if (_following)
        {
            _viewer.Offset = new Vector(_viewer.Offset.X, Math.Max(0, _viewer.Extent.Height - _viewer.Viewport.Height));
        }
    }
}
