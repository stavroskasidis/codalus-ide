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
    /// The user's request: Update, in the git branches menus, brings a branch up to date from its remote without checking
    /// it out and pulling. Git → Branches and the Git Changes window's ⋯ menu offer Update Branch…; its quick pick lists
    /// the local branches that have an upstream, the current one first, and picking one fast-forwards it while the
    /// checked-out branch and its files stay as they are. With a branch as the argument (as a branch's own menu will
    /// pass it) it says when there's nothing to do, no upstream, or a branch that has diverged. On a plain folder, so no
    /// build or language host starts.
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

        // Where it is: Git → Branches, after Switch and Create, and the Git Changes window's ⋯ menu.
        Assert.Equal(
            ["git.switchBranch", "git.createBranch", "git.updateBranch"],
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

        // The quick pick: branches with an upstream, the current one first; local-only isn't offered.
        await f.RunAsync("git.updateBranch");
        await ShellFixture.PumpUntil(() => f.Shell.Palette.IsOpen && f.Shell.Palette.Rows.Count > 0);
        Assert.Equal("Update which branch from its upstream?", f.Shell.Palette.Placeholder);
        Assert.Equal(["main", "feature"], f.Shell.Palette.Rows.Select(r => r.Label));
        Assert.Equal(["current, from origin/main", "from origin/feature"], f.Shell.Palette.Rows.Select(r => r.Detail));
        f.Shell.Palette.Accept(f.Shell.Palette.Rows.Single(r => r.Label == "feature"));
        await ShellFixture.PumpUntil(() => messages.Contains("Updated feature from origin/feature."), describe: () => string.Join(" | ", messages) + "; git log: " + f.Shell.GitLog.GetText());
        Assert.Contains("Updating feature…", messages);
        Assert.Equal(tip, Git(folder, "rev-parse", "refs/heads/feature").Trim());
        Assert.Equal(mainBefore, Git(folder, "rev-parse", "HEAD").Trim());
        Assert.Equal("main", f.Shell.Git.Status!.Branch);
        Assert.False(File.Exists(Path.Combine(folder, "feature.txt")));

        // By argument (a branch's full ref, as a branch's own menu passes it): nothing more to bring in.
        await f.ExecuteAsync("git.updateBranch", JsonSerializer.SerializeToElement("refs/heads/feature"));
        await ShellFixture.PumpUntil(() => messages.Contains("feature is already up to date with origin/feature."), describe: () => string.Join(" | ", messages));

        // No upstream: said, and nothing runs.
        await f.ExecuteAsync("git.updateBranch", JsonSerializer.SerializeToElement("local-only"));
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(n => n.Message == "local-only has no upstream branch to update from."));

        // Diverged: a commit here that the remote doesn't have, and one there: feature stays where it is, and the warning says what to do.
        var mine = Git(folder, "commit-tree", "feature^{tree}", "-p", "feature", "-m", "mine").Trim();
        Git(folder, "update-ref", "refs/heads/feature", mine);
        CommitFile(other, "more.txt", "more from the other clone");
        Git(other, "push", "-q", "origin", "feature");
        await f.ExecuteAsync("git.updateBranch", JsonSerializer.SerializeToElement("feature"));
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(n => n.Severity == Workbench.Notifications.NotificationSeverity.Warning
            && n.Message == "feature can't be updated from origin/feature: each has commits the other doesn't, so it can't be fast-forwarded. Check it out, then pull, merge or rebase it."),
            describe: () => string.Join(" | ", f.Shell.Workbench.Notifications.Items.Select(n => n.Message)));
        Assert.Equal(mine, Git(folder, "rev-parse", "refs/heads/feature").Trim());
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
        await view.HunkActionAsync("stage");
        await ShellFixture.PumpUntil(() => f.Shell.GitChanges.Staged.Any(r => r.Name == "Greeter.cs") && !f.Shell.GitChanges.Changes.Any(r => r.Name == "Greeter.cs"));

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
        await ShellFixture.PumpUntil(() => vm.Changes.Count == 2);
        Assert.Equal(["Alpha.txt", "Zeta.txt"], Assert.Single(vm.ChangesTree).Children.Select(c => c.Text));
        vm.IsTreeView = false;
        Assert.True(vm.IsListView);
        Assert.Equal(Path.GetFileName(folder), vm.ChangesItems[0].Location);
        vm.SortBy("name");
        Assert.Equal(["Zeta.txt", "Alpha.txt"], vm.ChangesItems.Select(r => r.Name));
        vm.ShowFilter = true;
        vm.FilterText = "alp";
        Assert.Equal(["Alpha.txt"], vm.ChangesItems.Select(r => r.Name));
        vm.ShowFilter = false;
        Assert.Equal(2, vm.ChangesItems.Count);
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
