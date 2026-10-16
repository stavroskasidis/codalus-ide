using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinUIApp.Controls;

/// <summary>A user control with two dependency properties: the rating and its maximum.</summary>
public sealed partial class RatingControl : UserControl
{
    /// <summary>The rating, from 0 to <see cref="Maximum"/>.</summary>
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(RatingControl), new PropertyMetadata(0d));

    /// <summary>The highest rating.</summary>
    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(RatingControl), new PropertyMetadata(5d));

    public RatingControl() => InitializeComponent();

    /// <summary>The rating, from 0 to <see cref="Maximum"/>.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>The highest rating.</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }
}
