namespace Codalus.Plugins;

/// <summary>A file a source control provider lists: its path and status (<c>M</c>odified, <c>A</c>dded, <c>D</c>eleted, <c>?</c> untracked…).</summary>
/// <param name="Path">The file's full path.</param>
/// <param name="Status">One or two letters, as the provider's tool shows them.</param>
/// <param name="Tooltip">More about the change, shown on hover.</param>
public sealed record SourceControlResource(string Path, string Status, string? Tooltip = null);

/// <summary>A group of a provider's files: "Changes", "Staged", "Conflicts"…</summary>
/// <param name="Id">The group's id.</param>
/// <param name="Label">What the user sees.</param>
/// <param name="Resources">Its files.</param>
public sealed record SourceControlGroup(string Id, string Label, IReadOnlyList<SourceControlResource> Resources);

/// <summary>What a provider shows: its groups, and its commit box's hint and button.</summary>
/// <param name="Groups">The groups, in order.</param>
public sealed record SourceControlState(IReadOnlyList<SourceControlGroup> Groups)
{
    /// <summary>The commit box's hint ("Message (Ctrl+Enter to commit)").</summary>
    public string? InputPlaceholder { get; init; }

    /// <summary>The commit button's label ("Commit", "Submit", "Check In").</summary>
    public string? AcceptLabel { get; init; }

    /// <summary>A short status (the branch, a changelist).</summary>
    public string? Status { get; init; }
}

/// <summary>
/// A source control system a plugin provides (API 1.0, M22a): Mercurial, Perforce, Subversion. The Source Control
/// Providers window shows its state (<see cref="ISourceControl.Update"/>); its commit box calls
/// <see cref="AcceptAsync"/>, and opening a file compares it with <see cref="GetOriginalAsync"/>. Git is built in.
/// </summary>
public interface ISourceControlProvider
{
    /// <summary>Commits (submits, checks in) with the commit box's message.</summary>
    Task AcceptAsync(string message, CancellationToken cancellationToken);

    /// <summary>The file as the repository has it (to compare with); null when it's new.</summary>
    Task<string?> GetOriginalAsync(string path, CancellationToken cancellationToken);
}

/// <summary>Source control providers (API 1.0, M22a).</summary>
public interface ISourceControl
{
    /// <summary>Provides source control as <paramref name="id"/> for the folder <paramref name="root"/> until disposed.</summary>
    IDisposable RegisterProvider(string id, string label, string root, ISourceControlProvider provider);

    /// <summary>What the provider shows now.</summary>
    void Update(string id, SourceControlState state);
}
