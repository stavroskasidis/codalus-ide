using Avalonia.Controls;
using Avalonia.Input;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

public sealed partial class FindResultsView : UserControl, IPaneView
{
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
    }

    private void OpenSelected()
    {
        if (DataContext is FindResultsViewModel vm && List.SelectedItem is FindResultRow row)
        {
            _ = vm.OpenAsync(row);
        }
    }

    public void FocusPrimary() => List.Focus();
}
