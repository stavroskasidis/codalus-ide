using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// Connect to <em>agent</em> (D43): the in-window dialog frame at its widest, 720 px (narrower only in a window too small
/// for that). Enter chooses the primary button when it's enabled, Escape (or the header's close button) cancels, Tab
/// stays inside; the focus goes to the first thing to do in each state (the chosen radio button, the key, the primary).
/// </summary>
public sealed partial class ConnectAgentView : UserControl
{
    private const double FrameWidth = 720;
    private const double WindowMargin = 32;

    private ConnectAgentViewModel? _viewModel;

    public ConnectAgentView()
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

        _viewModel = DataContext as ConnectAgentViewModel;
        if (_viewModel is not null)
        {
            _viewModel.Opened += OnOpened;
        }
    }

    private void OnOpened(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_viewModel is not { IsOpen: true } vm)
        {
            return;
        }

        Control? target = vm.ShowKey && vm.ShowForm ? KeyBox
            : vm.ShowForm ? this.GetVisualDescendants().OfType<RadioButton>().FirstOrDefault(r => r.GroupName == "signIn" && r.IsChecked == true)
            : vm.ShowInstall ? this.GetVisualDescendants().OfType<RadioButton>().FirstOrDefault(r => r.GroupName == "install" && r.IsChecked == true)
            : null;
        target ??= this.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("accent") && b.IsEnabled);
        (target ?? CloseButton).Focus(NavigationMethod.Tab);
    }, DispatcherPriority.Loaded);

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is not { IsOpen: true } vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                vm.Cancel();
                e.Handled = true;
                break;
            case Key.Enter when e.Source is not Button:
                vm.Accept();
                e.Handled = true;
                break;
        }
    }
}
