using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// The main window's notifications, palette and in-window dialogs (the dialog frame, Connect to an agent, Report a
/// Problem), drawn in a window that covers the main window: the setup and the start window, which the main window owns,
/// so they stay above it. What opened while one of them was open was drawn in the main window, under it: the .NET
/// install's choice from the setup's Tools step, Connect after the setup with the start window open, a notification.
/// The window on top draws them (<see cref="ShellHost.OverlayWindow"/>); the main window's own stay hidden meanwhile.
/// Each layer is made the first time it shows, as the main window's Connect and Report a Problem are (M50).
/// </summary>
internal sealed class CompanionOverlays : Panel
{
    private readonly ShellHost _shell;
    private readonly Window _window;
    private readonly ItemsControl _toasts;
    private Panel? _connectLayer;
    private Panel? _feedbackLayer;
    private Panel? _dialogLayer;
    private Panel? _paletteLayer;
    private Panel? _terminalLayer;
    private QuickPickView? _palette;
    private IInputElement? _focusBefore;
    private bool _wasModal;
    private IInputElement? _focusBeforeCard;
    private bool _cardWasOpen;

    private CompanionOverlays(ShellHost shell, Window window, Thickness toastMargin)
    {
        _shell = shell;
        _window = window;
        _toasts = new ItemsControl
        {
            ItemsSource = shell.Notifications.Toasts,
            ItemTemplate = new FuncDataTemplate<NotificationViewModel>((_, _) => new NotificationCard { Margin = new Thickness(0, 8, 0, 0), Floating = true }),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Width = 440,
            Margin = toastMargin,
        };
        Children.Add(_toasts);
    }

    /// <summary>Whether this window draws the overlays now: it is the one on top.</summary>
    public bool IsHost => ReferenceEquals(_shell.OverlayWindow, _window);

    /// <summary>Whether a dialog or the palette is open in this window: its own keys (Escape, Alt+Right…) wait.</summary>
    public bool IsModalOpen => IsHost && IsAnyModalOpen;

    private bool IsAnyModalOpen => _shell.Dialogs.IsOpen || _shell.ConnectAgent.IsOpen || _shell.Feedback.IsOpen || _shell.Palette.IsOpen || _shell.TerminalCard.IsOpen;

    /// <summary>
    /// Puts the overlays over <paramref name="window"/>'s content (before it's shown). <paramref name="toastMargin"/>
    /// keeps the notifications off the window's own buttons in its bottom right corner.
    /// </summary>
    public static CompanionOverlays Attach(ShellHost shell, Window window, Thickness toastMargin)
    {
        var overlays = new CompanionOverlays(shell, window, toastMargin);
        var content = window.Content as Control;
        window.Content = null;
        var root = new Panel();
        if (content is not null)
        {
            root.Children.Add(content);
        }

        root.Children.Add(overlays);
        window.Content = root;

        PropertyChangedEventHandler changed = (_, e) =>
        {
            if (e.PropertyName == "IsOpen")
            {
                overlays.Update();
            }
        };
        EventHandler moved = (_, _) => overlays.Update();
        shell.Dialogs.PropertyChanged += changed;
        shell.ConnectAgent.PropertyChanged += changed;
        shell.Feedback.PropertyChanged += changed;
        shell.Palette.PropertyChanged += changed;
        shell.TerminalCard.PropertyChanged += changed;
        shell.OverlayWindowChanged += moved;
        window.Closed += (_, _) =>
        {
            shell.Dialogs.PropertyChanged -= changed;
            shell.ConnectAgent.PropertyChanged -= changed;
            shell.Feedback.PropertyChanged -= changed;
            shell.Palette.PropertyChanged -= changed;
            shell.TerminalCard.PropertyChanged -= changed;
            shell.OverlayWindowChanged -= moved;
            overlays.Detach();
        };
        overlays.Update();
        return overlays;
    }

    /// <summary>
    /// Lets go of the shell's view models when the window closes: each view subscribed to its own, which would keep the
    /// closed window alive and its views answering every later dialog.
    /// </summary>
    private void Detach()
    {
        _toasts.ItemsSource = null;
        foreach (var layer in new[] { _connectLayer, _feedbackLayer, _dialogLayer, _paletteLayer, _terminalLayer })
        {
            if (layer is { Children: [Control view] })
            {
                view.DataContext = null;
            }
        }
    }

