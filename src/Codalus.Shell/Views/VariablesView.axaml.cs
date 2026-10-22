using Avalonia.Controls;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views.DataTips;

namespace Codalus.Shell.Views;

/// <summary>
/// Locals and Autos: the data tip's rows in one flat list (D44, decision 9). F2 or a double-click on a value edits it in
/// place; the context menu copies, watches and visualizes it.
/// </summary>
public sealed partial class VariablesView : UserControl, IPaneView
{
    public VariablesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is VariablesViewModel vm)
            {
                Rows.Attach(vm.Roots, new VariableRowActions { SetValue = vm.SetValueAsync, Reason = vm.ReadOnlyReason }, vm.Expansion);
            }
        };
    }

    public void FocusPrimary() => Rows.FocusRow();
}
