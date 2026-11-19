using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// The branch drop-down's content (#23, D38: B1-B26): the search box has the focus and Down moves into the list; a click
/// on a branch or Enter checks it out, as in the classic IDE's drop-down; a click on a folder, or Enter, expands or
/// collapses it, Right and Left expand and collapse it (with Ctrl, every folder under it too) and Left on a branch goes to
/// its folder; ⋯ (on the row under the pointer or selected), right-click, Shift+F10 and the Menu key open a branch's menu
/// over the drop-down, which stays open under it (B7); F2 and Delete in the list rename and delete (Backspace, Delete and
/// F2 edit the search text in the box); Escape closes it. As it opens, the current branch is scrolled into view.
/// </summary>
public sealed partial class GitBranchPickerView : UserControl
{
    private GitBranchPickerViewModel? _attached;

    public GitBranchPickerView()
    {
        InitializeComponent();
        List.ExpandRow = (row, expanded) => ViewModel?.SetExpanded(row, expanded);
        DataContextChanged += (_, _) =>
        {
            if (_attached is not null)
            {
                _attached.RevealRequested -= OnRevealRequested;
            }

            _attached = ViewModel;
            if (_attached is not null)
            {
                _attached.RevealRequested += OnRevealRequested;
            }
        };
        Search.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        List.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        List.ContextRequested += OnContextRequested;
        List.AddHandler(TappedEvent, OnTapped, RoutingStrategies.Bubble, handledEventsToo: false);
        Footer.Click += (_, _) => _ = ViewModel?.NewBranch?.Invoke(ViewModel.FooterName);
        ManageBranches.Click += (_, _) => _ = ViewModel?.ManageBranches?.Invoke();
        TryAgain.Click += (_, _) => _ = ViewModel?.LoadAsync();
        BannerAction.Click += (_, _) => _ = ViewModel?.RunBannerActionAsync();
    }

    private GitBranchPickerViewModel? ViewModel => DataContext as GitBranchPickerViewModel;

    /// <summary>A branch's menu is open over the drop-down: the drop-down stays open while it is (B7).</summary>
    public bool IsMenuOpen { get; private set; }

    /// <summary>Raised when the drop-down should close (Escape).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Puts the focus in the search box (as it opens).</summary>
    public void FocusSearch() => Dispatcher.UIThread.Post(() => Search.Focus(), DispatcherPriority.Input);

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                vm.MoveSelection(1);
                FocusSelected();
                e.Handled = true;
                break;
            case Key.Enter when vm.Selected?.Branch is { } branch:
                _ = vm.Checkout?.Invoke(branch);
                e.Handled = true;
                break;
            case Key.Enter when vm.Selected is { IsFolder: true } folder:
                vm.SetExpanded(folder, !folder.IsExpanded);
                e.Handled = true;
                break;
            case Key.Enter when vm.IsEmpty && vm.FooterName is not null:
                _ = vm.NewBranch?.Invoke(vm.FooterName);
                e.Handled = true;
                break;
            case Key.Escape:
                CloseRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var mac = OperatingSystem.IsMacOS();
        var branch = vm.Selected?.Branch;
        var folder = vm.Selected is { IsFolder: true } f ? f : null;
        var ctrl = e.KeyModifiers == KeyModifiers.Control;
        switch (e.Key)
        {
            case Key.Enter when branch is not null:
                _ = vm.Checkout?.Invoke(branch);
                break;
            case Key.Enter when folder is not null:
                Expand(folder, !folder.IsExpanded, under: ctrl);
                break;
            case Key.Right when folder is not null && (ctrl || !folder.IsExpanded):
                Expand(folder, true, under: ctrl);
                break;
            case Key.Left when folder is not null && (ctrl || folder.IsExpanded):
                Expand(folder, false, under: ctrl);
                break;
            case Key.Left when vm.Selected is { Depth: > 0 } row && e.KeyModifiers == KeyModifiers.None && ParentOf(vm, row) is { } parent:
                vm.Selected = parent;
                FocusSelected();
                break;
            case Key.Escape:
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
            case Key.Up when vm.Rows.FirstOrDefault(r => r.IsSelectable) == vm.Selected:
                Search.Focus();
                break;
            case Key.Up:
                vm.MoveSelection(-1);
                FocusSelected();
                break;
            case Key.Down:
                vm.MoveSelection(1);
                FocusSelected();
                break;
            case Key.F2 when branch is not null && e.KeyModifiers == KeyModifiers.None:
                _ = vm.RunOnBranch?.Invoke("git.renameBranch", branch);
                break;
            case Key.Delete when branch is not null && !mac && e.KeyModifiers == KeyModifiers.None:
            case Key.Back when branch is not null && mac && e.KeyModifiers == KeyModifiers.Meta:
                _ = vm.RunOnBranch?.Invoke("git.deleteBranch", branch!);
                break;
            case Key.F10 when e.KeyModifiers == KeyModifiers.Shift && branch is not null:
            case Key.Apps when branch is not null:
                OpenMenu(branch, ContainerOf(vm.Selected) ?? (Control)List, under: true);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Expands or collapses a folder (with <paramref name="under"/>, every folder under it), the focus kept on its row.</summary>
    private void Expand(GitBranchRow folder, bool expanded, bool under)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        if (under)
        {
            vm.SetExpandedUnder(folder, expanded);
        }
        else
        {
            vm.SetExpanded(folder, expanded);
        }

        FocusSelected();
    }

