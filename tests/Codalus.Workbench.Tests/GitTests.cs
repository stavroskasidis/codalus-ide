using System.Diagnostics;
using Codalus.Workbench.Git;

namespace Codalus.Workbench.Tests;

/// <summary>The git CLI service (ADR 0008) against real temporary repositories.</summary>
public sealed class GitServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-git-" + Guid.NewGuid().ToString("N"));
    private readonly GitService _git = new(watch: false);

    private string Repo => Path.Combine(_root, "repo");

    private string File1 => Path.Combine(Repo, "a.txt");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] s_lines = ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve"];

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Repo);
        await Git(Repo, "init", "-q", "-b", "main");
        await Configure(Repo);
        await File.WriteAllTextAsync(File1, string.Join('\n', s_lines) + "\n", Ct);
        await Git(Repo, "add", ".");
        await Git(Repo, "commit", "-q", "-m", "initial");
        Assert.True(await _git.OpenAsync(Repo, Ct));
    }

    public ValueTask DisposeAsync()
    {
        _git.Dispose();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal); // git objects are read-only
        }

        Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// git starts on the thread pool, never on the caller's thread, which is often the UI thread (Sync, Git Changes): a
    /// start can take long (CreateProcess on Windows, a fork on Linux). Here the start, reading the variables it's given,
    /// waits for the call to have returned, which a start on the caller's thread can't see.
    /// </summary>
    [Fact]
    public async Task Git_starts_off_the_callers_thread()
    {
        using var returned = new ManualResetEventSlim();
        var probe = new StartProbe(returned);
        var run = _git.RunAsync(Repo, ["--version"], Ct, environment: probe);
        returned.Set();
        var (code, output, _) = await run;
        Assert.Equal(0, code);
        Assert.StartsWith("git version", output, StringComparison.Ordinal);
        Assert.True(probe.CallerHadReturned, "git started on the caller's thread, before RunAsync returned");
    }

    /// <summary>
    /// A write that finds the index locked tries again for a moment: on Windows the antivirus can hold the last command's
    /// <c>index.lock</c> after git deleted it (the command sweep's Stage File). A lock that stays is still an error.
    /// </summary>
    [Fact]
    public async Task A_write_that_finds_the_index_locked_tries_again_until_the_lock_is_gone()
    {
        await File.AppendAllTextAsync(File1, "thirteen\n", Ct);
        var lockFile = Path.Combine(Repo, ".git", "index.lock");
        await File.WriteAllTextAsync(lockFile, string.Empty, Ct);
        var stage = _git.StageAsync([File1], Ct);
        await Task.Delay(150, Ct);
        File.Delete(lockFile);
        await stage;
        var (_, staged, _) = await _git.RunAsync(Repo, ["diff", "--cached", "--name-only"], Ct);
        Assert.Equal("a.txt", staged.Trim());

        await File.AppendAllTextAsync(File1, "fourteen\n", Ct);
        await File.WriteAllTextAsync(lockFile, string.Empty, Ct);
        try
        {
            var error = await Assert.ThrowsAsync<GitException>(() => _git.StageAsync([File1], Ct));
            Assert.Contains("index.lock", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(lockFile);
        }
    }

    [Fact]
    public void Only_an_existing_lock_file_is_a_busy_lock()
    {
        Assert.True(GitService.IsLockBusy(128, "fatal: Unable to create 'C:/repo/.git/index.lock': File exists.\n\nAnother git process seems to be running in this repository"));
        Assert.True(GitService.IsLockBusy(128, "error: cannot lock ref 'refs/heads/main': Unable to create '/repo/.git/refs/heads/main.lock': File exists."));
        Assert.False(GitService.IsLockBusy(0, "Unable to create '/repo/.git/index.lock': File exists."));
        Assert.False(GitService.IsLockBusy(1, "error: pathspec 'b.txt' did not match any file(s) known to git"));
    }

    /// <summary>Variables for a git process that, when read, wait for the caller of RunAsync to have returned (5 s at most).</summary>
    private sealed class StartProbe(ManualResetEventSlim returned) : IReadOnlyDictionary<string, string>
    {
        public bool CallerHadReturned { get; private set; }

        public int Count => 0;

        public IEnumerable<string> Keys => [];

        public IEnumerable<string> Values => [];

        public string this[string key] => throw new KeyNotFoundException(key);

        public bool ContainsKey(string key) => false;

        public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out string value)
        {
            value = null;
            return false;
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            CallerHadReturned = returned.Wait(TimeSpan.FromSeconds(5));
            return Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static async Task Configure(string repo)
    {
        await Git(repo, "config", "user.name", "Ada Tester");
        await Git(repo, "config", "user.email", "ada@example.com");
        await Git(repo, "config", "commit.gpgsign", "false");
        await Git(repo, "config", "core.autocrlf", "false");
    }

    private static async Task<string> Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(Ct);
        var error = await process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
        return output;
    }

    /// <summary>Runs git where it may fail (a rebase or merge that stops on a conflict): its exit code.</summary>
    private static async Task<int> GitExitCode(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["GIT_EDITOR"] = "true";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        await Task.WhenAll(output, error);
        return process.ExitCode;
    }

    private async Task Edit(params (int Line, string Text)[] changes)
    {
        var lines = (await File.ReadAllTextAsync(File1, Ct)).TrimEnd('\n').Split('\n');
        foreach (var (line, text) in changes)
        {
            lines[line - 1] = text;
        }

        await File.WriteAllTextAsync(File1, string.Join('\n', lines) + "\n", Ct);
    }

    [Fact]
    public async Task Status_hunks_per_hunk_staging_and_discard()
    {
        await Edit((2, "TWO"), (11, "ELEVEN"));
        await File.WriteAllTextAsync(Path.Combine(Repo, "b.txt"), "new\n", Ct);
        await _git.RefreshAsync(Ct);

        var status = _git.Status!;
        Assert.Equal("main", status.Branch);
        Assert.Contains(status.Unstaged, f => f is { RelativePath: "a.txt", WorkTree: 'M' });
        Assert.Contains(status.Unstaged, f => f is { RelativePath: "b.txt", IsUntracked: true });
        Assert.Empty(status.Staged);

        var diff = (await _git.DiffAsync(File1, staged: false, Ct))!;
        Assert.Equal(2, diff.Hunks.Count);
        Assert.Contains(diff.Hunks[0].Lines, l => l is { Kind: '+', Text: "TWO" });

        await _git.StageHunkAsync(diff, diff.Hunks[0], Ct);
        Assert.Contains(_git.Status!.Staged, f => f.RelativePath == "a.txt");
        var staged = (await _git.DiffAsync(File1, staged: true, Ct))!;
        Assert.Single(staged.Hunks);
        Assert.Contains(staged.Hunks[0].Lines, l => l is { Kind: '+', Text: "TWO" });
        Assert.Single((await _git.DiffAsync(File1, staged: false, Ct))!.Hunks);
        Assert.Contains("TWO", await _git.ShowAsync(File1, ":", Ct), StringComparison.Ordinal);
        Assert.DoesNotContain("TWO", await _git.ShowAsync(File1, "HEAD", Ct), StringComparison.Ordinal);

        await _git.UnstageHunkAsync(staged, staged.Hunks[0], Ct);
        Assert.Empty(_git.Status!.Staged);

        diff = (await _git.DiffAsync(File1, staged: false, Ct))!;
        await _git.DiscardHunkAsync(diff, diff.Hunks[1], Ct);
        var text = await File.ReadAllTextAsync(File1, Ct);
        Assert.Contains("TWO", text, StringComparison.Ordinal);
        Assert.Contains("eleven", text, StringComparison.Ordinal);

        var untracked = (await _git.DiffAsync(Path.Combine(Repo, "b.txt"), staged: false, Ct))!;
        Assert.Contains(untracked.Hunks.Single().Lines, l => l is { Kind: '+', Text: "new" });

        await _git.DiscardAsync([File1, Path.Combine(Repo, "b.txt")], Ct);
        Assert.Empty(_git.Status!.Files);
        Assert.False(File.Exists(Path.Combine(Repo, "b.txt")));
    }

    [Fact]
    public async Task Commit_amend_branches_and_merge_conflicts()
    {
        await Edit((1, "ONE"));
        await _git.StageAsync([File1], Ct);
        await _git.CommitAsync("Change one\n\nDetails.", amend: false, all: false, Ct);
        Assert.Empty(_git.Status!.Files);
        Assert.Equal("Change one\n\nDetails.", await _git.LastCommitMessageAsync(Ct));

        await _git.CommitAsync("Change 1", amend: true, all: false, Ct);
        Assert.Equal("Change 1", await _git.LastCommitMessageAsync(Ct));
        Assert.Equal("2", (await Git(Repo, "rev-list", "--count", "HEAD")).Trim());
        await Assert.ThrowsAsync<GitException>(() => _git.CommitAsync("  ", amend: false, all: false, Ct));

        await _git.CreateBranchAsync("feature", switchTo: true, Ct);
        Assert.Equal("feature", _git.Status!.Branch);
        await Edit((5, "FIVE from feature"));
        await _git.CommitAsync("Feature", amend: false, all: true, Ct);

        var main = (await _git.BranchesAsync(Ct)).Single(b => b.Name == "main");
        await _git.SwitchAsync(main, Ct);
        Assert.Equal("main", _git.Status!.Branch);
        await Edit((5, "FIVE from main"));
        await _git.CommitAsync("Main", amend: false, all: true, Ct);

        await Assert.ThrowsAsync<GitException>(() => _git.MergeAsync("feature", Ct));
        Assert.True(_git.Status!.IsMerging);
        Assert.Single(_git.Status.Conflicts);
        await _git.AbortMergeAsync(Ct);
        Assert.False(_git.Status!.IsMerging);
        Assert.Empty(_git.Status.Files);
    }

    [Fact]
    public async Task Push_fetch_and_pull_with_a_remote()
    {
        var bare = Path.Combine(_root, "remote.git");
        await Git(_root, "init", "-q", "--bare", "-b", "main", bare);
        await Git(Repo, "remote", "add", "origin", bare);
        await _git.PushAsync(Ct); // first push: sets the upstream
        Assert.Equal("origin/main", _git.Status!.Upstream);

        var other = Path.Combine(_root, "other");
        await Git(_root, "clone", "-q", bare, other);
        await Configure(other);
        await File.WriteAllTextAsync(Path.Combine(other, "c.txt"), "from the other clone\n", Ct);
        await Git(other, "add", ".");
        await Git(other, "commit", "-q", "-m", "other");
        await Git(other, "push", "-q");

        await _git.FetchAsync(Ct);
        Assert.Equal(1, _git.Status!.Behind);
        await _git.PullAsync(Ct);
        Assert.Equal(0, _git.Status!.Behind);
        Assert.True(File.Exists(Path.Combine(Repo, "c.txt")));

        await Edit((3, "THREE"));
        await _git.CommitAsync("three", amend: false, all: true, Ct);
        Assert.Equal(1, _git.Status!.Ahead);
        await _git.PushAsync(Ct);
        Assert.Equal(0, _git.Status!.Ahead);
        Assert.Contains(await _git.BranchesAsync(Ct), b => b is { Name: "origin/main", IsRemote: true });
    }

    /// <summary>
    /// A bare remote with main and feature, both tracked here, and another clone of it that commits to both (the first
    /// commit to <paramref name="file"/>). Returns the other clone and its new feature commit.
    /// </summary>
    private async Task<(string Other, string FeatureTip)> RemoteWithNewCommitsAsync(string file = "c.txt")
    {
        var bare = Path.Combine(_root, "remote.git");
        await Git(_root, "init", "-q", "--bare", "-b", "main", bare);
        await Git(Repo, "remote", "add", "origin", bare);
        await Git(Repo, "push", "-q", "-u", "origin", "main");
        await Git(Repo, "branch", "feature");
        await Git(Repo, "push", "-q", "-u", "origin", "feature");

        var other = Path.Combine(_root, "other");
        await Git(_root, "clone", "-q", bare, other);
        await Configure(other);
        await File.WriteAllTextAsync(Path.Combine(other, file), "main from the other clone\n", Ct);
        await Git(other, "add", ".");
        await Git(other, "commit", "-q", "-m", "main from the other clone");
        await Git(other, "push", "-q", "origin", "main");
        await Git(other, "switch", "-q", "feature");
        await File.WriteAllTextAsync(Path.Combine(other, "feature.txt"), "feature from the other clone\n", Ct);
        await Git(other, "add", ".");
        await Git(other, "commit", "-q", "-m", "feature from the other clone");
        await Git(other, "push", "-q", "origin", "feature");
        return (other, (await Git(other, "rev-parse", "HEAD")).Trim());
    }

    /// <summary>
    /// Update Branch fast-forwards a branch that isn't checked out to its upstream (fetching only that branch), and leaves
    /// HEAD, the working tree and the other branches alone; the current branch is fast-forwarded with its files.
    /// </summary>
    [Fact]
    public async Task Update_branch_fast_forwards_a_branch_without_checking_it_out()
    {
        var (_, featureTip) = await RemoteWithNewCommitsAsync();
        var mainBefore = (await Git(Repo, "rev-parse", "main")).Trim();
        var featureBefore = (await Git(Repo, "rev-parse", "feature")).Trim();

        var update = await _git.UpdateBranchAsync("feature", Ct);
        Assert.Equal(new GitBranchUpdate("feature", GitBranchUpdateOutcome.Updated) { Upstream = "origin/feature", Before = featureBefore, After = featureTip, Behind = 1 }, update);
        Assert.Equal(featureTip, (await Git(Repo, "rev-parse", "refs/heads/feature")).Trim());
        Assert.Equal(featureTip, (await Git(Repo, "rev-parse", "refs/remotes/origin/feature")).Trim()); // its remote-tracking branch too
        Assert.Equal("main", _git.Status!.Branch);
        Assert.Equal(mainBefore, (await Git(Repo, "rev-parse", "HEAD")).Trim());
        Assert.False(File.Exists(Path.Combine(Repo, "feature.txt")));
        Assert.Empty(_git.Status.Files);

        // Only feature was fetched: main's remote-tracking branch hasn't moved.
        Assert.Equal(mainBefore, (await Git(Repo, "rev-parse", "refs/remotes/origin/main")).Trim());

        // Again, by its full ref: nothing to do.
        update = await _git.UpdateBranchAsync("refs/heads/feature", Ct);
        Assert.Equal(GitBranchUpdateOutcome.UpToDate, update.Outcome);
        Assert.Equal((featureTip, featureTip), (update.Before, update.After));

        // The current branch: fast-forwarded, its new file in the working tree, nothing left to pull.
        await File.WriteAllTextAsync(Path.Combine(Repo, "notes.txt"), "uncommitted\n", Ct);
        update = await _git.UpdateBranchAsync("main", Ct);
        Assert.Equal((GitBranchUpdateOutcome.Updated, true, 1), (update.Outcome, update.IsCurrent, update.Behind));
        Assert.Equal(update.After, (await Git(Repo, "rev-parse", "HEAD")).Trim());
        Assert.NotEqual(mainBefore, update.After);
        Assert.True(File.Exists(Path.Combine(Repo, "c.txt")));
        Assert.Equal("uncommitted\n", await File.ReadAllTextAsync(Path.Combine(Repo, "notes.txt"), Ct));
        Assert.Equal(("main", 0), (_git.Status!.Branch, _git.Status.Behind));
    }

    /// <summary>
    /// Update Branch never rewrites a branch: one that has diverged from its upstream stays as it is (with both counts), one
    /// with only its own commits has nothing to update, and one with no upstream or checked out in another worktree is
    /// left for the shell to explain.
    /// </summary>
    [Fact]
    public async Task Update_branch_leaves_diverged_unpublished_and_elsewhere_checked_out_branches()
    {
        var (_, featureTip) = await RemoteWithNewCommitsAsync();

        // feature gets a commit of its own here, so it and origin/feature have diverged.
        await Git(Repo, "switch", "-q", "feature");
        await File.WriteAllTextAsync(Path.Combine(Repo, "mine.txt"), "mine\n", Ct);
        await Git(Repo, "add", ".");
        await Git(Repo, "commit", "-q", "-m", "mine");
        var mine = (await Git(Repo, "rev-parse", "HEAD")).Trim();
        await Git(Repo, "switch", "-q", "main");
        await _git.RefreshAsync(Ct);

        var update = await _git.UpdateBranchAsync("feature", Ct);
        Assert.Equal(new GitBranchUpdate("feature", GitBranchUpdateOutcome.Diverged) { Upstream = "origin/feature", Before = mine, After = mine, Ahead = 1, Behind = 1 }, update);
        Assert.Equal(mine, (await Git(Repo, "rev-parse", "refs/heads/feature")).Trim());
        Assert.Equal(featureTip, (await Git(Repo, "rev-parse", "refs/remotes/origin/feature")).Trim()); // fetched all the same

        // The current branch diverged too: no merge commit, nothing changes.
        await File.WriteAllTextAsync(Path.Combine(Repo, "main.txt"), "main here\n", Ct);
        await Git(Repo, "add", ".");
        await Git(Repo, "commit", "-q", "-m", "main here");
        var mainHere = (await Git(Repo, "rev-parse", "HEAD")).Trim();
        await _git.RefreshAsync(Ct);
        update = await _git.UpdateBranchAsync("main", Ct);
        Assert.Equal((GitBranchUpdateOutcome.Diverged, true, 1, 1), (update.Outcome, update.IsCurrent, update.Ahead, update.Behind));
        Assert.Equal(mainHere, (await Git(Repo, "rev-parse", "HEAD")).Trim());
        Assert.False(File.Exists(Path.Combine(Repo, "c.txt")));

        // Ahead of its upstream only: nothing to update (Push sends it), and not rewound.
        await Git(Repo, "branch", "--track", "ahead", "origin/main");
        await Git(Repo, "switch", "-q", "ahead");
        await File.WriteAllTextAsync(Path.Combine(Repo, "ahead.txt"), "ahead\n", Ct);
        await Git(Repo, "add", ".");
        await Git(Repo, "commit", "-q", "-m", "ahead");
        var aheadTip = (await Git(Repo, "rev-parse", "HEAD")).Trim();
        await Git(Repo, "switch", "-q", "main");
        await _git.RefreshAsync(Ct);
        update = await _git.UpdateBranchAsync("ahead", Ct);
        Assert.Equal(new GitBranchUpdate("ahead", GitBranchUpdateOutcome.Ahead) { Upstream = "origin/main", Before = aheadTip, After = aheadTip, Ahead = 1 }, update);
        Assert.Equal(aheadTip, (await Git(Repo, "rev-parse", "refs/heads/ahead")).Trim());

        // No upstream: nothing is fetched.
        await Git(Repo, "branch", "local-only");
        update = await _git.UpdateBranchAsync("local-only", Ct);
        Assert.Equal(GitBranchUpdateOutcome.NoUpstream, update.Outcome);
        Assert.Null(update.Upstream);

        // Checked out in another worktree: that worktree's files would no longer match, so it's left to a pull there.
        var worktree = Path.Combine(_root, "worktree");
        await Git(Repo, "branch", "--track", "elsewhere", "origin/feature");
        await Git(Repo, "branch", "-f", "elsewhere", featureTip + "~1");
        await Git(Repo, "worktree", "add", "-q", worktree, "elsewhere");
        update = await _git.UpdateBranchAsync("elsewhere", Ct);
        Assert.Equal(GitBranchUpdateOutcome.CheckedOutElsewhere, update.Outcome);
        Assert.Equal(Codalus.Platform.RealPath.Resolve(worktree), Codalus.Platform.RealPath.Resolve(update.WorktreePath!));
        Assert.Equal(Path.GetFullPath(update.WorktreePath!), update.WorktreePath); // the OS's separators (git writes C:/ on Windows)
        Assert.Null(update.Operation);
        Assert.Equal((await Git(Repo, "rev-parse", featureTip + "~1")).Trim(), (await Git(Repo, "rev-parse", "refs/heads/elsewhere")).Trim());

        // A name that is only a folder of branches isn't a branch.
        await Git(Repo, "branch", "topic/one");
        var missing = await Assert.ThrowsAsync<GitException>(() => _git.UpdateBranchAsync("topic", Ct));
        Assert.Contains("'topic'", missing.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rebase detaches HEAD, so git lists the branch being rebased as checked out nowhere, yet it counts as current
    /// (D38, question 21): Update leaves it alone, here and in another worktree, without running git (git 2.35 and later
    /// refuse to fetch into it, older git moved it under the rebase, which then couldn't finish). So does the current
    /// branch during a merge.
    /// </summary>
    [Fact]
    public async Task Update_branch_leaves_a_branch_being_rebased_and_a_merging_current_branch_alone()
    {
        await RemoteWithNewCommitsAsync("a.txt");

        // feature changes a.txt as origin/main does, so rebasing it onto origin/main stops on a conflict.
        await Git(Repo, "switch", "-q", "feature");
        await File.WriteAllTextAsync(File1, "feature's own a.txt\n", Ct);
        await Git(Repo, "commit", "-q", "-am", "feature's a.txt");
        var featureHere = (await Git(Repo, "rev-parse", "HEAD")).Trim();
        await Git(Repo, "fetch", "-q", "origin", "main");
        Assert.NotEqual(0, await GitExitCode(Repo, "rebase", "origin/main"));
        await _git.RefreshAsync(Ct);
        Assert.Equal("rebase", _git.Status!.Operation);
        Assert.Null(_git.Status.Branch); // HEAD is detached
        var log = new List<string>();
        _git.Log += (_, line) => log.Add(line);

        var update = await _git.UpdateBranchAsync("feature", Ct);
        Assert.Equal((GitBranchUpdateOutcome.OperationInProgress, "rebase"), (update.Outcome, update.Operation));
        Assert.Equal(featureHere, (await Git(Repo, "rev-parse", "refs/heads/feature")).Trim());
        Assert.Contains(await _git.BranchOperationsAsync(Ct), o => o is { Branch: "feature", Operation: "rebase", IsThisWorktree: true });
        Assert.DoesNotContain(log, l => l.StartsWith("> git fetch", StringComparison.Ordinal));
        await Git(Repo, "rebase", "--abort");

        // In another worktree: said to be checked out there, and where.
        var worktree = Path.Combine(_root, "worktree");
        await Git(Repo, "switch", "-q", "main");
        await Git(Repo, "worktree", "add", "-q", worktree, "feature");
        await Configure(worktree);
        Assert.NotEqual(0, await GitExitCode(worktree, "rebase", "origin/main"));
        update = await _git.UpdateBranchAsync("feature", Ct);
        Assert.Equal((GitBranchUpdateOutcome.CheckedOutElsewhere, "rebase"), (update.Outcome, update.Operation));
        Assert.Equal(Codalus.Platform.RealPath.Resolve(worktree), Codalus.Platform.RealPath.Resolve(update.WorktreePath!));
        Assert.Equal(featureHere, (await Git(Repo, "rev-parse", "refs/heads/feature")).Trim());
        Assert.DoesNotContain(log, l => l.StartsWith("> git fetch", StringComparison.Ordinal));
        await Git(worktree, "rebase", "--abort");

        // The current branch during a merge: nothing is fetched, nothing moves.
        await Git(Repo, "switch", "-q", "-c", "conflicting");
        await File.WriteAllTextAsync(File1, "conflicting a.txt\n", Ct);
        await Git(Repo, "commit", "-q", "-am", "conflicting a.txt");
        await Git(Repo, "switch", "-q", "main");
        await File.WriteAllTextAsync(File1, "main's own a.txt\n", Ct);
        await Git(Repo, "commit", "-q", "-am", "main's a.txt");
        var mainHere = (await Git(Repo, "rev-parse", "HEAD")).Trim();
        Assert.NotEqual(0, await GitExitCode(Repo, "merge", "--no-edit", "conflicting"));
        await _git.RefreshAsync(Ct);
        update = await _git.UpdateBranchAsync("main", Ct);
        Assert.Equal((GitBranchUpdateOutcome.OperationInProgress, "merge", true), (update.Outcome, update.Operation, update.IsCurrent));
        Assert.Equal(mainHere, (await Git(Repo, "rev-parse", "HEAD")).Trim());
        Assert.DoesNotContain(log, l => l.StartsWith("> git fetch", StringComparison.Ordinal));
    }

    /// <summary>
    /// Updates of two branches at once, one an ancestor of the other, while a full fetch also runs: each ends at its own
    /// upstream's tip (a branch that isn't checked out moves in git's own fetch, never from a FETCH_HEAD another fetch may
    /// have rewritten), and a second Update waits for the first (<see cref="GitService.FetchGate"/>), as does a cancel.
    /// </summary>
    [Fact]
    public async Task Update_branch_runs_one_at_a_time_and_each_branch_gets_its_own_upstream()
    {
        var (other, featureTip) = await RemoteWithNewCommitsAsync();

        // develop: behind origin/develop, and an ancestor of origin/feature's tip.
        await Git(other, "branch", "develop", featureTip + "~1");
        await Git(other, "push", "-q", "origin", "develop");
        await Git(Repo, "fetch", "-q", "origin", "develop");
        await Git(Repo, "branch", "--track", "develop", "origin/develop");
        await Git(other, "switch", "-q", "develop");
        await File.WriteAllTextAsync(Path.Combine(other, "develop.txt"), "develop\n", Ct);
        await Git(other, "add", ".");
        await Git(other, "commit", "-q", "-m", "develop");
        await Git(other, "push", "-q", "origin", "develop");
        var developTip = (await Git(other, "rev-parse", "HEAD")).Trim();

        var develop = _git.UpdateBranchAsync("develop", Ct);
        var feature = _git.UpdateBranchAsync("feature", Ct);
        var fetch = _git.FetchAsync(Ct);
        await Task.WhenAll(develop, feature, fetch);
        Assert.Equal((GitBranchUpdateOutcome.Updated, developTip, 1), ((await develop).Outcome, (await develop).After, (await develop).Behind));
        Assert.Equal((GitBranchUpdateOutcome.Updated, featureTip, 1), ((await feature).Outcome, (await feature).After, (await feature).Behind));
        Assert.Equal(developTip, (await Git(Repo, "rev-parse", "refs/heads/develop")).Trim());
        Assert.Equal(featureTip, (await Git(Repo, "rev-parse", "refs/heads/feature")).Trim());

        // While another FETCH_HEAD writer holds the gate, Update waits; a cancel stops it waiting.
        await _git.FetchGate.WaitAsync(Ct);
        Task<GitBranchUpdate> waiting;
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            waiting = _git.UpdateBranchAsync("feature", cancel.Token);
            await Task.Delay(300, Ct);
            Assert.False(waiting.IsCompleted);
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }

        waiting = _git.UpdateBranchAsync("feature", Ct);
        await Task.Delay(300, Ct);
        Assert.False(waiting.IsCompleted);
        _git.FetchGate.Release();
        Assert.Equal(GitBranchUpdateOutcome.UpToDate, (await waiting).Outcome);
    }

    /// <summary>
    /// The current branch's fast-forward is git's <c>merge --ff-only</c>: when it would overwrite a local change, git
    /// refuses and says so, and nothing moves or is lost (no stash, no merge commit).
    /// </summary>
    [Fact]
    public async Task Update_branch_keeps_local_changes_the_current_branch_fast_forward_would_overwrite()
    {
        await RemoteWithNewCommitsAsync("a.txt"); // origin/main changes a.txt
        var mainBefore = (await Git(Repo, "rev-parse", "HEAD")).Trim();
        await File.WriteAllTextAsync(File1, "my uncommitted a.txt\n", Ct);
        await _git.RefreshAsync(Ct);

        var failure = await Assert.ThrowsAsync<GitException>(() => _git.UpdateBranchAsync("main", Ct));
        Assert.Contains("a.txt", failure.Message, StringComparison.Ordinal);
        Assert.Equal(mainBefore, (await Git(Repo, "rev-parse", "HEAD")).Trim());
        Assert.Equal("my uncommitted a.txt\n", await File.ReadAllTextAsync(File1, Ct));
        Assert.Equal(1, _git.Status!.Behind); // fetched, so the count shows what's waiting
    }

    [Fact]
    public async Task Blame_annotates_committed_and_uncommitted_lines()
    {
        var blame = await _git.BlameAsync(File1, contents: null, Ct);
        var range = Assert.Single(blame);
        Assert.Equal((1, 12), (range.StartLine, range.LineCount));
        Assert.Equal("Ada Tester", range.Author);
        Assert.Equal("initial", range.Summary);

        var edited = string.Join('\n', s_lines.Select((l, i) => i == 5 ? "SIX" : l)) + "\n";
        blame = await _git.BlameAsync(File1, edited, Ct);
        Assert.Equal(3, blame.Count);
        Assert.True(blame[1].IsUncommitted);
        Assert.Equal(6, blame[1].StartLine);
    }

    [Fact]
    public async Task Paths_through_a_symbolic_link_reach_the_same_files()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symbolic links need extra privileges on Windows.");
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, Repo);
        await Edit((4, "FOUR"));
        await _git.RefreshAsync(Ct);

        var viaLink = Path.Combine(link, "a.txt");
        Assert.Single((await _git.DiffAsync(viaLink, staged: false, Ct))!.Hunks);
        await _git.StageAsync([viaLink], Ct);
        Assert.Contains(_git.Status!.Staged, f => f.RelativePath == "a.txt");
    }

    [Fact]
    public async Task Folders_outside_a_repository_have_no_status()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);
        using var git = new GitService(watch: false);
        Assert.False(await git.OpenAsync(plain, Ct) && git.Root!.StartsWith(plain, StringComparison.OrdinalIgnoreCase));
    }
}

