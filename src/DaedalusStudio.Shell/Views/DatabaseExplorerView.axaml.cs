using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>
/// The Database Explorer (ADR 0037). Double-click (or Enter) on a table or view shows its data; on a routine, a query
/// that calls it. The context menu comes from the menu registry (<see cref="ContextMenuLocation"/>, with the
/// <c>dataNodeKind</c> context key).
/// </summary>
public sealed partial class DatabaseExplorerView : UserControl, IPaneView
{
    public const string ContextMenuLocation = "dataExplorer/context";

    public DatabaseExplorerView()
    {
        InitializeComponent();
        Tree.DoubleTapped += (_, e) =>
        {
            if (e.Source is Visual visual && visual.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is DbNodeViewModel node)
            {
                Activated?.Invoke(this, node);
                Shell?.ActivateDataNode(node);
            }
        };
        Tree.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && Tree.SelectedItem is DbNodeViewModel node)
            {
                Activated?.Invoke(this, node);
                Shell?.ActivateDataNode(node);
                e.Handled = true;
            }
        }, RoutingStrategies.Bubble);
        Tree.ContextRequested += OnContextRequested;
    }

    private ShellHost? Shell => (TopLevel.GetTopLevel(this) as MainWindow)?.DataContext as ShellHost;

    /// <summary>A node was double-clicked or Enter was pressed on it (raised before the shell's handler).</summary>
    public event EventHandler<DbNodeViewModel>? Activated;

    public void FocusPrimary() => Tree.Focus();

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Shell is not { } shell || DataContext is not DatabaseExplorerViewModel vm)
        {
            return;
        }

        if (e.Source is Visual visual && visual.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is DbNodeViewModel node)
        {
            vm.Selected = node;
        }

        if (MenuBuilder.Context(shell, ContextMenuLocation) is not { } menu)
        {
            return;
        }

        menu.Open(Tree);
        e.Handled = true;
    }
}
