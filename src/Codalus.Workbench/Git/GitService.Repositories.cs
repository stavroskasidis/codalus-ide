using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Codalus.Workbench.Git;

/// <summary>
/// M13b (ADR 0031): worktrees, Git LFS, solutions that span several repositories, the askpass environment, checking out
/// a fetched branch (pull requests) and publishing.
/// </summary>
public sealed partial class GitService
{
    private static readonly char[] s_newLines = ['\r', '\n'];
    private IReadOnlyList<string> _repositories = [];

    /// <summary>
    /// Variables every git process gets (the shell's askpass, ADR 0031). The caller's own variables win over them.
    /// </summary>
    public IReadOnlyDictionary<string, string>? AskpassEnvironment { get; set; }

    // ------------------------------------------------------------------ several repositories

    /// <summary>The repositories of the open solution (its own first), as set by <see cref="FindRepositoriesAsync"/>.</summary>
    public IReadOnlyList<string> Repositories
    {
        get => _repositories;
        set => _repositories = value;
    }

    /// <summary>
    /// The repositories that contain <paramref name="folders"/> (a solution's folder and its projects' folders), in the
    /// order first seen, without duplicates. Folders outside any repository are left out.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindRepositoriesAsync(IEnumerable<string> folders, CancellationToken cancellationToken = default)
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(Workbench.Projects.SolutionService.PathComparer);
        foreach (var folder in folders.Where(Directory.Exists).Select(Real).Distinct(Workbench.Projects.SolutionService.PathComparer))
        {
            // Inside a repository already found, and not in a nested one: no need to ask git (one rev-parse per repository).
            var container = roots.Where(root => IsUnder(folder, root)).OrderByDescending(root => root.Length).FirstOrDefault();
            if (container is not null && !HasNestedRepository(folder, container))
            {
                continue;
            }

            var (code, output, _) = await RunAsync(folder, ["rev-parse", "--show-toplevel"], cancellationToken, check: false, readOnly: true).ConfigureAwait(false);
            if (code == 0 && output.Trim() is { Length: > 0 } top && seen.Add(Path.GetFullPath(top)))
            {
                roots.Add(Path.GetFullPath(top));
            }
        }