public class LineDiffTests
{
    [Fact]
    public void Changes_are_added_modified_and_deleted_runs()
    {
        string[] old = ["a", "b", "c", "d", "e", "f"];
        string[] now = ["a", "B", "c", "x", "y", "d", "f"];
        var changes = LineDiff.Changes(old, now);
        Assert.Equal(
            [new LineChange(LineChangeKind.Modified, 2, 1), new LineChange(LineChangeKind.Added, 4, 2), new LineChange(LineChangeKind.Deleted, 7, 0)],
            changes);
    }

    [Fact]
    public void The_edit_script_reproduces_both_texts()
    {
        var random = new Random(7);
        for (var round = 0; round < 50; round++)
        {
            var old = Enumerable.Range(0, random.Next(0, 40)).Select(_ => random.Next(0, 6).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var now = Enumerable.Range(0, random.Next(0, 40)).Select(_ => random.Next(0, 6).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var ops = LineDiff.Diff(old, now);
            Assert.Equal(old, ops.Where(o => o.Kind != '+').Select(o => old[o.OldIndex]));
            Assert.Equal(now, ops.Where(o => o.Kind != '-').Select(o => now[o.NewIndex]));
            Assert.All(ops.Where(o => o.Kind == ' '), o => Assert.Equal(old[o.OldIndex], now[o.NewIndex]));
        }
    }

    [Fact]
    public void Lines_and_hunk_headers()
    {
        Assert.Equal(["a", "b"], LineDiff.Lines("a\r\nb\n"));
        Assert.Empty(LineDiff.Lines(string.Empty));
        Assert.Equal((3, 1, 3, 2), GitParsers.ParseHunkHeader("@@ -3 +3,2 @@ void M()"));
    }

    [Fact]
    public void Porcelain_v2_status_with_renames_and_conflicts()
    {
        var output = "# branch.oid abc\0# branch.head my branch\0# branch.upstream origin/my branch\0# branch.ab +2 -1\0"
                     + "1 .M N... 100644 100644 100644 h h src/a b.cs\0"
                     + "2 R. N... 100644 100644 100644 h h R100 new.cs\0old.cs\0"
                     + "u UU N... 100644 100644 100644 100644 h h h c.cs\0"
                     + "? d.cs\0";
        var status = GitParsers.ParseStatus("/r", output, isMerging: true);
        Assert.Equal(("my branch", "origin/my branch", 2, 1), (status.Branch, status.Upstream, status.Ahead, status.Behind));
        Assert.Equal(["src/a b.cs", "new.cs", "c.cs", "d.cs"], status.Files.Select(f => f.RelativePath));
        Assert.True(status.Files[1].IsStaged);
        Assert.EndsWith("old.cs", status.Files[1].OriginalPath, StringComparison.Ordinal);
        Assert.True(status.Files[2].IsConflict);
        Assert.True(status.Files[3].IsUntracked);
    }
}

public class RealPathTests
{
    [Fact]
    public void Symbolic_links_in_any_component_are_resolved()
    {
        var root = Codalus.Platform.RealPath.Resolve(Path.Combine(Path.GetTempPath(), "codalus-realpath-" + Guid.NewGuid().ToString("N")));
        var target = Directory.CreateDirectory(Path.Combine(root, "target", "inner")).FullName;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(target, Codalus.Platform.RealPath.Resolve(target + Path.DirectorySeparatorChar + "."));
                return;
            }

            Directory.CreateSymbolicLink(Path.Combine(root, "link"), Path.Combine(root, "target"));
            Assert.Equal(Path.Combine(target, "file.cs"), Codalus.Platform.RealPath.Resolve(Path.Combine(root, "link", "inner", "file.cs")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
