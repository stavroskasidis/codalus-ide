using Avalonia;
using Avalonia.Controls.Primitives;

namespace AvaloniaControls;

/// <summary>A row of stars: <see cref="Value"/> of <see cref="Maximum"/> filled.</summary>
public class RatingControl : TemplatedControl
{
    /// <summary>Defines the <see cref="Value"/> property.</summary>
    public static readonly StyledProperty<int> ValueProperty =
        AvaloniaProperty.Register<RatingControl, int>(nameof(Value));

    /// <summary>Defines the <see cref="Maximum"/> property.</summary>
    public static readonly StyledProperty<int> MaximumProperty =
        AvaloniaProperty.Register<RatingControl, int>(nameof(Maximum), 5);

    /// <summary>How many stars are filled.</summary>
    public int Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>How many stars there are.</summary>
    public int Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>The stars as text, for the template.</summary>
    public string Stars => new string('★', Math.Clamp(Value, 0, Maximum)) + new string('☆', Math.Max(0, Maximum - Value));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == MaximumProperty)
        {
            RaisePropertyChanged(StarsProperty, string.Empty, Stars);
        }
    }

    /// <summary>Defines the <see cref="Stars"/> property.</summary>
    public static readonly DirectProperty<RatingControl, string> StarsProperty =
        AvaloniaProperty.RegisterDirect<RatingControl, string>(nameof(Stars), o => o.Stars);
}
