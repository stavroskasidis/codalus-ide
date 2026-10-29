using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// The Git Repository document: a double-click or Enter on a branch checks it out (a pull request opens, a message's
/// action runs); right-click, Shift+F10 or the Menu key opens a node's menu; F2 and Delete rename and delete a branch;
/// the links over the history fetch, pull, update, push and sync the selected branch.
/// </summary>
public sealed partial class GitRepositoryView : UserControl, IPaneView
{
    public GitRepositoryView()
    {
        InitializeComponent();
        Tree.DoubleTapped += (_, e) =>
        {
            if (NodeAt(e.Source) is { } node && node.Children.Count == 0)
            {
                Activate(node);
                e.Handled = true;
            }
        };
        Tree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        Tree.ContextRequested += OnContextRequested;
    }

    private GitRepositoryViewModel? ViewModel => DataContext as GitRepositoryViewModel;

    public void FocusPrimary() => Tree.Focus();

    private static GitRepositoryNode? NodeAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as GitRepositoryNode;

    private void Activate(GitRepositoryNode node) => _ = ViewModel?.ActivateAsync(node);

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { Selected: { } node } vm)
        {
            return;
        }

        var mac = OperatingSystem.IsMacOS();
        switch (e.Key)
        {
            case Key.Enter when node.Children.Count == 0:
                Activate(node);
                break;
            case Key.F2 when node.Branch is { IsRemote: false } && e.KeyModifiers == KeyModifiers.None:
                _ = vm.RunOnNodeAsync("git.renameBranch", node);
                break;
            case Key.Delete when node.Kind is GitRepositoryNodeKind.Branch or GitRepositoryNodeKind.Tag && !mac && e.KeyModifiers == KeyModifiers.None:
            case Key.Back when node.Kind is GitRepositoryNodeKind.Branch or GitRepositoryNodeKind.Tag && mac && e.KeyModifiers == KeyModifiers.Meta:
                _ = vm.RunOnNodeAsync(node.Tag is null ? "git.deleteBranch" : "git.deleteTag", node);
                break;
            case Key.F10 when e.KeyModifiers == KeyModifiers.Shift:
            case Key.Apps:
                OpenMenu(node, Tree.ContainerFromItem(node) as Control ?? Tree, under: true);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (ViewModel is not { } vm || (NodeAt(e.Source) ?? vm.Selected) is not { } node)
        {
            return;
        }

        vm.Selected = node;
        node.IsSelected = true;
        OpenMenu(node, (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true) ?? (Control)Tree, under: !e.TryGetPosition(Tree, out _));
        e.Handled = true;
    }

    private void OpenMenu(GitRepositoryNode node, Control anchor, bool under)
    {
        if (ViewModel?.MenuFor(node) is not { } menu)
        {
            return;
        }

        if (under)
        {
            menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        }

        menu.Open(anchor);
    }

    private void OnLink(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string command } && ViewModel is { } vm)
        {
            _ = vm.RunAsync(command);
        }
    }
}
