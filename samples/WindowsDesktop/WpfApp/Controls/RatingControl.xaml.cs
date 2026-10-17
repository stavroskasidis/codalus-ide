using System.Windows;
using System.Windows.Controls;

namespace WpfApp.Controls;

/// <summary>A user control with two dependency properties: the rating and its maximum.</summary>
public partial class RatingControl : UserControl
{
    /// <summary>The rating, from 0 to <see cref="Maximum"/>.</summary>
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(int), typeof(RatingControl), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>The highest rating.</summary>
    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(int), typeof(RatingControl), new PropertyMetadata(5));

    public RatingControl() => InitializeComponent();

    /// <summary>The rating, from 0 to <see cref="Maximum"/>.</summary>
    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>The highest rating.</summary>
    public int Maximum
    {
        get => (int)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }
}

/// <summary>An attached property: whether an element is highlighted.</summary>
public static class Highlight
{
    /// <summary>Whether the element is highlighted.</summary>
    public static readonly DependencyProperty IsOnProperty =
        DependencyProperty.RegisterAttached("IsOn", typeof(bool), typeof(Highlight), new PropertyMetadata(false));

    public static bool GetIsOn(DependencyObject element) => (bool)element.GetValue(IsOnProperty);

    public static void SetIsOn(DependencyObject element, bool value) => element.SetValue(IsOnProperty, value);
}
