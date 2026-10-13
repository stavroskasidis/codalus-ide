using Avalonia;
using Avalonia.Controls;

namespace Codalus.Shell.Views;

/// <summary>One notification: a floating toast, or a flat entry in the Notifications window.</summary>
public sealed partial class NotificationCard : UserControl
{
    public static readonly StyledProperty<bool> FloatingProperty = AvaloniaProperty.Register<NotificationCard, bool>(nameof(Floating));

    public NotificationCard()
    {
        InitializeComponent();
        UpdateCardStyle();
    }

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
