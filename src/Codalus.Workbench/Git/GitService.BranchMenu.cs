using System.Globalization;
using Codalus.Workbench.Localization;

namespace Codalus.Workbench.Git;

/// <summary>
/// What the Git Changes window's branch drop-down and its menus ask of git (#23, D38): the branches with their tracking
/// and worktrees, rename, delete by git's own merged rule, pushing a branch that isn't checked out, and the files two
/// branches' tips differ in. One git process per operation, machine-readable output (ADR 0008).
/// </summary>
public sealed partial class GitService
{
    /// <summary>
    /// The local and remote branches (<c>for-each-ref</c>), with each local branch's upstream, its outgoing and incoming
    /// counts (<c>%(upstream:track)</c>, as of the last fetch) and the other worktree it's checked out in
    /// (<c>%(worktreepath)</c>, or where a rebase or bisect works on it). A remote's HEAD is left out; a remote branch's
    /// <see cref="GitBranchDetails.Remote"/> is the longest remote name its ref starts with.
    /// </summary>
    public async Task<IReadOnlyList<GitBranchDetails>> BranchDetailsAsync(CancellationToken cancellationToken = default)
    {
        var root = Root ?? throw new InvalidOperationException("No git repository is open.");
        var (_, output, _) = await RunAsync(
            root,
            ["for-each-ref", "--format=%(refname)%00%(HEAD)%00%(upstream:short)%00%(upstream)%00%(upstream:track,nobracket)%00%(worktreepath)%00%(objectname)%00%(symref)", "refs/heads", "refs/remotes"],
            cancellationToken, readOnly: true).ConfigureAwait(false);
        var (_, remoteNames, _) = await RunAsync(root, ["remote"], cancellationToken, check: false, readOnly: true).ConfigureAwait(false);
        var remotes = remoteNames.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).OrderByDescending(r => r.Length).ToList();
        var held = await BranchOperationsAsync(root, cancellationToken).ConfigureAwait(false);
        var here = Path.GetFullPath(root);

        var branches = new List<GitBranchDetails>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\0');
            if (parts.Length < 8 || parts[7].Length > 0)
            {
                continue; // a symbolic ref: origin/HEAD
            }

            var fullRef = parts[0];
            var isRemote = fullRef.StartsWith("refs/remotes/", StringComparison.Ordinal);
            var name = fullRef[(isRemote ? "refs/remotes/".Length : "refs/heads/".Length)..];
            if (isRemote)
            {
                branches.Add(new GitBranchDetails(fullRef, name, IsRemote: true, IsCurrent: false)
                {
                    Remote = remotes.FirstOrDefault(r => name.StartsWith(r + "/", StringComparison.Ordinal)) ?? name[..Math.Max(0, name.IndexOf('/', StringComparison.Ordinal))],
                    Commit = parts[6],
                });
                continue;
            }