        return roots;
    }

    /// <summary>A repository nested in <paramref name="root"/> (a submodule, or a clone inside its folder) has its own <c>.git</c> below it.</summary>
    private static bool HasNestedRepository(string folder, string root)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (var directory = new DirectoryInfo(folder); directory is not null && IsUnder(directory.FullName, root) && !directory.FullName.TrimEnd(Path.DirectorySeparatorChar).Equals(root.TrimEnd(Path.DirectorySeparatorChar), comparison); directory = directory.Parent)
        {
            var dotGit = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(dotGit) || File.Exists(dotGit))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The repository of <see cref="Repositories"/> (or the open one) that contains <paramref name="path"/>, the innermost first.</summary>
    public string? RepositoryFor(string path)
    {
        var full = Real(path);
        return _repositories.Append(Root).OfType<string>()
            .Where(root => IsUnder(full, root))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();
    }

    private static bool IsUnder(string path, string root)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(trimmed, comparison) || path.StartsWith(trimmed + Path.DirectorySeparatorChar, comparison);
    }

    private static string RelativeTo(string root, string path) => Path.GetRelativePath(root, Real(path)).Replace('\\', '/');

    /// <summary>A file's content at a revision in the repository that contains it (any of <see cref="Repositories"/>).</summary>
    public async Task<string?> ShowInRepositoryAsync(string path, string revision, CancellationToken cancellationToken = default)
    {
        if (RepositoryFor(path) is not { } root)
        {
            return null;
        }

        var relative = RelativeTo(root, path);
        var spec = revision == ":" ? ":" + relative : $"{revision}:{relative}";
        var (code, output, _) = await RunAsync(root, ["show", spec], cancellationToken, check: false, readOnly: true).ConfigureAwait(false);
        return code == 0 ? output : null;
    }

    /// <summary>Blame in the repository that contains the file.</summary>
    public async Task<IReadOnlyList<BlameRange>> BlameInRepositoryAsync(string path, string? contents, CancellationToken cancellationToken = default)
    {
        var root = RepositoryFor(path) ?? throw new GitException("This file is not in a git repository.", -1);
        var relative = RelativeTo(root, path);
        var arguments = contents is null
            ? new List<string> { "blame", "--porcelain", "--", relative }
            : ["blame", "--porcelain", "--contents", "-", "--", relative];
        var (_, output, _) = await RunAsync(root, arguments, cancellationToken, contents, readOnly: true).ConfigureAwait(false);
        return GitParsers.ParseBlame(output);
    }

    /// <summary>The branch, ahead and behind of another repository (for the repository picker), without opening it.</summary>
    public async Task<GitStatus?> PeekStatusAsync(string root, CancellationToken cancellationToken = default)
    {
        var (code, output, _) = await RunAsync(root, ["status", "--porcelain=v2", "-z", "--branch", "--untracked-files=no"], cancellationToken, check: false, readOnly: true).ConfigureAwait(false);
        return code == 0 ? GitParsers.ParseStatus(root, output, isMerging: false) : null;
    }

    // ------------------------------------------------------------------ worktrees

    public async Task<IReadOnlyList<GitWorktree>> WorktreesAsync(CancellationToken cancellationToken = default)
    {
        var (_, output, _) = await GitAsync(["worktree", "list", "--porcelain"], cancellationToken, readOnly: true).ConfigureAwait(false);
        return GitParsers.ParseWorktrees(output);
    }

    /// <summary>
    /// Adds a worktree at <paramref name="path"/>: on a new branch <paramref name="newBranch"/> (from
    /// <paramref name="commitish"/>, HEAD by default), or on an existing branch or commit.
    /// </summary>
    public Task AddWorktreeAsync(string path, string? newBranch, string? commitish, CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "worktree", "add" };
        if (newBranch is { Length: > 0 })
        {
            arguments.Add("-b");
            arguments.Add(newBranch);
        }

        arguments.Add(Path.GetFullPath(path));
        if (commitish is { Length: > 0 })
        {
            arguments.Add(commitish);
        }

        return MutateAsync(arguments, cancellationToken);
    }

    /// <summary>Removes a worktree; <paramref name="force"/> also when it has changes (they're lost).</summary>
    public Task RemoveWorktreeAsync(string path, bool force, CancellationToken cancellationToken = default) =>
        MutateAsync(force ? ["worktree", "remove", "--force", path] : ["worktree", "remove", path], cancellationToken);

    /// <summary>Forgets worktrees whose folders are gone.</summary>
    public Task PruneWorktreesAsync(CancellationToken cancellationToken = default) => MutateAsync(["worktree", "prune"], cancellationToken);

    public Task LockWorktreeAsync(string path, string? reason, CancellationToken cancellationToken = default) =>
        MutateAsync(reason is { Length: > 0 } ? ["worktree", "lock", "--reason", reason, path] : ["worktree", "lock", path], cancellationToken);

    public Task UnlockWorktreeAsync(string path, CancellationToken cancellationToken = default) => MutateAsync(["worktree", "unlock", path], cancellationToken);

    // ------------------------------------------------------------------ Git LFS

    /// <summary>
    /// Which files Git LFS tracks (from <c>.gitattributes</c> through <c>git check-attr</c>, so it works without
    /// git-lfs), which of them are still pointer files, and the locks when git-lfs is installed.
    /// </summary>
    public async Task<GitLfsStatus> LfsStatusAsync(bool includeLocks = true, CancellationToken cancellationToken = default)
    {
        var root = Root ?? throw new InvalidOperationException("No git repository is open.");
        if (!await UsesLfsAsync(root, cancellationToken).ConfigureAwait(false))
        {
            return GitLfsStatus.None;
        }

        var (_, listed, _) = await RunAsync(root, ["ls-files", "-z", "--cached", "--others", "--exclude-standard"], cancellationToken, readOnly: true).ConfigureAwait(false);
        var paths = listed.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var files = new List<GitLfsFile>();
        if (paths.Length > 0)
        {
            var (_, attributes, _) = await RunAsync(root, ["check-attr", "-z", "--stdin", "filter"], cancellationToken, string.Join('\0', paths) + "\0", readOnly: true).ConfigureAwait(false);
            var fields = attributes.Split('\0');
            for (var i = 0; i + 2 < fields.Length; i += 3)
            {
                if (fields[i + 2] == "lfs")
                {
                    var full = GitParsers.Full(root, fields[i]);
                    var (isPointer, size) = ReadPointer(full);
                    files.Add(new GitLfsFile(full, fields[i], isPointer, size ?? (File.Exists(full) ? new FileInfo(full).Length : null)));
                }
            }
        }

        var (versionCode, _, _) = await RunAsync(root, ["lfs", "version"], cancellationToken, check: false, readOnly: true).ConfigureAwait(false);
        var installed = versionCode == 0;
        IReadOnlyList<GitLfsLock> locks = [];
        string? locksError = installed ? null : "git-lfs is not installed.";
        if (installed && includeLocks)
        {
            (locks, locksError) = await LfsLocksAsync(root, cancellationToken).ConfigureAwait(false);
        }

        return new GitLfsStatus(true, installed, files, locks, locksError);
    }

    /// <summary>Whether any <c>.gitattributes</c> of the repository mentions the LFS filter (a cheap test before listing files).</summary>
    private static Task<bool> UsesLfsAsync(string root, CancellationToken cancellationToken)
    {
        return Task.Run(
            () =>
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var file in Directory.EnumerateFiles(root, ".gitattributes", options))
                {
                    if (file.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        || file.Contains(Path.DirectorySeparatorChar + "node_modules" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    try
                    {
                        if (File.ReadAllText(file).Contains("filter=lfs", StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }

                return false;
            },
            cancellationToken);
    }

    /// <summary>A working-tree file that is an LFS pointer (<c>version https://git-lfs.github.com/spec/v1</c>), and the size it stands for.</summary>
    public static (bool IsPointer, long? Size) ReadPointer(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 1024)
            {
                return (false, null);
            }

            var text = File.ReadAllText(path);
            if (!text.StartsWith("version https://git-lfs.github.com/spec/", StringComparison.Ordinal))
            {
                return (false, null);
            }

            var sizeLine = text.Split(s_newLines, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(l => l.StartsWith("size ", StringComparison.Ordinal));
            return (true, sizeLine is not null && long.TryParse(sizeLine[5..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, null);
        }
    }

    private async Task<(IReadOnlyList<GitLfsLock>, string?)> LfsLocksAsync(string root, CancellationToken cancellationToken)
    {
        var (code, output, error) = await RunAsync(root, ["lfs", "locks", "--json"], cancellationToken, check: false, readOnly: true).ConfigureAwait(false);
        if (code != 0)
        {
            return ([], (error.Trim().Length > 0 ? error : output).Trim());
        }

        try
        {
            return (GitParsers.ParseLfsLocks(root, output), null);
        }
        catch (JsonException ex)
        {
            return ([], "git lfs locks: " + ex.Message);
        }
    }

    /// <summary>Locks a file on the LFS server (git-lfs needed).</summary>
    public Task LockFileAsync(string path, CancellationToken cancellationToken = default) => MutateAsync(["lfs", "lock", Relative(path)], cancellationToken);

    /// <summary>Unlocks a file; <paramref name="force"/> also someone else's lock.</summary>
    public Task UnlockFileAsync(string path, bool force, CancellationToken cancellationToken = default) =>
        MutateAsync(force ? ["lfs", "unlock", "--force", Relative(path)] : ["lfs", "unlock", Relative(path)], cancellationToken);

    /// <summary>Downloads the LFS contents (pointer files become the real files).</summary>
    public Task LfsPullAsync(CancellationToken cancellationToken = default) => MutateAsync(["lfs", "pull"], cancellationToken);

    // ------------------------------------------------------------------ pull requests and publishing

    /// <summary>
    /// Fetches <paramref name="sourceRef"/> from <paramref name="remote"/> (a remote's name or a URL) and switches to
    /// <paramref name="localBranch"/>: created at what was fetched, or fast-forwarded to it when it exists. With
    /// <paramref name="track"/> (a branch of the remote, not a pull request ref) the local branch tracks
    /// <c>&lt;remote&gt;/&lt;sourceRef&gt;</c>.
    /// </summary>
    public Task FetchAndSwitchAsync(string remote, string sourceRef, string localBranch, bool track, CancellationToken cancellationToken = default) =>
        WithFetchGateAsync(() => FetchAndSwitchUnderGateAsync(remote, sourceRef, localBranch, track, cancellationToken), cancellationToken);

    private async Task FetchAndSwitchUnderGateAsync(string remote, string sourceRef, string localBranch, bool track, CancellationToken cancellationToken)
    {
        try
        {
            var exists = (await RunAsync(Root!, ["rev-parse", "--verify", "--quiet", "refs/heads/" + localBranch], cancellationToken, check: false, readOnly: true).ConfigureAwait(false)).ExitCode == 0;
            string target;
            if (track)
            {
                var branch = sourceRef.StartsWith("refs/heads/", StringComparison.Ordinal) ? sourceRef["refs/heads/".Length..] : sourceRef;
                await GitAsync(["fetch", remote, $"+refs/heads/{branch}:refs/remotes/{remote}/{branch}"], cancellationToken).ConfigureAwait(false);
                target = $"{remote}/{branch}";
                if (!exists)
                {
                    await GitAsync(["switch", "-c", localBranch, "--track", target], cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                await GitAsync(["fetch", remote, sourceRef], cancellationToken).ConfigureAwait(false);
                target = (await GitAsync(["rev-parse", "FETCH_HEAD"], cancellationToken, readOnly: true).ConfigureAwait(false)).Output.Trim();
                if (!exists)
                {
                    await GitAsync(["switch", "-c", localBranch, target], cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            if (Status?.Branch != localBranch)
            {
                await GitAsync(["switch", localBranch], cancellationToken).ConfigureAwait(false);
            }

            await GitAsync(["merge", "--ff-only", target], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Pushes <paramref name="branch"/> to <paramref name="remote"/>, setting it as the upstream.</summary>
    public Task PublishBranchAsync(string remote, string branch, CancellationToken cancellationToken = default) =>
        MutateAsync(["push", "-u", remote, branch], cancellationToken);

    /// <summary>A free remote name for a new remote: <paramref name="preferred"/>, else <paramref name="fallback"/>, else with a number.</summary>
    public async Task<string> FreeRemoteNameAsync(string preferred, string fallback, CancellationToken cancellationToken = default)
    {
        var names = (await RemotesAsync(cancellationToken).ConfigureAwait(false)).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        if (!names.Contains(preferred))
        {
            return preferred;
        }

        for (var i = 1; ; i++)
        {
            var name = i == 1 ? fallback : fallback + i.ToString(CultureInfo.InvariantCulture);
            if (!names.Contains(name))
            {
                return name;
            }
        }
    }

    /// <summary>The upstream of the current branch on <paramref name="remote"/> exists (it was pushed).</summary>
    public async Task<bool> IsBranchPublishedAsync(string remote, string branch, CancellationToken cancellationToken = default) =>
        (await RunAsync(Root!, ["rev-parse", "--verify", "--quiet", $"refs/remotes/{remote}/{branch}"], cancellationToken, check: false, readOnly: true).ConfigureAwait(false)).ExitCode == 0;

    /// <summary>The commit at the tip of a ref, or null.</summary>
    public async Task<string?> ResolveRefAsync(string reference, CancellationToken cancellationToken = default)
    {
        var (code, output, _) = await RunAsync(Root!, ["rev-parse", "--verify", "--quiet", reference + "^{commit}"], cancellationToken, check: false, readOnly: true).ConfigureAwait(false);
        return code == 0 ? output.Trim() : null;
    }
}

/// <summary>Parsers for the M13b commands.</summary>
public static partial class GitParsers
{
    /// <summary>Parses <c>git worktree list --porcelain</c>: blank-line separated records; the first is the main worktree.</summary>
    public static IReadOnlyList<GitWorktree> ParseWorktrees(string output)
    {
        var worktrees = new List<GitWorktree>();
        GitWorktree? current = null;
        void Flush()
        {
            if (current is not null)
            {
                worktrees.Add(current with { IsMain = worktrees.Count == 0 });
                current = null;
            }
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            var space = line.IndexOf(' ', StringComparison.Ordinal);
            var key = space < 0 ? line : line[..space];
            var value = space < 0 ? null : line[(space + 1)..];
            switch (key)
            {
                case "worktree":
                    Flush();
                    current = new GitWorktree(System.IO.Path.GetFullPath(value!), null, null);
                    break;
                case "HEAD" when current is not null:
                    current = current with { Head = value };
                    break;
                case "branch" when current is not null:
                    current = current with { Branch = value!.StartsWith("refs/heads/", StringComparison.Ordinal) ? value["refs/heads/".Length..] : value };
                    break;
                case "bare" when current is not null:
                    current = current with { IsBare = true };
                    break;
                case "locked" when current is not null:
                    current = current with { IsLocked = true, LockReason = value };
                    break;
                case "prunable" when current is not null:
                    current = current with { IsPrunable = true, PrunableReason = value };
                    break;
            }
        }

        Flush();
        return worktrees;
    }

    /// <summary>Parses <c>git lfs locks --json</c>: <c>[{ "id", "path", "owner": { "name" }, "locked_at" }]</c>.</summary>
    public static IReadOnlyList<GitLfsLock> ParseLfsLocks(string root, string json)
    {
        using var document = JsonDocument.Parse(json.Trim().Length == 0 ? "[]" : json);
        var locks = new List<GitLfsLock>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var path = item.TryGetProperty("path", out var p) ? p.GetString() ?? string.Empty : string.Empty;
            var owner = item.TryGetProperty("owner", out var o) && o.ValueKind == JsonValueKind.Object && o.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            DateTimeOffset? at = item.TryGetProperty("locked_at", out var l) && l.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(l.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
            locks.Add(new GitLfsLock(item.TryGetProperty("id", out var id) ? id.ToString() : string.Empty, Full(root, path), path, owner, at));
        }

        return locks;
    }
}
