using System.Diagnostics;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.Editor;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Git;

namespace Codalus.Shell.Tests;

/// <summary>Git in the shell (ADR 0008) on a repository made from samples/BuildAndRun.</summary>
public sealed class GitShellTests
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

    private static int GitExitCode(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        error.GetAwaiter().GetResult();
        return process.ExitCode;
    }

    private static void Configure(string repository)
    {
        Git(repository, "config", "user.name", "Ada Tester");
        Git(repository, "config", "user.email", "ada@example.com");
        Git(repository, "config", "commit.gpgsign", "false");
        Git(repository, "config", "core.autocrlf", "false");
    }

    private static void CommitFile(string repository, string file, string message)
    {
        File.WriteAllText(Path.Combine(repository, file), message + "\n");
        Git(repository, "add", ".");
        Git(repository, "commit", "-q", "-m", message);
    }

    /// <summary>
    /// Update Branch… brings a branch up to date from its remote without checking it out and pulling (D38, question 21).
    /// The user asked for it on a branch's own menu, which waits for the Git Changes window's branch drop-down (#23, B22;
    /// TODO(issue) in ShellHost.Git.cs); meanwhile Git → Branches, the Git Changes window's ⋯ menu and the palette offer
    /// it, and this test covers those and the argument a branch's menu will pass. The quick pick lists the local branches
    /// that have an upstream, the current one first; picking one fast-forwards it, with a cancellable task in the status
    /// bar, while the checked-out branch and its files stay as they are. Then each result in B24-B26's words: up to date,
    /// only ahead, no upstream (and git not run), diverged with Checkout (which checks it out), the current branch
    /// diverged, the current branch updated with its files, a branch in another worktree with Open in New Window, and the
    /// quick pick leaving out the current branch during a merge. On a plain folder, so no build or language host starts.
    /// </summary>
    [AvaloniaFact]
    public async Task Update_branch_brings_a_branch_up_to_date_without_checking_it_out()
    {
        await using var f = ShellFixture.Create();
        var folder = Path.Combine(f.UserData, "repository");
        var bare = Path.Combine(f.UserData, "remote.git");
        var other = Path.Combine(f.UserData, "other");
        Directory.CreateDirectory(folder);
        Git(f.UserData, "init", "-q", "--bare", "-b", "main", bare);
        Git(folder, "init", "-q", "-b", "main");
        Configure(folder);
        CommitFile(folder, "README.md", "initial");
        Git(folder, "remote", "add", "origin", bare);
        Git(folder, "push", "-q", "-u", "origin", "main");
        Git(folder, "branch", "feature");
        Git(folder, "push", "-q", "-u", "origin", "feature");
        Git(folder, "branch", "local-only");
        Git(f.UserData, "clone", "-q", "-b", "feature", bare, other);
        Configure(other);
        CommitFile(other, "feature.txt", "feature from the other clone");
        Git(other, "push", "-q", "origin", "feature");
        var tip = Git(other, "rev-parse", "HEAD").Trim();
        var mainBefore = Git(folder, "rev-parse", "main").Trim();

        f.Shell.Trust.Trust(folder);
        await f.Shell.OpenFolderAsync(folder);
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "main" } && f.Shell.Workbench.Commands.IsEnabled("git.updateBranch"));

        // Where it is for now: Git → Branches, after Manage Branches, Switch and Create, and the Git Changes window's ⋯ menu.
        Assert.Equal(
            ["git.manageBranches", "git.switchBranch", "git.createBranch", "git.updateBranch"],
            f.Shell.Workbench.Menus.GetItems(BuiltIns.GitBranchesMenu).Where(i => i.Group == "1-branch").Select(i => i.Command));
        Assert.Equal("Update Branch…", f.Shell.Workbench.Menus.GetItems(BuiltIns.GitBranchesMenu).Single(i => i.Command == "git.updateBranch").Title);
        f.Shell.Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<GitChangesView>().Any());
        var entry = f.Window.GetVisualDescendants().OfType<GitChangesView>().Single().GetVisualDescendants().OfType<Avalonia.Controls.Button>()
            .Select(b => b.Flyout).OfType<Avalonia.Controls.MenuFlyout>()
            .SelectMany(m => m.Items.OfType<Avalonia.Controls.MenuItem>())
            .Single(i => i.Tag is "git.updateBranch");
        Assert.Equal("Update Branch…", entry.Header);

        var messages = new List<string>();
        f.Shell.StatusBar.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(f.Shell.StatusBar.Message) && f.Shell.StatusBar.Message is { } message)
            {
                messages.Add(message);
            }
        };
        var tasks = new List<(string Title, string Source, bool Cancellable)>();
        f.Shell.Workbench.Progress.Changed += (_, _) =>
        {
            foreach (var task in f.Shell.Workbench.Progress.Running)
            {
                lock (tasks)
                {
                    tasks.Add((task.Title, task.Source, task.Cancellable));
                }
            }
        };
        Workbench.Notifications.Notification Notified(string message) =>
            f.Shell.Workbench.Notifications.Items.Single(n => n.Message == message);
        bool Has(string message) => f.Shell.Workbench.Notifications.Items.Any(n => n.Message == message);
        string Notifications() => string.Join(" | ", f.Shell.Workbench.Notifications.Items.Select(n => n.Message));

        // The quick pick: branches with an upstream, the current one first; local-only isn't offered.
        await f.RunAsync("git.updateBranch");
        await ShellFixture.PumpUntil(() => f.Shell.Palette.IsOpen && f.Shell.Palette.Rows.Count > 0);
        Assert.Equal("Update which branch from its upstream?", f.Shell.Palette.Placeholder);
        Assert.Equal(["main", "feature"], f.Shell.Palette.Rows.Select(r => r.Label));
        Assert.Equal(["current, from origin/main", "from origin/feature"], f.Shell.Palette.Rows.Select(r => r.Detail));
        f.Shell.Palette.Accept(f.Shell.Palette.Rows.Single(r => r.Label == "feature"));

        // B23: the status bar's task while it runs, with a cancel button; B24: how many commits came.
        await ShellFixture.PumpUntil(() => messages.Contains("Updated feature from origin/feature: 1 commit."), describe: () => string.Join(" | ", messages) + "; git log: " + f.Shell.GitLog.GetText());
        lock (tasks)
        {
            Assert.Contains(("Updating feature from origin/feature", "Git", true), tasks);
        }

        Assert.Empty(f.Shell.Workbench.Progress.Running);
        Assert.Equal(tip, Git(folder, "rev-parse", "refs/heads/feature").Trim());
        Assert.Equal(mainBefore, Git(folder, "rev-parse", "HEAD").Trim());
        Assert.Equal("main", f.Shell.Git.Status!.Branch);
        Assert.False(File.Exists(Path.Combine(folder, "feature.txt")));

        // By argument (a branch's full ref, as a branch's own menu will pass it): nothing more to bring in.
        await f.ExecuteAsync("git.updateBranch", JsonSerializer.SerializeToElement("refs/heads/feature"));
        Assert.Contains("feature is already up to date with origin/feature.", messages);

        // No upstream: said, and git doesn't fetch.
        var logBefore = f.Shell.GitLog.GetText().Length;
        await f.ExecuteAsync("git.updateBranch", JsonSerializer.SerializeToElement("local-only"));
        Assert.True(Has("local-only has no upstream branch to update from."), Notifications());
        Assert.DoesNotContain("> git fetch", f.Shell.GitLog.GetText()[logBefore..], StringComparison.Ordinal);

        // Only ahead: nothing to update, and Push sends it.
        var mine = Git(folder, "commit-tree", "feature^{tree}", "-p", "feature", "-m", "mine").Trim();
        Git(folder, "update-ref", "refs/heads/feature", mine);
        await f.ExecuteAsync("git.updateBranch", JsonSerializer.SerializeToElement("feature"));
        Assert.Contains("feature has nothing to update from origin/feature: it's ahead by 1 commit, which Push sends.", messages);

        // B25, diverged: a commit here that the remote doesn't have, and one there. feature stays where it is; Checkout checks it out.
        CommitFile(other, "more.txt", "more from the other clone");
        Git(other, "push", "-q", "origin", "feature");
        await f.ExecuteAsync("git.updateBranch", JsonSerializer.SerializeToElement("feature"));
        const string Diverged = "feature has diverged from origin/feature: 1 commit here, 1 there. Update only fast-forwards; check it out to merge or rebase.";
        Assert.True(Has(Diverged), Notifications());
        var diverged = Notified(Diverged);
        Assert.Equal(Workbench.Notifications.NotificationSeverity.Warning, diverged.Severity);
        Assert.Equal(["Checkout"], diverged.Actions);
        Assert.Equal(mine, Git(folder, "rev-parse", "refs/heads/feature").Trim());
        f.Shell.Workbench.Notifications.Choose(diverged, "Checkout");
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "feature" }, describe: Notifications);

        // The current branch diverged: merge or rebase instead (it is already checked out), and no Checkout.
        await f.ExecuteAsync("git.updateBranch", JsonSerializer.SerializeToElement("feature"));
        const string CurrentDiverged = "feature has diverged from origin/feature: 1 commit here, 1 there. Update only fast-forwards; merge origin/feature into it, or rebase it onto origin/feature.";
        Assert.True(Has(CurrentDiverged), Notifications());
        Assert.Empty(Notified(CurrentDiverged).Actions);
        Assert.Equal(mine, Git(folder, "rev-parse", "HEAD").Trim());

        // The current branch behind its upstream: fast-forwarded with its files.
        Git(folder, "switch", "-q", "main");
        Git(other, "fetch", "-q", "origin", "main");
        Git(other, "switch", "-q", "-c", "main", "origin/main");
        CommitFile(other, "main.txt", "main from the other clone");
        Git(other, "push", "-q", "origin", "main");
        await f.Shell.Git.RefreshAsync();
        await f.ExecuteAsync("git.updateBranch", JsonSerializer.SerializeToElement("main"));
        Assert.Contains("Updated main from origin/main: 1 commit.", messages);
        Assert.True(File.Exists(Path.Combine(folder, "main.txt")));

        // B26, checked out in another worktree: git isn't run, the notification says where and opens it in a new window.
        var worktree = Path.Combine(f.UserData, "worktree");
        Git(folder, "branch", "--track", "elsewhere", "origin/main");
        Git(folder, "worktree", "add", "-q", worktree, "elsewhere");
        logBefore = f.Shell.GitLog.GetText().Length;
        await f.ExecuteAsync("git.updateBranch", JsonSerializer.SerializeToElement("elsewhere"));
        var elsewhere = f.Shell.Workbench.Notifications.Items.Single(n => n.Message.StartsWith("elsewhere is checked out in ", StringComparison.Ordinal));
        var where = elsewhere.Message["elsewhere is checked out in ".Length..^", so it can't be updated here. Pull in that window updates it.".Length];
        Assert.Equal(Codalus.Platform.RealPath.Resolve(worktree), Codalus.Platform.RealPath.Resolve(where));
        Assert.Equal(Path.GetFullPath(where), where); // the OS's separators
        Assert.Equal(["Open in New Window"], elsewhere.Actions);
        Assert.DoesNotContain("> git fetch", f.Shell.GitLog.GetText()[logBefore..], StringComparison.Ordinal);

        // During a merge the quick pick leaves out the current branch, as Pull is hidden there.
        Git(folder, "switch", "-q", "-c", "conflicting");
        CommitFile(folder, "README.md", "conflicting");
        Git(folder, "switch", "-q", "main");
        CommitFile(folder, "README.md", "main's own");
        Assert.NotEqual(0, GitExitCode(folder, "merge", "--no-edit", "conflicting"));
        await f.Shell.Git.RefreshAsync();
        Assert.Equal("merge", f.Shell.Git.Status!.Operation);
        await f.RunAsync("git.updateBranch");
        await ShellFixture.PumpUntil(() => f.Shell.Palette.IsOpen && f.Shell.Palette.Rows.Count > 0);
        Assert.Equal(["elsewhere", "feature"], f.Shell.Palette.Rows.Select(r => r.Label));
        f.Shell.Palette.Cancel();
    }

    [AvaloniaFact]
    public async Task Markers_changes_view_hunk_staging_commit_blame_and_branches()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("BuildAndRun");
        Git(folder, "init", "-q", "-b", "main");
        Git(folder, "config", "user.name", "Ada Tester");
        Git(folder, "config", "user.email", "ada@example.com");
        Git(folder, "config", "commit.gpgsign", "false");
        Git(folder, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(folder, ".gitignore"), "bin/\nobj/\n");
        Git(folder, "add", ".");
        Git(folder, "commit", "-q", "-m", "initial");

        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BuildAndRun.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "main" });
        await ShellFixture.PumpUntil(() => f.Shell.StatusBar.Right.Any(i => i.Item.Id == "codalus.git.branch" && i.Text.Contains("main", StringComparison.Ordinal)));
        Assert.Equal(true, f.Shell.Workbench.Context.GetValue("gitRepository"));

        // Typing adds a gutter marker before anything is saved.
        var greeter = Path.Combine(folder, "src", "Greetings", "Greeter.cs");
        var editor = await f.Shell.OpenFileAsync(greeter);
        await ShellFixture.PumpUntil(() => editor?.Controller is not null);
        var controller = editor!.Controller!;
        controller.Editor.Document.Insert(0, "// a new first line\n");
        await ShellFixture.PumpUntil(() => controller.GitMargin.Changes.Any(c => c is { Kind: LineChangeKind.Added, Line: 1 }));

        // Saved: the Changes view lists it; its diff stages the hunk.
        await f.Shell.SaveActiveDocumentAsync();
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => f.Shell.GitChanges.Changes.Any(r => r.Name == "Greeter.cs"));
        f.Shell.Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<GitChangesView>().Any());

        var diff = await f.Shell.OpenDiffAsync(greeter, staged: false);
        Assert.NotNull(diff);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<DiffView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<DiffView>().Single();
        Assert.StartsWith("// a new first line", view.RightText, StringComparison.Ordinal);
        Assert.StartsWith("\n", view.LeftText, StringComparison.Ordinal); // a filler row on the old side
        Assert.True(await view.ApplyToChangeAsync(DiffAction.Stage));
        await ShellFixture.PumpUntil(
            () => f.Shell.GitChanges.Staged.Any(r => r.Name == "Greeter.cs") && !f.Shell.GitChanges.Changes.Any(r => r.Name == "Greeter.cs"),
            describe: () => $"staged [{string.Join(", ", f.Shell.GitChanges.Staged.Select(r => r.Name))}], changes [{string.Join(", ", f.Shell.GitChanges.Changes.Select(r => r.Name))}], git status [{string.Join(", ", f.Shell.Git.Status?.Files.Select(x => x.RelativePath + ":" + x.Index + x.WorkTree) ?? [])}], diff cached: {Git(folder, "diff", "--cached", "--stat").Trim()}, unstaged: {Git(folder, "diff").Replace("\n", " / ", StringComparison.Ordinal)}");

        // Commit from the Changes view.
        f.Shell.GitChanges.Message = "Add a comment";
        await f.Shell.GitChanges.CommitAsync(all: false);
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Files.Count: 0 });
        Assert.Equal(string.Empty, f.Shell.GitChanges.Message);
        // The index-changed notice (from the file watcher) refreshes the markers; it can lag on Linux.
        await controller.UpdateGitMarkersAsync();
        await ShellFixture.PumpUntil(() => controller.GitMargin.Changes.Count == 0);

        // Blame annotates the editor; a new branch shows in the status bar.
        f.Shell.Layout.OpenDocument(editor);
        await f.RunAsync("git.toggleBlame");
        await ShellFixture.PumpUntil(() => controller.Blame is { Ranges.Count: > 0 });
        Assert.Equal("Add a comment", controller.Blame!.Ranges[0].Summary);
        Assert.Contains("Ada Tester", BlameMargin.Describe(controller.Blame.Ranges[0]), StringComparison.Ordinal);

        await f.Shell.Git.CreateBranchAsync("feature/x", switchTo: true, TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => f.Shell.StatusBar.Right.Any(i => i.Item.Id == "codalus.git.branch" && i.Text.Contains("feature/x", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A fetch, pull, push or sync shows in Git Changes while it runs, as in the classic IDE: what it's doing under the
    /// branch over a running bar, Cancel in place of Fetch, Pull, Push and Sync, and a cancellable task in the status bar.
    /// Cancel kills git, says so, and puts the buttons back; a push that finishes puts them back too. The remote's ssh
    /// command only sleeps, so the fetch runs until it is cancelled. On a plain folder, so no build or language host starts.
    /// </summary>
    [AvaloniaFact]
    public async Task A_running_fetch_shows_in_git_changes_until_cancel_stops_it()
    {
        await using var f = ShellFixture.Create();
        var folder = Path.Combine(f.UserData, "repository");
        var bare = Path.Combine(f.UserData, "remote.git");
        Directory.CreateDirectory(folder);
        Git(f.UserData, "init", "-q", "--bare", "-b", "main", bare);
        Git(folder, "init", "-q", "-b", "main");
        Configure(folder);
        CommitFile(folder, "README.md", "initial");
        Git(folder, "remote", "add", "origin", bare);
        Git(folder, "push", "-q", "-u", "origin", "main");
        // Fetch and pull reach origin over an ssh that never answers; push goes to the bare repository.
        Git(folder, "config", "remote.origin.pushurl", bare);
        Git(folder, "config", "remote.origin.url", "ssh://git.invalid/remote.git");
        Git(folder, "config", "ssh.variant", "simple");
        Git(folder, "config", "core.sshCommand", "sleep 120 #");

        f.Shell.Trust.Trust(folder);
        await f.Shell.OpenFolderAsync(folder);
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "main" } && f.Shell.Workbench.Commands.IsEnabled("git.fetch"));
        var vm = f.Shell.GitChanges;
        f.Shell.Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<GitChangesView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<GitChangesView>().Single();
        var buttons = view.GetVisualDescendants().OfType<Avalonia.Controls.Button>().ToList();
        var remoteButtons = buttons.Where(b => b.Classes.Contains("toolbar") && b.Tag is "git.fetch" or "git.pull" or "git.push" or "git.sync").ToList();
        Assert.Equal(4, remoteButtons.Count);
        var cancel = buttons.Single(b => b.Name == "CancelProgressButton");
        var progress = view.GetVisualDescendants().OfType<Avalonia.Controls.StackPanel>().Single(p => p.Name == "Progress");
        Assert.True(vm.IsIdle);
        Assert.All(remoteButtons, b => Assert.True(b.IsVisible));
        Assert.False(cancel.IsVisible);
        Assert.False(progress.IsVisible);

        // Fetch: the window and the status bar show it while it runs.
        await f.RunAsync("git.fetch");
        await ShellFixture.PumpUntil(() => vm.IsBusy && cancel.IsVisible);
        Assert.Equal("Fetching…", vm.ProgressText);
        Assert.True(progress.IsVisible);
        Assert.Contains(progress.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>(), t => t.Text == "Fetching…");
        Assert.All(remoteButtons, b => Assert.False(b.IsVisible));
        var task = Assert.Single(f.Shell.Workbench.Progress.Running, t => t.Source == "Git");
        Assert.Equal("Fetching", task.Title);
        Assert.True(task.Cancellable);

        // Cancel kills git: the buttons come back, the status bar says so, and git's failure isn't reported as an error.
        cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await ShellFixture.PumpUntil(() => vm.IsIdle, describe: () => $"still {vm.ProgressText}; git log: {f.Shell.GitLog.GetText()}");
        await ShellFixture.PumpUntil(() => remoteButtons.All(b => b.IsVisible) && !cancel.IsVisible && !progress.IsVisible);
        Assert.DoesNotContain(f.Shell.Workbench.Progress.Running, t => t.Source == "Git");
        Assert.Equal("Cancelled.", f.Shell.StatusBar.Message);
        Assert.Contains("Fetching… Cancelled.", f.Shell.GitLog.GetText(), StringComparison.Ordinal);
        Assert.DoesNotContain(f.Shell.Workbench.Notifications.Items, n => n.Message.StartsWith("Git:", StringComparison.Ordinal));

        // A push that finishes shows Pushing… while it runs, then puts the buttons back.
        CommitFile(folder, "notes.txt", "a commit to push");
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Ahead: 1 });
        var seen = new List<string?>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.ProgressText))
            {
                seen.Add(vm.ProgressText);
            }
        };
        await f.RunAsync("git.push");
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Ahead: 0 } && vm.IsIdle, describe: () => $"git log: {f.Shell.GitLog.GetText()}");
        Assert.Equal(["Pushing…", null], seen);
        Assert.Equal(Git(folder, "rev-parse", "HEAD"), Git(bare, "rev-parse", "main"));
        await ShellFixture.PumpUntil(() => remoteButtons.All(b => b.IsVisible) && !cancel.IsVisible);
    }

    [Fact]
    public void Related_items_are_the_issue_references_in_the_message()
    {
        Assert.Equal(["#12", "#3"], GitChangesViewModel.MentionedItems("Fix #12 and #3 (see #12); not a#4 or ##5 or #x"));
        Assert.Empty(GitChangesViewModel.MentionedItems("no references"));
    }

    private static GitFileRow Row(string relative, char workTree = 'M') =>
        new(new GitFileStatus(Path.Combine("/repo", relative), relative, '.', workTree), staged: false);

    [Fact]
    public void The_changes_tree_is_the_repository_its_compacted_folders_and_files()
    {
        var sep = Path.DirectorySeparatorChar;
        var single = GitChangesViewModel.BuildTree("/repo", [Row("src/Codalus.Data.Host/Transfer.cs")], "changes", staged: false, new HashSet<string>());
        var repository = Assert.Single(single);
        Assert.True(repository.IsRepository);
        var folder = Assert.Single(repository.Children);
        Assert.Equal($"src{sep}Codalus.Data.Host", folder.Text);
        Assert.Equal("Transfer.cs", Assert.Single(folder.Children).Text);
        Assert.Equal("M", folder.Children[0].Letter);

        var collapsed = new HashSet<string> { "changes:/src/C" };
        var tree = GitChangesViewModel.BuildTree("/repo", [Row("README.md"), Row("src/C/z.cs"), Row("src/A/B/y.cs"), Row("src/A/B/x.cs")], "changes", staged: false, collapsed);
        var root = Assert.Single(tree);
        Assert.Equal(["src", "README.md"], root.Children.Select(c => c.Text));
        var src = root.Children[0];
        Assert.Equal([$"A{sep}B", "C"], src.Children.Select(c => c.Text));
        Assert.Equal(["x.cs", "y.cs"], src.Children[0].Children.Select(c => c.Text));
        Assert.False(src.Children[1].IsExpanded);
        Assert.True(src.Children[0].IsExpanded);
        Assert.Equal(3, src.Rows.Count());
        Assert.Empty(GitChangesViewModel.BuildTree("/repo", [], "changes", staged: false, new HashSet<string>()));
    }

    [AvaloniaFact]
    public async Task Changes_view_lays_out_like_the_classic_ide_commits_all_with_new_files_and_lists_stashes()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("BuildAndRun");
        Git(folder, "init", "-q", "-b", "main");
        Git(folder, "config", "user.name", "Ada Tester");
        Git(folder, "config", "user.email", "ada@example.com");
        Git(folder, "config", "commit.gpgsign", "false");
        Git(folder, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(folder, ".gitignore"), "bin/\nobj/\n");
        Git(folder, "add", ".");
        Git(folder, "commit", "-q", "-m", "initial");

        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BuildAndRun.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "main" });
        var vm = f.Shell.GitChanges;
        f.Shell.Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<GitChangesView>().Any());

        // The branch picker, an unpublished branch, and a commit button that waits for a message and a change.
        Assert.Equal("main", vm.BranchName);
        Assert.True(vm.IsNotPublished);
        Assert.Equal("Changes", vm.ChangesHeader);
        Assert.False(vm.CanCommit);
        Assert.True(vm.HasNoStashes);

        // A new (untracked) file: Commit All commits it too.
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "notes\n");
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => vm.Changes.Any(r => r.Name == "Notes.txt"));
        Assert.Equal("Commit All", vm.CommitText);
        Assert.False(vm.CanCommit);
        vm.Message = "Add notes for #7";
        Assert.True(vm.CanCommit);
        Assert.Equal([new RelatedItem("#7", null)], vm.RelatedItems);
        await vm.CommitAsync(all: true);
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Files.Count: 0 });
        Assert.Empty(vm.RelatedItems);

        // The tree: the repository, then the file; the list: Name and Path, sorted and filtered.
        File.WriteAllText(Path.Combine(folder, "Zeta.txt"), "z\n");
        File.WriteAllText(Path.Combine(folder, "Alpha.txt"), "a\n");
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => vm.Changes.Count == 2 && vm.ChangesTree.Count == 1);
        Assert.Equal(["Alpha.txt", "Zeta.txt"], Assert.Single(vm.ChangesTree).Children.Select(c => c.Text));
        Assert.Equal(["Alpha.txt", "Zeta.txt"], vm.ChangesRows.Skip(1).Select(r => r.Text));
        Assert.Equal(1, vm.ChangesRows[1].Depth);

        // The list, sorted and filtered off the UI thread (M56).
        vm.IsTreeView = false;
        Assert.True(vm.IsListView);
        await ShellFixture.PumpUntil(() => vm.ChangesItems.Count == 2);
        Assert.Equal(Path.GetFileName(folder), vm.ChangesItems[0].Location);
        vm.SortBy("name");
        await ShellFixture.PumpUntil(() => vm.ChangesItems.Select(r => r.Name).SequenceEqual(["Zeta.txt", "Alpha.txt"]));
        vm.ShowFilter = true;
        vm.FilterText = "alp";
        await ShellFixture.PumpUntil(() => vm.ChangesItems.Select(r => r.Name).SequenceEqual(["Alpha.txt"]));
        vm.ShowFilter = false;
        await ShellFixture.PumpUntil(() => vm.ChangesItems.Count == 2);
        vm.IsTreeView = true;
        File.Delete(Path.Combine(folder, "Zeta.txt"));
        File.Delete(Path.Combine(folder, "Alpha.txt"));
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => vm.Changes.Count == 0);

        // Staged changes turn it into Commit Staged.
        File.AppendAllText(Path.Combine(folder, "Notes.txt"), "more\n");
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => vm.HasChanges);
        await vm.StageAllAsync();
        await ShellFixture.PumpUntil(() => vm.HasStaged);
        Assert.Equal("Commit Staged", vm.CommitText);
        Assert.Equal("Commit Staged and Push", vm.CommitAndPushText);

        // Stashing lists the stash; applying it brings the change back; clearing empties the list.
        await f.Shell.Git.StashAsync("wip notes", includeUntracked: false, cancellationToken: TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => vm.Stashes.Count == 1);
        Assert.Contains("wip notes", vm.Stashes[0].Message, StringComparison.Ordinal);
        Assert.Equal("Stashes (1)", vm.StashesHeader);
        await vm.ApplyStashAsync(vm.Stashes[0], pop: true);
        await ShellFixture.PumpUntil(() => vm.Stashes.Count == 0 && (vm.HasStaged || vm.HasChanges));
        await f.Shell.Git.StashAsync(null, includeUntracked: false, cancellationToken: TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => vm.Stashes.Count == 1);
        await f.Shell.Git.ClearStashesAsync(TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => vm.HasNoStashes);
    }
}