            var (ahead, behind, gone) = ParseTrack(parts[4]);
            var isCurrent = parts[1] == "*";
            var worktree = parts[5].Length > 0 && !isCurrent && !Projects.SolutionService.PathComparer.Equals(Path.GetFullPath(parts[5]), here) ? Path.GetFullPath(parts[5]) : null;
            var operation = held.FirstOrDefault(o => o.Branch == name);
            branches.Add(new GitBranchDetails(fullRef, name, IsRemote: false, isCurrent)
            {
                Upstream = parts[2].Length > 0 ? parts[2] : null,
                UpstreamRef = parts[3].Length > 0 ? parts[3] : null,
                UpstreamGone = gone,
                Ahead = ahead,
                Behind = behind,
                WorktreePath = worktree ?? (operation is { IsThisWorktree: false } ? operation.WorktreePath : null),
                IsRebasingHere = operation is { IsThisWorktree: true, Operation: "rebase" },
                Commit = parts[6],
            });
        }

        return branches;
    }

    /// <summary><c>%(upstream:track,nobracket)</c>: "ahead 2, behind 1", "ahead 2", "behind 1", "gone" or nothing.</summary>
    private static (int Ahead, int Behind, bool Gone) ParseTrack(string track)
    {
        if (track.Trim() == "gone")
        {
            return (0, 0, true);
        }

        var (ahead, behind) = (0, 0);
        foreach (var part in track.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var words = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 2 && int.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
            {
                if (words[0] == "ahead")
                {
                    ahead = count;
                }
                else if (words[0] == "behind")
                {
                    behind = count;
                }
            }
        }

        return (ahead, behind, false);
    }

    /// <summary>Renames a local branch (<c>git branch -m</c>); its upstream setting goes with it.</summary>
    public Task RenameBranchAsync(string name, string newName, CancellationToken cancellationToken = default) =>
        MutateAsync(["branch", "-m", LocalName(name), newName], cancellationToken);

    /// <summary>Deletes a local branch: <c>git branch -d</c>, which refuses an unmerged branch, or with <paramref name="force"/> <c>-D</c>.</summary>
    public Task DeleteBranchAsync(string name, bool force, CancellationToken cancellationToken = default) =>
        MutateAsync(["branch", force ? "-D" : "-d", LocalName(name)], cancellationToken);

    /// <summary>Deletes a branch on a remote (<c>git push &lt;remote&gt; --delete</c>), and so its remote-tracking branch here.</summary>
    public Task DeleteRemoteBranchAsync(string remote, string branch, CancellationToken cancellationToken = default) =>
        MutateAsync(["push", remote, "--delete", "refs/heads/" + branch], cancellationToken);

    /// <summary>
    /// Whether <paramref name="branch"/> is merged by git's rule for <c>git branch -d</c>: into its upstream when it has
    /// one (and it isn't gone), otherwise into HEAD; into HEAD when <paramref name="deletingUpstream"/>, since an upstream
    /// that goes too is no reference (D38, question 8). The commits that would be lost are listed, newest first, and with
    /// <paramref name="deletingUpstream"/> also the upstream's that neither the branch nor HEAD has.
    /// </summary>
    public async Task<GitMergeCheck> IsFullyMergedAsync(string branch, bool deletingUpstream, CancellationToken cancellationToken = default)
    {
        var root = Root ?? throw new InvalidOperationException("No git repository is open.");
        var name = LocalName(branch);
        var details = (await BranchDetailsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(b => b.FullRef == "refs/heads/" + name)
            ?? throw new GitException($"There is no local branch named '{name}'.", -1);
        var upstream = details is { UpstreamRef: { } reference, UpstreamGone: false } ? reference : null;
        var head = Status?.Branch ?? "HEAD";
        if (upstream is not null && !deletingUpstream)
        {
            var commits = await CommitsAsync(root, "refs/heads/" + name, [upstream], cancellationToken).ConfigureAwait(false);
            return new GitMergeCheck(commits.Count == 0, details.Upstream!, ComparedWithUpstream: true, commits);
        }

        var lost = await CommitsAsync(root, "refs/heads/" + name, ["HEAD"], cancellationToken).ConfigureAwait(false);
        var upstreamOnly = deletingUpstream && upstream is not null
            ? await CommitsAsync(root, upstream, ["refs/heads/" + name, "HEAD"], cancellationToken).ConfigureAwait(false)
            : [];
        return new GitMergeCheck(lost.Count == 0 && upstreamOnly.Count == 0, head, ComparedWithUpstream: false, lost) { UpstreamOnly = upstreamOnly };
    }

    /// <summary>The upstream's commits that <paramref name="branch"/> doesn't have, newest first: lost on the server if the upstream is deleted (P7).</summary>
    public async Task<IReadOnlyList<GitCommitSummary>> UpstreamOnlyCommitsAsync(string branch, CancellationToken cancellationToken = default)
    {
        var root = Root ?? throw new InvalidOperationException("No git repository is open.");
        var name = LocalName(branch);
        var details = (await BranchDetailsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(b => b.FullRef == "refs/heads/" + name);
        return details is { UpstreamRef: { } upstream, UpstreamGone: false }
            ? await CommitsAsync(root, upstream, ["refs/heads/" + name], cancellationToken).ConfigureAwait(false)
            : [];
    }

    /// <summary><c>git log include --not exclude…</c>: the short ids and subjects, newest first.</summary>
    private async Task<IReadOnlyList<GitCommitSummary>> CommitsAsync(string root, string include, IReadOnlyList<string> exclude, CancellationToken cancellationToken)
    {
        var (_, output, _) = await RunAsync(root, ["log", "--format=%h%x00%s", "--abbrev=7", include, "--not", .. exclude, "--"], cancellationToken, readOnly: true).ConfigureAwait(false);
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\0')).Where(p => p.Length == 2).Select(p => new GitCommitSummary(p[0], p[1]))];
    }

    /// <summary>
    /// Pushes a local branch, checked out or not (Push on a branch's menu): to its upstream when it has one
    /// (<c>push &lt;remote&gt; refs/heads/b:&lt;its ref there&gt;</c>), otherwise to origin (or the first remote), setting the
    /// upstream, as the current branch's first push does.
    /// </summary>
    public async Task PushBranchAsync(string branch, CancellationToken cancellationToken = default)
    {
        var root = Root ?? throw new InvalidOperationException("No git repository is open.");
        var name = LocalName(branch);
        if (Status?.Branch == name)
        {
            await PushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var (_, output, _) = await RunAsync(root, ["for-each-ref", "--format=%(refname)%00%(upstream:remotename)%00%(upstream:remoteref)", "refs/heads/" + name], cancellationToken, readOnly: true).ConfigureAwait(false);
        var fields = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\0')).FirstOrDefault(p => p.Length >= 3 && p[0] == "refs/heads/" + name)
            ?? throw new GitException($"There is no local branch named '{name}'.", -1);
        if (fields[1].Length > 0 && fields[2].Length > 0 && fields[1] != ".")
        {
            await MutateAsync(["push", fields[1], "refs/heads/" + name + ":" + fields[2]], cancellationToken).ConfigureAwait(false);
            return;
        }

        var (_, remotes, _) = await GitAsync(["remote"], cancellationToken, readOnly: true).ConfigureAwait(false);
        var remote = remotes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } names
            ? names.Contains("origin") ? "origin" : names[0]
            : throw new GitException("This repository has no remote to push to.", -1);
        await MutateAsync(["push", "-u", remote, name], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The files <paramref name="to"/>'s tip differs in from <paramref name="from"/>'s (<c>diff --name-status -M</c>, tip to
    /// tip; D38, question 9): a file new on <paramref name="to"/> is A.
    /// </summary>
    public async Task<IReadOnlyList<GitChangedFile>> CompareBranchesAsync(string from, string to, CancellationToken cancellationToken = default)
    {
        var root = Root ?? throw new InvalidOperationException("No git repository is open.");
        var (_, output, _) = await RunAsync(root, ["diff", "--name-status", "-z", "-M", "--no-ext-diff", from, to, "--"], cancellationToken, readOnly: true).ConfigureAwait(false);
        return [.. ParseNameStatus(root, output).Select(f => new GitChangedFile(f.Status, f.Path, f.OldPath))];
    }

    /// <summary><c>--name-status -z</c> output: each file's letter, path and (renames, copies) old path, with the OS's separators.</summary>
    internal static IReadOnlyList<(char Status, string Path, string? OldPath)> ParseNameStatus(string root, string output)
    {
        string Full(string relative) => Path.Combine(root, relative).Replace('/', Path.DirectorySeparatorChar);
        var files = new List<(char, string, string?)>();
        var parts = output.Split('\0');
        for (var i = 0; i < parts.Length; i++)
        {
            var status = parts[i].Trim('\n');
            if (status.Length == 0 || i + 1 >= parts.Length)
            {
                continue;
            }

            if (status[0] is 'R' or 'C' && i + 2 < parts.Length)
            {
                files.Add((status[0], Full(parts[i + 2]), Full(parts[i + 1])));
                i += 2;
            }
            else
            {
                files.Add((status[0], Full(parts[i + 1]), null));
                i++;
            }
        }

        return files;
    }

    private static string LocalName(string branch) => branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch["refs/heads/".Length..] : branch;
}

/// <summary>git's rules for a branch name (<c>git check-ref-format --branch</c>, and the names <c>git branch</c> refuses itself).</summary>
public static class GitRefNames
{
    /// <summary>What is wrong with <paramref name="name"/> as a branch name, in a sentence for under the box; null when git takes it.</summary>
    public static string? BranchNameError(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return Localizer.Get("msg.git.enterABranchName", "Enter a name for the branch.");
        }

        if (name.StartsWith('-'))
        {
            return Localizer.Get("msg.git.branchNameStartsWithDash", "A branch name can't start with '-'.");
        }

        if (name is "@" or "HEAD")
        {
            return Localizer.Format("msg.git.branchNameReserved", "'{0}' is reserved by git.", name);
        }

        foreach (var c in name)
        {
            if (c < 0x20 || c == 0x7f || c is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\')
            {
                return c == ' '
                    ? Localizer.Get("msg.git.branchNameHasSpace", "A branch name can't contain spaces.")
                    : Localizer.Format("msg.git.branchNameHasCharacter", "A branch name can't contain '{0}'.", c < 0x20 || c == 0x7f ? "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture) : c.ToString());
            }
        }

        if (name.Contains("..", StringComparison.Ordinal))
        {
            return Localizer.Get("msg.git.branchNameHasTwoDots", "A branch name can't contain '..'.");
        }

        if (name.Contains("@{", StringComparison.Ordinal))
        {
            return Localizer.Get("msg.git.branchNameHasAtBrace", "A branch name can't contain '@{'.");
        }

        if (name.StartsWith('/') || name.EndsWith('/') || name.Contains("//", StringComparison.Ordinal))
        {
            return Localizer.Get("msg.git.branchNameHasEmptyPart", "A branch name can't start or end with '/' or have '//' in it.");
        }

        if (name.EndsWith('.'))
        {
            return Localizer.Get("msg.git.branchNameEndsWithDot", "A branch name can't end with '.'.");
        }

        foreach (var part in name.Split('/'))
        {
            if (part.StartsWith('.'))
            {
                return Localizer.Get("msg.git.branchNamePartStartsWithDot", "No part of a branch name between slashes can start with '.'.");
            }

            if (part.EndsWith(".lock", StringComparison.Ordinal))
            {
                return Localizer.Get("msg.git.branchNamePartEndsWithLock", "No part of a branch name between slashes can end with '.lock'.");
            }
        }

        return null;
    }
}
