using Avalonia.Controls;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>A data tip: the hovered expression as a tree that expands and edits values (F2, double-click).</summary>
public sealed partial class DataTipView : UserControl
{
    public DataTipView()
    {
        InitializeComponent();
        VariableTreeSupport.Attach(
            Tree,
            (node, value) => DataContext is DataTipViewModel vm ? vm.SetValueAsync(node, value) : Task.FromResult<string?>(null),
            node => !node.IsError && (node.EvaluateName is not null || node.ParentReference is not null));
    }
}
