using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Git;

/// <summary>What Solution Explorer's source control glyph says about a file (#357), as the classic IDE's does.</summary>
public enum GitFileState
{
    /// <summary>Not in an open repository, or ignored there: no glyph.</summary>
    None,

    /// <summary>Committed and unchanged (a lock).</summary>
    Unchanged,

    /// <summary>Changed in the index or the working tree: modified, renamed, deleted or its type changed (a check mark).</summary>
    Modified,

    /// <summary>Untracked, or added to the index and not yet committed (a plus).</summary>
    Added,

    /// <summary>Unmerged: a merge, rebase, cherry-pick or revert stopped at a conflict in it (an exclamation mark).</summary>
    Conflict,
}

/// <summary>
/// Each file's source control state for Solution Explorer's glyphs (#357), in the open repository and its siblings
/// (All Repositories, ADR 0085). The status says what changed; what is committed and unchanged is what the index lists
/// that the status doesn't (<c>git ls-files</c>), read again only when a repository's index file was written. Reads run
/// off the caller's thread, one at a time, after each status the repositories read; <see cref="Changed"/> is raised (on
/// any thread) when a state may differ. Nothing is read while <see cref="Enabled"/> is off.
/// </summary>
public sealed class GitFileStates : IDisposable
{
    private readonly GitService _git;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private volatile IReadOnlyList<Repository> _repositories = [];
    private bool _dirty;
    private bool _running;
    private bool _enabled;

    // A path's folder as git sees it, with its links resolved (macOS's /var is /private/var), for a path under no root.
    private readonly Dictionary<string, string> _realFolders = new(SolutionService.PathComparer);

    public GitFileStates(GitService git)
    {
        _git = git;
        _git.StatusChanged += OnStatusChanged;
        _git.SiblingStatusChanged += OnStatusChanged;
        _git.IndexChanged += OnStatusChanged;
    }

