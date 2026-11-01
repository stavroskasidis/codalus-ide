using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// <c>git.changesMenus</c> (#23, D38): in the shipped app, a folder of the Git Changes window opens its menu built from
/// <c>gitChanges/context</c>, the branch button opens the drop-down listing the branches, and a branch's menu from
/// <c>gitChanges/branch</c> opens over the drop-down, which stays open under it (B7). Screenshots of each.
/// </summary>
internal sealed partial class SmokeTest
{
    private async Task GitChangesMenusStepAsync()
    {
        shell.Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId);
        await WaitAsync(() => window.GetVisualDescendants().OfType<GitChangesView>().Any(), "the Git Changes window shows").ConfigureAwait(true);
        var view = window.GetVisualDescendants().OfType<GitChangesView>().Single();

        // A folder with a change: its menu, from the registered location, with the folder's commands.
        var original = await File.ReadAllTextAsync(Greeter).ConfigureAwait(true);
        await File.WriteAllTextAsync(Greeter, original + "// menu\n").ConfigureAwait(true);
        try
        {
            await shell.Git.RefreshAsync().ConfigureAwait(true);
            var folder = Path.GetDirectoryName(Greeter)!;
            await WaitAsync(() => shell.GitChanges.ChangesTree.Count > 0, "the change is listed").ConfigureAwait(true);
            var items = shell.GitChangesMenuItems(folder, "changes", "folder").Select(i => i.Command).ToList();
            Check(items.SequenceEqual(["git.stage", "git.discard", "git.stash", "git.history", "explorer.reveal", "terminal.openInTerminal", "explorer.copyPath", "explorer.copyRelativePath", "gitChanges.expandAll", "gitChanges.collapseAll"]), "a folder's menu has the classic IDE's entries: " + string.Join(", ", items));
            var tree = view.GetVisualDescendants().OfType<ListBox>().Single(t => t.Name == "ChangesTree");
            var node = shell.GitChanges.ChangesTree.SelectMany(Nodes).First(n => !n.IsFile && !n.IsRepository);
            Check(shell.GitChanges.ChangesRows.Any(r => ReferenceEquals(r.Node, node)), "the folder has its row in the Changes tree's list");
            var menu = shell.GitChanges.MenuOf(node) ?? throw new SmokeFailure("The folder's menu didn't build.");
            menu.Open(tree);
            await WaitAsync(() => menu.IsOpen, "the folder's menu opens").ConfigureAwait(true);
            Check(menu.Items.OfType<MenuItem>().All(i => i.Icon is not null), "every entry of the folder's menu has its icon");
            await RenderedAsync().ConfigureAwait(true);
            Screenshot("smoke-test-git-folder-menu.png", menu.Parent is Popup folderPopup ? [folderPopup] : []);
            menu.Close();

            // The branch drop-down: the button opens it, listing the local branches with the current one first.
            var button = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "BranchButton");
            Check(AutomationProperties.GetName(button) == "Branch: " + shell.Git.Status!.Branch, "the branch button's name says the branch");
            var current = shell.Git.Status.Branch;
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => shell.GitBranches.IsOpen && !shell.GitBranches.IsLoading && shell.GitBranches.Rows.Any(r => r.IsCurrent), "the branch drop-down lists the branches").ConfigureAwait(true);
            var popup = view.GetVisualDescendants().OfType<Popup>().Single(p => p.Name == "BranchPopup");
            await WaitAsync(() => popup.IsOpen && popup.Child is { Bounds.Width: > 300 }, "the drop-down is shown, 340 px wide").ConfigureAwait(true);
            Check(shell.GitBranches.Rows.First(r => r.IsBranch).IsCurrent, "the current branch is first");
            await RenderedAsync().ConfigureAwait(true);
            Screenshot("smoke-test-git-branch-dropdown.png", [popup]);

            // A branch's menu over the drop-down (B7): the drop-down stays open under it.
            if (!shell.GitBranches.Rows.Any(r => r.Branch is { IsRemote: false, IsCurrent: false }))
            {
                await shell.Git.CreateBranchAtAsync("smoke/menus", "HEAD", switchTo: false).ConfigureAwait(true);
                await shell.GitBranches.LoadAsync().ConfigureAwait(true);
            }

            var otherRow = shell.GitBranches.Rows.First(r => r.Branch is { IsRemote: false, IsCurrent: false });
            var other = otherRow.Branch!;
            shell.GitBranches.Selected = otherRow;
            var titles = shell.GitBranchMenuItems(other).Select(i => i.Title).ToList();
            Check(titles.Contains("Checkout") && titles.Contains($"Merge '{other.Name}' into '{current}'"), "a branch's menu names the branches: " + string.Join(", ", titles));
            var branchMenu = view.BranchPickerView.OpenMenuForTests(other) ?? throw new SmokeFailure("The branch's menu didn't build.");
            await WaitAsync(() => branchMenu.IsOpen, "the branch's menu opens").ConfigureAwait(true);
            Check(popup.IsOpen && shell.GitBranches.IsOpen, "the drop-down stays open under the branch's menu");
            var origin = Avalonia.VisualExtensions.PointToScreen(window, default);
            await WaitAsync(
                () => Avalonia.VisualExtensions.PointToScreen(branchMenu, default) is var at && at.X >= origin.X && at.Y >= origin.Y && at.X < origin.X + window.Bounds.Width * window.RenderScaling,
                "the branch's menu opens over the drop-down, by its row, inside the window").ConfigureAwait(true);
            var menuTop = TopLevel.GetTopLevel(branchMenu);
            await RenderedAsync().ConfigureAwait(true);
            Screenshot("smoke-test-git-branch-menu.png", branchMenu.Parent is Popup menuPopup ? [popup, menuPopup] : [popup]);
            branchMenu.Close();
            shell.GitBranches.IsOpen = false;
            await WaitAsync(() => !popup.IsOpen, "the drop-down closes").ConfigureAwait(true);
        }
        finally
        {
            await File.WriteAllTextAsync(Greeter, original).ConfigureAwait(true);
            await shell.Git.RefreshAsync().ConfigureAwait(true);
        }

        static IEnumerable<GitChangeNode> Nodes(GitChangeNode node) => node.Children.SelectMany(Nodes).Prepend(node);
    }
}
