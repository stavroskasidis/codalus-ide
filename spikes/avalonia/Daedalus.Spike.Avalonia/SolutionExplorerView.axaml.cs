using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace Daedalus.Spike.Avalonia;

public sealed partial class SolutionExplorerView : UserControl
{
    public SolutionExplorerView()
    {
        AvaloniaXamlLoader.Load(this);
        var tree = this.FindControl<TreeView>("Tree")!;
        tree.DoubleTapped += (_, _) => OpenSelected(tree);
        tree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                OpenSelected(tree);
                e.Handled = true;
            }
        };
    }

    private void OpenSelected(TreeView tree)
    {
        if (DataContext is SolutionExplorerTool tool && tree.SelectedItem is SolutionNodeViewModel node)
        {
            tool.Open(node);
        }
    }
}
