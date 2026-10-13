using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>The Pull Requests window (ADR 0031): the list, and the selected one's description and review threads.</summary>
public sealed partial class PullRequestsView : UserControl, IPaneView
{
    private PullRequestsViewModel? _viewModel;

    public PullRequestsView()
    {
        InitializeComponent();
        List.DoubleTapped += (_, _) => _viewModel?.CheckOutCommand.Execute(null);
        List.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _viewModel is { HasSelection: true } vm)
            {
                vm.OpenInBrowserCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.DescriptionChanged -= OnDescriptionChanged;
        }

        _viewModel = DataContext as PullRequestsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.DescriptionChanged += OnDescriptionChanged;
            OnDescriptionChanged(this, EventArgs.Empty);
        }
    }

    private void OnDescriptionChanged(object? sender, EventArgs e)
    {
        if (_viewModel is { } vm)
        {
            DescriptionHost.Content = vm.HasDetails ? MarkdownRenderer.Render(vm.Description, vm.OpenLink) : null;
        }
    }

    private void OnOpenThread(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is { } vm && sender is Button { DataContext: ReviewThreadRow row })
        {
            vm.OpenThread(row);
        }
    }

    public void FocusPrimary() => List.Focus();
}
