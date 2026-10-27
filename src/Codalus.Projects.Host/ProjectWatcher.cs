namespace Codalus.Projects.Host;

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
    private Pending _held = new();
    private int _holding;
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
                if (!roots.Any(r => IsSameOrUnder(directory, r)))
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

    /// <summary>
    /// Keeps NuGet's restore outputs (assets files and the imports it generates) back while the IDE's own restore runs,
    /// until <see cref="TakeRestoreOutputs"/> or <see cref="ResumeRestoreOutputs"/>. A restore writes them project by
    /// project, for minutes when a package source is unreachable, and each one reported on its own evaluated and built
    /// its projects again, restarting the language service's analysis every time.
    /// </summary>
    public void HoldRestoreOutputs()
    {
        lock (_gate)
        {
            _holding++;
        }
    }

    /// <summary>Ends a <see cref="HoldRestoreOutputs"/>: what it kept back, for the restore to evaluate once.</summary>
    public Changes TakeRestoreOutputs()
    {
        lock (_gate)
        {
            if (_holding == 0 || --_holding > 0)
            {
                return new Changes(false, [], []);
            }

            var held = _held;
            _held = new Pending();
            return new Changes(false, [.. held.MSBuildFiles], [.. held.Directories]);
        }
    }

    /// <summary>Ends a <see cref="HoldRestoreOutputs"/>, reporting what it kept back as any other change.</summary>
    public void ResumeRestoreOutputs()
    {
        var held = TakeRestoreOutputs();
        lock (_gate)
        {
            _pending.MSBuildFiles.UnionWith(held.MSBuildFiles);
            _pending.Directories.UnionWith(held.Directories);
            if (!_pending.IsEmpty)
            {
                _timer ??= new Timer(_ => Flush());
                _timer.Change(s_debounce, Timeout.InfiniteTimeSpan);
            }
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

            if (_holding > 0 && IsNuGetImport(path))
            {
                _held.MSBuildFiles.Add(path);
                return;
            }

            if (_holding > 0 && IsRestoreOutput(path))
            {
                _held.Directories.Add(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!);
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
        Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj" or ".sqlproj" or ".dcproj" or ".props" or ".targets"
        || Path.GetFileName(path).Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase);

    private static bool IsRestoreOutput(string path) =>
        Path.GetFileName(path).Equals("project.assets.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>An import NuGet generates in <c>obj</c> (<c>App.csproj.nuget.g.props</c> and <c>.targets</c>).</summary>
    private static bool IsNuGetImport(string path) =>
        path.EndsWith(".nuget.g.props", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".nuget.g.targets", StringComparison.OrdinalIgnoreCase);

    /// <summary>As <c>ProjectFileEditor.IsSameOrUnder</c>, here so the watcher has no MSBuild types (it is tested on its own).</summary>
    private static bool IsSameOrUnder(string path, string root)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar);
        var comparison = s_pathComparer == StringComparer.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(trimmed, comparison) || path.StartsWith(trimmed + Path.DirectorySeparatorChar, comparison);
    }

    private static bool IsBuildOutput(string path)
    {
        foreach (var segment in path.Split(Path.DirectorySeparatorChar))
        {
            if (segment.Equals("bin", StringComparison.OrdinalIgnoreCase) || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || segment is ".git" or ".vs" or ".idea" or ".codalus")
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
