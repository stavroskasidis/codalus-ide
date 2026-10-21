namespace Codalus.Workbench.Git;

public sealed partial class GitService
{
    // ------------------------------------------------------------------ updating a branch

    /// <summary>
    /// Brings a local branch up to date with its upstream without checking it out, as a pull would if it were checked
    /// out, but only by a fast-forward: fetches the upstream branch alone (<c>fetch &lt;remote&gt; &lt;ref&gt;</c>, which
    /// also moves its remote-tracking branch), then moves the branch to what was fetched when that only adds commits.
    /// The current branch moves with <c>merge --ff-only</c>, so its files follow; any other branch with
    /// <c>fetch . &lt;commit&gt;:refs/heads/&lt;branch&gt;</c>, where git itself refuses a non-fast-forward and a branch
    /// checked out in another worktree. A branch with no upstream, one checked out in another worktree, and one that has
    /// diverged from its upstream are left as they are, and the result says why. git's failures (no network, the
    /// upstream branch deleted on the remote, local changes a fast-forward would overwrite) throw
    /// <see cref="GitException"/>.
    /// </summary>
    /// <param name="branch">A local branch: its name (<c>main</c>) or full ref (<c>refs/heads/main</c>).</param>
    public async Task<GitBranchUpdate> UpdateBranchAsync(string branch, CancellationToken cancellationToken = default)
    {
        var root = Root ?? throw new InvalidOperationException("No git repository is open.");
        var name = branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch["refs/heads/".Length..] : branch;
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
            return new GitBranchUpdate(name, GitBranchUpdateOutcome.NoUpstream) { Before = before, After = before };
        }

        var upstream = fields[3].Length > 0 ? fields[3]
            : remote + "/" + (remoteRef.StartsWith("refs/heads/", StringComparison.Ordinal) ? remoteRef["refs/heads/".Length..] : remoteRef);
        GitBranchUpdate Result(GitBranchUpdateOutcome outcome, string after) => new(name, outcome) { Upstream = upstream, Before = before, After = after };
        if (!isCurrent && worktree.Length > 0)
        {
            return Result(GitBranchUpdateOutcome.CheckedOutElsewhere, before) with { WorktreePath = worktree };
        }

        try
        {
            // The upstream branch alone, as git pull fetches it; FETCH_HEAD is what it fetched.
            await RunAsync(root, ["fetch", remote, remoteRef], cancellationToken).ConfigureAwait(false);
            var fetched = (await RunAsync(root, ["rev-parse", "--verify", "FETCH_HEAD^{commit}"], cancellationToken, readOnly: true).ConfigureAwait(false)).Output.Trim();
            if (fetched == before || await IsAncestorAsync(root, fetched, before, cancellationToken).ConfigureAwait(false))
            {
                return Result(GitBranchUpdateOutcome.UpToDate, before);
            }

            if (!await IsAncestorAsync(root, before, fetched, cancellationToken).ConfigureAwait(false))
            {
                return Result(GitBranchUpdateOutcome.Diverged, before);
            }

            await RunAsync(root, isCurrent ? ["merge", "--ff-only", fetched] : ["fetch", ".", fetched + ":refs/heads/" + name], cancellationToken).ConfigureAwait(false);
            return Result(GitBranchUpdateOutcome.Updated, fetched);
        }
        finally
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary><c>merge-base --is-ancestor</c>: exit code 0 is true, 1 false, and anything else git's failure.</summary>
    private async Task<bool> IsAncestorAsync(string root, string ancestor, string descendant, CancellationToken cancellationToken)
    {
        var (code, output, error) = await RunAsync(root, ["merge-base", "--is-ancestor", ancestor, descendant], cancellationToken, check: false, readOnly: true).ConfigureAwait(false);
        return code switch
        {
            0 => true,
            1 => false,
            _ => throw new GitException((error.Trim().Length > 0 ? error : output).Trim() is { Length: > 0 } message ? message : "git merge-base failed.", code),
        };
    }
}
