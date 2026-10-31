using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>
/// All Repositories (ADR 0085): a folder whose repository the user joins with another (Add Repository…), shown together
/// in Git Changes; changes, staging and Commit All Repos in each; Common branches and each repository's in the branch
/// drop-down; an operation in each that carries on past the ones that fail and lists them; a node's menu in its own
/// repository; one repository picked, kept with the solution, and a repository left out. On plain folders, so no build or
/// language host starts.
/// </summary>
public sealed class AllRepositoriesShellTests
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

    private static string CreateRepository(string folder, string file)
    {
        Directory.CreateDirectory(folder);
        Git(folder, "init", "-q", "-b", "main");
        Git(folder, "config", "user.name", "Ada Tester");
        Git(folder, "config", "user.email", "ada@example.com");
        Git(folder, "config", "commit.gpgsign", "false");
        Git(folder, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(folder, file), "initial\n");
        Git(folder, "add", ".");
        Git(folder, "commit", "-q", "-m", "initial");
        Git(folder, "branch", "develop");
        return Path.GetFullPath(folder);
    }

    [AvaloniaFact]
    public async Task A_solution_in_two_repositories_shows_both_commits_in_each_and_reports_what_failed()
    {
        await using var f = ShellFixture.Create();
        var app = CreateRepository(Path.Combine(f.UserData, "App"), "README.md");
        var library = CreateRepository(Path.Combine(f.UserData, "Library"), "Library.cs");
        Git(app, "branch", "only-app");

        f.Shell.Trust.Trust(app);
        await f.Shell.OpenFolderAsync(app);
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "main" });
        await f.Shell.DiscoverRepositoriesAsync();
        Assert.False(f.Shell.GitChanges.HasSeveralRepositories);

        // Add Repository…: the library has no project here, and joins the folder's repositories; all are shown.
        Directory.CreateDirectory(Path.Combine(library, "src"));
        await f.Shell.AddRepositoryAsync(Path.Combine(library, "src"));
        Assert.Equal([app, library], f.Shell.Git.Repositories);
        var changes = f.Shell.GitChanges;
        Assert.True(changes.IsAllRepositories);
        Assert.Equal(true, f.Shell.Workbench.Context.GetValue("gitAllRepositories"));
        Assert.Equal(["All Repositories (2)", "App", "Library"], changes.Repositories.Select(r => r.Name));
        Assert.Equal("2 Repositories", changes.RepositoryButtonText);
        Assert.False(changes.CanAmend);
        Assert.Equal("main", changes.BranchName);
        changes.RepositoryFilter = "lib";
        Assert.Equal(["Library"], changes.ShownRepositories.Select(r => r.Name));
        changes.RepositoryFilter = string.Empty;

        // A change in each: one tree per repository, the solution's own first.
        File.WriteAllText(Path.Combine(app, "README.md"), "changed\n");
        File.WriteAllText(Path.Combine(library, "Library.cs"), "changed\n");
        await f.Shell.Git.RefreshAsync();
        await f.Shell.Git.Siblings.Single().RefreshAsync();
        await ShellFixture.PumpUntil(() => changes.Changes.Count == 2);
        Assert.Equal(["App", "Library"], changes.Changes.Select(r => r.Location));
        Assert.Equal("Commit All Repos", changes.CommitText);

        // The trees are built while the window is shown (M56).
        f.Shell.Layout.ShowToolWindow(GitChangesViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<GitChangesView>().Any());
        await ShellFixture.PumpUntil(() => changes.ChangesTree.Count == 2);
        Assert.Equal([app, library], changes.ChangesTree.Select(n => n.FullPath));
        Assert.Equal(["App", "Library"], changes.ChangesTree.Select(n => n.Text));
        NavigationDepthTests.Screenshot(f, "all-repositories-changes");

        // A node's menu runs in its own repository, and says which.
        var libraryFile = Path.Combine(library, "Library.cs");
        var scope = f.Shell.GitChangeScope(libraryFile, "changes", "file");
        Assert.Equal(library, scope.GetValue("gitChangeRepository"));
        await f.Shell.StageChangesAsync(GitChangesViewModel.ArgumentsFor(libraryFile, "changes", [libraryFile]));
        await ShellFixture.PumpUntil(() => changes.Staged.Count == 1);
        Assert.Equal(library, changes.Staged.Single().Repository);
        Assert.Equal("Commit Staged", changes.CommitText);

        // Commit All Repos: one message in each repository that has changes.
        await changes.UnstageAllAsync();
        await ShellFixture.PumpUntil(() => changes.Staged.Count == 0 && changes.Changes.Count == 2);
        changes.Message = "Change both";
        await changes.CommitAsync(all: true);
        await ShellFixture.PumpUntil(() => changes.Changes.Count == 0);
        Assert.Equal(string.Empty, changes.Message);
        Assert.Equal("Change both", Git(app, "log", "-1", "--format=%s").Trim());
        Assert.Equal("Change both", Git(library, "log", "-1", "--format=%s").Trim());

        // Push in each: neither has a remote, so both fail, and one notification lists them.
        await f.Shell.PushAsync();
        var failed = f.Shell.Workbench.Notifications.Items.Single(n => n.Message.Contains("repositories failed", StringComparison.Ordinal)).Message;
        Assert.Contains("2 of 2 repositories failed:", failed, StringComparison.Ordinal);
        Assert.Contains("App: This repository has no remote to push to.", failed, StringComparison.Ordinal);
        Assert.Contains("Library: This repository has no remote to push to.", failed, StringComparison.Ordinal);

        // The branch drop-down: Common branches, then each repository's.
        var branches = f.Shell.GitBranches;
        branches.IsOpen = true;
        await ShellFixture.PumpUntil(() => branches.IsAllRepositories && branches.Rows.Any(r => r.IsCommon));
        Assert.Equal(["develop", "main"], branches.Rows.Where(r => r.IsCommon).Select(r => r.Name));
        Assert.True(branches.Rows.Single(r => r.IsCommon && r.Name == "main").IsCurrent);
        Assert.Equal(
            ["Common branches", "All branches", "App (main)", "Library (main)"],
            branches.Rows.Where(r => r.IsHeader).Select(r => r.HeaderText));
        Assert.Contains(branches.Rows, r => r.Name == "only-app" && r.Repository == f.Shell.Git);
        NavigationDepthTests.Screenshot(f, "all-repositories-branches");

        // A Common branch's menu: what runs in every repository, and nothing that is one repository's.
        var develop = branches.Rows.Single(r => r.IsCommon && r.Name == "develop").Branch!;
        Assert.Equal(
            ["git.switchBranch", "git.createBranch", "git.merge", "git.rebase", "git.deleteBranch"],
            f.Shell.GitBranchMenuItems(develop).Select(i => i.Command));
        Assert.Equal("Merge 'develop' into 'main'", f.Shell.GitBranchMenuItems(develop).Single(i => i.Command == "git.merge").Title);

        // Checkout on it switches every repository.
        await branches.Checkout!(develop);
        await ShellFixture.PumpUntil(() => f.Shell.GitChanges.Services.All(s => s.Status?.Branch == "develop"));
        Assert.Equal("develop", changes.BranchName);
        Assert.Contains("Checked out develop in 2 repositories.", f.Shell.StatusBar.Message ?? string.Empty, StringComparison.Ordinal);

        // One repository: Library alone, kept with the folder's state.
        changes.SelectedRepository = changes.Repositories.Single(r => r.Name == "Library");
        await ShellFixture.PumpUntil(() => f.Shell.Git.Root == library && !changes.IsAllRepositories);
        Assert.Empty(f.Shell.Git.Siblings);
        Assert.Equal("Library", changes.RepositoryButtonText);
        Assert.True(changes.CanAmend);
        f.Shell.SaveEditorSession();
        await f.Shell.SolutionState.FlushAsync();
        var session = File.ReadAllText(Path.Combine(f.Shell.SolutionStateDirectory!, "session.json"));
        Assert.Contains("\"gitRepositories\"", session, StringComparison.Ordinal);
        Assert.Contains("\"shown\"", session, StringComparison.Ordinal);

        // Back to all of them, then leave the library out: one repository again.
        await f.Shell.ShowAllRepositoriesAsync();
        Assert.True(changes.IsAllRepositories);
        Assert.Equal(app, f.Shell.Git.Root);
        await f.Shell.RemoveRepositoryAsync(library);
        Assert.Equal([app], f.Shell.Git.Repositories);
        Assert.False(changes.HasSeveralRepositories);
        Assert.False(changes.IsAllRepositories);
        Assert.Empty(f.Shell.Git.Siblings);
    }
}
