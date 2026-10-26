using System.Globalization;

namespace Codalus.Workbench.Git;

public sealed partial class GitService
{
    /// <summary>
    /// Held by every operation that writes FETCH_HEAD (Update, Fetch, Pull, fetching a pull request): Update reads what its
    /// fetch wrote there, and another fetch in between would make it read that fetch's commit. A second Update waits for
    /// the first (D38, question 21).
    /// </summary>
    internal SemaphoreSlim FetchGate { get; } = new(1, 1);

    private async Task WithFetchGateAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await FetchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            FetchGate.Release();
        }
    }

    // ------------------------------------------------------------------ updating a branch

    /// <summary>
    /// Brings a local branch up to date with its upstream without checking it out, but only by a fast-forward (D38,
    /// question 21). A branch that isn't checked out takes one process, <c>fetch &lt;remote&gt;
    /// refs/heads/&lt;up&gt;:refs/heads/&lt;branch&gt;</c>: git only fast-forwards it, and moves its remote-tracking branch too;
    /// when git refuses (exit 1), <c>rev-list --left-right --count</c> against FETCH_HEAD says whether it has diverged or
    /// is only ahead. The current branch: <c>fetch &lt;remote&gt; refs/heads/&lt;up&gt;</c>, the same count, then
    /// <c>merge --ff-only</c> when it is behind and not ahead, so its files follow and no merge commit or rebase is ever
    /// made. git isn't run for a branch with no upstream, one checked out in another worktree (read first: git before 2.35
    /// moved it and left that worktree out of step), one a rebase or bisect is working on (HEAD is detached then, so git
    /// lists it nowhere; it counts as checked out where that runs), or the current branch during a merge or rebase; the
    /// result says which. Runs under <see cref="FetchGate"/>. git's failures (no network, the upstream branch deleted on
    /// the remote, local changes the current branch's fast-forward would overwrite) throw <see cref="GitException"/>;
    /// cancelling kills git.
    /// </summary>
    /// <param name="branch">A local branch: its name (<c>main</c>) or full ref (<c>refs/heads/main</c>).</param>
    public async Task<GitBranchUpdate> UpdateBranchAsync(string branch, CancellationToken cancellationToken = default)
    {
        var root = Root ?? throw new InvalidOperationException("No git repository is open.");
        var name = branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch["refs/heads/".Length..] : branch;
        await FetchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var fetched = false;
        try
        {
            var (_, output, _) = await RunAsync(
                root,
                ["for-each-ref", "--format=%(refname)%00%(HEAD)%00%(objectname)%00%(upstream:short)%00%(upstream:remotename)%00%(upstream:remoteref)%00%(worktreepath)", "refs/heads/" + name],
                cancellationToken, readOnly: true).ConfigureAwait(false);

            // A pattern matches the refs below it too (refs/heads/feature matches refs/heads/feature/x): only the branch itself.
            var fields = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\0'))
                .FirstOrDefault(parts => parts.Length >= 7 && parts[0] == "refs/heads/" + name)
                ?? throw new GitException($"There is no local branch named '{name}'.", -1);
            var (isCurrent, before, remote, remoteRef, worktree) = (fields[1] == "*", fields[2], fields[4], fields[5], fields[6]);
            if (remote.Length == 0 || remoteRef.Length == 0)
            {
                return new GitBranchUpdate(name, GitBranchUpdateOutcome.NoUpstream) { Before = before, After = before, IsCurrent = isCurrent };
            }

            var upstream = fields[3].Length > 0 ? fields[3]
                : remote + "/" + (remoteRef.StartsWith("refs/heads/", StringComparison.Ordinal) ? remoteRef["refs/heads/".Length..] : remoteRef);
            GitBranchUpdate Result(GitBranchUpdateOutcome outcome, string after, int ahead = 0, int behind = 0) =>
                new(name, outcome) { Upstream = upstream, Before = before, After = after, IsCurrent = isCurrent, Ahead = ahead, Behind = behind };

            // A rebase or bisect counts as having its branch checked out, here or in another worktree.
            if ((await BranchOperationsAsync(root, cancellationToken).ConfigureAwait(false)).FirstOrDefault(o => o.Branch == name) is { } held)
            {
                return held.IsThisWorktree
                    ? Result(GitBranchUpdateOutcome.OperationInProgress, before) with { Operation = held.Operation }
                    : Result(GitBranchUpdateOutcome.CheckedOutElsewhere, before) with { Operation = held.Operation, WorktreePath = held.WorktreePath };
            }

            if (!isCurrent && worktree.Length > 0)
            {
                return Result(GitBranchUpdateOutcome.CheckedOutElsewhere, before) with { WorktreePath = Path.GetFullPath(worktree) };
            }

            // The current branch while a merge or rebase (git am) runs on it.
            if (isCurrent && await GitDirectoryOfAsync(root, cancellationToken).ConfigureAwait(false) is { } gitDir && OperationIn(gitDir) is ("merge" or "rebase") and var current)
            {
                return Result(GitBranchUpdateOutcome.OperationInProgress, before) with { Operation = current };
            }

            fetched = true;
            if (!isCurrent)
            {
                // One process: git moves the branch only by a fast-forward (and its remote-tracking branch with it).
                var (code, fetchOutput, error) = await RunAsync(root, ["fetch", remote, remoteRef + ":refs/heads/" + name], cancellationToken, check: false).ConfigureAwait(false);
                if (code == 0)
                {
                    var after = (await RunAsync(root, ["rev-parse", "--verify", "refs/heads/" + name], cancellationToken, readOnly: true).ConfigureAwait(false)).Output.Trim();
                    return after == before
                        ? Result(GitBranchUpdateOutcome.UpToDate, before)
                        : Result(GitBranchUpdateOutcome.Updated, after, behind: await CountAsync(root, before + ".." + after, cancellationToken).ConfigureAwait(false));
                }

                // Exit 1 is git refusing a non-fast-forward; FETCH_HEAD has what it fetched all the same. Git 2.51 no longer
                // moves the remote-tracking branch when the update is refused, so fetch the branch alone again: the
                // remote's own refspec moves it (and FETCH_HEAD is the same commit).
                if (code == 1)
                {
                    await RunAsync(root, ["fetch", remote, remoteRef], cancellationToken, check: false).ConfigureAwait(false);
                    var (ahead, behind) = await LeftRightAsync(root, "refs/heads/" + name, "FETCH_HEAD", cancellationToken).ConfigureAwait(false);
                    if (ahead > 0)
                    {
                        return Result(behind > 0 ? GitBranchUpdateOutcome.Diverged : GitBranchUpdateOutcome.Ahead, before, ahead, behind);
                    }
                }

                throw Failure(error, fetchOutput, "git fetch failed.", code);
            }

            await RunAsync(root, ["fetch", remote, remoteRef], cancellationToken).ConfigureAwait(false);
            var commit = (await RunAsync(root, ["rev-parse", "--verify", "FETCH_HEAD^{commit}"], cancellationToken, readOnly: true).ConfigureAwait(false)).Output.Trim();
            var (mine, theirs) = await LeftRightAsync(root, before, commit, cancellationToken).ConfigureAwait(false);
            if (theirs == 0)
            {
                return mine == 0 ? Result(GitBranchUpdateOutcome.UpToDate, before) : Result(GitBranchUpdateOutcome.Ahead, before, mine);
            }

            if (mine > 0)
            {
                return Result(GitBranchUpdateOutcome.Diverged, before, mine, theirs);
            }

            await RunAsync(root, ["merge", "--ff-only", commit], cancellationToken).ConfigureAwait(false);
            return Result(GitBranchUpdateOutcome.Updated, commit, behind: theirs);
        }
        finally
        {
            FetchGate.Release();
            if (fetched)
            {
                // Even after a cancel or a failure, git may have moved a ref.
                await RefreshSafeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The branches a rebase or bisect is working on, in this worktree and the others, from each worktree's git folder
    /// (<c>rebase-merge/head-name</c>, <c>rebase-apply/head-name</c>, <c>BISECT_START</c>), as git itself finds them
    /// before it fetches into a branch. HEAD is detached while they run, so <c>%(HEAD)</c> and <c>%(worktreepath)</c> don't
    /// show these branches.
    /// </summary>
    public async Task<IReadOnlyList<GitBranchOperation>> BranchOperationsAsync(CancellationToken cancellationToken = default) =>
        await BranchOperationsAsync(Root ?? throw new InvalidOperationException("No git repository is open."), cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<GitBranchOperation>> BranchOperationsAsync(string root, CancellationToken cancellationToken)
    {
        var (code, output, _) = await RunAsync(root, ["rev-parse", "--absolute-git-dir", "--git-common-dir"], cancellationToken, check: false, readOnly: true).ConfigureAwait(false);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (code != 0 || lines.Length < 2)
        {
            return [];
        }

        // --git-common-dir may be relative to the folder git ran in (the root).
        var here = Path.GetFullPath(lines[0]);
        var common = Path.GetFullPath(Path.Combine(root, lines[1]));
        var gitDirs = new List<(string GitDir, bool Main)> { (common, true) };
        var linked = Path.Combine(common, "worktrees");
        if (Directory.Exists(linked))
        {
            gitDirs.AddRange(Directory.EnumerateDirectories(linked).Select(d => (d, false)));
        }

        var operations = new List<GitBranchOperation>();
        foreach (var (gitDir, main) in gitDirs)
        {
            foreach (var (file, operation) in s_operationFiles)
            {
                // head-name holds the full ref (or "detached HEAD"); BISECT_START the branch's name (or a commit, started detached).
                if (ReadSmallFile(Path.Combine(gitDir, file)) is not { Length: > 0 } text || operations.Any(o => o.Branch == text))
                {
                    continue;
                }

                var isHere = Projects.SolutionService.PathComparer.Equals(Path.GetFullPath(gitDir), here);
                var worktree = isHere ? root : main ? await MainWorktreeAsync(root, common, cancellationToken).ConfigureAwait(false) : LinkedWorktree(gitDir);
                operations.Add(new GitBranchOperation(text.StartsWith("refs/heads/", StringComparison.Ordinal) ? text["refs/heads/".Length..] : text, operation, worktree, isHere));
            }
        }

        return operations;
    }

    private static readonly (string File, string Operation)[] s_operationFiles =
        [("rebase-merge/head-name", "rebase"), ("rebase-apply/head-name", "rebase"), ("BISECT_START", "bisect")];

    private static string? ReadSmallFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A linked worktree's folder: its git folder's <c>gitdir</c> file names the worktree's <c>.git</c> file.</summary>
    private static string? LinkedWorktree(string gitDir) =>
        ReadSmallFile(Path.Combine(gitDir, "gitdir")) is { Length: > 0 } dotGit && Path.GetDirectoryName(Path.GetFullPath(dotGit)) is { } folder ? folder : null;

    /// <summary>The main worktree's folder: the first of <c>worktree list --porcelain</c> (the common git folder's parent, usually).</summary>
    private async Task<string?> MainWorktreeAsync(string root, string common, CancellationToken cancellationToken)
    {
        var (code, output, _) = await RunAsync(root, ["worktree", "list", "--porcelain"], cancellationToken, check: false, readOnly: true).ConfigureAwait(false);
        var first = output.Split('\n', StringSplitOptions.TrimEntries).FirstOrDefault(l => l.StartsWith("worktree ", StringComparison.Ordinal));
        return code == 0 && first is not null ? Path.GetFullPath(first["worktree ".Length..]) : Path.GetDirectoryName(common);
    }

    private async Task<string?> GitDirectoryOfAsync(string root, CancellationToken cancellationToken)
    {
        var (code, output, _) = await RunAsync(root, ["rev-parse", "--absolute-git-dir"], cancellationToken, check: false, readOnly: true).ConfigureAwait(false);
        return code == 0 && output.Trim().Length > 0 ? output.Trim() : null;
    }

    /// <summary><c>rev-list --left-right --count left...right</c>: the commits only <paramref name="left"/> has, and only <paramref name="right"/>.</summary>
    private async Task<(int Left, int Right)> LeftRightAsync(string root, string left, string right, CancellationToken cancellationToken)
    {
        var (_, output, _) = await RunAsync(root, ["rev-list", "--left-right", "--count", left + "..." + right], cancellationToken, readOnly: true).ConfigureAwait(false);
        var counts = output.Split((char[])['\t', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        return (int.Parse(counts[0], CultureInfo.InvariantCulture), int.Parse(counts[1], CultureInfo.InvariantCulture));
    }

    private async Task<int> CountAsync(string root, string range, CancellationToken cancellationToken) =>
        int.Parse((await RunAsync(root, ["rev-list", "--count", range], cancellationToken, readOnly: true).ConfigureAwait(false)).Output.Trim(), CultureInfo.InvariantCulture);

    private static GitException Failure(string error, string output, string fallback, int code) =>
        new((error.Trim().Length > 0 ? error : output).Trim() is { Length: > 0 } message ? message : fallback, code);
}
