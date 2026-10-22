using Avalonia.Controls;
using Avalonia.Input;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views.DataTips;

namespace Codalus.Shell.Views;

/// <summary>
/// The Watch window: Enter in the box adds an expression; Delete removes the selected top-level watch; F2 or a
/// double-click on a value edits it in place (a watch with <c>setExpression</c>, a member with <c>setVariable</c>).
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
        DataContextChanged += (_, _) =>
        {
            if (DataContext is WatchViewModel vm)
            {
                Rows.Attach(vm.Roots, new VariableRowActions
                {
                    SetValue = vm.SetValueAsync,
                    Reason = vm.ReadOnlyReason,
                    Remove = node =>
                    {
                        if (vm.Roots.Contains(node))
                        {
                            vm.Remove(node);
                        }
                    },
                }, vm.Expansion);
            }
        };
    }

    public void FocusPrimary() => Input.Focus();
}