    /// <summary>The folder a row is in: the row above it one level up.</summary>
    private static GitBranchRow? ParentOf(GitBranchPickerViewModel vm, GitBranchRow row)
    {
        for (var i = vm.Rows.IndexOf(row) - 1; i >= 0; i--)
        {
            if (vm.Rows[i].Depth < row.Depth)
            {
                return vm.Rows[i].IsFolder ? vm.Rows[i] : null;
            }
        }

        return null;
    }

    /// <summary>The current branch's row as the drop-down opens: in view (in the tree, it can be under many folders).</summary>
    private void OnRevealRequested(GitBranchRow row) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (ViewModel?.Rows.Contains(row) == true)
            {
                List.ScrollIntoView(row);
            }
        }, DispatcherPriority.Loaded);

    private void FocusSelected()
    {
        if (ViewModel?.Selected is { } row)
        {
            List.ScrollIntoView(row);
            Dispatcher.UIThread.Post(() => ContainerOf(row)?.Focus(), DispatcherPriority.Input);
        }
    }

    private ListBoxItem? ContainerOf(GitBranchRow? row) => row is null ? null : List.ContainerFromItem(row) as ListBoxItem;

    private static GitBranchRow? RowAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as GitBranchRow;

    /// <summary>A click on a branch checks it out (question 6), not a click on its ⋯; on a folder, it expands or collapses it.</summary>
    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null || ViewModel is not { } vm)
        {
            return;
        }

        switch (RowAt(e.Source))
        {
            case { Branch: { } branch }:
                _ = vm.Checkout?.Invoke(branch);
                e.Handled = true;
                break;
            case { IsFolder: true } folder:
                vm.Selected = folder;
                Expand(folder, !folder.IsExpanded, under: false);
                e.Handled = true;
                break;
        }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (ViewModel is not { } vm || (RowAt(e.Source) ?? vm.Selected) is not { Branch: { } branch } row)
        {
            return;
        }

        vm.Selected = row;

        // From the pointer, at the pointer; from the keyboard (Shift+F10, the Menu key), under the row.
        OpenMenu(branch, ContainerOf(row) ?? (Control)List, under: !e.TryGetPosition(List, out _));
        e.Handled = true;
    }

    /// <summary>⋯ on a row: its menu under the button, its right edge on the button's.</summary>
    private void OnMore(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: GitBranchRow { Branch: { } branch } row } button && ViewModel is { } vm)
        {
            vm.Selected = row;
            OpenMenu(branch, button, under: true);
            e.Handled = true;
        }
    }

    // TODO(issue): D38's spike on macOS and Windows (question 16): a real click in this menu over the drop-down. Linux passes;
    // if either OS fails, B8 (the drop-down's list swapped for the branch's commands, Back returning) ships everywhere.
    private void OpenMenu(Workbench.Git.GitBranchDetails branch, Control anchor, bool under)
    {
        if (ViewModel?.MenuFor?.Invoke(branch) is not { } menu)
        {
            return;
        }

        if (under)
        {
            // Under ⋯ (its right edge on the button's) or under the row: never at a pointer that isn't there.
            menu.Placement = anchor is Button ? PlacementMode.BottomEdgeAlignedRight : PlacementMode.BottomEdgeAlignedLeft;
        }

        IsMenuOpen = true;
        menu.Closed += (_, _) =>
        {
            IsMenuOpen = false;
            if (ViewModel is { IsOpen: true })
            {
                FocusSelected();
            }
        };
        menu.Open(anchor);
    }

    /// <summary>The menu open over the drop-down, if any (tests).</summary>
    internal ContextMenu? OpenMenuForTests(Workbench.Git.GitBranchDetails branch)
    {
        if (ViewModel?.MenuFor?.Invoke(branch) is not { } menu)
        {
            return null;
        }

        IsMenuOpen = true;
        menu.Closed += (_, _) => IsMenuOpen = false;
        menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        menu.Open(ViewModel.Selected is { } row && ContainerOf(row) is { } container ? container : List);
        return menu;
    }
}
