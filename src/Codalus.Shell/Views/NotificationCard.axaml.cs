using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>One notification: a floating toast, or a flat entry in the Notifications window.</summary>
public sealed partial class NotificationCard : UserControl
{
    public static readonly StyledProperty<bool> FloatingProperty = AvaloniaProperty.Register<NotificationCard, bool>(nameof(Floating));

    public NotificationCard()
    {
        InitializeComponent();
        UpdateCardStyle();

        // A toast doesn't hide under the pointer, nor at all once the user clicked in it (selecting its text, say) or tabbed
        // into it; the buttons handle their own presses, so the press is heard in the tunnel.
        PointerEntered += (_, _) => Toast?.IsHovered = true;
        PointerExited += (_, _) => Toast?.IsHovered = false;
        AddHandler(PointerPressedEvent, (_, _) => Toast?.IsPinned = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(GotFocusEvent, (_, _) => Toast?.IsPinned = true, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private NotificationViewModel? Toast => Floating ? DataContext as NotificationViewModel : null;

    /// <summary>Toasts float over the layout with a shadow; entries in the Notifications window are flat.</summary>
    public bool Floating
    {
        get => GetValue(FloatingProperty);
        set => SetValue(FloatingProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FloatingProperty)
        {
            UpdateCardStyle();
        }
    }

    private void UpdateCardStyle() => Card.Classes.Set("overlay", Floating);
}
