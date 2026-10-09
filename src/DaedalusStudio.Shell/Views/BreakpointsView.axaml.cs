using Avalonia.Controls;
using Avalonia.Input;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>The Breakpoints window: double-click or Enter opens the location; Delete removes the breakpoint.</summary>
public sealed partial class BreakpointsView : UserControl, IPaneView
{
    public BreakpointsView()
    {
        InitializeComponent();
        List.DoubleTapped += (_, _) => Open();
        List.KeyDown += (_, e) =>
        {
            if (DataContext is not BreakpointsViewModel vm || List.SelectedItem is not BreakpointRow row)
            {
                return;
            }

            if (e.Key == Key.Enter)
            {
                _ = vm.OpenAsync(row);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                vm.Remove(row);
                e.Handled = true;
            }
        };
    }

    private void Open()
    {
        if (DataContext is BreakpointsViewModel vm && List.SelectedItem is BreakpointRow row)
        {
            _ = vm.OpenAsync(row);
        }
    }

    public void FocusPrimary() => List.Focus();
}