    /// <summary>A file's state may have changed: read <see cref="StateOf"/> again.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether the states are read (the glyphs' setting). Off, every path is <see cref="GitFileState.None"/>.</summary>
    public bool Enabled
    {
        get
        {
            lock (_gate)
            {
                return _enabled;
            }
        }

        set
        {
            lock (_gate)
            {
                if (_enabled == value)
                {
                    return;
                }

                _enabled = value;
            }

            if (value)
            {
                Schedule();
            }
            else
            {
                _repositories = [];
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Whether a repository's states are read: the glyphs' setting is on and the workspace is in a repository (or has
    /// one beside it). Solution Explorer keeps its glyph column while it is (#357). Changes raise <see cref="Changed"/>.
    /// </summary>
    public bool HasRepositories => _repositories is { Count: > 0 };

    /// <summary>The state of the file at <paramref name="path"/> (a full path).</summary>
    public GitFileState StateOf(string path)
    {
        var repositories = _repositories;
        if (repositories.Count == 0)
        {
            return GitFileState.None;
        }

        if (Find(repositories, path) is { } found)
        {
            return found.StateOf(path);
        }

        // Under no root as written: the solution may sit behind a link git resolved. Its folder is resolved once.
        var folder = Path.GetDirectoryName(path);
        if (folder is null)
        {
            return GitFileState.None;
        }

        string real;
        lock (_realFolders)
        {
            if (!_realFolders.TryGetValue(folder, out real!))
            {
                if (_realFolders.Count > 4096)
                {
                    _realFolders.Clear();
                }

                _realFolders[folder] = real = Codalus.Platform.RealPath.Resolve(folder);
            }
        }

        var resolved = Path.Combine(real, Path.GetFileName(path));
        return !SolutionService.PathComparer.Equals(real, folder) && Find(repositories, resolved) is { } repository
            ? repository.StateOf(resolved)
            : GitFileState.None;
    }

    /// <summary>The innermost repository that contains <paramref name="path"/>, or null.</summary>
    private static Repository? Find(IReadOnlyList<Repository> repositories, string path)
    {
        Repository? best = null;
        foreach (var repository in repositories)
        {
            if (repository.Contains(path) && (best is null || repository.Root.Length > best.Root.Length))
            {
                best = repository;
            }
        }

        return best;
    }

    /// <summary>Reads the states now (tests; the repositories' events read them by themselves).</summary>
    public async Task UpdateAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _readGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (!Enabled)
            {
                return;
            }

            var before = _repositories;
            var after = new List<Repository>();
            foreach (var service in _git.AllServices)
            {
                if (service.Root is not { } root)
                {
                    continue;
                }

                var previous = before.FirstOrDefault(r => SolutionService.PathComparer.Equals(r.Root, root));
                after.Add(await ReadAsync(service, root, previous, linked.Token).ConfigureAwait(false));
            }

            if (!Enabled)
            {
                return;
            }

            _repositories = after;
            if (before.Count != after.Count || before.Zip(after).Any(p => !p.First.SameAs(p.Second)))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _readGate.Release();
        }
    }

    private async Task<Repository> ReadAsync(GitService service, string root, Repository? previous, CancellationToken cancellationToken)
    {
        var status = service.Status;
        var stamp = IndexStamp(service.GitDirectory);
        var tracked = previous is not null && stamp is not null && previous.IndexStamp == stamp
            ? previous.Tracked
            : await TrackedAsync(service, root, cancellationToken).ConfigureAwait(false);
        var changed = new Dictionary<string, GitFileStatus>(SolutionService.PathComparer);
        foreach (var file in status?.Files ?? [])
        {
            changed[file.Path] = file;
        }

        return new Repository(root, stamp, tracked, changed, status?.Files ?? []);
    }

    /// <summary>The files the index lists: committed (or staged), whatever the working tree has.</summary>
    private static async Task<IReadOnlySet<string>> TrackedAsync(GitService service, string root, CancellationToken cancellationToken)
    {
        var (code, output, _) = await service.RunAsync(root, ["ls-files", "-z", "--cached"], cancellationToken, check: false, readOnly: true).ConfigureAwait(false);
        var tracked = new HashSet<string>(SolutionService.PathComparer);
        if (code == 0)
        {
            foreach (var relative in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                tracked.Add(GitParsers.Full(root, relative));
            }
        }

        return tracked;
    }

    /// <summary>When the index file was last written, and its size: what <c>git ls-files</c> lists changes only with it.</summary>
    private static string? IndexStamp(string? gitDirectory)
    {
        if (gitDirectory is null)
        {
            return null;
        }

        try
        {
            var index = new FileInfo(Path.Combine(gitDirectory, "index"));
            return index.Exists ? $"{index.LastWriteTimeUtc.Ticks}:{index.Length}" : "none";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void OnStatusChanged(object? sender, EventArgs e) => Schedule();

    /// <summary>One read at a time: a status read while one runs reads again after it.</summary>
    private void Schedule()
    {
        lock (_gate)
        {
            if (!_enabled || _lifetime.IsCancellationRequested)
            {
                return;
            }

            _dirty = true;
            if (_running)
            {
                return;
            }

            _running = true;
        }

        _ = Task.Run(ReadLoopAsync);
    }

    private async Task ReadLoopAsync()
    {
        while (true)
        {
            lock (_gate)
            {
                if (!_dirty || _lifetime.IsCancellationRequested)
                {
                    _running = false;
                    return;
                }

                _dirty = false;
            }

            try
            {
                await UpdateAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is GitException or IOException or InvalidOperationException)
            {
                // The repository closed or git failed: the next status read tries again.
            }
        }
    }

    public void Dispose()
    {
        _git.StatusChanged -= OnStatusChanged;
        _git.SiblingStatusChanged -= OnStatusChanged;
        _git.IndexChanged -= OnStatusChanged;
        _lifetime.Cancel();
    }

    private sealed record Repository(
        string Root, string? IndexStamp, IReadOnlySet<string> Tracked, IReadOnlyDictionary<string, GitFileStatus> Changed, IReadOnlyList<GitFileStatus> Files)
    {
        private readonly string _prefix = Path.TrimEndingDirectorySeparator(Root) + Path.DirectorySeparatorChar;

        public bool Contains(string path) => path.StartsWith(_prefix, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

        public GitFileState StateOf(string path) =>
            Changed.TryGetValue(path, out var file) ? StateOf(file) : Tracked.Contains(path) ? GitFileState.Unchanged : GitFileState.None;

        private static GitFileState StateOf(GitFileStatus file) =>
            file.IsConflict ? GitFileState.Conflict
            : file.IsUntracked || file.Index is 'A' or 'C' ? GitFileState.Added
            : GitFileState.Modified;

        public bool SameAs(Repository other) =>
            SolutionService.PathComparer.Equals(Root, other.Root) && Files.SequenceEqual(other.Files)
            && (ReferenceEquals(Tracked, other.Tracked) || Tracked.SetEquals(other.Tracked));
    }
}
