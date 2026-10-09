using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

public sealed partial class FindResultsView : UserControl, IPaneView
{
    private FindResultsViewModel? _viewModel;

    public FindResultsView()
    {
        InitializeComponent();
        List.DoubleTapped += (_, _) => OpenSelected();
        List.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                OpenSelected();
                e.Handled = true;
            }
        };
        QueryBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _viewModel is { } vm)
            {
                _ = vm.SearchAsync();
                e.Handled = true;
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is { } old)
            {
                old.FocusRequested -= OnFocusRequested;
            }

            _viewModel = DataContext as FindResultsViewModel;
            if (_viewModel is { } vm)
            {
                vm.FocusRequested += OnFocusRequested;
            }
        };
    }

    private void OnFocusRequested(object? sender, EventArgs e)
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
    }

    private void OpenSelected()
    {
        if (_viewModel is { } vm && List.SelectedItem is FindResultRow row)
        {
            _ = vm.OpenAsync(row);
        }
    }

    public void FocusPrimary() => QueryBox.Focus();
}
