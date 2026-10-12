using Avalonia.Controls;
using Avalonia.Input;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>The XAML Previewer (M19a): a click selects the element in the XAML; the pointer's moves go to the preview (hover effects).</summary>
public sealed partial class XamlPreviewView : UserControl
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
        Scroller.SizeChanged += (_, _) => Fit();
        DataContextChanged += (_, _) =>
        {
            if (_model is { } old)
            {
                old.PropertyChanged -= OnModelChanged;
                old.FitRequested -= OnFitRequested;
            }

            _model = DataContext as XamlPreviewViewModel;
            if (_model is { } model)
            {
                model.PropertyChanged += OnModelChanged;
                model.FitRequested += OnFitRequested;
            }
        };
    }

    private XamlPreviewViewModel? _model;

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(XamlPreviewViewModel.FrameCount))
        {
            Fit();
        }
    }

    private void OnFitRequested(object? sender, EventArgs e) => Fit();

    /// <summary>Fits the frame to the pane while the zoom follows it (the surface's margin aside).</summary>
    private void Fit() => _model?.FitTo(Scroller.Bounds.Width - 24, Scroller.Bounds.Height - 24);

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
