using System.Collections.Specialized;
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Git;

namespace Codalus.Shell.Tests;

/// <summary>
/// Git Changes with thousands of changed files (M56): a status read builds the window's files off the UI thread and changes
/// nothing when nothing changed; a hidden window builds no tree; a shown one shows each section's tree as a virtualized list
/// of rows, updated in place, whose folders expand and collapse without building the tree again. On a plain folder made a
/// repository, so no build or language host starts.
/// </summary>
public sealed class GitChangesRowsTests
{
    private static string Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error.GetAwaiter().GetResult()}");
        return output;
    }

    /// <summary>A repository with one commit and <paramref name="folders"/> × <paramref name="files"/> untracked files under src/.</summary>
    internal static string CreateRepository(string folder, int folders, int files)
    {
        Directory.CreateDirectory(folder);
        Git(folder, "init", "-q", "-b", "main");
        Git(folder, "config", "user.name", "Ada Tester");
        Git(folder, "config", "user.email", "ada@example.com");
        Git(folder, "config", "commit.gpgsign", "false");
        Git(folder, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(folder, "README.md"), "initial\n");
        Git(folder, "add", ".");
        Git(folder, "commit", "-q", "-m", "initial");
        for (var d = 0; d < folders; d++)
        {
            var directory = Path.Combine(folder, "src", $"D{d:000}");
            Directory.CreateDirectory(directory);
            for (var i = 0; i < files; i++)
            {
                File.WriteAllText(Path.Combine(directory, $"F{i:00}.cs"), "// generated\n");
            }
        }

        return Path.GetFullPath(folder);
    }

    private static int Count(INotifyCollectionChanged collection, List<NotifyCollectionChangedAction> actions)
    {
        collection.CollectionChanged += (_, e) => actions.Add(e.Action);
        return actions.Count;
    }

    private static JsonElement Json(string value) => JsonSerializer.SerializeToElement(value);

    /// <summary>A repository of <paramref name="folders"/> × <paramref name="files"/> new files under src/, Git Changes shown with their rows.</summary>
    private static async Task<(GitChangesViewModel Vm, ListBox Tree, string Folder)> ShowAsync(ShellFixture f, int folders, int files)
    {
        var folder = CreateRepository(Path.Combine(f.UserData, "Tree"), folders, files);
        f.Shell.Trust.Trust(folder);
        f.Window.Height = 900;
        await f.Shell.OpenFolderAsync(folder);
        var vm = f.Shell.GitChanges;
        f.Shell.Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId);
        var rows = 2 + (folders * (1 + files)); // the repository, src, each folder and its files
        await ShellFixture.PumpUntil(() => vm.IsViewShown && vm.ChangesRows.Count == rows && !vm.IsRefreshing, seconds: 60, describe: () => $"{vm.ChangesRows.Count} rows");
        var view = f.Window.GetVisualDescendants().OfType<GitChangesView>().Single();
        var tree = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ChangesTree");
        await ShellFixture.PumpUntil(() => tree.GetRealizedContainers().Any());
        return (vm, tree, folder);
    }

    /// <summary>A row's control, brought into view.</summary>
    private static async Task<ListBoxItem> ItemAsync(ListBox tree, GitChangeRow row)
    {
        tree.ScrollIntoView(row);
        await ShellFixture.PumpUntil(() => tree.ContainerFromItem(row) is ListBoxItem, describe: () => $"no control for {row.Key}");
        return (ListBoxItem)tree.ContainerFromItem(row)!;
    }

    /// <summary>What a row's control shows: its chevron, and its state for a screen reader (its tree item's, M57).</summary>
    private static async Task<(string? Chevron, ExpandCollapseState State)> ShownAsync(ListBox tree, GitChangeRow row)
    {
        var item = await ItemAsync(tree, row);
        var chevron = item.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("chevron"));
        return ((chevron.Child as Controls.Icon)?.Kind, TreeRowsAutomationTests.PeerOf(tree, row).ExpandCollapseState);
    }

    private static void Click(TopLevel top, Visual control, int clicks = 1)
    {
        ShellFixture.RenderFrame(top);
        var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        top.MouseMove(at);
        for (var i = 0; i < clicks; i++)
        {
            top.MouseDown(at, MouseButton.Left);
            top.MouseUp(at, MouseButton.Left);
        }

        ShellFixture.Pump();
    }

    /// <summary>
    /// Expand All and Collapse All (the menu, Ctrl+Left and Right, * and /) show the state of the rows they keep, and a
    /// refresh keeps it (M56): they set the nodes of those rows, which told the rows nothing, so a collapsed folder still
    /// showed chevron-down, an open folder and "expanded" with its rows gone, and its chevron did the opposite of what it showed.
    /// </summary>
    [AvaloniaFact]
    public async Task Expand_all_and_collapse_all_show_the_state_of_the_rows_they_keep_through_refreshes()
    {
        await using var f = ShellFixture.Create();
        var (vm, tree, folder) = await ShowAsync(f, folders: 3, files: 2);
        GitChangeRow Row(string path) => vm.ChangesRows.Single(r => r.Node.RelativePath == path);
        var repository = Row(string.Empty);

        // Collapse All on the repository: its row stays, and says it's collapsed.
        await f.ExecuteAsync("gitChanges.collapseAll", Json(folder), Json("changes"));
        Assert.Equal([repository], vm.ChangesRows);
        var (chevron, state) = await ShownAsync(tree, repository);
        Assert.Equal("chevron-right", chevron);
        Assert.Equal(ExpandCollapseState.Collapsed, state);

        // Expand All: every folder's row says it's expanded, the kept one's too.
        await f.ExecuteAsync("gitChanges.expandAll", Json(folder), Json("changes"));
        Assert.Equal(11, vm.ChangesRows.Count);
        Assert.Same(repository, vm.ChangesRows[0]);
        foreach (var row in vm.ChangesRows.Where(r => r.HasChildren).ToList())
        {
            (chevron, state) = await ShownAsync(tree, row);
            Assert.Equal("chevron-down", chevron);
            Assert.Equal(ExpandCollapseState.Expanded, state);
        }

        // Expand All on src with a folder under it collapsed: the folder's row, which stays, says it's expanded.
        vm.SetExpanded(Row("src/D001"), false);
        var d001 = Row("src/D001");
        Assert.Equal("chevron-right", (await ShownAsync(tree, d001)).Chevron);
        await f.ExecuteAsync("gitChanges.expandAll", Json(Path.Combine(folder, "src")), Json("changes"));
        Assert.Equal(11, vm.ChangesRows.Count);
        Assert.Same(d001, Row("src/D001"));
        (chevron, state) = await ShownAsync(tree, d001);
        Assert.Equal("chevron-down", chevron);
        Assert.Equal(ExpandCollapseState.Expanded, state);

        // Collapse All on src, then a refresh: its new nodes have the state Collapse All left, and the rows still say it.
        await f.ExecuteAsync("gitChanges.collapseAll", Json(Path.Combine(folder, "src")), Json("changes"));
        var src = Row("src");
        Assert.Equal([repository, src], vm.ChangesRows);
        Assert.Equal("chevron-right", (await ShownAsync(tree, src)).Chevron);
        await File.WriteAllTextAsync(Path.Combine(folder, "NOTES.md"), "notes\n", TestContext.Current.CancellationToken);
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => vm.Changes.Count == 7 && !vm.IsRefreshing);
        Assert.Equal(3, vm.ChangesRows.Count);
        Assert.Same(src, Row("src"));
        (chevron, state) = await ShownAsync(tree, src);
        Assert.Equal("chevron-right", chevron);
        Assert.Equal(ExpandCollapseState.Collapsed, state);
    }

    /// <summary>
    /// The trees' rows expand and collapse as a tree view's (M56): Left and Right (or to the parent), Enter, Ctrl with each
    /// for the folders under the row too, + and -, * and /, the chevron, and a double-click on a folder. A double-click on a
    /// file's row opens it, on its blank chevron place too, and Enter on a row's focused hover button presses the button.
    /// </summary>
    [AvaloniaFact]
    public async Task Tree_rows_expand_and_collapse_from_the_keyboard_the_chevron_and_a_double_click()
    {
        await using var f = ShellFixture.Create();
        var (vm, tree, folder) = await ShowAsync(f, folders: 3, files: 2);
        GitChangeRow Row(string path) => vm.ChangesRows.Single(r => r.Node.RelativePath == path);
        bool Expanded(string path) => vm.FindNode("changes", Path.Combine([folder, .. path.Split('/')]))!.IsExpanded;
        List<string> opened = [];
        vm.OpenFileAsDefault = true;
        vm.OpenFile = path =>
        {
            opened.Add(path);
            return Task.CompletedTask;
        };

        async Task Select(string path)
        {
            var row = Row(path);
            tree.SelectedItem = row;
            (await ItemAsync(tree, row)).Focus();
            ShellFixture.Pump();
        }

        // Left collapses a folder, then goes to its parent; Right expands it; - and + do as Left and Right.
        await Select("src/D001");
        f.Press(Key.Left);
        Assert.Equal((9, false), (vm.ChangesRows.Count, Expanded("src/D001")));
        f.Press(Key.Left);
        Assert.Same(Row("src"), tree.SelectedItem);
        await Select("src/D001");
        f.Press(Key.Right);
        Assert.Equal(11, vm.ChangesRows.Count);
        f.Press(Key.Subtract);
        Assert.Equal(9, vm.ChangesRows.Count);
        f.Press(Key.Add);
        Assert.Equal(11, vm.ChangesRows.Count);

        // / and * collapse and expand every folder under the row, as Ctrl+Left and Ctrl+Right do.
        await Select("src");
        f.Press(Key.Divide);
        Assert.Equal(2, vm.ChangesRows.Count);
        Assert.False(Expanded("src/D000"));
        f.Press(Key.Multiply);
        Assert.Equal(11, vm.ChangesRows.Count);
        f.Press(Key.Left, RawInputModifiers.Control);
        Assert.Equal((2, false), (vm.ChangesRows.Count, Expanded("src/D002")));
        f.Press(Key.Right, RawInputModifiers.Control);
        Assert.Equal(11, vm.ChangesRows.Count);

        // Enter toggles a folder; Ctrl+Enter the folders under it too.
        await Select("src/D002");
        f.Press(Key.Enter);
        Assert.False(Expanded("src/D002"));
        f.Press(Key.Enter);
        Assert.True(Expanded("src/D002"));
        await Select("src");
        f.Press(Key.Enter, RawInputModifiers.Control);
        Assert.Equal(2, vm.ChangesRows.Count);
        Assert.All(["src/D000", "src/D001", "src/D002"], p => Assert.False(Expanded(p)));
        f.Press(Key.Enter, RawInputModifiers.Control);
        Assert.Equal(11, vm.ChangesRows.Count);

        // On a file, Enter opens it and Left goes to its folder.
        await Select("src/D000/F01.cs");
        f.Press(Key.Enter);
        Assert.Equal([Path.Combine(folder, "src", "D000", "F01.cs")], opened);
        f.Press(Key.Left);
        Assert.Same(Row("src/D000"), tree.SelectedItem);

        // The chevron toggles a folder on each click, a double-click's second too (it raises no tap); a double-click on
        // the folder's row toggles it once. The chevron selects nothing, as a tree view's expander (M57 review).
        var d002 = await ItemAsync(tree, Row("src/D002"));
        var chevron = d002.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("chevron"));
        Click(f.Window, chevron);
        Assert.False(Expanded("src/D002"));
        Click(f.Window, chevron);
        Assert.True(Expanded("src/D002"));
        Assert.Same(Row("src/D000"), tree.SelectedItem);
        Click(f.Window, d002.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "D002"), clicks: 2);
        Assert.False(Expanded("src/D002"));
        Click(f.Window, chevron);
        Assert.True(Expanded("src/D002"));

        // A double-click on a file's row opens it, on the blank place of its chevron too.
        opened.Clear();
        var file = await ItemAsync(tree, Row("src/D001/F00.cs"));
        Click(f.Window, file.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("chevron")), clicks: 2);
        await ShellFixture.PumpUntil(() => opened.Count > 0);
        Assert.Equal([Path.Combine(folder, "src", "D001", "F00.cs")], opened);

        // In the list view too, Enter opens a file.
        opened.Clear();
        vm.IsTreeView = false;
        var list = f.Window.GetVisualDescendants().OfType<GitChangesView>().Single().GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ChangesList");
        await ShellFixture.PumpUntil(() => vm.ChangesItems.Count == 6 && !vm.IsRefreshing && list.ContainerFromIndex(0) is ListBoxItem);
        list.SelectedIndex = 0;
        ((ListBoxItem)list.ContainerFromIndex(0)!).Focus();
        ShellFixture.Pump();
        f.Press(Key.Enter);
        Assert.Equal([vm.ChangesItems[0].Status.Path], opened);
        vm.IsTreeView = true;
        await ShellFixture.PumpUntil(() => vm.ChangesRows.Count == 11 && !vm.IsRefreshing);

        // Enter on a folder's focused hover button presses the button (Stage), and doesn't toggle the folder.
        await Select("src/D000");
        var stage = (await ItemAsync(tree, Row("src/D000"))).GetVisualDescendants().OfType<Button>().Single(b => b.Tag is "stage");
        stage.Focus();
        ShellFixture.Pump();
        f.Press(Key.Enter);
        await ShellFixture.PumpUntil(() => vm.Staged.Count == 2 && !vm.IsRefreshing, describe: () => $"staged {vm.Staged.Count}, D000 expanded {Expanded("src/D000")}");
    }

    /// <summary>
    /// Dock builds a tool window's view again for a new layout while the old one still shows it: the new view says it's
    /// shown, then the old one says it isn't. With one flag for both, Git Changes took itself for hidden while on screen,
    /// and its trees stopped following status reads (M56).
    /// </summary>
    [AvaloniaFact]
    public async Task A_view_built_again_while_the_old_one_shows_keeps_git_changes_shown()
    {
        await using var f = ShellFixture.Create();
        var (vm, _, folder) = await ShowAsync(f, folders: 3, files: 2);
        var second = new Window { Width = 400, Height = 700, Content = new GitChangesView { DataContext = vm } };
        second.Show();
        ShellFixture.Pump();
        f.Shell.Layout.CloseToolWindow(GitChangesViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<GitChangesView>().All(v => !v.IsEffectivelyVisible));
        for (var i = 0; i < 3; i++)
        {
            ShellFixture.Pump();
        }

        Assert.True(vm.IsViewShown, "Git Changes took itself for hidden while a view still shows it");
        await File.WriteAllTextAsync(Path.Combine(folder, "src", "D001", "New.cs"), "// new\n", TestContext.Current.CancellationToken);
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => vm.ChangesRows.Any(r => r.Node.RelativePath == "src/D001/New.cs") && !vm.IsRefreshing);

        second.Close();
        await ShellFixture.PumpUntil(() => !vm.IsViewShown);
    }

    /// <summary>
    /// A refresh computed while the window was hidden that lands after a view showed it builds the trees (M56): showing it
    /// found them current, and the window showed the trees of the last time it was shown, with the headers' new counts.
    /// The Git LFS state starts the refresh here, as a status read does; its files, read as the refresh computes, wait for
    /// the view to show the window, so the refresh is still computing then (one that ended at once left the views stale).
    /// </summary>
    [AvaloniaFact]
    public async Task A_refresh_computed_while_hidden_that_lands_once_shown_builds_the_trees()
    {
        await using var f = ShellFixture.Create();
        var (vm, _, folder) = await ShowAsync(f, folders: 3, files: 2);

        // The shell's first reads of the repository have landed, each a refresh of the window: its Git LFS state, which this
        // test sets (landing after, it would put back no LFS), and its repositories (750 ms after the folder opened).
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Context.GetValue("gitLfs") is false && f.Shell.Workbench.Context.GetValue("gitRepositories") is false && !vm.IsRefreshing);
        f.Shell.Layout.CloseToolWindow(GitChangesViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => !vm.IsViewShown && !vm.IsRefreshing);

        using var shown = new ManualResetEventSlim();
        var asset = Path.Combine(folder, "src", "D001", "F00.cs");
        vm.Lfs = new GitLfsStatus(true, true, new GatedList<GitLfsFile>([new GitLfsFile(asset, "src/D001/F00.cs", IsPointer: false, Size: 1)], shown), [], null);
        Assert.True(vm.IsRefreshing);
        var view = new object();
        vm.SetViewShown(view, true);
        shown.Set();
        await ShellFixture.PumpUntil(() => !vm.IsRefreshing);
        Assert.Equal("LFS", vm.ChangesRows.Single(r => r.Node.RelativePath == "src/D001/F00.cs").Badge);
        vm.SetViewShown(view, false);
    }

    /// <summary>A list whose reading waits for <paramref name="gate"/> (30 s at most).</summary>
    private sealed class GatedList<T>(IReadOnlyList<T> items, ManualResetEventSlim gate) : IReadOnlyList<T>
    {
        public int Count => items.Count;

        public T this[int index] => items[index];

        public IEnumerator<T> GetEnumerator()
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            return items.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [AvaloniaFact]
    public async Task A_status_read_that_changes_nothing_changes_no_row_and_a_hidden_window_builds_no_tree()
    {
        await using var f = ShellFixture.Create();
        var folder = CreateRepository(Path.Combine(f.UserData, "Many"), folders: 200, files: 10);
        f.Shell.Trust.Trust(folder);
        await f.Shell.OpenFolderAsync(folder);
        var vm = f.Shell.GitChanges;
        await ShellFixture.PumpUntil(() => vm.Changes.Count == 2000 && !vm.IsRefreshing, seconds: 60);

        // Hidden: the files are current (the commands read them), but no tree or list is built.
        Assert.False(vm.IsViewShown);
        Assert.Equal(0, vm.ViewsBuilt);
        Assert.Empty(vm.ChangesRows);
        Assert.Empty(vm.ChangesTree);

        // The same status again changes no file: no reset of 2,000 rows, nothing built.
        List<NotifyCollectionChangedAction> files = [];
        Count(vm.Changes, files);
        var first = vm.Changes[0];
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => !vm.IsRefreshing);
        Assert.Empty(files);
        Assert.Same(first, vm.Changes[0]);
        Assert.Equal(0, vm.ViewsBuilt);

        // Shown: the tree is built once, as rows, and only the rows in view get controls.
        f.Shell.Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(
            () => vm.IsViewShown && vm.ViewsBuilt >= 1 && !vm.IsRefreshing,
            describe: () => $"shown {vm.IsViewShown}, built {vm.ViewsBuilt} times, refreshing {vm.IsRefreshing}, rows {vm.ChangesRows.Count}");
        Assert.Equal(1 + 1 + 200 + 2000, vm.ChangesRows.Count); // the repository, src, its folders, their files
        Assert.Equal([folder, "src", "D000", "F00.cs"], vm.ChangesRows.Take(4).Select(r => r.Text));
        Assert.Equal([0, 1, 2, 3], vm.ChangesRows.Take(4).Select(r => r.Depth));
        var view = f.Window.GetVisualDescendants().OfType<GitChangesView>().Single();
        var list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ChangesTree");
        await ShellFixture.PumpUntil(() => list.GetRealizedContainers().Any());
        Assert.InRange(list.GetRealizedContainers().Count(), 1, 150);
        Assert.InRange(view.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 200);

        // The same status again changes no row.
        List<NotifyCollectionChangedAction> rows = [];
        Count(vm.ChangesRows, rows);
        var row = vm.ChangesRows[5];
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => !vm.IsRefreshing);
        Assert.Empty(rows);
        Assert.Empty(files);
        Assert.Same(row, vm.ChangesRows[5]);

        // A file gone: its row goes, with one removal; the rows around it stay, the counts change in place.
        File.Delete(Path.Combine(folder, "src", "D150", "F03.cs"));
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => vm.Changes.Count == 1999 && !vm.IsRefreshing);
        Assert.Equal([NotifyCollectionChangedAction.Remove], rows);
        Assert.Equal([NotifyCollectionChangedAction.Reset], files);
        Assert.Equal(2201, vm.ChangesRows.Count);
        Assert.Same(row, vm.ChangesRows[5]);
        Assert.Equal(1999, vm.ChangesRows[0].Node.TotalFiles);

        // Its tree items say where a row is (M57): F04.cs, its folder's fourth file of nine now, at level 4.
        var f04 = vm.ChangesRows.Single(r => r.Node.RelativePath == "src/D150/F04.cs");
        await ItemAsync(list, f04);
        TreeRowsAutomationTests.AssertTreeItem(list, f04, level: 4, position: 4, size: 9, ExpandCollapseState.LeafNode);
        TreeRowsAutomationTests.AssertTreeItem(list, vm.ChangesRows.Single(r => r.Text == "D150"), level: 3, position: 151, size: 200, ExpandCollapseState.Expanded);

        // Collapsing the repository hides its rows with one removal; expanding shows them with one insertion.
        rows.Clear();
        vm.SetExpanded(vm.ChangesRows[0], false);
        Assert.Single(vm.ChangesRows);
        await ItemAsync(list, vm.ChangesRows[0]);
        TreeRowsAutomationTests.AssertTreeItem(list, vm.ChangesRows[0], level: 1, position: 1, size: 1, ExpandCollapseState.Collapsed);
        TreeRowsAutomationTests.Invoke(list, vm.ChangesRows[0], expand: true); // a screen reader's Expand, as Right
        Assert.Equal(2201, vm.ChangesRows.Count);
        Assert.Equal([NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add], rows);

        // A collapsed folder stays collapsed through refreshes.
        var d010 = vm.ChangesRows.Single(r => r.Text == "D010");
        vm.SetExpanded(d010, false);
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => !vm.IsRefreshing);
        Assert.Equal(2191, vm.ChangesRows.Count);
        Assert.False(vm.ChangesRows.Single(r => r.Text == "D010").IsExpanded);

        // The same Git LFS state again (read after every status read) refreshes nothing.
        var built = vm.ViewsBuilt;
        vm.Lfs = new GitLfsStatus(false, false, [], [], null);
        Assert.False(vm.IsRefreshing);
        Assert.Equal(built, vm.ViewsBuilt);
    }
}
