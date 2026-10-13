using Avalonia.Controls;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>Locals and Autos. F2 (or double-click) edits a value; the context menu copies, watches and visualizes it.</summary>
public sealed partial class VariablesView : UserControl, IPaneView
{
    public VariablesView()
    {
        InitializeComponent();
        VariableTreeSupport.Attach(
            Tree,
            (node, value) => DataContext is VariablesViewModel vm ? vm.SetValueAsync(node, value) : Task.FromResult<string?>(null),
            node => node.ParentReference is not null && !node.IsError);
    }

    public void FocusPrimary() => Tree.Focus();
}
