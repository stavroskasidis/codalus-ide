using Avalonia.Controls;
using Avalonia.Input;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

public sealed partial class ErrorListView : UserControl, IPaneView
{
    public ErrorListView()
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
        Scope.SelectionChanged += (_, _) =>
        {
            if (DataContext is ErrorListViewModel vm)
            {
                vm.CurrentDocumentOnly = Scope.SelectedIndex == 1;
            }
        };
    }

    private void OpenSelected()
    {
        if (DataContext is ErrorListViewModel vm && List.SelectedItem is ErrorListRow row)
        {
            _ = vm.OpenAsync(row);
        }
    }

    public void FocusPrimary() => List.Focus();
}
