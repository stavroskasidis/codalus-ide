using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// Report a Problem and Suggest a Feature (M51): the in-window dialog frame at its widest, 720 px (narrower only in a
/// window too small for that). The focus starts in the title box; Escape (or the header's close button) cancels, Tab
/// stays inside. Enter makes new lines in the description and the steps, so it opens nothing: Open in Browser is a click.
/// </summary>
public sealed partial class FeedbackView : UserControl
{
    private const double FrameWidth = 720;
    private const double WindowMargin = 32;

    private FeedbackViewModel? _viewModel;

    public FeedbackView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? FrameWidth : Math.Min(FrameWidth, Math.Max(0, availableSize.Width - WindowMargin));
        if (!Frame.Width.Equals(width))
        {
            Frame.Width = width;
        }

        return base.MeasureOverride(availableSize);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.Opened -= OnOpened;
        }

        _viewModel = DataContext as FeedbackViewModel;
        if (_viewModel is not null)
        {
            _viewModel.Opened += OnOpened;
        }
    }

    private void OnOpened(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_viewModel is { IsOpen: true })
        {
            TitleBox.Focus(NavigationMethod.Tab);
        }
    }, DispatcherPriority.Loaded);

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is { IsOpen: true } vm && e.Key == Key.Escape)
        {
            vm.Cancel();
            e.Handled = true;
        }
    }
}
