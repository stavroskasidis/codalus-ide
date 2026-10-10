using Avalonia.Controls;
using Avalonia.Input;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>
/// The Watch window: Enter in the box adds an expression; Delete removes the selected top-level watch; F2 edits a value
/// (a watch with <c>setExpression</c>, a member with <c>setVariable</c>).
/// </summary>
public sealed partial class WatchView : UserControl, IPaneView
{
    public WatchView()
    {
        InitializeComponent();
        Input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is WatchViewModel vm)
            {
                _ = vm.AddAsync(vm.NewExpression);
                e.Handled = true;
            }
        };
        Tree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && DataContext is WatchViewModel vm && Tree.SelectedItem is VariableNode node && vm.Roots.Contains(node))
            {
                vm.Remove(node);
                e.Handled = true;
            }
        };
        VariableTreeSupport.Attach(
            Tree,
            (node, value) => DataContext is WatchViewModel vm ? vm.SetValueAsync(node, value) : Task.FromResult<string?>(null),
            node => !node.IsError && node.Value.Length > 0 && (node.EvaluateName is not null || node.ParentReference is not null));
    }

    public void FocusPrimary() => Input.Focus();
}
