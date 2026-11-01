namespace Codalus.Workbench.Git;

/// <summary>
/// A worktree of the repository (M13b), from <c>git worktree list --porcelain</c>. <see cref="Branch"/> is the short
/// name, null when detached (or bare).
/// </summary>
public sealed record GitWorktree(string Path, string? Head, string? Branch)
{
    public bool IsMain { get; init; }

    public bool IsBare { get; init; }

    public bool IsDetached => Branch is null && !IsBare;

    public bool IsLocked { get; init; }

    public string? LockReason { get; init; }

    /// <summary>Its folder is gone: <c>git worktree prune</c> removes it.</summary>
    public bool IsPrunable { get; init; }

    public string? PrunableReason { get; init; }
}

/// <summary>
/// A file that Git LFS tracks (M13b): a <c>filter=lfs</c> path. <see cref="IsPointer"/>: the working copy is still the
/// pointer file (its content wasn't downloaded: git-lfs is missing, or the file was skipped).
/// </summary>
public sealed record GitLfsFile(string Path, string RelativePath, bool IsPointer, long? Size);

/// <summary>A Git LFS lock (<c>git lfs locks</c>).</summary>
public sealed record GitLfsLock(string Id, string Path, string RelativePath, string Owner, DateTimeOffset? LockedAt);

/// <summary>
/// The repository's Git LFS state (M13b). <see cref="IsUsed"/>: some path is tracked by LFS. <see cref="IsInstalled"/>:
/// git-lfs is installed, so contents download and locks can be listed. <see cref="LocksError"/>: why the locks couldn't
/// be read (no LFS server that supports locking, not installed…).
/// </summary>
public sealed record GitLfsStatus(bool IsUsed, bool IsInstalled, IReadOnlyList<GitLfsFile> Files, IReadOnlyList<GitLfsLock> Locks, string? LocksError)
{
    public static readonly GitLfsStatus None = new(false, false, [], [], null);

    // Each list's index by path, built on its first lookup: Git Changes looks up every changed file, and a scan of the LFS
    // files for each was 200 million comparisons for 10,000 changes in a repository of 20,000 assets. Kept by the list,
    // not in the record, which `with` copies (a status with the last locks keeps its own files' index).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<GitLfsFile>, Dictionary<string, GitLfsFile>> s_files = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<GitLfsLock>, Dictionary<string, GitLfsLock>> s_locks = new();

    public GitLfsLock? LockOf(string path) => Locks.Count == 0 ? null : s_locks.GetValue(Locks, locks => Index(locks, l => l.Path)).GetValueOrDefault(path);

    public GitLfsFile? FileOf(string path) => Files.Count == 0 ? null : s_files.GetValue(Files, files => Index(files, f => f.Path)).GetValueOrDefault(path);

    /// <summary>The same Git LFS state: the same files and locks, each the same (the lists compare by reference in a record).</summary>
    public bool IsSameAs(GitLfsStatus other) =>
        this with { Files = other.Files, Locks = other.Locks } == other && Files.SequenceEqual(other.Files) && Locks.SequenceEqual(other.Locks);

    private static Dictionary<string, T> Index<T>(IEnumerable<T> items, Func<T, string> path)
    {
        var index = new Dictionary<string, T>(Workbench.Projects.SolutionService.PathComparer);
        foreach (var item in items)
        {
            index.TryAdd(path(item), item); // the first, as a scan finds
        }

        return index;
    }
}
