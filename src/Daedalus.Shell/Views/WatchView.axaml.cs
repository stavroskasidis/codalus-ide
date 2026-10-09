using Avalonia.Controls;
using Avalonia.Input;
using Daedalus.Shell.Controls;
using Daedalus.Shell.ViewModels;

namespace Daedalus.Shell.Views;

/// <summary>The Watch window: Enter in the box adds an expression; Delete removes the selected top-level watch.</summary>
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
    }

    public void FocusPrimary() => Input.Focus();
}
