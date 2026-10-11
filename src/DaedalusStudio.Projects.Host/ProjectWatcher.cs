namespace DaedalusStudio.Projects.Host;

/// <summary>
/// Watches the solution and its projects for external changes and reports them in debounced batches:
/// the solution file, MSBuild files (projects and their imports), and folders whose file set changed
/// (globs pick up added, removed and renamed files). Content edits of ordinary files and everything under
/// <c>bin/</c> and <c>obj/</c> are ignored, except NuGet's restore outputs.
/// </summary>
internal sealed class ProjectWatcher(Func<ProjectWatcher.Changes, Task> onChanged) : IDisposable
{
    private static readonly TimeSpan s_debounce = TimeSpan.FromMilliseconds(300);
    private static readonly StringComparer s_pathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(s_pathComparer);
    private readonly Dictionary<string, DateTime> _suppressed = new(s_pathComparer);
    private HashSet<string> _imports = new(s_pathComparer);
    private string _solution = string.Empty;
    private Pending _pending = new();
    private Timer? _timer;

    public sealed record Changes(bool SolutionFileChanged, IReadOnlyList<string> MSBuildFiles, IReadOnlyList<string> Directories);

    private sealed class Pending
    {
        public bool Solution { get; set; }

        public HashSet<string> MSBuildFiles { get; } = new(s_pathComparer);

        public HashSet<string> Directories { get; } = new(s_pathComparer);

        public bool IsEmpty => !Solution && MSBuildFiles.Count == 0 && Directories.Count == 0;
    }

    /// <summary>
    /// Watches the workspace folder (the solution's, or the folder in folder mode) and each project folder outside it.
    /// <paramref name="solution"/> is the solution file, or null without one.
    /// </summary>
    public void Watch(string workspace, string? solution, IEnumerable<string> projects)
    {
        lock (_gate)
        {
            _solution = solution ?? string.Empty;
            var roots = new List<string> { workspace };
            foreach (var directory in projects.Select(p => Path.GetDirectoryName(p)!))
            {
                if (!roots.Any(r => ProjectFileEditor.IsSameOrUnder(directory, r)))
                {
                    roots.Add(directory);
                }
            }

            foreach (var stale in _watchers.Keys.Where(k => !roots.Contains(k, s_pathComparer)).ToList())
            {
                _watchers[stale].Dispose();
                _watchers.Remove(stale);
            }

            foreach (var root in roots.Where(r => !_watchers.ContainsKey(r) && Directory.Exists(r)))
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += (_, e) => OnEvent(e.FullPath, structural: true);
                watcher.Deleted += (_, e) => OnEvent(e.FullPath, structural: true);
                watcher.Changed += (_, e) => OnEvent(e.FullPath, structural: false);
                watcher.Renamed += (_, e) =>
                {
                    OnEvent(e.OldFullPath, structural: true);
                    OnEvent(e.FullPath, structural: true);
                };
                watcher.Error += (_, _) => OnOverflow();
                watcher.EnableRaisingEvents = true;
                _watchers[root] = watcher;
            }
        }
    }

    /// <summary>Imported MSBuild files (Directory.Build.props, …) whose changes re-evaluate the importing projects.</summary>
    public void SetImports(IEnumerable<string> imports)
    {
        lock (_gate)
        {
            _imports = new HashSet<string>(imports, s_pathComparer);
        }
    }

    /// <summary>Ignores changes to <paramref name="path"/> for a moment (our own save).</summary>
    public void Suppress(string path)
    {
        lock (_gate)
        {
            _suppressed[path] = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        }
    }

    private void OnEvent(string path, bool structural)
    {
        lock (_gate)
        {
            if (_suppressed.TryGetValue(path, out var until) && DateTime.UtcNow < until)
            {
                return;
            }

            if (s_pathComparer.Equals(path, _solution))
            {
                _pending.Solution = true;
            }
            else if (IsMSBuildFile(path) || _imports.Contains(path))
            {
                _pending.MSBuildFiles.Add(path);
            }
            else if (IsRestoreOutput(path))
            {
                _pending.Directories.Add(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!);
            }
            else if (structural && !IsBuildOutput(path))
            {
                _pending.Directories.Add(Path.GetDirectoryName(path)!);
            }
            else
            {
                return;
            }

            _timer ??= new Timer(_ => Flush());
            _timer.Change(s_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The OS dropped events: treat every watched folder as changed.</summary>
    private void OnOverflow()
    {
        lock (_gate)
        {
            _pending.Solution = true;
            _pending.Directories.UnionWith(_watchers.Keys);
            _timer ??= new Timer(_ => Flush());
            _timer.Change(s_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        Pending pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = new Pending();
        }

        if (!pending.IsEmpty)
        {
            _ = onChanged(new Changes(pending.Solution, [.. pending.MSBuildFiles], [.. pending.Directories]));
        }
    }

    private static bool IsMSBuildFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj" or ".sqlproj" or ".props" or ".targets"
        || Path.GetFileName(path).Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase);

    private static bool IsRestoreOutput(string path) =>
        Path.GetFileName(path).Equals("project.assets.json", StringComparison.OrdinalIgnoreCase);

    private static bool IsBuildOutput(string path)
    {
        foreach (var segment in path.Split(Path.DirectorySeparatorChar))
        {
            if (segment.Equals("bin", StringComparison.OrdinalIgnoreCase) || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || segment is ".git" or ".vs" or ".idea" or ".daedalus")
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
            _timer?.Dispose();
            _timer = null;
        }
    }
}
