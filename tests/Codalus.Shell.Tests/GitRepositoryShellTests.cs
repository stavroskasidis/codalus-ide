using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Git;

namespace Codalus.Shell.Tests;

/// <summary>
/// The Git Repository document (Git → Manage Branches): the tree of branches in folders, remotes and tags, Pull
/// Requests and Submodules; the selected ref's history with a local branch's incoming commits; the menus and links.
/// </summary>
public sealed class GitRepositoryShellTests
{
    private static string Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
        return output;
    }

    private static void Configure(string folder)
    {
        Git(folder, "config", "user.name", "Ada Tester");
        Git(folder, "config", "user.email", "ada@example.com");
        Git(folder, "config", "commit.gpgsign", "false");
        Git(folder, "config", "tag.gpgsign", "false");
        Git(folder, "config", "core.autocrlf", "false");
    }

    /// <summary>
    /// BuildAndRun as a repository on main with feature/sign-in and fix (one commit each), a tag v1.0, and an origin (a
    /// bare repository on disk) that has one commit main doesn't, fetched.
    /// </summary>
    private static async Task<(ShellFixture Fixture, string Folder)> OpenRepositoryAsync()
    {
        var f = ShellFixture.Create();
        var folder = f.CopySample("BuildAndRun");
        Git(folder, "init", "-q", "-b", "main");
        Configure(folder);
        File.WriteAllText(Path.Combine(folder, ".gitignore"), "bin/\nobj/\n");
        Git(folder, "add", ".");
        Git(folder, "commit", "-q", "-m", "initial");
        Git(folder, "tag", "-a", "v1.0", "-m", "First release");

        var origin = Path.Combine(Path.GetDirectoryName(folder)!, "origin.git");
        Git(Path.GetDirectoryName(folder)!, "init", "-q", "--bare", "-b", "main", origin);
        Git(folder, "remote", "add", "origin", origin);
        Git(folder, "push", "-q", "-u", "origin", "main");

        // Someone else pushes a commit to origin's main.
        var other = Path.Combine(Path.GetDirectoryName(folder)!, "other");
        Git(Path.GetDirectoryName(folder)!, "clone", "-q", origin, other);
        Configure(other);
        File.WriteAllText(Path.Combine(other, "NOTES.md"), "notes\n");
        Git(other, "add", ".");
        Git(other, "commit", "-q", "-m", "Add notes upstream");
        Git(other, "push", "-q", "origin", "main");
        Git(folder, "fetch", "-q", "origin");

        Git(folder, "branch", "feature/sign-in");
        Git(folder, "switch", "-q", "feature/sign-in");
        File.WriteAllText(Path.Combine(folder, "SIGNIN.md"), "sign in\n");
        Git(folder, "add", ".");
        Git(folder, "commit", "-q", "-m", "Sign in");
        Git(folder, "switch", "-q", "-c", "fix", "main");
        File.WriteAllText(Path.Combine(folder, "FIX.md"), "fix\n");
        Git(folder, "add", ".");
        Git(folder, "commit", "-q", "-m", "Fix it");
        Git(folder, "switch", "-q", "main");

        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BuildAndRun.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "main" } && Equals(f.Shell.Workbench.Context.GetValue("gitRepository"), true));
        return (f, folder);
    }

    private static async Task<GitRepositoryViewModel> OpenDocumentAsync(ShellFixture f)
    {
        await f.RunAsync("git.manageBranches");
        await ShellFixture.PumpUntil(
            () => f.Shell.Layout.ActiveDocument is GitRepositoryDocument { ViewModel: { IsLoading: false, Roots.Count: 3 } vm } && vm.Find("refs/heads/main") is not null && vm.Roots[1].Children.All(c => !c.Text.StartsWith("Loading", StringComparison.Ordinal)),
            describe: () => $"active {f.Shell.Layout.ActiveDocument?.GetType().Name}; notifications: {string.Join(" | ", f.Shell.Notifications.Items.Select(n => n.Message))}");
        return ((GitRepositoryDocument)f.Shell.Layout.ActiveDocument!).ViewModel;
    }

    private static IEnumerable<string> Texts(IEnumerable<GitRepositoryNode> nodes) => nodes.Select(n => n.Text);

    [AvaloniaFact]
    public async Task The_tree_has_the_branches_in_folders_the_remotes_and_tags_then_pull_requests_and_submodules()
    {
        var (f, folder) = await OpenRepositoryAsync();
        await using var fixture = f;
        var vm = await OpenDocumentAsync(f);

        Assert.Equal(["Branches / Tags", "Pull Requests", "Submodules"], Texts(vm.Roots));
        var repository = Assert.Single(vm.Roots[0].Children);
        Assert.Equal($"{Path.GetFileName(folder)} (main)", repository.Text);
        Assert.True(repository.IsExpanded);

        // Folders first, then the local branches by name, then each remote's branches and the tags, collapsed.
        Assert.Equal(["feature", "fix", "main", "remotes/origin", "tags"], Texts(repository.Children));
        var feature = repository.Children[0];
        Assert.Equal(GitRepositoryNodeKind.Folder, feature.Kind);
        Assert.True(feature.IsExpanded);
        Assert.Equal("sign-in", Assert.Single(feature.Children).Text);
        Assert.Equal("refs/heads/feature/sign-in", feature.Children[0].Reference);

        var main = repository.Children[2];
        Assert.True(main.IsCurrent);
        Assert.Equal("↓1", main.Track);
        Assert.Equal("1 incoming (origin/main)", main.TrackName);
        Assert.False(repository.Children[1].IsCurrent);

        var remotes = repository.Children[3];
        Assert.False(remotes.IsExpanded);
        Assert.Equal(["main"], Texts(remotes.Children));
        Assert.Equal("refs/remotes/origin/main", remotes.Children[0].Reference);
        var tags = repository.Children[4];
        Assert.False(tags.IsExpanded);
        var tag = Assert.Single(tags.Children);
        Assert.Equal(("v1.0", "First release", "refs/tags/v1.0"), (tag.Text, tag.Detail, tag.Reference));

        // Not on a Git host Codalus knows (a folder as origin): nothing is asked of a host, and the node says why.
        var pullRequests = Assert.Single(vm.Roots[1].Children);
        Assert.Equal(GitRepositoryNodeKind.Message, pullRequests.Kind);
        Assert.Equal("The remotes aren't on a Git host Codalus knows.", pullRequests.Text);
        Assert.Empty(vm.Roots[2].Children);

        // The view: the tree and, for the current branch selected at first, its history.
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<GitRepositoryView>().Any());
        Assert.Same(main, vm.Selected);

        // The filter keeps what matches, with its folders open; clearing it brings the remembered state back.
        vm.Filter = "sign";
        repository = vm.Roots[0].Children.Single();
        Assert.Equal(["feature"], Texts(repository.Children));
        Assert.True(repository.Children[0].IsExpanded);
        vm.Filter = "v1";
        Assert.Equal(["tags"], Texts(vm.Roots[0].Children.Single().Children));
        Assert.True(vm.Roots[0].Children.Single().Children[0].IsExpanded);
        vm.Filter = string.Empty;
        Assert.False(vm.Roots[0].Children.Single().Children[4].IsExpanded);
    }

    [AvaloniaFact]
    public async Task A_local_branch_shows_its_incoming_commits_and_history_and_a_tag_its_own()
    {
        var (f, _) = await OpenRepositoryAsync();
        await using var fixture = f;
        var vm = await OpenDocumentAsync(f);

        // main: one incoming commit, none outgoing; Pull and Sync as it's checked out.
        await ShellFixture.PumpUntil(() => vm.History is { Rows.Count: > 0 } && vm.Incoming.Count == 1);
        Assert.Equal("Branch / Tag: main", vm.Header);
        Assert.True(vm.HasUpstream);
        Assert.Equal("Incoming (1)", vm.IncomingHeader);
        Assert.Equal("Local History (0 Outgoing)", vm.HistoryHeader);
        Assert.Equal("Add notes upstream", vm.Incoming[0].Subject);
        Assert.Equal(["initial"], vm.History!.Rows.Select(r => r.Subject));
        Assert.False(vm.History.ShowBranchFilter);
        Assert.True(vm.IsCurrentBranch);
        Assert.True(vm.CanSync);
        Assert.False(vm.CanUpdate);

        // feature/sign-in: its own history, no upstream.
        vm.Selected = vm.Find("refs/heads/feature/sign-in");
        await ShellFixture.PumpUntil(() => vm.History is { IsLoading: false, Rows.Count: 2 });
        Assert.Equal("Branch / Tag: feature/sign-in", vm.Header);
        Assert.False(vm.HasUpstream);
        Assert.Equal("History", vm.HistoryHeader);
        Assert.Equal(["Sign in", "initial"], vm.History!.Rows.Select(r => r.Subject));
        Assert.Empty(vm.Incoming);
        Assert.True(vm.CanPush);
        Assert.False(vm.CanSync);

        // The remote branch: what origin has.
        vm.Selected = vm.Find("refs/remotes/origin/main");
        await ShellFixture.PumpUntil(() => vm.History is { IsLoading: false, Rows.Count: 2 });
        Assert.Equal(["Add notes upstream", "initial"], vm.History!.Rows.Select(r => r.Subject));
        Assert.False(vm.CanPush);

        // A tag.
        vm.Selected = vm.Find("refs/tags/v1.0");
        await ShellFixture.PumpUntil(() => vm.History is { IsLoading: false, Rows.Count: 1 });
        Assert.Equal("Branch / Tag: v1.0", vm.Header);

        // A group: no history, and the right side says what to do.
        vm.Selected = vm.Roots[2];
        await ShellFixture.PumpUntil(() => vm.HasMessage);
        Assert.Null(vm.History);
        Assert.Equal("Select a branch or tag to see its history.", vm.Message);
    }

    [AvaloniaFact]
    public async Task A_branch_has_the_branch_drop_downs_menu_and_a_double_click_checks_it_out()
    {
        var (f, folder) = await OpenRepositoryAsync();
        await using var fixture = f;
        var vm = await OpenDocumentAsync(f);

        // The menu is gitChanges/branch's, titled for the branch, as in the drop-down.
        var fix = vm.Find("refs/heads/fix")!;
        var menu = f.Shell.GitRepositoryMenu(fix)!;
        var titles = menu.Items.OfType<MenuItem>().Select(i => Avalonia.Automation.AutomationProperties.GetName(i)).ToList();
        Assert.Contains("Checkout", titles);
        Assert.Contains("Merge 'fix' into 'main'", titles);
        Assert.Contains("Rebase 'main' onto 'fix'", titles);
        Assert.Contains("Rename…", titles);
        Assert.Contains("Compare with Current Branch", titles);

        // A remote branch with a local one of its name: Switch to Local 'main' and how far behind it is (the tree's data,
        // without the drop-down).
        var remoteMenu = f.Shell.GitRepositoryMenu(vm.Find("refs/remotes/origin/main")!)!;
        Assert.Contains("Switch to Local 'main' (1 commit behind)", remoteMenu.Items.OfType<MenuItem>().Select(i => Avalonia.Automation.AutomationProperties.GetName(i)));

        // A tag's own menu.
        var tagMenu = f.Shell.GitRepositoryMenu(vm.Find("refs/tags/v1.0")!)!;
        Assert.Equal(
            ["Checkout (Detached)", "New Local Branch From…", "Push", "Delete", "Delete from Remote…", "View History", "Copy Tag Name"],
            tagMenu.Items.OfType<MenuItem>().Select(i => Avalonia.Automation.AutomationProperties.GetName(i)));

        // A double-click (Enter) on a branch checks it out; the tree follows.
        await vm.ActivateAsync(fix);
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "fix" });
        await ShellFixture.PumpUntil(() => vm.Find("refs/heads/fix") is { IsCurrent: true } && vm.Roots[0].Children[0].Text == $"{Path.GetFileName(folder)} (fix)");
        Assert.False(vm.Find("refs/heads/main")!.IsCurrent);

        // Checkout on a remote branch whose local branch exists switches to it (the drop-down never opened).
        await vm.ActivateAsync(vm.Find("refs/remotes/origin/main")!);
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "main" });
    }

    [AvaloniaFact]
    public async Task Delete_on_a_tag_asks_then_deletes_it_and_the_tree_follows()
    {
        var (f, folder) = await OpenRepositoryAsync();
        await using var fixture = f;
        var vm = await OpenDocumentAsync(f);

        var tag = vm.Find("refs/tags/v1.0")!;
        var deleting = vm.RunOnNodeAsync("git.deleteTag", tag);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Equal("Delete the tag v1.0?", f.Shell.Dialogs.Title);
        Assert.Equal("Cancel", f.Shell.Dialogs.Buttons.Single(b => b.IsDefault).Label);
        f.Shell.Dialogs.Buttons.Single(b => b.Label == "Delete").Command.Execute(null);
        await deleting;
        Assert.Equal(string.Empty, Git(folder, "tag", "--list").Trim());
        await ShellFixture.PumpUntil(() => vm.Find("refs/tags/v1.0") is null);

        // F2 on a branch renames it through the branch menu's command (the dialog), only where the menu has it.
        var renaming = vm.RunOnNodeAsync("git.renameBranch", vm.Find("refs/heads/fix")!);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Equal("Rename Branch", f.Shell.Dialogs.Title);
        f.Shell.Dialogs.Cancel();
        await renaming;
    }

    [AvaloniaFact]
    public async Task Manage_Branches_is_in_the_Git_menu_Branches_and_the_Git_Changes_menus()
    {
        var (f, _) = await OpenRepositoryAsync();
        await using var fixture = f;

        Assert.Contains(f.Shell.Workbench.Menus.GetItems(Workbench.Menus.MenuLocations.MenuBar("git"), f.Shell.Workbench.Context), i => i.Command == "git.manageBranches");
        Assert.Contains(f.Shell.Workbench.Menus.GetItems(BuiltIns.GitBranchesMenu, f.Shell.Workbench.Context), i => i.Command == "git.manageBranches");
        Assert.Equal(Codalus.Platform.KeySequence.Parse("primary+0 primary+r", Codalus.Platform.PlatformInfo.Current), f.Shell.Workbench.Keybindings.GetBinding("git.manageBranches"));

        // The branch drop-down's link closes the drop-down and opens the document.
        f.Shell.ShowBranchPicker();
        Assert.True(f.Shell.GitBranches.IsOpen);
        await f.Shell.GitBranches.ManageBranches!();
        Assert.False(f.Shell.GitBranches.IsOpen);
        Assert.IsType<GitRepositoryDocument>(f.Shell.Layout.ActiveDocument);
    }
}
