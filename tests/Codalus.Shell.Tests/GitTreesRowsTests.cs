using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>
/// The Git Repository, Commit Details and Compare trees at scale (M57), as Git Changes' sections (M56): each is a
/// virtualized list of a flattened tree's rows, built off the UI thread and updated in place, whose rows expand and
/// collapse with the same mouse and keys (<see cref="TreeRows"/>). The Git Repository document reads git only while it's
/// shown, builds nothing for refs that didn't change, and filters once per pause in typing. On plain folders made
/// repositories, so no build or language host starts.
/// </summary>
public sealed class GitTreesRowsTests
{
    private static string GitWithInput(string directory, string? input, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        if (input is not null)
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }

        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error.GetAwaiter().GetResult()}");
        return output;
    }

    private static string Git(string directory, params string[] arguments) => GitWithInput(directory, null, arguments);

    internal static void DeleteTag(string folder, string tag) => Git(folder, "tag", "-d", tag);

    /// <summary>
    /// A repository with one commit, <paramref name="tags"/> tags (<c>t00000</c>…) and <paramref name="branches"/> branches
    /// in folders of 100 (<c>team00/b0000</c>…), made at once with <c>git update-ref --stdin</c>.
    /// </summary>
    internal static string CreateManyRefs(string folder, int tags, int branches)
    {
        folder = GitChangesRowsTests.CreateRepository(folder, folders: 0, files: 0);
        var head = Git(folder, "rev-parse", "HEAD").Trim();
        var input = new System.Text.StringBuilder();
        for (var i = 0; i < tags; i++)
        {
            input.Append(System.Globalization.CultureInfo.InvariantCulture, $"create refs/tags/t{i:00000} {head}\n");
        }

        for (var i = 0; i < branches; i++)
        {
            input.Append(System.Globalization.CultureInfo.InvariantCulture, $"create refs/heads/team{i / 100:00}/b{i:0000} {head}\n");
        }

        GitWithInput(folder, input.ToString(), "update-ref", "--stdin");
        return folder;
    }

    /// <summary>A repository whose second commit adds <paramref name="folders"/> × <paramref name="files"/> files under src/ (on <paramref name="branch"/> when given, then back on main); its id.</summary>
    internal static (string Folder, string Hash) CreateLargeCommit(string folder, int folders, int files, string? branch = null)
    {
        folder = GitChangesRowsTests.CreateRepository(folder, folders, files);
        if (branch is not null)
        {
            Git(folder, "switch", "-q", "-c", branch);
        }

        Git(folder, "add", "-A");
        Git(folder, "commit", "-q", "-m", "many");
        var hash = Git(folder, "rev-parse", "HEAD").Trim();
        if (branch is not null)
        {
            Git(folder, "switch", "-q", "main");
        }

        return (folder, hash);
    }

    /// <summary>A folder with many refs, open, and its Git Repository document shown with its tree.</summary>
    internal static async Task<(GitRepositoryViewModel Vm, ListBox Tree, string Folder)> ShowRepositoryAsync(ShellFixture f, int tags, int branches)
    {
        var folder = CreateManyRefs(Path.Combine(f.UserData, "Refs"), tags, branches);
        f.Shell.Trust.Trust(folder);
        f.Window.Height = 900;
        await f.Shell.OpenFolderAsync(folder);
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "main" });
        await f.RunAsync("git.manageBranches");
        await ShellFixture.PumpUntil(
            () => f.Shell.Layout.ActiveDocument is GitRepositoryDocument { ViewModel: { IsLoading: false, IsReading: false, IsViewShown: true } vm } && vm.Find("refs/heads/main") is not null,
            seconds: 60);
        var vm = ((GitRepositoryDocument)f.Shell.Layout.ActiveDocument!).ViewModel;
        var tree = await RepositoryListAsync(f);
        return (vm, tree, folder);
    }

    internal static async Task<ListBox> RepositoryListAsync(ShellFixture f)
    {
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<GitRepositoryView>().Any(v => v.IsEffectivelyVisible));
        var tree = f.Window.GetVisualDescendants().OfType<GitRepositoryView>().Single(v => v.IsEffectivelyVisible).GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "Tree");
        await ShellFixture.PumpUntil(() => tree.GetRealizedContainers().Any());
        return tree;
    }

    /// <summary>A row's control, brought into view.</summary>
    private static async Task<ListBoxItem> ItemAsync(ListBox list, object row)
    {
        list.ScrollIntoView(row);
        await ShellFixture.PumpUntil(() => list.ContainerFromItem(row) is ListBoxItem, describe: () => $"no control for {row}");
        return (ListBoxItem)list.ContainerFromItem(row)!;
    }

    /// <summary>Selects a row and gives its control the focus, for the keys.</summary>
    private static async Task FocusAsync(ListBox list, object row)
    {
        list.SelectedItem = row;
        (await ItemAsync(list, row)).Focus();
        ShellFixture.Pump();
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

    private static List<NotifyCollectionChangedAction> Changes(INotifyCollectionChanged collection)
    {
        var actions = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, e) => actions.Add(e.Action);
        return actions;
    }

    /// <summary>A status read (git's changes, as a save makes them), and the document's read of the refs after it.</summary>
    private static async Task StatusReadAsync(ShellFixture f, GitRepositoryViewModel vm)
    {
        var reads = vm.RefsReads;
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => vm.RefsReads > reads && !vm.IsReading, seconds: 60, describe: () => $"{vm.RefsReads} reads, reading {vm.IsReading}");
    }

    private static GitRepositoryRow Row(GitRepositoryViewModel vm, string text) => vm.Rows.Single(r => r.Text == text);

    private static void DeleteFolder(string folder)
    {
        try
        {
            // git writes its object files read-only.
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [AvaloniaFact]
    public async Task Git_repository_with_thousands_of_refs_shows_rows_in_place_and_reads_nothing_while_hidden()
    {
        await using var f = ShellFixture.Create();
        var (vm, tree, folder) = await ShowRepositoryAsync(f, tags: 5000, branches: 2000);

        // The branches in their folders, folders first, with the tags collapsed: a row for each node shown, 16 px a level.
        Assert.Equal(["Branches / Tags", Path.GetFileName(folder) + " (main)", "team00", "b0000"], vm.Rows.Take(4).Select(r => r.Text));
        Assert.Equal([0, 1, 2, 3], vm.Rows.Take(4).Select(r => r.Depth));
        Assert.Equal(new Thickness(48, 0, 0, 0), vm.Rows[3].Indent);
        Assert.True(Row(vm, "team19").IsExpanded);
        Assert.Equal("main", vm.Rows[2 + (20 * 101)].Text); // after the 20 folders of 100 branches
        Assert.False(Row(vm, "tags").IsExpanded);
        Assert.Equal(Row(vm, "tags").Node.AccessibleName, Row(vm, "tags").AccessibleName); // its state is its tree item's (M57)

        // The current branch's history: its one commit has all 7,001 refs, of which the Branch / Tag column makes 8 chips
        // (it made one for each, 0.5 to 1 s for 320 at each selection), every name in the tooltip.
        await ShellFixture.PumpUntil(() => vm.History is { IsLoading: false, Rows.Count: > 0 }, seconds: 60);
        var commit = vm.History!.Rows.Single();
        Assert.Equal(7001, commit.RefChips.Count);
        Assert.Equal(GitCommitRow.MaxShownRefs, commit.ShownRefs.Count);
        Assert.Contains("t04999", commit.RefsTip, StringComparison.Ordinal);
        ShellFixture.RenderFrame(f.Window);
        var history = f.Window.GetVisualDescendants().OfType<GitHistoryView>().Single(v => v.IsEffectivelyVisible);
        Assert.InRange(history.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("ref")), 1, GitCommitRow.MaxShownRefs);

        // The tags expanded: 5,000 rows more, one insertion; only the rows in view get controls.
        var rows = Changes(vm.Rows);
        var count = vm.Rows.Count;
        vm.SetExpanded(Row(vm, "tags"), true);
        Assert.Equal(count + 5000, vm.Rows.Count);
        Assert.Equal([NotifyCollectionChangedAction.Add], rows);
        ShellFixture.RenderFrame(f.Window);
        Assert.InRange(tree.GetRealizedContainers().Count(), 1, 150);

        // A tag far down selected and in view; a status read that changes no ref builds nothing and changes no row.
        var built = vm.TreesBuilt;
        Assert.True(vm.Select("refs/tags/t04000"));
        var selected = vm.SelectedRow!;
        Assert.Equal("t04000", selected.Text);
        await ShellFixture.PumpUntil(() => tree.ContainerFromItem(selected) is ListBoxItem { IsSelected: true });
        var scroller = tree.GetVisualDescendants().OfType<ScrollViewer>().First();
        var offset = scroller.Offset;
        Assert.True(offset.Y > 1000, $"offset {offset.Y}");
        rows.Clear();
        await File.WriteAllTextAsync(Path.Combine(folder, "NOTES.md"), "notes\n", TestContext.Current.CancellationToken);
        await StatusReadAsync(f, vm);
        Assert.Empty(rows);
        Assert.Equal(built, vm.TreesBuilt);

        // A tag deleted below the selection: its row goes with one removal; the selection and the scroll stay.
        Git(folder, "tag", "-d", "t04990");
        await StatusReadAsync(f, vm);
        Assert.Equal([NotifyCollectionChangedAction.Remove], rows);
        Assert.Equal(built + 1, vm.TreesBuilt);
        Assert.Same(selected, vm.SelectedRow);
        Assert.Same(selected, tree.SelectedItem);
        ShellFixture.RenderFrame(f.Window);
        Assert.Equal(offset.Y, scroller.Offset.Y, 1);
        Assert.Null(vm.Find("refs/tags/t04990"));

        // The selected tag deleted: nothing is selected, and the history says so.
        Git(folder, "tag", "-d", "t04000");
        await StatusReadAsync(f, vm);
        Assert.Null(vm.Selected);
        Assert.Null(vm.SelectedRow);
        await ShellFixture.PumpUntil(() => vm.HasMessage);

        // Typing in the filter filters once, a pause after the last keystroke, with every match's folders open.
        built = vm.TreesBuilt;
        foreach (var text in new[] { "t", "t0", "t01", "t012" })
        {
            vm.Filter = text;
            var until = DateTime.UtcNow.AddMilliseconds(40);
            await ShellFixture.PumpUntil(() => DateTime.UtcNow >= until);
            Assert.Equal(built, vm.TreesBuilt);
        }

        await ShellFixture.PumpUntil(() => !vm.IsRebuilding);
        Assert.Equal(built + 1, vm.TreesBuilt);
        Assert.Equal(Enumerable.Range(1200, 100).Select(i => $"t{i:00000}"), vm.Rows.Where(r => r.Node.Kind == GitRepositoryNodeKind.Tag).Select(r => r.Text));
        Assert.True(Row(vm, "tags").IsExpanded);
        vm.Filter = string.Empty;
        await ShellFixture.PumpUntil(() => !vm.IsRebuilding);
        Assert.Equal(count + 5000 - 2, vm.Rows.Count); // the tags as the user left them, expanded

        // Still nothing selected after those builds and another ref's change: the current branch was selected only at first.
        Git(folder, "branch", "another");
        await StatusReadAsync(f, vm);
        Assert.NotNull(vm.Find("refs/heads/another"));
        Assert.Null(vm.Selected);
        Assert.Null(vm.SelectedRow);
        Assert.Null(vm.History);

        // Behind another tab: git's changes read nothing and build nothing.
        await ShellFixture.PumpUntil(() => !vm.IsReading);
        var document = f.Shell.Layout.ActiveDocument!;
        await f.Shell.OpenFileAsync(Path.Combine(folder, "README.md"));
        await ShellFixture.PumpUntil(() => !vm.IsViewShown);
        var (reads, trees) = (vm.RefsReads, vm.TreesBuilt);
        Git(folder, "branch", "while-hidden");
        Git(folder, "tag", "-d", "t00001");
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        var later = DateTime.UtcNow.AddMilliseconds(1200); // past the reload's 400 ms wait
        await ShellFixture.PumpUntil(() => DateTime.UtcNow >= later);
        Assert.Equal((reads, trees), (vm.RefsReads, vm.TreesBuilt));
        Assert.Null(vm.Find("refs/heads/while-hidden"));

        // Selecting a ref (a menu's View History) reads the refs first, once; showing the tab then reads nothing more.
        Assert.True(await vm.SelectAsync("refs/heads/while-hidden"));
        Assert.Equal(reads + 1, vm.RefsReads);
        Assert.Null(vm.Find("refs/tags/t00001"));
        f.Shell.Layout.OpenDocument(document, focus: true);
        tree = await RepositoryListAsync(f);
        await ShellFixture.PumpUntil(() => vm.IsViewShown && !vm.IsReading);
        Assert.Equal(reads + 1, vm.RefsReads);

        // The selected row is brought into view as the tab comes back (the list doesn't scroll to its selection by itself).
        await ShellFixture.PumpUntil(() => vm.SelectedRow is { } row && tree.ContainerFromItem(row) is ListBoxItem { IsSelected: true }, describe: () => $"selected {vm.SelectedRow?.Text}");
        Assert.Equal("while-hidden", vm.SelectedRow!.Text);

        // Shown again with changes made while hidden: one read when it's shown.
        await f.Shell.OpenFileAsync(Path.Combine(folder, "README.md"));
        await ShellFixture.PumpUntil(() => !vm.IsViewShown);
        Git(folder, "tag", "-d", "t00002");
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        later = DateTime.UtcNow.AddMilliseconds(1200);
        await ShellFixture.PumpUntil(() => DateTime.UtcNow >= later);
        Assert.Equal(reads + 1, vm.RefsReads);
        f.Shell.Layout.OpenDocument(document, focus: true);
        await ShellFixture.PumpUntil(() => vm.IsViewShown && vm.RefsReads == reads + 2 && !vm.IsReading);
        Assert.Null(vm.Find("refs/tags/t00002"));
    }

    /// <summary>
    /// The Git Repository tree's rows expand and collapse as Git Changes' do, through the same code (M57): Left and Right
    /// (or to the parent), Ctrl with each for everything under the row, + and -, * and /, Enter and Ctrl+Enter, the
    /// chevron and a double-click; Enter on a branch checks it out, F2 renames a branch, Delete deletes a tag.
    /// </summary>
    [AvaloniaFact]
    public async Task Git_repository_rows_expand_and_collapse_from_the_keyboard_the_chevron_and_a_double_click()
    {
        await using var f = ShellFixture.Create();
        var (vm, tree, _) = await ShowRepositoryAsync(f, tags: 20, branches: 300);
        var full = vm.Rows.Count;

        // Left collapses a folder, then goes to its parent; Right expands it; - and + do as Left and Right.
        await FocusAsync(tree, Row(vm, "team01"));
        f.Press(Key.Left);
        Assert.Equal((full - 100, false), (vm.Rows.Count, Row(vm, "team01").IsExpanded));
        f.Press(Key.Left);
        Assert.Same(vm.Rows[1], tree.SelectedItem); // the repository
        await FocusAsync(tree, Row(vm, "team01"));
        f.Press(Key.Right);
        Assert.Equal(full, vm.Rows.Count);
        f.Press(Key.Subtract);
        Assert.Equal(full - 100, vm.Rows.Count);
        f.Press(Key.Add);
        Assert.Equal(full, vm.Rows.Count);

        // / and * on the repository: everything under it, as Ctrl+Left and Ctrl+Right; Ctrl+Enter toggles everything.
        await FocusAsync(tree, vm.Rows[1]);
        f.Press(Key.Divide);
        Assert.Equal(["Branches / Tags", vm.Rows[1].Text, "Pull Requests", "No remote: publish the repository to a Git host for pull requests.", "Submodules"], vm.Rows.Select(r => r.Text));
        f.Press(Key.Multiply);
        Assert.Equal(full + 20, vm.Rows.Count); // the tags too
        Assert.True(Row(vm, "tags").IsExpanded);
        f.Press(Key.Left, RawInputModifiers.Control);
        Assert.Equal(5, vm.Rows.Count);
        f.Press(Key.Right, RawInputModifiers.Control);
        Assert.Equal(full + 20, vm.Rows.Count);
        f.Press(Key.Enter, RawInputModifiers.Control);
        Assert.Equal(5, vm.Rows.Count);
        f.Press(Key.Enter, RawInputModifiers.Control);
        Assert.Equal(full + 20, vm.Rows.Count);

        // Enter toggles a folder.
        await FocusAsync(tree, Row(vm, "tags"));
        f.Press(Key.Enter);
        Assert.Equal(full, vm.Rows.Count);
        f.Press(Key.Enter);
        Assert.Equal(full + 20, vm.Rows.Count);

        // The chevron toggles a folder on each click, a double-click's second too; a double-click on its row toggles it once.
        // The chevron selects nothing, as a tree view's expander (M57 review: the folder selected by the press dropped the
        // selected branch's history beside the tree).
        await FocusAsync(tree, Row(vm, "b0001"));
        await ShellFixture.PumpUntil(() => vm.History is { IsLoading: false, Rows.Count: > 0 });
        var (branch, history) = (vm.Selected, vm.History);
        var team02 = await ItemAsync(tree, Row(vm, "team02"));
        var chevron = team02.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("chevron"));
        Click(f.Window, chevron);
        Assert.False(Row(vm, "team02").IsExpanded);
        Assert.Same(branch, vm.Selected);
        Assert.Same(history, vm.History);
        Click(f.Window, chevron);
        Assert.True(Row(vm, "team02").IsExpanded);
        Assert.Same(branch, vm.Selected);
        Assert.Same(history, vm.History);
        Assert.True(tree.IsKeyboardFocusWithin);
        Click(f.Window, team02.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "team02"), clicks: 2);
        Assert.False(Row(vm, "team02").IsExpanded);
        Assert.Equal(ExpandCollapseState.Collapsed, TreeRowsAutomationTests.PeerOf(tree, Row(vm, "team02")).ExpandCollapseState);
        Click(f.Window, chevron);
        Assert.True(Row(vm, "team02").IsExpanded);

        // From outside the tree (the filter box), the chevron focuses the selected row, not the list itself, so the arrow
        // keys move from it (M57 review: the list held the focus, and Down moved nothing).
        var filterBox = tree.FindAncestorOfType<GitRepositoryView>()!.FindControl<TextBox>("FilterBox")!;
        filterBox.Focus();
        await ShellFixture.PumpUntil(() => filterBox.IsFocused && !tree.IsKeyboardFocusWithin);
        var selected = vm.Selected; // team02, which the double-click on its row selected
        Click(f.Window, chevron);
        Assert.False(Row(vm, "team02").IsExpanded);
        Assert.Same(selected, vm.Selected);
        Assert.Same(tree.ContainerFromItem(tree.SelectedItem!), f.Window.FocusManager?.GetFocusedElement());
        var before = tree.SelectedItem;
        f.Press(Key.Down);
        Assert.NotSame(before, tree.SelectedItem);
        Click(f.Window, chevron);
        Assert.True(Row(vm, "team02").IsExpanded);

        // Its tree item (M57): the third of the repository's branch folders and branches, at level 3; a screen reader's
        // Collapse and Expand do what the chevron does.
        var siblings = vm.Rows.Skip(2).TakeWhile(r => r.Depth > 1).Count(r => r.Depth == 2);
        TreeRowsAutomationTests.AssertTreeItem(tree, Row(vm, "team02"), level: 3, position: 3, size: siblings, ExpandCollapseState.Expanded);
        TreeRowsAutomationTests.Invoke(tree, Row(vm, "team02"), expand: false);
        Assert.False(Row(vm, "team02").IsExpanded);
        Assert.Equal(ExpandCollapseState.Collapsed, TreeRowsAutomationTests.PeerOf(tree, Row(vm, "team02")).ExpandCollapseState);
        TreeRowsAutomationTests.Invoke(tree, Row(vm, "team02"), expand: true);
        Assert.True(Row(vm, "team02").IsExpanded);
        await ItemAsync(tree, Row(vm, "b0201"));
        TreeRowsAutomationTests.AssertTreeItem(tree, Row(vm, "b0201"), level: 4, position: 2, size: 100, ExpandCollapseState.LeafNode);

        // F2 on a branch renames it; Delete on a tag deletes it (each asks first).
        await FocusAsync(tree, Row(vm, "b0150"));
        f.Press(Key.F2);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Equal("Rename Branch", f.Shell.Dialogs.Title);
        f.Shell.Dialogs.Cancel();
        await ShellFixture.PumpUntil(() => !f.Shell.Dialogs.IsOpen);
        vm.SetExpanded(Row(vm, "tags"), true);
        await FocusAsync(tree, Row(vm, "t00003"));
        f.Press(OperatingSystem.IsMacOS() ? Key.Back : Key.Delete, OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.None);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Equal("Delete the tag t00003?", f.Shell.Dialogs.Title);
        f.Shell.Dialogs.Cancel();
        await ShellFixture.PumpUntil(() => !f.Shell.Dialogs.IsOpen);

        // Enter on a branch checks it out; a double-click on another too.
        await FocusAsync(tree, Row(vm, "b0150"));
        f.Press(Key.Enter);
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "team01/b0150" });
        await ShellFixture.PumpUntil(() => vm.Find("refs/heads/team01/b0150") is { IsCurrent: true } && !vm.IsReading);
        // Selected first, its history loaded: the headless platform stamps a click when the test sends it, so the work of
        // the first click's selection counted against the double-click time (real platforms stamp it when the user clicks).
        await FocusAsync(tree, Row(vm, "b0007"));
        await ShellFixture.PumpUntil(() => vm.History is { IsLoading: false, Rows.Count: > 0 });
        var b0007 = await ItemAsync(tree, Row(vm, "b0007"));
        Click(f.Window, b0007.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "b0007"), clicks: 2);
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "team00/b0007" });
    }

    /// <summary>
    /// A double-click off the rows (the space under the last one) acts on no row (M57 review: it activated the selected
    /// one, so a branch selected to read its history was checked out); Git Changes' trees open the selected file there,
    /// as its lists do.
    /// </summary>
    [AvaloniaFact]
    public async Task A_double_click_under_the_git_repository_rows_toggles_and_checks_out_nothing()
    {
        await using var f = ShellFixture.Create();
        var (vm, tree, _) = await ShowRepositoryAsync(f, tags: 0, branches: 3);
        ShellFixture.RenderFrame(f.Window);
        var below = new Point(tree.Bounds.Width / 2, tree.Bounds.Height - 8);
        Assert.Null(TreeRows.RowAt(tree.InputHitTest(below))); // a few rows: the space under them is the list's
        var at = tree.TranslatePoint(below, f.Window)!.Value;
        void DoubleClickBelow()
        {
            f.Window.MouseMove(at);
            for (var i = 0; i < 2; i++)
            {
                f.Window.MouseDown(at, MouseButton.Left);
                f.Window.MouseUp(at, MouseButton.Left);
            }

            ShellFixture.Pump();
        }

        // A folder selected stays as it is.
        await FocusAsync(tree, Row(vm, "team00"));
        Assert.True(Row(vm, "team00").IsExpanded);
        DoubleClickBelow();
        Assert.True(Row(vm, "team00").IsExpanded);
        Assert.Same(Row(vm, "team00"), tree.SelectedItem);

        // A branch selected, its history read: not checked out.
        await FocusAsync(tree, Row(vm, "b0001"));
        await ShellFixture.PumpUntil(() => vm.History is { IsLoading: false, Rows.Count: > 0 });
        DoubleClickBelow();
        var until = DateTime.UtcNow.AddMilliseconds(1500);
        await ShellFixture.PumpUntil(() => DateTime.UtcNow >= until);
        Assert.Equal("main", f.Shell.Git.Status?.Branch);
        Assert.Same(Row(vm, "b0001"), tree.SelectedItem);
    }

    /// <summary>
    /// The document closed while its tree is built off the UI thread: the build shows nothing and starts no history (M57
    /// review: it selected the current branch and read its log for a closed document, a history nothing disposed).
    /// </summary>
    [AvaloniaFact]
    public async Task Git_repository_closed_during_a_build_starts_no_history()
    {
        var temp = Directory.CreateTempSubdirectory("codalus-repository-").FullName;
        var folder = CreateManyRefs(Path.Combine(temp, "Refs"), tags: 10, branches: 10);
        try
        {
            using var git = new Codalus.Workbench.Git.GitService(watch: false);
            Assert.True(await git.OpenAsync(folder, TestContext.Current.CancellationToken));
            await git.RefreshAsync(TestContext.Current.CancellationToken);
            GitRepositoryViewModel? vm = null;
            var (asked, histories) = (0, 0);
            vm = new GitRepositoryViewModel(new GitRepositoryActions
            {
                // Asked once by the read, then by the build for what it's built of, on the UI thread before it runs on the
                // pool: the document closes there, while the build is under way.
                Repositories = () =>
                {
                    if (++asked == 2)
                    {
                        vm!.Dispose();
                    }

                    return [git];
                },
                PullRequests = (_, _) => Task.FromResult(new GitRepositoryPullRequests([], null)),
                CreateHistory = (service, query, title, _) =>
                {
                    histories++;
                    return new GitHistoryViewModel(service, query, title, (_, _) => Task.CompletedTask);
                },
                Run = (_, _, _) => Task.CompletedTask,
            });
            await vm.LoadAsync(pullRequests: false);
            Assert.Equal(2, asked);
            Assert.False(vm.IsReading);
            Assert.Equal(0, vm.TreesBuilt);
            Assert.Equal(0, histories);
            Assert.Null(vm.History);
        }
        finally
        {
            DeleteFolder(temp);
        }
    }

    [AvaloniaFact]
    public async Task A_commit_of_ten_thousand_files_shows_rows_selects_its_first_file_and_keeps_them_across_tabs()
    {
        await using var f = ShellFixture.Create();
        var (folder, hash) = CreateLargeCommit(Path.Combine(f.UserData, "Commit"), folders: 1000, files: 10);
        f.Shell.Trust.Trust(folder);
        f.Window.Height = 900;
        await f.Shell.OpenFolderAsync(folder);
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "main" });
        var document = (await f.Shell.ShowCommitAsync(hash))!;
        var vm = document.ViewModel;

        // Every folder expanded, folders first: src, each folder and its files; the first file selected, its diff shown.
        Assert.Equal(1 + 1000 + 10_000, vm.Rows.Count);
        Assert.Equal(["src", "D000", "F00.cs", "F01.cs"], vm.Rows.Take(4).Select(r => r.Text));
        Assert.Equal([0, 1, 2, 2], vm.Rows.Take(4).Select(r => r.Depth));
        Assert.Equal(10_000, vm.Files.Count);
        Assert.Equal("src/D000/F00.cs", vm.SelectedRow?.Tip);
        Assert.Equal("A", vm.SelectedRow?.Letter);
        Assert.Equal("F00.cs in src/D000, added", vm.SelectedRow?.AccessibleName);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<GitCommitView>().Any(v => v.DiffView is { } d && d.RightText.Contains("// generated", StringComparison.Ordinal)));
        var view = f.Window.GetVisualDescendants().OfType<GitCommitView>().Single();
        var list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "Changes");
        await ShellFixture.PumpUntil(() => list.GetRealizedContainers().Any());
        Assert.InRange(list.GetRealizedContainers().Count(), 1, 300);
        Assert.Same(vm.SelectedRow, list.SelectedItem);

        // The keys, as Git Changes': Left to the folder, Left collapses it, Right expands it, Ctrl+Left and Ctrl+Right on src.
        await FocusAsync(list, vm.Rows[3]);
        f.Press(Key.Left);
        Assert.Same(vm.Rows[1], list.SelectedItem);
        f.Press(Key.Left);
        Assert.Equal(1 + 1000 + 9990, vm.Rows.Count);
        f.Press(Key.Right);
        Assert.Equal(11_001, vm.Rows.Count);
        await FocusAsync(list, vm.Rows[0]);
        f.Press(Key.Left, RawInputModifiers.Control);
        Assert.Single(vm.Rows);
        f.Press(Key.Right, RawInputModifiers.Control);
        Assert.Equal(11_001, vm.Rows.Count);

        // Its tree items (M57): D000's files at level 3, ten of them; Collapse and Expand on D000 as Left and Right.
        await ItemAsync(list, vm.Rows[3]);
        TreeRowsAutomationTests.AssertTreeItem(list, vm.Rows[3], level: 3, position: 2, size: 10, ExpandCollapseState.LeafNode);
        TreeRowsAutomationTests.AssertTreeItem(list, vm.Rows[1], level: 2, position: 1, size: 1000, ExpandCollapseState.Expanded);
        TreeRowsAutomationTests.Invoke(list, vm.Rows[1], expand: false);
        Assert.Equal(1 + 1000 + 9990, vm.Rows.Count);
        TreeRowsAutomationTests.Invoke(list, vm.Rows[1], expand: true);
        Assert.Equal(11_001, vm.Rows.Count);

        // A file far down selected; another tab, then back: the same row selected, brought into view.
        Assert.True(vm.SelectFile("src/D700/F05.cs"));
        var selected = vm.SelectedRow!;
        await ShellFixture.PumpUntil(() => list.ContainerFromItem(selected) is ListBoxItem { IsSelected: true });
        await ShellFixture.PumpUntil(() => view.DiffView is { } d && d.ViewModel == vm.Diff);
        await f.Shell.OpenFileAsync(Path.Combine(folder, "README.md"));
        await ShellFixture.PumpUntil(() => !f.Window.GetVisualDescendants().OfType<GitCommitView>().Any(v => v.IsEffectivelyVisible));
        f.Shell.Layout.OpenDocument(document, focus: true);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<GitCommitView>().Any(v => v.IsEffectivelyVisible));
        view = f.Window.GetVisualDescendants().OfType<GitCommitView>().Single(v => v.IsEffectivelyVisible);
        list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "Changes");
        await ShellFixture.PumpUntil(() => list.ContainerFromItem(selected) is ListBoxItem { IsSelected: true }, describe: () => $"selected {vm.SelectedRow?.Tip}");
        Assert.Same(selected, vm.SelectedRow);
        Assert.Equal(11_001, vm.Rows.Count);
    }

    [AvaloniaFact]
    public async Task Compare_of_branches_ten_thousand_files_apart_builds_the_shown_view_and_refresh_keeps_its_state()
    {
        await using var f = ShellFixture.Create();
        var (folder, _) = CreateLargeCommit(Path.Combine(f.UserData, "Compare"), folders: 1000, files: 10, branch: "many");
        f.Shell.Trust.Trust(folder);
        f.Window.Height = 900;
        await f.Shell.OpenFolderAsync(folder);
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "main" });
        var vm = (await f.Shell.OpenBranchCompareAsync("refs/heads/many"))!.ViewModel;

        // The tree only: the repository, src, each folder and its files, built once; the list isn't.
        Assert.Equal("10000 files", vm.CountText);
        Assert.Equal(1 + 1 + 1000 + 10_000, vm.Rows.Count);
        Assert.Equal((1, 0), vm.ViewsBuilt);
        Assert.Empty(vm.Items);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<GitCompareView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<GitCompareView>().Single();
        var tree = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "Tree");
        await ShellFixture.PumpUntil(() => tree.GetRealizedContainers().Any());
        Assert.InRange(tree.GetRealizedContainers().Count(), 1, 300);

        // A folder collapsed and a file selected: Refresh keeps both, and changes no row.
        GitChangeRow Row(string path) => vm.Rows.Single(r => r.Node.RelativePath == path);
        await FocusAsync(tree, Row("src/D001"));
        f.Press(Key.Left);
        Assert.False(Row("src/D001").IsExpanded);

        // Its tree item (M57): src's second folder of 1,000, at level 3; Expand and Collapse as Right and Left.
        TreeRowsAutomationTests.AssertTreeItem(tree, Row("src/D001"), level: 3, position: 2, size: 1000, ExpandCollapseState.Collapsed);
        TreeRowsAutomationTests.Invoke(tree, Row("src/D001"), expand: true);
        Assert.True(Row("src/D001").IsExpanded);
        await ItemAsync(tree, Row("src/D001/F09.cs"));
        TreeRowsAutomationTests.AssertTreeItem(tree, Row("src/D001/F09.cs"), level: 4, position: 10, size: 10, ExpandCollapseState.LeafNode);
        TreeRowsAutomationTests.Invoke(tree, Row("src/D001"), expand: false);
        Assert.False(Row("src/D001").IsExpanded);
        var file = Row("src/D002/F03.cs");
        await FocusAsync(tree, file);
        Assert.Same(file, vm.SelectedRow);
        var rows = Changes(vm.Rows);
        await vm.LoadAsync();
        Assert.Empty(rows);
        Assert.Equal((2, 0), vm.ViewsBuilt);
        Assert.False(Row("src/D001").IsExpanded);
        Assert.Equal(1 + 1 + 1000 + 9990, vm.Rows.Count);
        Assert.Same(file, vm.SelectedRow);
        Assert.Same(file, tree.SelectedItem);

        // Enter opens the file's diff.
        List<string> opened = [];
        vm.OpenFile = changed =>
        {
            opened.Add(changed.Path);
            return Task.CompletedTask;
        };
        await FocusAsync(tree, file);
        f.Press(Key.Enter);
        Assert.Equal([Path.Combine(folder, "src", "D002", "F03.cs")], opened);

        // The list: filled with one reset, once; back to the tree, which isn't built again.
        var items = Changes(vm.Items);
        vm.IsTreeView = false;
        await ShellFixture.PumpUntil(() => vm.Items.Count == 10_000);
        Assert.Equal([NotifyCollectionChangedAction.Reset], items);
        Assert.Equal((2, 1), vm.ViewsBuilt);
        vm.IsTreeView = true;
        ShellFixture.Pump();
        Assert.Equal((2, 1), vm.ViewsBuilt);
        Assert.Same(file, vm.SelectedRow);

        // Refresh in the list builds the list again only; the tree is built when it's shown.
        vm.IsTreeView = false;
        await vm.LoadAsync();
        Assert.Equal((2, 2), vm.ViewsBuilt);
        vm.IsTreeView = true;
        await ShellFixture.PumpUntil(() => vm.ViewsBuilt.Tree == 3);
        Assert.False(Row("src/D001").IsExpanded);

        // The list chosen while a Refresh's tree is built: the tree is shown, then the list is built of the new files.
        vm.TreeBuilt = () =>
        {
            vm.TreeBuilt = null;
            vm.ViewIndex = 1;
        };
        await vm.LoadAsync();
        Assert.True(vm.IsListView);
        Assert.Equal((4, 3), vm.ViewsBuilt);
        Assert.Equal(10_000, vm.Items.Count);
        Assert.Same(vm.Files[0], vm.Items[0]);

        // Refreshed in the list, then Tree, List and Tree while its tree is built, and a folder collapsed meanwhile: one
        // tree built at a time, built again for the folder, which stays collapsed.
        await vm.LoadAsync();
        Assert.Equal((4, 4), vm.ViewsBuilt);
        vm.TreeBuilt = () =>
        {
            vm.TreeBuilt = null;
            vm.ViewIndex = 1;
            vm.ViewIndex = 0;
            vm.Changes.SetExpanded(Row("src/D003"), false);
        };
        vm.ViewIndex = 0;
        await ShellFixture.PumpUntil(() => vm.ViewsBuilt.Tree >= 5);
        var until = DateTime.UtcNow.AddMilliseconds(500);
        await ShellFixture.PumpUntil(() => DateTime.UtcNow >= until);
        Assert.Equal((5, 4), vm.ViewsBuilt);
        Assert.False(Row("src/D001").IsExpanded);
        Assert.False(Row("src/D003").IsExpanded);
        Assert.Equal(1 + 1 + 1000 + 9980, vm.Rows.Count);
    }
}
