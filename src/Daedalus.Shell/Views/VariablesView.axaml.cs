using Avalonia.Controls;
using Avalonia.Input;
using Daedalus.Shell.Controls;
using Daedalus.Shell.ViewModels;

namespace Daedalus.Shell.Views;

/// <summary>Locals and Autos. F2 (or double-click) on a top-level variable edits its value.</summary>
public sealed partial class VariablesView : UserControl, IPaneView
{
    public VariablesView()
    {
        InitializeComponent();
        Tree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.F2)
            {
                EditSelected();
                e.Handled = true;
            }
        };
        Tree.DoubleTapped += (_, _) => EditSelected();
    }

    /// <summary>Asks for a new value (set by the shell, which owns the input dialog).</summary>
    public static Func<VariableNode, Task<string?>>? AskValue { get; set; }

    private void EditSelected()
    {
        if (DataContext is VariablesViewModel vm && Tree.SelectedItem is VariableNode { ParentReference: not null } node && AskValue is { } ask)
        {
            _ = EditAsync(vm, node, ask);
        }
    }

    private static async Task EditAsync(VariablesViewModel vm, VariableNode node, Func<VariableNode, Task<string?>> ask)
    {
        if (await ask(node).ConfigureAwait(true) is { } value && await vm.SetValueAsync(node, value).ConfigureAwait(true) is { } error)
        {
            node.Update(error, node.Type, 0);
        }
    }

    public void FocusPrimary() => Tree.Focus();
}
