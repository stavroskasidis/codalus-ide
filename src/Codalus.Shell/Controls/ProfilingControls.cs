using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Codalus.Shell.Controls;

/// <summary>
/// A thread's states over time (the report's Threads tab, ADR 0046): one cell per bucket of the trace, colored by
/// state (<c>R</c> running managed code, <c>W</c> waiting or in native code, <c>C</c> blocked on a lock, a space when
/// the thread wasn't seen). The row's text says the same for screen readers.
/// </summary>
public sealed class StateStrip : Control
{
    public static readonly StyledProperty<string?> StatesProperty = AvaloniaProperty.Register<StateStrip, string?>(nameof(States));

    static StateStrip() => AffectsRender<StateStrip>(StatesProperty);

    public string? States
    {
        get => GetValue(StatesProperty);
        set => SetValue(StatesProperty, value);
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var resource) && resource is IBrush brush ? brush : fallback;

    public override void Render(DrawingContext context)
    {
        var states = States;
        if (string.IsNullOrEmpty(states) || Bounds.Width <= 0)
        {
            return;
        }

        var running = Brush("Shell.Diff.Added", Brushes.SeaGreen);
        var waiting = Brush("Shell.Border", Brushes.LightGray);
        var blocked = Brush("Shell.Error", Brushes.IndianRed);
        var width = Bounds.Width / states.Length;
        var height = Bounds.Height;
        for (var i = 0; i < states.Length; i++)
        {
            var brush = states[i] switch
            {
                'R' => running,
                'W' => waiting,
                'C' => blocked,
                _ => null,
            };
            if (brush is not null)
            {
                context.FillRectangle(brush, new Rect(i * width, 2, Math.Max(width, 1), Math.Max(1, height - 4)));
            }
        }
    }
}

/// <summary>
/// A bar on a shared time axis (the .NET Async and Activities tabs): from <see cref="Start"/> for <see cref="Length"/>,
/// both fractions of the trace (0 to 1), at least a pixel wide.
/// </summary>
public sealed class SpanBar : Control
{
    public static readonly StyledProperty<double> StartProperty = AvaloniaProperty.Register<SpanBar, double>(nameof(Start));

    public static readonly StyledProperty<double> LengthProperty = AvaloniaProperty.Register<SpanBar, double>(nameof(Length));

    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<SpanBar, IBrush?>(nameof(Fill));

    static SpanBar() => AffectsRender<SpanBar>(StartProperty, LengthProperty, FillProperty);

    public double Start
    {
        get => GetValue(StartProperty);
        set => SetValue(StartProperty, value);
    }

    public double Length
    {
        get => GetValue(LengthProperty);
        set => SetValue(LengthProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0)
        {
            return;
        }

        var track = this.TryFindResource("Shell.Border", ActualThemeVariant, out var resource) && resource is IBrush border ? border : Brushes.LightGray;
        context.FillRectangle(track, new Rect(0, (Bounds.Height / 2) - 0.5, Bounds.Width, 1));
        var x = Math.Clamp(Start, 0, 1) * Bounds.Width;
        var width = Math.Max(1, Math.Clamp(Length, 0, 1) * Bounds.Width);
        context.FillRectangle(Fill ?? Brushes.SteelBlue, new Rect(Math.Min(x, Bounds.Width - 1), 3, width, Math.Max(1, Bounds.Height - 6)));
    }
}
