using Avalonia.Controls;
using Avalonia.Input;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// The Breakpoints window: double-click or Enter opens the location; Delete removes the breakpoint; the context menu
/// edits labels. The toolbar runs shell commands (new function breakpoint, export, import, delete all).
/// </summary>
public sealed partial class BreakpointsView : UserControl, IPaneView
{
    /// <summary>Runs a command by id (set by the shell).</summary>
    public static Action<string>? RunCommand { get; set; }

    /// <summary>Asks for a row's labels (set by the shell, which owns the input dialog); null when cancelled.</summary>
    public static Func<BreakpointRow, Task<string?>>? AskLabels { get; set; }

    public BreakpointsView()
    {
        InitializeComponent();
        BreakpointsViewModel? Vm() => DataContext as BreakpointsViewModel;
        NewFunction.Click += (_, _) => RunCommand?.Invoke("debug.newFunctionBreakpoint");
        Export.Click += (_, _) => RunCommand?.Invoke("debug.exportBreakpoints");
        Import.Click += (_, _) => RunCommand?.Invoke("debug.importBreakpoints");
        DeleteAll.Click += (_, _) => RunCommand?.Invoke("debug.deleteAllBreakpoints");
        EnableAll.Click += (_, _) => Vm()?.SetAllEnabled(true);
        DisableAll.Click += (_, _) => Vm()?.SetAllEnabled(false);
        List.DoubleTapped += (_, _) => Open();
        List.KeyDown += (_, e) =>
        {
            if (Vm() is not { } vm || List.SelectedItem is not BreakpointRow row)
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
        List.ContextRequested += (_, e) =>
        {
            if (Vm() is not { } vm || List.SelectedItem is not BreakpointRow row)
            {
                return;
            }

            var items = new List<Control>();
            if (row.Spec is not null)
            {
                items.Add(Item("Go To Source", () => _ = vm.OpenAsync(row)));
            }

            items.Add(Item(row.Enabled ? "Disable" : "Enable", () => row.Enabled = !row.Enabled));
            items.Add(Item("Edit Labels…", () => _ = EditLabelsAsync(vm, row)));
            items.Add(new Separator());
            items.Add(Item("Delete", () => vm.Remove(row)));
            new ContextMenu { ItemsSource = items }.Open(List);
            e.Handled = true;
        };
    }

    private static async Task EditLabelsAsync(BreakpointsViewModel vm, BreakpointRow row)
    {
        if (AskLabels is { } ask && await ask(row).ConfigureAwait(true) is { } labels)
        {
            vm.SetLabels(row, labels);
        }
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
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
