namespace DaedalusStudio.Workbench.Git;

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

    public GitLfsLock? LockOf(string path) => Locks.FirstOrDefault(l => Workbench.Projects.SolutionService.PathComparer.Equals(l.Path, path));

    public GitLfsFile? FileOf(string path) => Files.FirstOrDefault(f => Workbench.Projects.SolutionService.PathComparer.Equals(f.Path, path));
}
