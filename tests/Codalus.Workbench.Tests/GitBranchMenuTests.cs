using System.Diagnostics;
using Codalus.Workbench.Git;

namespace Codalus.Workbench.Tests;

/// <summary>
/// What the Git Changes window's branch drop-down and its menus ask of git (#23, D38): the branches with their tracking
/// and worktrees, rename, delete with git's own merged rule, push of a branch that isn't checked out, a new branch from
/// another without switching, and the files two branches' tips differ in. Real temporary repositories with a local bare
/// remote; nothing leaves the machine.
/// </summary>
public sealed class GitBranchMenuTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-git-branches-" + Guid.NewGuid().ToString("N"));
    private readonly GitService _git = new(watch: false);

    private string Repo => Path.Combine(_root, "repo");

    private string Bare => Path.Combine(_root, "remote.git");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Repo);
        await Git(Repo, "init", "-q", "-b", "main");
        await Configure(Repo);
        await Commit(Repo, "a.txt", "initial");
        await Git(_root, "init", "-q", "--bare", "-b", "main", Bare);
        await Git(Repo, "remote", "add", "origin", Bare);
        await Git(Repo, "push", "-q", "-u", "origin", "main");
        Assert.True(await _git.OpenAsync(Repo, Ct));
    }

    public ValueTask DisposeAsync()
    {
        _git.Dispose();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private static async Task Configure(string repo)
    {
        await Git(repo, "config", "user.name", "Ada Tester");
        await Git(repo, "config", "user.email", "ada@example.com");
        await Git(repo, "config", "commit.gpgsign", "false");
        await Git(repo, "config", "core.autocrlf", "false");
    }

    private static async Task Commit(string repo, string file, string message)
    {
        var path = Path.Combine(repo, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, message + "\n", Ct);
        await Git(repo, "add", ".");
        await Git(repo, "commit", "-q", "-m", message);
    }

    private static async Task<string> Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {await error}");
        return await output;
    }

    /// <summary>
    /// The drop-down's rows: local branches with their upstream and its counts (B1's chips), the one checked out in
    /// another worktree with that worktree's folder (B12), a branch whose upstream is gone, and the remote's branches
    /// under their remote without its prefix (question 4), the remote's HEAD left out.
    /// </summary>
    [Fact]
    public async Task Branch_details_have_tracking_counts_worktrees_and_remote_names()
    {
        // feature: 1 commit here, 1 on origin (diverged); release: checked out in another worktree; gone: its upstream deleted.
        await Git(Repo, "branch", "feature");
        await Git(Repo, "push", "-q", "-u", "origin", "feature");
        var other = Path.Combine(_root, "other");
        await Git(_root, "clone", "-q", "-b", "feature", Bare, other);
        await Configure(other);
        await Commit(other, "theirs.txt", "theirs");
        await Git(other, "push", "-q", "origin", "feature");
        await Git(Repo, "fetch", "-q", "origin");
        await Git(Repo, "switch", "-q", "feature");
        await Commit(Repo, "mine.txt", "mine");
        await Git(Repo, "switch", "-q", "main");
        await Git(Repo, "branch", "release");
        var worktree = Path.Combine(_root, "release-tree");
        await Git(Repo, "worktree", "add", "-q", worktree, "release");
        await Git(Repo, "branch", "gone");
        await Git(Repo, "push", "-q", "-u", "origin", "gone");
        await Git(Repo, "push", "-q", "origin", "--delete", "gone");
        await Git(Repo, "fetch", "-q", "--prune", "origin");
        await Git(Repo, "remote", "set-head", "origin", "main");

        var branches = await _git.BranchDetailsAsync(Ct);
        var main = branches.Single(b => b.FullRef == "refs/heads/main");
        Assert.Equal(("main", false, true, "origin/main", 0, 0, (string?)null), (main.Name, main.IsRemote, main.IsCurrent, main.Upstream, main.Ahead, main.Behind, main.WorktreePath));
        var feature = branches.Single(b => b.FullRef == "refs/heads/feature");
        Assert.Equal(("origin/feature", "refs/remotes/origin/feature", 1, 1, false), (feature.Upstream, feature.UpstreamRef, feature.Ahead, feature.Behind, feature.IsCurrent));
        var release = branches.Single(b => b.FullRef == "refs/heads/release");
        Assert.Equal(Path.GetFullPath(worktree), release.WorktreePath);
        Assert.Null(release.Upstream);
        var gone = branches.Single(b => b.FullRef == "refs/heads/gone");
        Assert.True(gone.UpstreamGone);

        var remote = branches.Where(b => b.IsRemote).ToList();
        Assert.Equal(["origin/feature", "origin/main"], remote.Select(b => b.Name).Order(StringComparer.Ordinal));
        Assert.All(remote, b => Assert.Equal("origin", b.Remote));
        Assert.Equal("feature", remote.Single(b => b.Name == "origin/feature").ShortName);
        Assert.Equal("refs/remotes/origin/feature", remote.Single(b => b.Name == "origin/feature").FullRef);
        Assert.DoesNotContain(branches, b => b.FullRef.EndsWith("/HEAD", StringComparison.Ordinal));
    }

    /// <summary>
    /// git's own rules for a branch name (git check-ref-format --branch), checked as the name is typed (P6): the dialog says
    /// what is wrong under the box. Every name git refuses is refused, and the names it takes are taken.
    /// </summary>
    [Theory]
    [InlineData("feature/sign-in", true)]
    [InlineData("release/0.6", true)]
    [InlineData("fix-#12", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("two..dots", false)]
    [InlineData("-leading-dash", false)]
    [InlineData("ends.lock", false)]
    [InlineData("a/.hidden", false)]
    [InlineData("trailing/", false)]
    [InlineData("trailing.", false)]
    [InlineData("double//slash", false)]
    [InlineData("at@{brace", false)]
    [InlineData("@", false)]
    [InlineData("tilde~1", false)]
    [InlineData("caret^", false)]
    [InlineData("colon:", false)]
    [InlineData("question?", false)]
    [InlineData("star*", false)]
    [InlineData("bracket[", false)]
    [InlineData("back\\slash", false)]
    [InlineData("HEAD", false)]
    public async Task Branch_names_follow_gits_rules(string name, bool valid)
    {
        Assert.Equal(valid, GitRefNames.BranchNameError(name) is null);

        // The same answer as git's own check ("@" and "HEAD" git's branch command refuses itself; older git's check takes them).
        if (name is not ("" or "@" or "HEAD"))
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = Repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("check-ref-format");
            start.ArgumentList.Add("--branch");
            start.ArgumentList.Add(name);
            using var process = Process.Start(start)!;
            await process.WaitForExitAsync(Ct);
            Assert.Equal(valid, process.ExitCode == 0);
        }
    }

    /// <summary>
    /// New Local Branch From… (P4, P5) makes the branch at another branch, switching to it or not; Rename… (P6) renames a
    /// branch that isn't checked out; Delete (P7-P10) follows git's rule for a merged branch (its upstream, else HEAD),
    /// compares with HEAD instead when the upstream goes too, lists the commits that would be lost on both sides, and
    /// deletes the branch, then its upstream on the remote (P11).
    /// </summary>
    [Fact]
    public async Task New_branch_from_rename_and_delete_with_gits_merged_rule()
    {
        await Git(Repo, "branch", "feature");
        await Git(Repo, "push", "-q", "-u", "origin", "feature");

        // From feature, without switching: main stays checked out.
        await _git.CreateBranchAtAsync("feature-tests", "refs/heads/feature", switchTo: false, Ct);
        Assert.Equal("main", _git.Status!.Branch);
        Assert.Contains(await _git.BranchDetailsAsync(Ct), b => b.FullRef == "refs/heads/feature-tests");

        await _git.RenameBranchAsync("feature-tests", "tests/sign-in", Ct);
        var names = (await _git.BranchDetailsAsync(Ct)).Select(b => b.FullRef).ToList();
        Assert.Contains("refs/heads/tests/sign-in", names);
        Assert.DoesNotContain("refs/heads/feature-tests", names);

        // feature: two commits pushed, one more only here, and one only on origin.
        await Git(Repo, "switch", "-q", "feature");
        await Commit(Repo, "one.txt", "Add the sign-in page");
        await Commit(Repo, "two.txt", "Store tokens per user");
        await Git(Repo, "push", "-q");
        await Commit(Repo, "three.txt", "Not pushed yet");
        var other = Path.Combine(_root, "other");
        await Git(_root, "clone", "-q", "-b", "feature", Bare, other);
        await Configure(other);
        await Commit(other, "four.txt", "Handle expired tokens");
        await Git(other, "push", "-q", "origin", "feature");
        await Git(Repo, "fetch", "-q", "origin");
        await Git(Repo, "switch", "-q", "main");
        await _git.RefreshAsync(Ct);

        // git's rule: compared with its upstream, which lacks one commit.
        var check = await _git.IsFullyMergedAsync("feature", deletingUpstream: false, Ct);
        Assert.False(check.Merged);
        Assert.Equal(("origin/feature", true), (check.ComparedWith, check.ComparedWithUpstream));
        Assert.Equal(["Not pushed yet"], check.Commits.Select(c => c.Subject));
        Assert.Equal(["Handle expired tokens"], (await _git.UpstreamOnlyCommitsAsync("feature", Ct)).Select(c => c.Subject));

        // With the upstream going too: compared with HEAD (main), and the commit only origin has is lost as well.
        check = await _git.IsFullyMergedAsync("feature", deletingUpstream: true, Ct);
        Assert.Equal(("main", false), (check.ComparedWith, check.ComparedWithUpstream));
        Assert.Equal(["Not pushed yet", "Store tokens per user", "Add the sign-in page"], check.Commits.Select(c => c.Subject));
        Assert.Equal(["Handle expired tokens"], check.UpstreamOnly.Select(c => c.Subject));
        Assert.All(check.Commits, c => Assert.Equal(7, c.Hash.Length));

        // A merged branch without an upstream: compared with HEAD, merged.
        check = await _git.IsFullyMergedAsync("tests/sign-in", deletingUpstream: false, Ct);
        Assert.Equal((true, "main", false), (check.Merged, check.ComparedWith, check.ComparedWithUpstream));
        await _git.DeleteBranchAsync("tests/sign-in", force: false, Ct);

        // Delete both: the branch (forced, git -d would refuse) and then origin's.
        await _git.DeleteBranchAsync("feature", force: true, Ct);
        await _git.DeleteRemoteBranchAsync("origin", "feature", Ct);
        Assert.DoesNotContain(await _git.BranchDetailsAsync(Ct), b => b.FullRef is "refs/heads/feature" or "refs/remotes/origin/feature" or "refs/heads/tests/sign-in");
        Assert.Equal(string.Empty, (await Git(Bare, "branch", "--list", "feature")).Trim());
    }

    /// <summary>
    /// Push on another branch's menu sends that branch, not the checked-out one: to its upstream when it has one, else to
    /// origin, setting the upstream, as the first push of the current branch does.
    /// </summary>
    [Fact]
    public async Task Push_sends_a_branch_that_is_not_checked_out()
    {
        await Git(Repo, "branch", "feature");
        await Git(Repo, "push", "-q", "-u", "origin", "feature");
        await Git(Repo, "switch", "-q", "feature");
        await Commit(Repo, "f.txt", "feature work");
        await Git(Repo, "switch", "-q", "-c", "local-only");
        await Commit(Repo, "l.txt", "local work");
        await Git(Repo, "switch", "-q", "main");
        await _git.RefreshAsync(Ct);

        await _git.PushBranchAsync("feature", Ct);
        Assert.Equal((await Git(Repo, "rev-parse", "feature")).Trim(), (await Git(Bare, "rev-parse", "feature")).Trim());

        await _git.PushBranchAsync("refs/heads/local-only", Ct);
        Assert.Equal((await Git(Repo, "rev-parse", "local-only")).Trim(), (await Git(Bare, "rev-parse", "local-only")).Trim());
        Assert.Equal("origin/local-only", (await _git.BranchDetailsAsync(Ct)).Single(b => b.FullRef == "refs/heads/local-only").Upstream);
        Assert.Equal("main", _git.Status!.Branch);
    }

    /// <summary>
    /// Compare with Current Branch (C1, question 9) is tip to tip, from the current branch to the other: a file new on the
    /// other is A, one it removed is D, a rename names its old path; two branches with the same files give none (C3).
    /// </summary>
    [Fact]
    public async Task Compare_branches_lists_the_files_their_tips_differ_in()
    {
        await Commit(Repo, "src/Greeter.cs", "greeter");
        await Commit(Repo, "src/Tokens.cs", "a token store that is long enough for git to see the rename\nline two\nline three");
        await Commit(Repo, "tools/setup.sh", "setup");
        await Git(Repo, "branch", "same");
        await Git(Repo, "switch", "-q", "-c", "feature");
        await Commit(Repo, "src/SignIn.cs", "sign in");
        await File.WriteAllTextAsync(Path.Combine(Repo, "src", "Greeter.cs"), "greeter, changed\n", Ct);
        await Git(Repo, "mv", "src/Tokens.cs", "src/TokenStore.cs");
        await Git(Repo, "rm", "-q", "tools/setup.sh");
        await Git(Repo, "commit", "-q", "-am", "feature");
        await Git(Repo, "switch", "-q", "main");
        await _git.RefreshAsync(Ct);

        var files = await _git.CompareBranchesAsync("refs/heads/main", "refs/heads/feature", Ct);
        string P(params string[] parts) => Path.Combine([Repo, .. parts]);
        Assert.Equal(
            [('A', P("src", "SignIn.cs"), (string?)null), ('D', P("tools", "setup.sh"), null), ('M', P("src", "Greeter.cs"), null), ('R', P("src", "TokenStore.cs"), P("src", "Tokens.cs"))],
            files.Select(f => (f.Status, f.Path, f.OldPath)).OrderBy(f => f.Item1));
        Assert.Empty(await _git.CompareBranchesAsync("refs/heads/main", "refs/heads/same", Ct));
    }
}
