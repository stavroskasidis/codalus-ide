using Avalonia.Controls;
using Avalonia.Input;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>The XAML Previewer (M19a): a click selects the element in the XAML; the pointer's moves go to the preview (hover effects).</summary>
public partial class XamlPreviewView : UserControl
{
    private long _lastMove;

    public XamlPreviewView()
    {
        InitializeComponent();
        Surface.PointerPressed += (_, e) =>
        {
            if (DataContext is XamlPreviewViewModel vm && e.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed)
            {
                var point = e.GetPosition(Surface);
                _ = vm.ClickAsync(point.X / vm.Zoom, point.Y / vm.Zoom);
                e.Handled = true;
            }
        };
        Surface.PointerMoved += OnPointerMoved;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        // At most 30 a second: hover effects, not a stream.
        var now = Environment.TickCount64;
        if (now - _lastMove < 33 || DataContext is not XamlPreviewViewModel vm)
        {
            return;
        }

        _lastMove = now;
        var point = e.GetPosition(Surface);
        _ = vm.MoveAsync(point.X / vm.Zoom, point.Y / vm.Zoom);
    }
}
