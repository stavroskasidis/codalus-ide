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
        List.Tapped += (_, e) =>
        {
            // A header toggles with one click.
            if (e.Source is Avalonia.StyledElement { DataContext: ErrorListGroup group } && DataContext is ErrorListViewModel vm)
            {
                var index = vm.Items.IndexOf(group);
                vm.ToggleGroup(group);
                List.SelectedIndex = index;
            }
        };
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
        if (DataContext is not ErrorListViewModel vm)
        {
            return;
        }

        if (List.SelectedItem is ErrorListRow row)
        {
            _ = vm.OpenAsync(row);
        }
        else if (List.SelectedItem is ErrorListGroup group)
        {
            var index = List.SelectedIndex;
            vm.ToggleGroup(group);
            List.SelectedIndex = index; // the header, rebuilt
        }
    }

    public void FocusPrimary() => List.Focus();
}
