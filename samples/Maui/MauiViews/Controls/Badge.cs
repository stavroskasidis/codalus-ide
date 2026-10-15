namespace MauiViews.Controls;

/// <summary>A count in a colored pill, with an attached property that highlights any view.</summary>
public class Badge : Label
{
    public static readonly BindableProperty CountProperty =
        BindableProperty.Create(nameof(Count), typeof(int), typeof(Badge), 0, propertyChanged: (b, _, n) => ((Badge)b).Text = n.ToString());

    public static readonly BindableProperty IsHighlightedProperty =
        BindableProperty.CreateAttached("IsHighlighted", typeof(bool), typeof(Badge), false);

    public int Count
    {
        get => (int)GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    public static bool GetIsHighlighted(BindableObject view) => (bool)view.GetValue(IsHighlightedProperty);

    public static void SetIsHighlighted(BindableObject view, bool value) => view.SetValue(IsHighlightedProperty, value);
}
