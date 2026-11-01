using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// The Git Repository document: a double-click or Enter on a branch checks it out (a pull request opens, a message's
/// action runs), on a group or folder expands or collapses it, and the rest of a tree's keys work as in Git Changes
/// (<see cref="TreeRows"/>); right-click, Shift+F10 or the Menu key opens a node's menu; F2 and Delete rename and delete a
/// branch; the history beside the tree is the selected ref's (<see cref="GitHistoryView"/>). The view tells its view
/// model whether it's shown: a document behind another tab reads nothing when git changes (M57).
/// </summary>
public sealed partial class GitRepositoryView : UserControl, IPaneView
{
    private GitRepositoryViewModel? _attached;

    public GitRepositoryView()
    {
        InitializeComponent();
        TreeRows.Attach(Tree, () => ViewModel, row =>
        {
            if (row is not GitRepositoryRow node)
            {
                return false;
            }

            Activate(node.Node);
            return true;
        });
        Tree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        Tree.ContextRequested += OnContextRequested;
        ShownTracker.Track(this); // reads git only while shown (M57)
        DataContextChanged += (_, _) => Attach(this.IsAttachedToVisualTree());

        // Back in view (a tab switch builds the view again, or shows it): the selected row in view, as it was left. A view
        // out of the tree hears nothing (Dock builds a document's view again, and the old one would stay subscribed).
        AttachedToVisualTree += (_, _) =>
        {
            Attach(listen: true);
            Dispatcher.UIThread.Post(RevealSelected, DispatcherPriority.Loaded);
        };
        DetachedFromVisualTree += (_, _) => Attach(listen: false);
    }

    private GitRepositoryViewModel? ViewModel => DataContext as GitRepositoryViewModel;

    public void FocusPrimary() => Tree.Focus();

    private void Attach(bool listen)
    {
        if (_attached is not null)
        {
            _attached.RevealRequested -= OnRevealRequested;
        }

        _attached = listen ? ViewModel : null;
        if (_attached is not null)
        {
            _attached.RevealRequested += OnRevealRequested;
        }
    }

    private void OnRevealRequested(GitRepositoryRow row) => TreeRows.Reveal(Tree, row, focus: Tree.IsKeyboardFocusWithin);

    private void RevealSelected()
    {
        if (ViewModel?.SelectedRow is { } row && this.IsAttachedToVisualTree())
        {
            Tree.ScrollIntoView(row);
        }
    }

    private static GitRepositoryNode? NodeAt(object? source) => (TreeRows.RowAt(source) as GitRepositoryRow)?.Node;

    private void Activate(GitRepositoryNode node) => _ = ViewModel?.ActivateAsync(node);

    /// <summary>The document's own keys (the tree's are <see cref="TreeRows"/>'): F2, Delete (Cmd+Backspace on macOS), Shift+F10 and the Menu key.</summary>
    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { Selected: { } node } vm)
        {
            return;
        }

        var mac = OperatingSystem.IsMacOS();
        switch (e.Key)
        {
            case Key.F2 when node.Branch is { IsRemote: false } && e.KeyModifiers == KeyModifiers.None:
                _ = vm.RunOnNodeAsync("git.renameBranch", node);
                break;
            case Key.Delete when node.Kind is GitRepositoryNodeKind.Branch or GitRepositoryNodeKind.Tag && !mac && e.KeyModifiers == KeyModifiers.None:
            case Key.Back when node.Kind is GitRepositoryNodeKind.Branch or GitRepositoryNodeKind.Tag && mac && e.KeyModifiers == KeyModifiers.Meta:
                _ = vm.RunOnNodeAsync(node.Tag is null ? "git.deleteBranch" : "git.deleteTag", node);
                break;
            case Key.F10 when e.KeyModifiers == KeyModifiers.Shift:
            case Key.Apps:
                OpenMenu(node, vm.SelectedRow is { } row ? Tree.ContainerFromItem(row) as Control ?? Tree : Tree, under: true);
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
        OpenMenu(node, (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) ?? (Control)Tree, under: !e.TryGetPosition(Tree, out _));
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
}
