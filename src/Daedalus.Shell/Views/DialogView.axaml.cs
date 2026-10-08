using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Daedalus.Shell.ViewModels;

namespace Daedalus.Shell.Views;

/// <summary>The in-window modal dialog: Enter chooses the default button, Escape cancels, Tab stays inside.</summary>
public sealed partial class DialogView : UserControl
{
    private DialogViewModel? _viewModel;

    public DialogView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.Opened -= OnOpened;
        }

        _viewModel = DataContext as DialogViewModel;
        if (_viewModel is not null)
        {
            _viewModel.Opened += OnOpened;
        }
    }

    private void OnOpened(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_viewModel is { HasInput: true } vm)
        {
            Input.Focus();
            var (start, length) = vm.Request?.InputSelection ?? (0, Input.Text?.Length ?? 0);
            Input.SelectionStart = start;
            Input.SelectionEnd = start + length;
        }
        else
        {
            this.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("accent"))?.Focus(NavigationMethod.Tab);
        }
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