    /// <summary>Shows the layers that are open, when this window is on top; hides them all when it isn't.</summary>
    private void Update()
    {
        var host = IsHost;
        _toasts.IsVisible = host;
        Show(ref _connectLayer, host && _shell.ConnectAgent.IsOpen, () => new ConnectAgentView { DataContext = _shell.ConnectAgent }, scrim: true, zIndex: 2);
        Show(ref _feedbackLayer, host && _shell.Feedback.IsOpen, () => new FeedbackView { DataContext = _shell.Feedback }, scrim: true, zIndex: 3);
        Show(ref _dialogLayer, host && _shell.Dialogs.IsOpen, () => new DialogView { DataContext = _shell.Dialogs }, scrim: true, zIndex: 4);
        // The terminal card over all of them: the command it shows may be waiting for a password (an install from the setup).
        Show(ref _terminalLayer, host && _shell.TerminalCard.IsOpen, () => new TerminalCardView { DataContext = _shell.TerminalCard }, scrim: true, zIndex: 5);
        var paletteOpened = Show(ref _paletteLayer, host && _shell.Palette.IsOpen, () => _palette = new QuickPickView
        {
            DataContext = _shell.Palette,
            Width = 640,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0),
        }, scrim: false, zIndex: 1);

        // The focus goes into what opened (the dialog and Connect move it themselves when they open, the palette here),
        // and back to where it was in this window when the last one closes.
        // The terminal card over a dialog that stays open (Connect): the focus back in that dialog when the card closes.
        var card = host && _shell.TerminalCard.IsOpen;
        if (card && !_cardWasOpen)
        {
            _focusBeforeCard = _window.FocusManager?.GetFocusedElement();
        }
        else if (!card && _cardWasOpen)
        {
            if (_focusBeforeCard is Visual before && TopLevel.GetTopLevel(before) == _window)
            {
                _focusBeforeCard.Focus();
            }

            _focusBeforeCard = null;
        }

        _cardWasOpen = card;
        var modal = host && IsAnyModalOpen;
        if (modal && !_wasModal)
        {
            _focusBefore = _window.FocusManager?.GetFocusedElement();
        }
        else if (!modal && _wasModal)
        {
            if (_focusBefore is Visual visual && TopLevel.GetTopLevel(visual) == _window)
            {
                _focusBefore.Focus();
            }

            _focusBefore = null;
        }

        _wasModal = modal;
        if (paletteOpened && _palette is { } palette)
        {
            Dispatcher.UIThread.Post(palette.FocusInput, DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// Shows or hides a layer, making it the first time it shows. True when it just showed. <paramref name="zIndex"/>: the
    /// main window's order, the notifications under the palette, Connect under the dialog frame (its confirmations go over it).
    /// </summary>
    private bool Show(ref Panel? layer, bool visible, Func<Control> view, bool scrim, int zIndex)
    {
        if (layer is null)
        {
            if (!visible)
            {
                return false;
            }

            var made = view();
            if (made.HorizontalAlignment == HorizontalAlignment.Stretch)
            {
                made.HorizontalAlignment = HorizontalAlignment.Center;
            }

            if (made.VerticalAlignment == VerticalAlignment.Stretch)
            {
                made.VerticalAlignment = VerticalAlignment.Center;
            }

            layer = new Panel { IsVisible = false };
            if (scrim)
            {
                layer.Bind(BackgroundProperty, layer.GetResourceObservable("Shell.Dialog.Scrim"));
            }
            else
            {
                // A click outside the palette closes it, as in the main window.
                layer.Background = Avalonia.Media.Brushes.Transparent;
                var owner = layer;
                layer.PointerPressed += (_, e) =>
                {
                    if (e.Source == owner)
                    {
                        _shell.Palette.Cancel();
                    }
                };
            }

            layer.ZIndex = zIndex;
            layer.Children.Add(made);
            Children.Add(layer);
        }

        var shown = visible && !layer.IsVisible;
        layer.IsVisible = visible;
        return shown;
    }
}
