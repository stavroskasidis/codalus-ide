using System.Diagnostics;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Git;
using Codalus.Workbench.Menus;

namespace Codalus.Shell.Tests;

/// <summary>
/// The Git Changes window's folder menu and branch drop-down (#23, D38, approved as drawn in
/// docs/design/controls/GitChanges.dc.html): the menus of its files, folders, conflicts and repository node built from the
/// registered location gitChanges/context, the branch drop-down with each branch's menu from gitChanges/branch, and the
/// prompts they open. On plain folders made into repositories with a local bare remote, so no build or language host
/// starts and nothing leaves the machine.
/// </summary>
public sealed class GitChangesMenuTests
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

    private static void Configure(string repository)
    {
        Git(repository, "config", "user.name", "Ada Tester");
        Git(repository, "config", "user.email", "ada@example.com");
        Git(repository, "config", "commit.gpgsign", "false");
        Git(repository, "config", "core.autocrlf", "false");
    }

    private static void Write(string repository, string file, string text)
    {
        var path = Path.Combine(repository, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text + "\n");
    }

    private static void CommitFile(string repository, string file, string message)
    {
        Write(repository, file, message);
        Git(repository, "add", ".");
        Git(repository, "commit", "-q", "-m", message);
    }

    private static JsonElement Json(string value) => JsonSerializer.SerializeToElement(value);

    private static async Task OpenAsync(ShellFixture f, string folder)
    {
        f.Shell.Trust.Trust(folder);
        await f.Shell.OpenFolderAsync(folder);
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is not null && f.Shell.Workbench.Commands.IsEnabled("git.stage"));
    }

    /// <summary>
    /// T1-T12 and P1-P3: each node's menu is gitChanges/context's entries that apply (hidden, never grayed: question 3),
    /// grouped open, stage, history, path and tree, each with its icon and none with a key (question 13). With a filter,
    /// the section headers and the folder say how many files show (T6, question 15), and a folder action acts on those
    /// (question 2): Discard asks with Cancel the default at the trailing edge and lists exactly the files it acts on, and
    /// says how many the filter hides; Stash stashes only the folder's files, untracked included; Expand and Collapse All
    /// act on the folders under the node; Add to .gitignore ignores a folder of untracked files.
    /// </summary>
    [AvaloniaFact]
    public async Task Folder_file_and_repository_menus_come_from_the_registered_location()
    {
        await using var f = ShellFixture.Create();
        var folder = Path.Combine(f.UserData, "BuildAndRun");
        Directory.CreateDirectory(folder);
        Git(folder, "init", "-q", "-b", "main");
        Configure(folder);
        foreach (var file in new[] { "src/Greetings/Greeter.cs", "src/Greetings/GreeterOptions.cs", "src/Greetings/Greetings.csproj", "src/Greetings/Salutation.cs", "src/Hello/Program.cs", "tools/setup.ps1", "tools/setup.sh", "README.md" })
        {
            Write(folder, file, "initial " + file);
        }

        Git(folder, "add", ".");
        Git(folder, "commit", "-q", "-m", "initial");
        foreach (var file in new[] { "src/Greetings/Greeter.cs", "src/Greetings/GreeterOptions.cs", "src/Greetings/Greetings.csproj", "src/Greetings/Salutation.cs", "README.md", "src/Hello/Program.cs" })
        {
            Write(folder, file, "changed " + file);
        }

        Write(folder, "src/Greetings/Farewell.cs", "farewell");
        Write(folder, "notes/ideas.md", "ideas");
        Write(folder, "notes/todo.md", "todo");
        File.Delete(Path.Combine(folder, "tools", "setup.ps1"));
        File.Delete(Path.Combine(folder, "tools", "setup.sh"));
        Directory.Delete(Path.Combine(folder, "tools"));
        Git(folder, "add", "src/Hello/Program.cs");

        await OpenAsync(f, folder);
        var vm = f.Shell.GitChanges;
        await ShellFixture.PumpUntil(() => vm.Changes.Count == 10 && vm.Staged.Count == 1);

        // The trees are built while the window is shown (M56).
        f.Shell.Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => vm.IsViewShown && vm.ChangesTree.Count == 1 && !vm.IsRefreshing);
        string P(params string[] parts) => Path.Combine([folder, .. parts]);
        string[] Commands(string path, string section, string kind) => [.. f.Shell.GitChangesMenuItems(path, section, kind).Select(i => i.Command)];

        // T2: a folder of Changes; Add to .gitignore hidden (it has tracked files).
        string[] path = ["explorer.reveal", "terminal.openInTerminal", "explorer.copyPath", "explorer.copyRelativePath"];
        Assert.Equal(["git.stage", "git.discard", "git.stash", "git.history", .. path, "gitChanges.expandAll", "gitChanges.collapseAll"], Commands(P("src", "Greetings"), "changes", "folder"));
        var groups = f.Shell.GitChangesMenuItems(P("src", "Greetings"), "changes", "folder").Select(i => i.Group).Distinct();
        Assert.Equal(["2-stage", "3-history", "4-path", "5-tree"], groups);
        Assert.All(f.Shell.GitChangesMenuItems(P("src", "Greetings"), "changes", "folder"), i => Assert.Null(i.RuleKeyLabel));

        // T3: a staged folder: Unstage, no Discard. T4: a folder that's gone: no Reveal or Open in Terminal; Copy Path stays.
        Assert.Equal(["git.unstage", "git.stash", "git.history", .. path, "gitChanges.expandAll", "gitChanges.collapseAll"], Commands(P("src", "Hello"), "staged", "folder"));
        Assert.Equal(["git.stage", "git.discard", "git.stash", "git.history", "explorer.copyPath", "explorer.copyRelativePath", "gitChanges.expandAll", "gitChanges.collapseAll"], Commands(P("tools"), "changes", "folder"));

        // T5: every file under it untracked: Add to .gitignore.
        Assert.Contains("explorer.addToGitignore", Commands(P("notes"), "changes", "folder"));

        // T7: a file: Compare with Unmodified and Open first, no Expand or Collapse. T9: a deleted file: no Open, Reveal or Terminal.
        Assert.Equal(["git.showDiff", "workbench.openFile", "git.stage", "git.discard", "git.stash", "git.history", .. path], Commands(P("src", "Greetings", "Greeter.cs"), "changes", "file"));
        Assert.Equal(["git.showDiff", "git.stage", "git.discard", "git.stash", "git.history", "explorer.copyPath", "explorer.copyRelativePath"], Commands(P("tools", "setup.sh"), "changes", "file"));
        Assert.Equal("Open", f.Shell.GitChangesMenuItems(P("README.md"), "changes", "file").Single(i => i.Command == "workbench.openFile").Title);

        // T10: the repository node: no Copy Relative Path.
        Assert.Equal(["git.stage", "git.discard", "git.stash", "git.history", "explorer.reveal", "terminal.openInTerminal", "explorer.copyPath", "gitChanges.expandAll", "gitChanges.collapseAll"], Commands(folder, "changes", "repository"));

        // The menu as built: every entry has its icon and its title as its name; Reveal names the OS's file manager.
        var greetings = vm.FindNode("changes", P("src", "Greetings"))!;
        var menu = vm.MenuOf(greetings)!;
        var entries = menu.Items.OfType<Avalonia.Controls.MenuItem>().ToList();
        Assert.All(entries, e => Assert.NotNull(e.Icon));
        Assert.Contains(entries, e => AutomationProperties.GetName(e) == (OperatingSystem.IsWindows() ? "Reveal in File Explorer" : OperatingSystem.IsMacOS() ? "Reveal in Finder" : "Reveal in File Manager"));
        Assert.Equal(3, menu.Items.OfType<Avalonia.Controls.Separator>().Count());

        // Every folder's accessible name has its count (question 15); the count shows on the row only with a filter (T6).
        Assert.Equal($"src{Path.DirectorySeparatorChar}Greetings, folder, 5 changed files", greetings.AccessibleName);
        Assert.Null(greetings.CountText);
        Assert.True(greetings.CanDiscard);
        vm.ShowFilter = true;
        vm.FilterText = "Greeter";
        await ShellFixture.PumpUntil(() => vm.ChangesHeader == "Changes (2 of 10)" && !vm.IsRefreshing);
        greetings = vm.FindNode("changes", P("src", "Greetings"))!;
        Assert.Equal("2 of 5 files", greetings.CountText);
        Assert.Equal("Changes (2 of 10)", vm.ChangesHeader);
        Assert.Equal("Staged Changes (0 of 1)", vm.StagedHeader);
        Assert.True(vm.StagedFilteredOut);
        Assert.Equal("No files match “Greeter”.", vm.FilteredOutText);

        // P2: Discard from the folder's hover button, filtered: the two files shown; Enter is Cancel and changes nothing.
        _ = vm.RunOnAsync("git.discard", greetings);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Equal($"Discard changes in src{Path.DirectorySeparatorChar}Greetings?", f.Shell.Dialogs.Title);
        Assert.Equal("The unstaged changes to these 2 files will be lost. This can't be undone.", f.Shell.Dialogs.Message);
        Assert.Contains(f.Shell.Dialogs.Notes, n => n.Text == "3 more files under this folder are hidden by the filter and won't change.");
        Assert.Equal(["Files", "M  src/Greetings/Greeter.cs", "M  src/Greetings/GreeterOptions.cs"], f.Shell.Dialogs.Changes.Select(c => c.Text));
        Assert.Equal($"{folder} · Changes · filtered by “Greeter”", f.Shell.Dialogs.Context);
        Assert.Equal(["Discard", "Cancel"], f.Shell.Dialogs.Buttons.Select(b => b.Label));
        Assert.Equal("Cancel", f.Shell.Dialogs.Buttons.Single(b => b.IsDefault).Label);
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => !f.Shell.Dialogs.IsOpen);
        Assert.Equal(10, vm.Changes.Count);

        _ = vm.RunOnAsync("git.discard", vm.FindNode("changes", P("src", "Greetings"))!);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        f.Shell.Dialogs.Buttons.Single(b => b.Label == "Discard").Command.Execute(null);
        await ShellFixture.PumpUntil(() => vm.Changes.Count == 8);
        Assert.Equal("initial src/Greetings/Greeter.cs\n", File.ReadAllText(P("src", "Greetings", "Greeter.cs")));
        Assert.Equal("changed src/Greetings/Salutation.cs\n", File.ReadAllText(P("src", "Greetings", "Salutation.cs")));
        vm.ShowFilter = false;

        // P3: Stash on the folder: only its files, the untracked one too (checked by default); the rest stays.
        f.Shell.Run("git.stash", GitChangesViewModel.ArgumentsFor(P("src", "Greetings"), "changes", vm.RowsUnder(P("src", "Greetings"), "changes", shownOnly: true).Select(r => r.Status.Path)));
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Equal($"Stash Changes in src{Path.DirectorySeparatorChar}Greetings", f.Shell.Dialogs.Title);
        Assert.True(f.Shell.Dialogs.HasOption);
        Assert.True(f.Shell.Dialogs.OptionChecked);
        Assert.Equal("Include untracked files", f.Shell.Dialogs.OptionLabel);
        Assert.Contains(f.Shell.Dialogs.Notes, n => n.Text == $"Only these 3 files under src{Path.DirectorySeparatorChar}Greetings are stashed, 1 of them untracked; changes elsewhere stay.");
        f.Shell.Dialogs.InputText = "Farewells, half done";
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => vm.Stashes.Count == 1 && vm.Changes.Count == 5);
        Assert.False(File.Exists(P("src", "Greetings", "Farewell.cs")));
        Assert.Contains(vm.Changes, r => r.Name == "README.md");
        Assert.Contains("Farewells, half done", vm.Stashes[0].Message, StringComparison.Ordinal);

        // Collapse All and Expand All on the repository node: every folder under it.
        var repository = Assert.Single(vm.ChangesTree);
        await f.ExecuteAsync("gitChanges.collapseAll", Json(folder), Json("changes"));
        Assert.All(vm.ChangesTree.SelectMany(Folders), n => Assert.False(n.IsExpanded));
        await f.ExecuteAsync("gitChanges.expandAll", Json(folder), Json("changes"));
        Assert.All(vm.ChangesTree.SelectMany(Folders), n => Assert.True(n.IsExpanded));

        // T5: Add to .gitignore on the untracked folder.
        await f.Shell.RunAsync("explorer.addToGitignore", GitChangesViewModel.ArgumentsFor(P("notes"), "changes", []), f.Shell.GitChangeScope(P("notes"), "changes", "folder"));
        await ShellFixture.PumpUntil(() => vm.Changes.All(r => !r.Status.RelativePath.StartsWith("notes/", StringComparison.Ordinal)));
        Assert.Contains("/notes/", File.ReadAllText(P(".gitignore")), StringComparison.Ordinal);

        static IEnumerable<GitChangeNode> Folders(GitChangeNode node) => node.IsFile ? [] : node.Children.SelectMany(Folders).Prepend(node);
    }

    /// <summary>A bare origin with main, feature/sign-in (diverged), release/0.6 (behind) and fix/null-greeting (only there), and local-only here.</summary>
    private static (string Folder, string Other) BranchRepository(string root)
    {
        var folder = Path.Combine(root, "repository");
        var bare = Path.Combine(root, "remote.git");
        var other = Path.Combine(root, "other");
        Directory.CreateDirectory(folder);
        Git(root, "init", "-q", "--bare", "-b", "main", bare);
        Git(folder, "init", "-q", "-b", "main");
        Configure(folder);
        CommitFile(folder, "README.md", "initial");
        Git(folder, "remote", "add", "origin", bare);
        Git(folder, "push", "-q", "-u", "origin", "main");
        foreach (var branch in new[] { "feature/sign-in", "release/0.6", "fix/null-greeting" })
        {
            Git(folder, "branch", branch);
            Git(folder, "push", "-q", "-u", "origin", branch);
        }

        Git(folder, "branch", "-D", "fix/null-greeting");
        Git(folder, "branch", "local-only");
        // core.autocrlf for the checkout itself: Windows runners set it to true, so the clone's README.md came out with CRLF,
        // which the first `git add .` here then committed into release/0.6 (a fourth file in its compare).
        Git(root, "clone", "-q", "-c", "core.autocrlf=false", bare, other);
        Configure(other);
        Git(other, "switch", "-q", "release/0.6");
        CommitFile(other, "r1.txt", "release one");
        CommitFile(other, "r2.txt", "release two");
        Git(other, "switch", "-q", "feature/sign-in");
        CommitFile(other, "theirs.txt", "Handle expired tokens");
        Git(other, "push", "-q", "origin", "release/0.6", "feature/sign-in");
        Git(folder, "fetch", "-q", "origin");
        Git(folder, "switch", "-q", "feature/sign-in");
        CommitFile(folder, "mine.txt", "Store tokens per user");
        Git(folder, "switch", "-q", "main");
        CommitFile(folder, "main.txt", "main work");
        return (folder, other);
    }

    /// <summary>
    /// B1-B26: the drop-down lists the branches (current first, then by name; origin's without the prefix, question 4) with
    /// their chips; it searches; each branch's menu is gitChanges/branch's entries for it, titled for the branches
    /// (question 5) with only the list's keys (F2, Delete); a remote branch with a local one switches to it and says how far
    /// behind (question 7); Update keeps the drop-down open, marks the row and reloads it, and a diverged branch or one in
    /// another worktree is explained in its banner with the next step; a click checks a branch out and closes it.
    /// </summary>
    [AvaloniaFact]
    public async Task The_branch_drop_down_lists_the_branches_each_with_its_menu()
    {
        await using var f = ShellFixture.Create();
        var (folder, _) = BranchRepository(f.UserData);
        await OpenAsync(f, folder);
        var picker = f.Shell.GitBranches;
        f.Shell.Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<GitChangesView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<GitChangesView>().Single();

        // The button: the branch's name and its accessible name; a click opens the drop-down.
        var button = view.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.Name == "BranchButton");
        Assert.Equal("Branch: main", AutomationProperties.GetName(button));
        Assert.Equal("main", Avalonia.Controls.ToolTip.GetTip(button));
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await ShellFixture.PumpUntil(() => picker.IsOpen && picker.Rows.Count > 0 && !picker.IsLoading);
        Assert.True(view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().Single(p => p.Name == "BranchPopup").IsOpen);

        string[] Row(GitBranchRow r) => r.IsHeader ? ["#" + r.HeaderText] : [r.Name + (r.IsCurrent ? " ✓" : string.Empty) + (r.HasTrack ? " " + r.Track : string.Empty)];
        Assert.Equal(
            ["#Local branches", "main ✓ ↑1", "feature/sign-in ↑1 ↓1", "local-only", "release/0.6 ↓2", "#origin", "feature/sign-in", "fix/null-greeting", "main", "release/0.6"],
            picker.Rows.SelectMany(Row));
        var feature = picker.Rows.Single(r => r.Key == "l:feature/sign-in");
        Assert.Equal("1 outgoing, 1 incoming (origin/feature/sign-in)", feature.TrackName);
        Assert.Equal("feature/sign-in, 1 outgoing, 1 incoming (origin/feature/sign-in)", feature.AccessibleName);
        Assert.Equal("8 of 8", picker.CountText);

        // B2, B3: searching; nothing matches: the footer offers the branch with its name.
        picker.Query = "sign";
        Assert.Equal(["#Local branches", "feature/sign-in ↑1 ↓1", "#origin", "feature/sign-in"], picker.Rows.SelectMany(Row));
        Assert.Equal([8, 9, 10, 11], picker.Rows[1].Highlights);
        Assert.Equal("2 of 8", picker.CountText);
        picker.Query = "zzz";
        Assert.True(picker.IsEmpty);
        Assert.Equal(("No branches match “zzz”.", "New Branch 'zzz'…", "zzz"), (picker.EmptyText, picker.FooterText, picker.FooterName));
        picker.Query = string.Empty;

        GitBranchDetails Branch(string fullRef) => picker.Branches.Single(b => b.FullRef == fullRef);
        string[] Menu(string fullRef) => [.. f.Shell.GitBranchMenuItems(Branch(fullRef)).Select(i => i.Title + (i.Keys is { } k ? " (" + k + ")" : string.Empty))];
        var delete = OperatingSystem.IsMacOS() ? "⌘⌫" : "Delete";

        // B7: another local branch with an upstream; B22: Update from its upstream before Push.
        Assert.Equal(
            ["Checkout", "New Local Branch From…", "Merge 'feature/sign-in' into 'main'", "Rebase 'main' onto 'feature/sign-in'", "Update from 'origin/feature/sign-in'", "Push", "Rename… (F2)", $"Delete ({delete})", "Compare with Current Branch", "View History", "Copy Branch Name"],
            Menu("refs/heads/feature/sign-in"));

        // B9: the current branch; B10: a remote branch no local branch has; B11: one with a local branch, 2 commits behind it.
        Assert.Equal(["New Local Branch From…", "Update from 'origin/main'", "Push", "Rename… (F2)", "View History", "Copy Branch Name"], Menu("refs/heads/main"));
        Assert.Equal(["Checkout", "New Local Branch From…", "Merge 'origin/fix/null-greeting' into 'main'", "Rebase 'main' onto 'origin/fix/null-greeting'", $"Delete from origin… ({delete})", "Compare with Current Branch", "View History", "Copy Branch Name"], Menu("refs/remotes/origin/fix/null-greeting"));
        Assert.Equal("Switch to Local 'release/0.6' (2 commits behind)", Menu("refs/remotes/origin/release/0.6")[0]);

        // A local branch without an upstream: no Update.
        Assert.DoesNotContain(Menu("refs/heads/local-only"), t => t.StartsWith("Update", StringComparison.Ordinal));

        // The menu as built over the drop-down: titles, keys only the rule's (no Ctrl+0 Ctrl+H on View History), icons.
        var menu = f.Shell.GitBranchMenu(Branch("refs/heads/feature/sign-in"))!;
        var entries = menu.Items.OfType<Avalonia.Controls.MenuItem>().ToList();
        Assert.All(entries, e => Assert.NotNull(e.Icon));
        Assert.Equal("View History", AutomationProperties.GetName(entries.Single(e => AutomationProperties.GetName(e) == "View History")));
        Assert.Null(AutomationProperties.GetAcceleratorKey(entries.Single(e => AutomationProperties.GetName(e) == "View History")));
        Assert.Equal("F2", AutomationProperties.GetAcceleratorKey(entries.Single(e => AutomationProperties.GetName(e) == "Rename…")));

        // B22-B24: Update from the drop-down: it stays open, the row says Updating… while it runs, then the chip goes.
        var messages = new List<string>();
        f.Shell.StatusBar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(f.Shell.StatusBar.Message) && f.Shell.StatusBar.Message is { } message)
            {
                messages.Add(message);
            }
        };
        var sawBusy = false;
        picker.Rows.Single(r => r.Key == "l:release/0.6").PropertyChanged += (_, e) => sawBusy |= e.PropertyName == nameof(GitBranchRow.Busy);
        await f.Shell.RunAsync("git.updateBranch", [Json("refs/heads/release/0.6")], f.Shell.GitBranchScope(Branch("refs/heads/release/0.6")));
        await ShellFixture.PumpUntil(() => messages.Contains("Updated release/0.6 from origin/release/0.6: 2 commits.") && picker.Rows.Any(r => r.Key == "l:release/0.6" && !r.HasTrack), describe: () => string.Join(" | ", messages));
        Assert.True(sawBusy);
        Assert.True(picker.IsOpen);

        // B25: diverged: the banner names both, with Checkout; nothing changes.
        var before = Git(folder, "rev-parse", "feature/sign-in").Trim();
        await f.Shell.RunAsync("git.updateBranch", [Json("refs/heads/feature/sign-in")], f.Shell.GitBranchScope(Branch("refs/heads/feature/sign-in")));
        await ShellFixture.PumpUntil(() => picker.HasBanner && picker.IsBannerWarning);
        Assert.Equal("feature/sign-in has diverged from origin/feature/sign-in: 1 commit here, 1 there. Update only fast-forwards; check it out to merge or rebase.", picker.Banner);
        Assert.Equal("Checkout", picker.BannerAction);
        Assert.Equal(before, Git(folder, "rev-parse", "feature/sign-in").Trim());
        Assert.True(picker.IsOpen);

        // B12, B13: a branch checked out in another worktree: the chip, no Checkout or Delete; Enter says where instead.
        var worktree = Path.Combine(f.UserData, "local-tree");
        Git(folder, "worktree", "add", "-q", worktree, "local-only");
        await picker.LoadAsync();
        var local = picker.Rows.Single(r => r.Key == "l:local-only");
        Assert.True(local.IsInOtherWorktree);
        Assert.Equal($"Checked out in {Path.GetFullPath(worktree)}", local.WorktreeTip);
        Assert.DoesNotContain("Checkout", Menu("refs/heads/local-only"));
        Assert.DoesNotContain(Menu("refs/heads/local-only"), t => t.StartsWith("Delete", StringComparison.Ordinal));
        await picker.Checkout!(local.Branch!);
        Assert.Equal($"local-only is checked out in {Path.GetFullPath(worktree)}, so it can't be checked out here too.", picker.Banner);
        Assert.Equal("Open in New Window", picker.BannerAction);
        Assert.Equal("main", f.Shell.Git.Status!.Branch);

        // A click (here, the row's action) checks a branch out and closes the drop-down.
        await picker.Checkout!(Branch("refs/heads/feature/sign-in"));
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "feature/sign-in" } && !picker.IsOpen);

        // B14: a detached HEAD: the banner, no check, Merge and Rebase name HEAD.
        Git(folder, "switch", "-q", "--detach", "main");
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        // Loaded again after the switch: an opening keeps the last list shown (not loading) until its own load replaces
        // it, so waiting for rows read the old ones first on macOS, main still current.
        picker.IsOpen = true;
        await picker.LoadAsync();
        Assert.StartsWith("HEAD is detached at ", picker.Banner, StringComparison.Ordinal);
        Assert.DoesNotContain(picker.Rows, r => r.IsCurrent);
        Assert.Contains("Merge 'feature/sign-in' into HEAD", Menu("refs/heads/feature/sign-in"));
        Assert.Contains("Compare with HEAD", Menu("refs/heads/feature/sign-in"));
        Assert.Contains("Update from 'origin/main'", Menu("refs/heads/main"));
        Assert.False(f.Shell.GitChanges.ShowPublishBranch);
        Assert.StartsWith("HEAD is detached at ", f.Shell.GitChanges.OutgoingIncomingTooltip, StringComparison.Ordinal);
        picker.IsOpen = false;
    }

    /// <summary>
    /// A load that a newer one replaces (the repositories' refresh 750 ms after git changed .git, Update's reload) returns
    /// only once the newer one has landed, so whoever awaited it reads the branches as they are, not the list before: the
    /// branch drop-down test read main as current after a detach, and a worktree without its chip, when the repositories'
    /// refresh came in the middle of its own load (macOS, runs 40480055094, 40538692111, 40547948961).
    /// </summary>
    [AvaloniaFact]
    public async Task A_load_that_a_newer_one_replaces_returns_with_the_newer_ones_branches()
    {
        await using var f = ShellFixture.Create();
        var (folder, _) = BranchRepository(f.UserData);
        await OpenAsync(f, folder);
        var picker = f.Shell.GitBranches;
        picker.IsOpen = true;
        await ShellFixture.PumpUntil(() => picker.Rows.Any(r => r.IsCurrent) && !picker.IsLoading);

        Git(folder, "switch", "-q", "--detach", "main");
        var first = picker.LoadAsync();
        var newer = picker.LoadAsync();
        await first;
        Assert.True(newer.IsCompleted);
        Assert.DoesNotContain(picker.Rows, r => r.IsCurrent);
        picker.IsOpen = false;
    }

    /// <summary>
    /// P4-P11 and C1-C3: New Local Branch From… makes a branch at another without switching when unchecked; Rename… shows
    /// git's rules and the names other branches have under the box; Delete offers the upstream in its one confirmation,
    /// then names what it compared with and lists the commits that would be lost, Delete plain and Cancel the default;
    /// a remote branch has its own confirmation; Compare with Current Branch opens "main → x" with name-status letters,
    /// or no differences with View History; during a merge, a new branch is made without being checked out.
    /// </summary>
    [AvaloniaFact]
    public async Task New_branch_rename_delete_and_compare_prompt_as_drawn()
    {
        await using var f = ShellFixture.Create();
        var (folder, _) = BranchRepository(f.UserData);
        await OpenAsync(f, folder);
        var dialogs = f.Shell.Dialogs;

        // P4: from feature/sign-in, unchecked: made there, main stays checked out.
        f.Shell.Run("git.createBranch", [Json("refs/heads/feature/sign-in")]);
        await ShellFixture.PumpUntil(() => dialogs.IsOpen);
        Assert.Equal(("New Local Branch", "Branch name:", "Based on: feature/sign-in"), (dialogs.Title, dialogs.Message, dialogs.Context));
        Assert.Equal(("Check out the new branch", true, true), (dialogs.OptionLabel, dialogs.OptionChecked, dialogs.OptionEnabled));
        dialogs.InputText = "main";
        Assert.Equal("A branch with that name exists.", dialogs.Error);
        dialogs.InputText = "feature/sign-in/tests";
        Assert.Equal("The branch 'feature/sign-in' is in the way: git can't have both.", dialogs.Error);
        dialogs.InputText = "bad name";
        Assert.Equal("A branch name can't contain spaces.", dialogs.Error);
        dialogs.InputText = "sign-in-tests";
        Assert.Null(dialogs.Error);
        dialogs.OptionChecked = false;
        dialogs.Accept();
        await ShellFixture.PumpUntil(() => Git(folder, "branch", "--list", "sign-in-tests").Trim().Length > 0);
        Assert.Equal(Git(folder, "rev-parse", "feature/sign-in").Trim(), Git(folder, "rev-parse", "sign-in-tests").Trim());
        Assert.Equal("main", f.Shell.Git.Status!.Branch);

        // P6: Rename… waits for a valid name.
        f.Shell.Run("git.renameBranch", [Json("refs/heads/sign-in-tests")]);
        await ShellFixture.PumpUntil(() => dialogs.IsOpen);
        Assert.Equal(("Rename Branch", "A new name for sign-in-tests:", "sign-in-tests"), (dialogs.Title, dialogs.Message, dialogs.InputText));
        dialogs.InputText = "release/0.6";
        Assert.Equal("A branch with that name exists.", dialogs.Error);
        dialogs.Accept();
        Assert.True(dialogs.IsOpen);
        dialogs.InputText = "tests/sign-in";
        dialogs.Accept();
        await ShellFixture.PumpUntil(() => Git(folder, "branch", "--list", "tests/sign-in").Trim().Length > 0);

        // P7: Delete feature/sign-in: the upstream unchecked, with the commit only origin has; Enter cancels.
        f.Shell.Run("git.deleteBranch", [Json("refs/heads/feature/sign-in")]);
        await ShellFixture.PumpUntil(() => dialogs.IsOpen);
        Assert.Equal(("Delete feature/sign-in?", "The branch is deleted from this repository.", "Local branch · tracks origin/feature/sign-in"), (dialogs.Title, dialogs.Message, dialogs.Context));
        Assert.Equal(["Delete", "Cancel"], dialogs.Buttons.Select(b => b.Label));
        Assert.Equal("Cancel", dialogs.Buttons.Single(b => b.IsDefault).Label);
        Assert.Equal(("Also delete origin/feature/sign-in on the server", false), (dialogs.OptionLabel, dialogs.OptionChecked));
        Assert.Equal("That deletes it for everyone. origin/feature/sign-in has 1 commit that isn't in feature/sign-in, and it goes too:", dialogs.OptionNote);
        Assert.Single(dialogs.OptionLines, l => l.Text.EndsWith("  Handle expired tokens", StringComparison.Ordinal));
        dialogs.Accept();
        await ShellFixture.PumpUntil(() => !dialogs.IsOpen);
        Assert.NotEmpty(Git(folder, "branch", "--list", "feature/sign-in").Trim());

        // P8, P10: the upstream too: compared with HEAD (main), both losses listed; Delete deletes both.
        f.Shell.Run("git.deleteBranch", [Json("refs/heads/feature/sign-in")]);
        await ShellFixture.PumpUntil(() => dialogs.IsOpen);
        dialogs.OptionChecked = true;
        dialogs.Buttons.Single(b => b.Label == "Delete").Command.Execute(null);
        await ShellFixture.PumpUntil(() => dialogs.IsOpen && dialogs.Title.Contains("isn't merged", StringComparison.Ordinal));
        Assert.Equal("feature/sign-in isn't merged into main", dialogs.Title);
        Assert.Equal("Compared with main (HEAD), since origin/feature/sign-in goes too", dialogs.Context);
        Assert.Equal("feature/sign-in has 1 commit that isn't in main, and origin/feature/sign-in 1 more. Deleting both loses these 2 unless another branch or a tag has them:", dialogs.Message);
        Assert.Contains(dialogs.Changes, l => l.Text.EndsWith("  Store tokens per user", StringComparison.Ordinal));
        Assert.Contains(dialogs.Changes, l => l.Text.EndsWith("  Handle expired tokens  (only on origin)", StringComparison.Ordinal));
        Assert.Equal("Cancel", dialogs.Buttons.Single(b => b.IsDefault).Label);
        dialogs.Buttons.Single(b => b.Label == "Delete").Command.Execute(null);
        await ShellFixture.PumpUntil(() => Git(folder, "branch", "--list", "feature/sign-in").Trim().Length == 0 && Git(folder, "branch", "-r", "--list", "origin/feature/sign-in").Trim().Length == 0);

        // P11: a remote branch's own confirmation.
        f.Shell.Run("git.deleteBranch", [Json("refs/remotes/origin/fix/null-greeting")]);
        await ShellFixture.PumpUntil(() => dialogs.IsOpen);
        Assert.Equal(("Delete origin/fix/null-greeting from origin?", "This deletes it on the server for everyone.", "Remote branch · no local branch tracks it"), (dialogs.Title, dialogs.Message, dialogs.Context));
        dialogs.Buttons.Single(b => b.Label == "Delete").Command.Execute(null);
        await ShellFixture.PumpUntil(() => Git(folder, "branch", "-r", "--list", "origin/fix/null-greeting").Trim().Length == 0);

        // C1: Compare with Current Branch: main → release/0.6, tip to tip.
        Git(folder, "fetch", "-q", "origin");
        await f.ExecuteAsync("git.compareBranch", Json("refs/remotes/origin/release/0.6"));
        var compare = f.Shell.Layout.ActiveDocument as GitCompareDocument;
        Assert.NotNull(compare);
        await ShellFixture.PumpUntil(() => !compare.ViewModel.IsLoading);
        Assert.Equal("main → origin/release/0.6", compare.Title);
        Assert.Equal("3 files", compare.ViewModel.CountText);
        Assert.Equal(["A r1.txt", "A r2.txt", "D main.txt"], compare.ViewModel.Files.Select(r => r.Letter + " " + r.Name).Order(StringComparer.Ordinal));

        // C3: the same files: no differences, with View History.
        Git(folder, "branch", "same", "main");
        await f.ExecuteAsync("git.compareBranch", Json("refs/heads/same"));
        compare = (GitCompareDocument)f.Shell.Layout.ActiveDocument!;
        await ShellFixture.PumpUntil(() => !compare.ViewModel.IsLoading);
        Assert.True(compare.ViewModel.IsEmpty);
        Assert.Equal("No differences: main and same have the same files. Their commits can still differ; View History lists them.", compare.ViewModel.EmptyText);

        // P5: during a merge, a new branch is made but not checked out.
        Git(folder, "switch", "-q", "tests/sign-in");
        File.WriteAllText(Path.Combine(folder, "main.txt"), "conflicting\n");
        Git(folder, "add", ".");
        Git(folder, "commit", "-q", "-m", "conflict");
        Git(folder, "switch", "-q", "main");
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "merge", "--no-edit", "tests/sign-in" })
        {
            start.ArgumentList.Add(argument);
        }

        using (var merge = Process.Start(start)!)
        {
            merge.StandardOutput.ReadToEnd();
            merge.WaitForExit();
        }

        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Operation: "merge" });
        f.Shell.Run("git.createBranch", [Json("refs/heads/tests/sign-in")]);
        await ShellFixture.PumpUntil(() => dialogs.IsOpen);
        Assert.Equal((false, false), (dialogs.OptionChecked, dialogs.OptionEnabled));
        Assert.Equal("Not while a merge is in progress: the branch is made at tests/sign-in, and main stays checked out.", dialogs.OptionNote);
        dialogs.InputText = "keep/sign-in";
        dialogs.Accept();
        await ShellFixture.PumpUntil(() => Git(folder, "branch", "--list", "keep/sign-in").Trim().Length > 0);
        Assert.Equal("main", f.Shell.Git.Status!.Branch);

        // B15: during the merge, Checkout, Merge, Rebase, Delete and main's Update are hidden; another branch's Update stays.
        await f.Shell.GitBranches.LoadAsync();
        Assert.Equal("A merge is in progress: until it is committed or aborted, Checkout, Merge, Rebase, Delete and main's Update are hidden, and a new branch isn't checked out.", f.Shell.GitBranches.Banner);
        var items = f.Shell.GitBranchMenuItems(f.Shell.GitBranches.Branches.Single(b => b.FullRef == "refs/heads/release/0.6")).Select(i => i.Command).ToList();
        Assert.DoesNotContain("git.switchBranch", items);
        Assert.DoesNotContain("git.merge", items);
        Assert.DoesNotContain("git.deleteBranch", items);
        Assert.Contains("git.updateBranch", items);
        Assert.Contains("git.createBranch", items);
        Assert.DoesNotContain("git.updateBranch", f.Shell.GitBranchMenuItems(f.Shell.GitBranches.Branches.Single(b => b.FullRef == "refs/heads/main")).Select(i => i.Command));
    }
}
