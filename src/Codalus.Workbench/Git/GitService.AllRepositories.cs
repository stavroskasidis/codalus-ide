namespace Codalus.Workbench.Git;

/// <summary>
/// All Repositories (ADR 0085): while Git Changes shows every repository of a solution that spans several, the other
/// repositories are open here as siblings, each a <see cref="GitService"/> of its own with its status and watcher. A
/// path's repository answers for it (<see cref="ServiceFor"/>); an operation on all of them runs in each, and reports the
/// ones that failed without undoing the others (<see cref="ForEachAsync"/>).
/// </summary>
public sealed partial class GitService
{
    private readonly List<GitService> _siblings = [];
    private readonly SemaphoreSlim _siblingsGate = new(1, 1);

    /// <summary>The repository's folder name (<c>WPylon</c>), or empty when none is open.</summary>
    public string Name => Root is { } root ? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) : string.Empty;

    /// <summary>The solution's other repositories, open while every repository is shown (none otherwise).</summary>
    public IReadOnlyList<GitService> Siblings
    {
        get
        {
            lock (_siblings)
            {
                return [.. _siblings];
            }
        }
    }

    /// <summary>This repository and its siblings, in the order of <see cref="Repositories"/> (this one first when it isn't listed).</summary>
    public IReadOnlyList<GitService> AllServices
    {
        get
        {
            var all = Siblings.Prepend(this).Where(s => s.Root is not null).ToList();
            var order = _repositories.ToList();
            return [.. all.OrderBy(s => order.FindIndex(r => Workbench.Projects.SolutionService.PathComparer.Equals(r, s.Root)) is var i and >= 0 ? i : -1)];
        }
    }

    /// <summary>A sibling's status changed (after its refresh). <see cref="StatusChanged"/> is this repository's own.</summary>
    public event EventHandler? SiblingStatusChanged;

    /// <summary>
    /// Opens <paramref name="roots"/> other than this repository as siblings (and closes the siblings not among them), each
    /// watched and with its status read; an empty list closes them all. A root that isn't a repository any more is left out.
    /// </summary>
    public async Task SetSiblingsAsync(IReadOnlyList<string> roots, CancellationToken cancellationToken = default)
    {
        await _siblingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wanted = roots
                .Where(r => Root is null || !Workbench.Projects.SolutionService.PathComparer.Equals(Path.GetFullPath(r), Root))
                .Select(Path.GetFullPath)
                .Distinct(Workbench.Projects.SolutionService.PathComparer)
                .ToList();
            List<GitService> stale;
            lock (_siblings)
            {
                stale = [.. _siblings.Where(s => s.Root is null || !wanted.Contains(s.Root, Workbench.Projects.SolutionService.PathComparer))];
                _siblings.RemoveAll(stale.Contains);
            }

            foreach (var sibling in stale)
            {
                DisposeSibling(sibling);
            }

            var changed = stale.Count > 0;
            foreach (var root in wanted)
            {
                lock (_siblings)
                {
                    if (_siblings.Any(s => Workbench.Projects.SolutionService.PathComparer.Equals(s.Root, root)))
                    {
                        continue;
                    }
                }

                var sibling = new GitService(_git, _watch) { AskpassEnvironment = AskpassEnvironment, Repositories = _repositories };
                var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                sibling.Log += (_, line) => Log?.Invoke(this, $"[{name}] {line}");
                sibling.StatusChanged += OnSiblingStatusChanged;
                sibling.IndexChanged += OnSiblingIndexChanged;
                if (!await sibling.OpenAsync(root, cancellationToken).ConfigureAwait(false))
                {
                    DisposeSibling(sibling);
                    continue;
                }

                lock (_siblings)
                {
                    _siblings.Add(sibling);
                }

                changed = true;
            }

            if (changed)
            {
                SiblingStatusChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _siblingsGate.Release();
        }
    }

    private void OnSiblingStatusChanged(object? sender, EventArgs e) => SiblingStatusChanged?.Invoke(this, EventArgs.Empty);

    private void OnSiblingIndexChanged(object? sender, EventArgs e) => IndexChanged?.Invoke(this, EventArgs.Empty);

    private void DisposeSibling(GitService sibling)
    {
        sibling.StatusChanged -= OnSiblingStatusChanged;
        sibling.IndexChanged -= OnSiblingIndexChanged;
        sibling.Dispose();
    }

    private void CloseSiblings()
    {
        List<GitService> siblings;
        lock (_siblings)
        {
            siblings = [.. _siblings];
            _siblings.Clear();
        }

        foreach (var sibling in siblings)
        {
            DisposeSibling(sibling);
        }
    }

    /// <summary>The open repository (this one or a sibling) that contains <paramref name="path"/>, the innermost; this one when none does.</summary>
    public GitService ServiceFor(string path)
    {
        var full = Real(path);
        return Siblings.Prepend(this)
            .Where(s => s.Root is { } root && IsUnder(full, root))
            .OrderByDescending(s => s.Root!.Length)
            .FirstOrDefault() ?? this;
    }

    /// <summary>The service open for <paramref name="root"/> (this one or a sibling), or null.</summary>
    public GitService? ServiceAt(string root) =>
        Siblings.Prepend(this).FirstOrDefault(s => s.Root is { } r && Workbench.Projects.SolutionService.PathComparer.Equals(
            Path.TrimEndingDirectorySeparator(r), Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))));

    /// <summary>The paths grouped by the open repository that contains each, in the order of <see cref="AllServices"/>.</summary>
    public IReadOnlyList<(GitService Service, IReadOnlyList<string> Paths)> GroupByRepository(IEnumerable<string> paths)
    {
        var groups = paths.GroupBy(ServiceFor).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g]);
        return [.. AllServices.Append(this).Distinct().Where(groups.ContainsKey).Select(s => (s, groups[s]))];
    }

    /// <summary>
    /// Runs <paramref name="action"/> in each repository, one after another (askpass prompts come one at a time), and
    /// carries on past a failure: what failed in which repository, nothing undone elsewhere. Cancellation stops it.
    /// </summary>
    public static async Task<IReadOnlyList<GitRepositoryFailure>> ForEachAsync(IEnumerable<GitService> services, Func<GitService, Task> action, CancellationToken cancellationToken = default)
    {
        var failures = new List<GitRepositoryFailure>();
        foreach (var service in services)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await action(service).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is GitException or InvalidOperationException or IOException)
            {
                failures.Add(new GitRepositoryFailure(service.Root ?? string.Empty, ex.Message));
            }
        }

        return failures;
    }

    /// <summary>
    /// The local branches every one of the repositories has (by name, in the first one's order), from each one's
    /// <see cref="BranchDetailsAsync"/>: what Git Changes lists under Common branches.
    /// </summary>
    public static IReadOnlyList<string> CommonBranches(IReadOnlyList<IReadOnlyList<GitBranchDetails>> branchesOfEach)
    {
        if (branchesOfEach.Count == 0)
        {
            return [];
        }

        var names = branchesOfEach.Select(b => b.Where(d => !d.IsRemote).Select(d => d.Name).ToHashSet(StringComparer.Ordinal)).ToList();
        return [.. branchesOfEach[0].Where(b => !b.IsRemote && names.All(n => n.Contains(b.Name))).Select(b => b.Name).Distinct(StringComparer.Ordinal)];
    }
}

/// <summary>What failed in one repository of an operation on all of them (All Repositories, ADR 0085).</summary>
public sealed record GitRepositoryFailure(string Root, string Message)
{
    public string Name => Path.GetFileName(Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>"WPylon: git's message", one line per failure in a summary.</summary>
    public override string ToString() => $"{Name}: {Message}";
}
