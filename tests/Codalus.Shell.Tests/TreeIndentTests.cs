using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.Tests;

/// <summary>
/// Every tree indents each level 12 px, the classic IDE's, so a deep tree stays compact horizontally: the flattened ones
/// (the Solution Explorer, Git, the Document Outline, the Test Explorer…) were 16 px a level, the WCF Test Client's and the
/// result lists' groups 18, and a <c>TreeView</c> (Options, Code Metrics, the Syntax Visualizer) took Fluent's 16.
/// </summary>
public class TreeIndentTests
{
    [Fact]
    public void Solution_explorer_rows_are_indented_12_px_a_level()
    {
        static ExplorerRowViewModel Row(int depth) => new(new ExplorerNode("id", ExplorerNodeKind.Folder, "Helpers"), depth, false);

        Assert.Equal(12, TreeMetrics.IndentPerLevel);
        Assert.Equal(new Thickness(4, 0, 0, 0), Row(0).Indent);
        Assert.Equal(new Thickness(4 + 12, 0, 0, 0), Row(1).Indent);
        Assert.Equal(new Thickness(4 + (5 * 12), 0, 0, 0), Row(5).Indent);
    }

    [AvaloniaFact]
    public void A_tree_view_indents_its_children_12_px()
    {
        var child = new TreeViewItem { Header = "Child" };
        var root = new TreeViewItem { Header = "Root", IsExpanded = true, ItemsSource = new[] { child } };
        var window = new Window { Width = 400, Height = 300, Content = new TreeView { ItemsSource = new[] { root } } };
        window.Show();
        try
        {
            window.UpdateLayout();
            static double HeaderX(TreeViewItem item, Visual window) =>
                item.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_HeaderPresenter").TranslatePoint(default, window)!.Value.X;

            Assert.Equal(12, HeaderX(child, window) - HeaderX(root, window), 1);
        }
        finally
        {
            window.Close();
        }
    }
}
