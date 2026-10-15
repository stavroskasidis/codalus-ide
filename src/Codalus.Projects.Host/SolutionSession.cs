using System.Collections.Concurrent;
using System.Diagnostics;
using Codalus.Projects.Protocol;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.VisualStudio.SolutionPersistence;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace Codalus.Projects.Host;

/// <summary>What the session reports back to the shell.</summary>
internal interface ISessionEvents
{
    Task SolutionChangedAsync(SolutionInfo solution);

    Task ProjectEvaluatedAsync(ProjectInfo project);

    Task ProgressAsync(EvaluationProgressParams progress);

    Task RestoreOutputAsync(string line);

    Task RestoreStateAsync(string state, string? message);

    Task LogAsync(string level, string message);

    Task CompilerInputsAsync(CompilerInputsInfo inputs);

    Task DesignTimeProgressAsync(DesignTimeProgressParams progress);

    /// <summary>Folders whose set of files changed on disk (Show All Files, the folder view; ADR 0033).</summary>
    Task FilesChangedAsync(FilesChangedParams changes);
    /// <summary>A message of a running <c>dotnet ef</c> (ADR 0036).</summary>
    Task EfOutputAsync(EfOutputParams output);

    /// <summary>A line of a running <c>dotnet aspnet-codegenerator</c> (ADR 0041).</summary>
    Task ScaffoldOutputAsync(ScaffoldOutputParams output);
}

/// <summary>The session's events plus those of builds.</summary>
internal interface IHostEvents : ISessionEvents, IBuildEvents;

/// <summary>
/// One open solution (or a single project opened directly). Reads the solution file, evaluates projects in
/// parallel (only when trusted), re-evaluates on file changes, runs restore, and performs file and solution edits.
/// </summary>
internal sealed partial class SolutionSession : IAsyncDisposable
{
    private static readonly StringComparer s_pathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly ISessionEvents _events;
    private readonly string _dotnet;
    private readonly ProjectCollection _collection = new();

    /// <summary>Project files being edited are loaded here, never in <see cref="_collection"/>, which evaluations share.</summary>
    private readonly ProjectCollection _editCollection = new();
    private readonly ProjectEvaluator _evaluator;
    private readonly ProjectFileEditor _editor;
    private readonly DesignTimeBuilder _designTime;
    private readonly SolutionBuilder _builder = new();
    private string[] _designTimeInFlight = [];
    private CancellationTokenSource? _buildCancellation;
    private readonly ConcurrentDictionary<string, string> _activeFrameworks = new(s_pathComparer);
    private readonly ConcurrentDictionary<string, DateTime> _builtAssets = new(s_pathComparer);
    private readonly HashSet<string> _designTimePending = new(s_pathComparer);
    private Task _designTimeWorker = Task.CompletedTask;
    private CancellationTokenSource _designTimeBatch = new();
    private readonly ConcurrentDictionary<string, EvaluatedProject> _evaluated = new(s_pathComparer);
    private readonly SemaphoreSlim _solutionLock = new(1, 1);

    /// <summary>
    /// One evaluation batch at a time: batches started by an edit, the file watcher and a finished restore would
    /// otherwise load the same project twice into the shared collection ("an equivalent project is already present") or
    /// reload a project file while another batch evaluates it (MSB0001 "Should not be dirty now").
    /// </summary>
    private readonly SemaphoreSlim _evaluationLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private ProjectWatcher? _watcher;
    private SolutionModel? _model;
    private ISolutionSerializer? _serializer;
    private SolutionInfo _info = null!;
    private CancellationTokenSource _evaluation = new();
    private Task _restore = Task.CompletedTask;

    /// <summary>The running build's end: a restore asked for meanwhile waits for it (its `dotnet restore` would rewrite the
    /// assets file without the build's RuntimeIdentifier: NETSDK1047 in publishes, on every OS).</summary>
    private Task _building = Task.CompletedTask;

    public SolutionSession(ISessionEvents events, string dotnet)
    {
        _events = events;
        _dotnet = dotnet;
        _evaluator = new ProjectEvaluator(_collection);
        _editor = new ProjectFileEditor(_editCollection);
        _designTime = new DesignTimeBuilder(_collection, _builder.Gate);

        // The SDK's tasks (Csc among them) and RunCommand find the dotnet host through this, as under `dotnet build`.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")) && System.IO.Path.IsPathRooted(dotnet))
        {
            Environment.SetEnvironmentVariable("DOTNET_HOST_PATH", dotnet);
        }
    }

    public string Path { get; private set; } = string.Empty;

    public bool Trusted { get; private set; }

    public SolutionConfigurationInfo Active { get; private set; } = new("Debug", "Any CPU");

    /// <summary>A project opened on its own (no solution file, not a folder).</summary>
    private bool IsSingleProject => _model is null && !IsFolder;

    private IEnumerable<string> ProjectPaths => _info.Projects.Select(p => p.Path);

    // ------------------------------------------------------------------ open

    public async Task<SolutionInfo> OpenAsync(OpenSolutionParams parameters, CancellationToken cancellationToken)
    {
        Path = System.IO.Path.GetFullPath(parameters.Path);
        string[]? filterProjects = null;
        if (Directory.Exists(Path))
        {
            IsFolder = true;
            Path = System.IO.Path.TrimEndingDirectorySeparator(Path);
        }
        else if (System.IO.Path.GetExtension(Path).Equals(".slnf", StringComparison.OrdinalIgnoreCase))
        {
            FilterPath = Path;
            (Path, filterProjects) = ReadFilter(FilterPath);
            if (!File.Exists(Path))
            {
                throw new IOException($"The solution of the filter {System.IO.Path.GetFileName(FilterPath)} does not exist: {Path}");
            }
        }

        Trusted = parameters.Trusted;
        _restoreOnOpen = parameters.Restore;
        foreach (var (project, framework) in parameters.ActiveFrameworks ?? [])
        {
            _activeFrameworks[System.IO.Path.GetFullPath(project)] = framework;
        }

        _info = await ReadSolutionAsync(cancellationToken).ConfigureAwait(false);
        LoadUnloadedState(filterProjects);
        if (parameters.Configuration is { } configuration && parameters.Platform is { } platform
            && _info.Configurations.FirstOrDefault(c => c.Configuration == configuration && c.Platform == platform) is { } requested)
        {
            Active = requested;
        }
        else if (_info.Configurations.Length > 0)
        {
            Active = _info.Configurations.FirstOrDefault(c => c.Configuration == "Debug") ?? _info.Configurations[0];
        }

        _info = _info with { Active = Active, Trusted = Trusted, FilterPath = FilterPath };
        if (Trusted)
        {
            StartWatching();
            StartEvaluation(restoreAfterwards: parameters.Restore && _info.Projects.Length > 0);
        }
        else if (IsFolder)
        {
            StartWatching(); // the folder view follows the disk; nothing is evaluated
        }

        return _info;
    }

    /// <summary>The user trusted the folder: evaluate (and restore) now.</summary>
    public void Trust(bool restore)
    {
        if (Trusted)
        {
            return;
        }

        Trusted = true;
        _info = _info with { Trusted = true };
        StartWatching();
        StartEvaluation(restore);
    }

    public void SetConfiguration(SetConfigurationParams parameters)
    {
        Active = new SolutionConfigurationInfo(parameters.Configuration, parameters.Platform);
        _info = _info with { Active = Active };
        if (Trusted)
        {
            CancelDesignTimeBuilds();
            StartEvaluation(restoreAfterwards: false);
        }
    }

    private async Task<SolutionInfo> ReadSolutionAsync(CancellationToken cancellationToken)
    {
        if (IsFolder)
        {
            // A folder: an in-memory solution of the projects the user loaded (ADR 0033).
            _model = null;
            _serializer = null;
            return FolderInfo(LoadFolderProjects());
        }

        var extension = System.IO.Path.GetExtension(Path);
        if (extension is ".sln" or ".slnx")
        {
            _serializer = SolutionSerializers.GetSerializerByMoniker(Path)
                          ?? throw new InvalidOperationException($"Unsupported solution format: {Path}");
            _model = await _serializer.OpenAsync(Path, cancellationToken).ConfigureAwait(false);
            return Describe(_model);
        }

        // A project opened on its own: an in-memory solution with that project.
        _model = null;
        _serializer = null;
        return new SolutionInfo(
            Path,
            "project",
            [],
            [new SolutionProjectInfo(Path, System.IO.Path.GetFileNameWithoutExtension(Path), null, null)],
            [new("Debug", "Any CPU"), new("Release", "Any CPU")],
            Active,
            Trusted);
    }

    private SolutionInfo Describe(SolutionModel model)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        var folders = model.SolutionFolders
            .Select(f => new SolutionFolderInfo(f.Path, f.Name, f.Parent?.Path, [.. (f.Files ?? []).Select(file => Full(directory, file))]))
            .ToArray();
        var projects = model.SolutionProjects
            .Select(p => new SolutionProjectInfo(Full(directory, p.FilePath), p.ActualDisplayName, p.Parent?.Path, p.Type))
            .ToArray();
        var buildTypes = model.BuildTypes.Count > 0 ? model.BuildTypes : ["Debug", "Release"];
        var platforms = model.Platforms.Count > 0 ? model.Platforms : ["Any CPU"];
        var configurations = buildTypes.SelectMany(b => platforms.Select(p => new SolutionConfigurationInfo(b, p))).ToArray();
        return new SolutionInfo(Path, System.IO.Path.GetExtension(Path).TrimStart('.'), folders, projects, configurations, Active, Trusted);
    }

    private static string Full(string directory, string path) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, path.Replace('\\', System.IO.Path.DirectorySeparatorChar)));

    // ------------------------------------------------------------------ evaluation

    private void StartEvaluation(bool restoreAfterwards)
    {
        var previous = Interlocked.Exchange(ref _evaluation, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
        previous.Cancel();
        previous.Dispose();
        var token = _evaluation.Token;
        _ = Task.Run(async () =>
        {
            await EvaluateAsync(ProjectPaths.ToList(), token).ConfigureAwait(false);
            if (restoreAfterwards && !token.IsCancellationRequested)
            {
                // Opening restores only when a project's packages may have changed since its last restore (M34b): a
                // no-op restore of a large solution costs seconds of processor time next to the design-time builds.
                if (PackagesUpToDate() is { } upToDate)
                {
                    await _events.LogAsync("info", $"Packages of {upToDate} project(s) are up to date: no restore on open.").ConfigureAwait(false);
                    await _events.RestoreStateAsync("succeeded", null).ConfigureAwait(false);
                }
                else
                {
                    StartRestore(null);
                }
            }
        }, token);
    }

    /// <summary>When an evaluation is followed by design-time builds.</summary>
    private enum DesignTimeMode
    {
        /// <summary>The project may have changed (opened, edited, files added, configuration switched).</summary>
        Always,

        /// <summary>After a restore: only projects whose assets file changed since their last design-time build.</summary>
        IfAssetsChanged,
    }

    /// <summary>
    /// Evaluates <paramref name="projects"/> in parallel, reporting each one as soon as it is done, then queues
    /// their design-time builds.
    /// </summary>
    private async Task EvaluateAsync(IReadOnlyList<string> projects, CancellationToken cancellationToken, DesignTimeMode designTime = DesignTimeMode.Always)
    {
        await _evaluationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EvaluateLockedAsync(projects, designTime, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _evaluationLock.Release();
        }
    }

    private async Task EvaluateLockedAsync(IReadOnlyList<string> projects, DesignTimeMode designTime, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var context = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared);
        var done = 0;
        var progressGate = new Lock();
        await _events.ProgressAsync(new EvaluationProgressParams(0, projects.Count, projects.Count == 0)).ConfigureAwait(false);
        await Parallel.ForEachAsync(
            projects,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = cancellationToken },
            async (project, token) =>
            {
                var (configuration, platform) = ProjectConfiguration(project);
                var result = IsUnloaded(project)
                    ? new EvaluatedProject(UnloadedInfo(project), [])
                    : EvaluateWithRetry(project, configuration, platform, context);
                if (result.Info.State != ProjectStates.Unloaded && IsUnloaded(project))
                {
                    result = new EvaluatedProject(UnloadedInfo(project), []); // unloaded while it was being evaluated
                }
                var before = _evaluated.GetValueOrDefault(project);
                _evaluated[project] = result;

                // An unchanged project isn't sent again (a restore that changed nothing, a watcher event): every one
                // costs the shell a refresh of the views that follow projects (M34b).
                if (before is null || !SameInfo(before.Info, result.Info))
                {
                    await _events.ProjectEvaluatedAsync(result.Info).ConfigureAwait(false);
                }

                // Counted and queued together, so progress never goes backwards (or ends "incomplete").
                lock (progressGate)
                {
                    done++;
                    _ = _events.ProgressAsync(new EvaluationProgressParams(done, projects.Count, done == projects.Count) { Current = project });
                }
            }).ConfigureAwait(false);
        _watcher?.SetImports(_evaluated.Values.SelectMany(e => e.Imports));
        await _events.LogAsync("info", $"Evaluated {projects.Count} project(s) in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms ({Active.Name}).").ConfigureAwait(false);
        ScheduleDesignTimeBuilds(designTime == DesignTimeMode.Always
            ? projects
            : WithDependents([.. projects.Where(p => !_builtAssets.TryGetValue(p, out var built) || built != AssetsStamp(p))]));
    }

    /// <summary>
    /// <paramref name="projects"/> and the loaded projects that reference them, directly or not (M34c): a project's
    /// design-time result (and its cache key) includes what it references, so it is checked again when they change, or
    /// its cache entry keeps the old key and the next open misses it (the Linux runner, where the restore's assets files
    /// arrive one evaluation at a time).
    /// </summary>
    private IReadOnlyCollection<string> WithDependents(IReadOnlyCollection<string> projects)
    {
        if (projects.Count == 0)
        {
            return projects;
        }

        var result = new HashSet<string>(projects, s_pathComparer);
        bool added;
        do
        {
            added = false;
            foreach (var (path, evaluated) in _evaluated)
            {
                if (!result.Contains(path) && evaluated.Info.References.Any(r => r.Kind == ReferenceKinds.Project && r.Path is not null && result.Contains(r.Path)))
                {
                    added |= result.Add(path);
                }
            }
        }
        while (added);

        return result;
    }

    private static bool SameInfo(ProjectInfo a, ProjectInfo b) =>
        System.Text.Json.JsonSerializer.Serialize(a, ProjectJsonContext.Default.ProjectInfo) == System.Text.Json.JsonSerializer.Serialize(b, ProjectJsonContext.Default.ProjectInfo);

    /// <summary>
    /// Evaluates a project, once more when MSBuild reports that its file changed under the evaluation (a design-time
    /// build or an editor saving it at that moment).
    /// </summary>
    private EvaluatedProject EvaluateWithRetry(string project, string configuration, string platform, EvaluationContext context)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return _evaluator.Evaluate(project, configuration, platform, context);
            }
            // The same race as the others: a file rewritten while it is read (a null target name in the evaluator, seen
            // after Build Events rewrote the project on the hosted Linux and macOS runners).
            catch (Exception ex) when (attempt < 3 && (ex.GetType().Name == "InternalErrorException"
                                                      || (ex is InvalidOperationException && ex.Message.Contains("equivalent project", StringComparison.Ordinal))
                                                      || (ex is ArgumentNullException && ex.StackTrace?.Contains("Microsoft.Build.Evaluation.Evaluator", StringComparison.Ordinal) == true)))
            {
                Thread.Sleep(50 * attempt);
                context = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared); // the shared one may have cached the old file
            }
        }
    }

    /// <summary>The project configuration the active solution configuration maps to (MSBuild spells "Any CPU" "AnyCPU").</summary>
    private (string Configuration, string Platform) ProjectConfiguration(string projectPath)
    {
        var (configuration, platform) = (Active.Configuration, Active.Platform);
        if (_model?.SolutionProjects.FirstOrDefault(p => s_pathComparer.Equals(Full(System.IO.Path.GetDirectoryName(Path)!, p.FilePath), projectPath)) is { } project)
        {
            var mapped = project.GetProjectConfiguration(Active.Configuration, Active.Platform);
            configuration = mapped.Item1 ?? configuration;
            platform = mapped.Item2 ?? platform;
        }

        return (configuration, platform.Replace(" ", string.Empty, StringComparison.Ordinal));
    }

    public Task ReevaluateAsync(IReadOnlyList<string> projects) => Trusted ? EvaluateAsync(projects, _lifetime.Token) : Task.CompletedTask;

    // ------------------------------------------------------------------ design-time builds

    /// <summary>Chooses the target framework the language service sees for a multi-targeted project.</summary>
    public void SetActiveFramework(SetActiveFrameworkParams parameters)
    {
        var path = System.IO.Path.GetFullPath(parameters.Path);
        if (_evaluated.TryGetValue(path, out var project) && !project.Info.TargetFrameworks.Contains(parameters.TargetFramework, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"'{parameters.TargetFramework}' is not a target framework of {System.IO.Path.GetFileName(path)}.");
        }

        _activeFrameworks[path] = parameters.TargetFramework;
        ScheduleDesignTimeBuilds([path]);
    }

    /// <summary>Runs design-time builds again (one project, or all).</summary>
    public void RequestDesignTimeBuild(string? path)
    {
        if (!Trusted)
        {
            throw new InvalidOperationException("Design-time builds run only in trusted solutions.");
        }

        IReadOnlyCollection<string> projects = path is null ? [.. ProjectPaths] : [System.IO.Path.GetFullPath(path)];
        lock (_designTimePending)
        {
            _forcedDesignTime.UnionWith(projects); // asked for: never from the cache
        }

        ScheduleDesignTimeBuilds(projects);
    }

    private void ScheduleDesignTimeBuilds(IReadOnlyCollection<string> projects)
    {
        if (!Trusted)
        {
            return;
        }

        lock (_designTimePending)
        {
            // SQL database projects (.sqlproj, ADR 0038) have no compiler command line: they build, but get no design-time build.
            // Compose projects (.dcproj, ADR 0042) aren't MSBuild projects to us at all.
            _designTimePending.UnionWith(projects.Where(p => !IsUnloaded(p) && !p.EndsWith(".sqlproj", StringComparison.OrdinalIgnoreCase) && !ComposeProject.Is(p)));
            if (_designTimePending.Count == 0 && _designTimeWorker.IsCompleted)
            {
                // Nothing to build: say so, or the shell would wait for design-time builds that never come (M34b).
                _ = _events.DesignTimeProgressAsync(new DesignTimeProgressParams(0, 0, true));
                return;
            }

            if (_designTimeWorker.IsCompleted)
            {
                var token = _designTimeBatch.Token;
                _designTimeWorker = Task.Run(() => RunDesignTimeBuildsAsync(token), CancellationToken.None);
            }
        }
    }

    private void CancelDesignTimeBuilds()
    {
        lock (_designTimePending)
        {
            _designTimePending.Clear();
            var previous = Interlocked.Exchange(ref _designTimeBatch, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
            previous.Cancel();
            previous.Dispose();
        }
    }

    /// <summary>Drains the pending set in batches, so changes made during a batch are built right after it.</summary>
    private async Task RunDesignTimeBuildsAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Let a burst of changes (watcher events, many projects evaluated) settle into one batch.
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                string[] batch;
                lock (_designTimePending)
                {
                    batch = [.. _designTimePending];
                    _designTimePending.Clear();
                }

                if (batch.Length == 0)
                {
                    return;
                }

                _designTimeInFlight = batch;
                try
                {
                    await BuildBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _designTimeInFlight = [];
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task BuildBatchAsync(string[] batch, CancellationToken cancellationToken)
    {
        var properties = SolutionProperties();
        var requests = new List<DesignTimeBuilder.Request>();
        var cached = new List<CompilerInputsInfo>();
        var misses = new List<string>();
        var keys = new Dictionary<string, (string Directory, string Entry, string Key)>(s_pathComparer);
        var used = new Dictionary<string, (string? Framework, string Key)>(s_pathComparer);
        HashSet<string> forced;
        lock (_designTimePending)
        {
            forced = [.. _forcedDesignTime.Intersect(batch, s_pathComparer)];
            _forcedDesignTime.ExceptWith(forced);
        }

        foreach (var path in batch)
        {
            if (!_evaluated.TryGetValue(path, out var project) || project.Info.State != ProjectStates.Loaded)
            {
                continue;
            }

            var frameworks = project.Info.TargetFrameworks;
            var framework = frameworks.Length <= 1 ? null
                : _activeFrameworks.TryGetValue(path, out var active) && frameworks.Contains(active, StringComparer.OrdinalIgnoreCase) ? active : frameworks[0];
            var (configuration, platform) = ProjectConfiguration(path);
            _builtAssets[path] = AssetsStamp(path);

            // The persisted cache (M34b): unchanged inputs give the last build's command line without building.
            if (project.IntermediateDirectory is { } obj && DesignTimeKey(path, framework, properties) is { } key)
            {
                used[path] = (framework, key);
                var entry = DesignTimeCache.Entry(configuration, platform, framework);
                if (forced.Contains(path))
                {
                    misses.Add($"{System.IO.Path.GetFileName(path)}: asked for");
                }
                else if (DesignTimeCache.TryRead(obj, entry, key, SolutionDirectory, out var miss) is { } hit)
                {
                    cached.Add(hit);
                    continue;
                }
                else
                {
                    misses.Add($"{System.IO.Path.GetFileName(path)}: {miss}");
                }

                keys[path] = (obj, entry, key);
            }
            else
            {
                misses.Add($"{System.IO.Path.GetFileName(path)}: no cache key (no intermediate folder, or a referenced project has none)");
            }

            requests.Add(new DesignTimeBuilder.Request(path, configuration, platform, framework));
        }

        var started = Stopwatch.GetTimestamp();
        var built = 0;
        var total = requests.Count + cached.Count;
        await _events.DesignTimeProgressAsync(new DesignTimeProgressParams(0, total, false)).ConfigureAwait(false);
        foreach (var inputs in cached)
        {
            await _events.CompilerInputsAsync(inputs).ConfigureAwait(false);
            built++;
        }

        if (cached.Count > 0)
        {
            await _events.DesignTimeProgressAsync(new DesignTimeProgressParams(built, total, false)).ConfigureAwait(false);
        }

        await _designTime.BuildAsync(requests, properties, async inputs =>
        {
            if (keys.TryGetValue(inputs.Path, out var key))
            {
                DesignTimeCache.Write(key.Directory, key.Entry, key.Key, inputs);
            }

            await _events.CompilerInputsAsync(inputs).ConfigureAwait(false);
            await _events.DesignTimeProgressAsync(new DesignTimeProgressParams(Interlocked.Increment(ref built), total, false)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        // A project evaluated again while this batch ran (the restore's assets files can arrive one evaluation at a time)
        // may have changed what a result here was built from: those build again, or their cache entry would keep a key
        // the next open never matches (M34c, the Linux runner).
        var current = SolutionProperties();
        var stale = used.Where(u => _evaluated.TryGetValue(u.Key, out var p) && p.Info.State == ProjectStates.Loaded
                                    && DesignTimeKey(u.Key, u.Value.Framework, current) is { } now && now != u.Value.Key)
            .Select(u => u.Key).ToList();
        bool more;
        lock (_designTimePending)
        {
            _designTimePending.UnionWith(stale);
            more = _designTimePending.Count > 0;
        }

        // Hits and misses since the last completed progress go with the next one (M34c: benchmarks assert that warm opens
        // hit the cache).
        int hits;
        string[] missed;
        lock (_cacheStatistics)
        {
            _cacheHits += cached.Count;
            _cacheStatistics.AddRange(misses);
            hits = _cacheHits;
            missed = [.. _cacheStatistics];
            if (!more)
            {
                _cacheHits = 0;
                _cacheStatistics.Clear();
            }
        }

        await _events.DesignTimeProgressAsync(new DesignTimeProgressParams(built, total, !more)
        {
            CacheHits = more ? null : hits,
            CacheMisses = more ? null : missed,
        }).ConfigureAwait(false);
        await _events.LogAsync("info", $"Design-time build of {requests.Count} project(s) in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms"
                                       + (cached.Count > 0 ? $"; {cached.Count} unchanged project(s) from the cache." : ".")).ConfigureAwait(false);
        if (cached.Count + misses.Count > 0)
        {
            // Why each project built instead of coming from the cache (the build host log, in the Output window).
            await _events.LogAsync("info", $"Design-time build cache: {cached.Count} hit(s), {misses.Count} miss(es)"
                                           + (misses.Count > 0 ? ": " + string.Join("; ", misses.Take(20)) + (misses.Count > 20 ? $"; and {misses.Count - 20} more" : string.Empty) : ".")).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ builds

    /// <summary>
    /// Builds the solution or a project (ADR 0006). Design-time builds in flight are cancelled and run again
    /// afterwards; projects whose assets file the build's restore changed are evaluated again.
    /// </summary>
    public async Task<BuildResultInfo> BuildAsync(BuildParams parameters, IBuildEvents events, CancellationToken cancellationToken)
    {
        if (!Trusted)
        {
            throw new InvalidOperationException("Builds run only in trusted solutions.");
        }

        string path;
        var properties = SolutionProperties();
        if (parameters.Project is { } project)
        {
            path = System.IO.Path.GetFullPath(project);
            (properties["Configuration"], properties["Platform"]) = ProjectConfiguration(path);
        }
        else if (IsSingleProject)
        {
            path = Path;
            (properties["Configuration"], properties["Platform"]) = ProjectConfiguration(path);
        }
        else if (IsFolder)
        {
            path = FolderBuildEntryPoint();
            properties["Configuration"] = Active.Configuration;
            properties["Platform"] = path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ? Active.Platform : Active.Platform.Replace(" ", string.Empty, StringComparison.Ordinal);
        }
        else
        {
            path = BuildEntryPoint();
            properties["Configuration"] = Active.Configuration;
            properties["Platform"] = Active.Platform;
        }

        foreach (var (name, value) in parameters.Options?.Properties ?? [])
        {
            properties[name] = value;
        }

        var perProject = parameters.Project is null && !IsSingleProject;
        IReadOnlyList<string> scope = !perProject ? [path] : BuiltProjects();
        return await ExclusiveBuildAsync(
            token => BuildWithCheckAsync(path, parameters.Target, parameters.Restore, properties, scope, BuildGlobals(perProject, properties), parameters.Options, events, token),
            new BuildResultInfo(false, true, 0, 0, 0),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a build exclusively: one at a time (<c>build/cancel</c> stops it), with design-time builds paused and queued
    /// again afterwards, and projects whose assets file the build's restore changed evaluated again.
    /// </summary>
    private async Task<T> ExclusiveBuildAsync<T>(Func<CancellationToken, Task<T>> build, T canceled, CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        if (Interlocked.CompareExchange(ref _buildCancellation, cancellation, null) is not null)
        {
            throw new InvalidOperationException("A build is already running.");
        }

        var building = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _building = building.Task;

        // A background restore (the one after opening) finishes first: it would rewrite the assets file without the
        // build's own global properties (a publish's RuntimeIdentifier: NETSDK1047, seen on macOS).
        try
        {
            await _restore.WaitAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Exchange(ref _buildCancellation, null);
            building.TrySetResult();
            return canceled;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            // A failed restore is reported by the restore itself; the build restores again.
        }

        var interrupted = await PauseDesignTimeBuildsAsync().ConfigureAwait(false);
        try
        {
            await _builder.Gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Exchange(ref _buildCancellation, null);
            building.TrySetResult();
            ScheduleDesignTimeBuilds(interrupted);
            return canceled;
        }

        try
        {
            return await build(cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _buildCancellation, null);
            building.TrySetResult();
            _builder.Gate.Release();
            ScheduleDesignTimeBuilds(interrupted);
            var restored = ProjectPaths.Where(p => _builtAssets.TryGetValue(p, out var built) && built != AssetsStamp(p)).ToList();
            if (restored.Count > 0)
            {
                _ = Task.Run(() => EvaluateAsync(restored, _lifetime.Token), CancellationToken.None);
            }
        }
    }

    /// <summary>Cancels the running build, if any; its result reports the cancellation.</summary>
    public void CancelBuild()
    {
        try
        {
            Volatile.Read(ref _buildCancellation)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Cancels design-time builds (running and queued) and returns their projects, to queue them again later.</summary>
    private async Task<string[]> PauseDesignTimeBuildsAsync()
    {
        string[] interrupted;
        Task worker;
        lock (_designTimePending)
        {
            interrupted = [.. _designTimePending, .. _designTimeInFlight];
            worker = _designTimeWorker;
        }

        CancelDesignTimeBuilds();
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        return interrupted;
    }

    /// <summary>How to start a project: the SDK's run properties for the active configuration and target framework.</summary>
    public RunInfo GetRunInfo(string projectPath, string? targetFramework = null)
    {
        if (!Trusted)
        {
            throw new InvalidOperationException("Projects run only in trusted solutions.");
        }

        var path = System.IO.Path.GetFullPath(projectPath);
        var properties = SolutionProperties();
        (properties["Configuration"], properties["Platform"]) = ProjectConfiguration(path);
        string? framework = null;
        if (_evaluated.TryGetValue(path, out var evaluated) && evaluated.Info.TargetFrameworks.Length > 1)
        {
            var frameworks = evaluated.Info.TargetFrameworks;
            framework = targetFramework is not null && frameworks.Contains(targetFramework, StringComparer.OrdinalIgnoreCase) ? targetFramework
                : _activeFrameworks.TryGetValue(path, out var active) && frameworks.Contains(active, StringComparer.OrdinalIgnoreCase) ? active
                : frameworks[0];
            properties["TargetFramework"] = framework;
        }

        using var collection = new ProjectCollection(properties);
        var project = collection.LoadProject(path);
        string? Property(string name) => project.GetPropertyValue(name) is { Length: > 0 } value ? value : null;
        var directory = System.IO.Path.GetDirectoryName(path)!;
        var launchSettings = System.IO.Path.Combine(directory, "Properties", "launchSettings.json");
        return new RunInfo(
            path,
            framework ?? Property("TargetFramework"),
            Property("OutputType"),
            Property("TargetPath"),
            Property("RunCommand"),
            Property("RunArguments"),
            Property("RunWorkingDirectory"),
            File.Exists(launchSettings) ? launchSettings : null);
    }

    /// <summary>The properties MSBuild sets when it builds a solution; some projects rely on them.</summary>
    private Dictionary<string, string> SolutionProperties()
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (IsFolder)
        {
            properties["SolutionDir"] = Path + System.IO.Path.DirectorySeparatorChar;
        }
        else if (!IsSingleProject)
        {
            properties["SolutionDir"] = System.IO.Path.GetDirectoryName(Path) + System.IO.Path.DirectorySeparatorChar;
            properties["SolutionPath"] = Path;
            properties["SolutionFileName"] = System.IO.Path.GetFileName(Path);
            properties["SolutionName"] = System.IO.Path.GetFileNameWithoutExtension(Path);
            properties["SolutionExt"] = System.IO.Path.GetExtension(Path);
        }

        return properties;
    }

    private DateTime AssetsStamp(string project) =>
        _evaluated.TryGetValue(project, out var evaluated) && evaluated.AssetsFile is { } assets && File.Exists(assets)
            ? File.GetLastWriteTimeUtc(assets)
            : DateTime.MinValue;

    // ------------------------------------------------------------------ watching

    private void StartWatching()
    {
        _watcher ??= new ProjectWatcher(OnFilesChanged);
        WatchNow();
    }

    /// <summary>Points the watcher at the workspace folder, the solution file (when there is one) and the projects.</summary>
    private void WatchNow() => _watcher?.Watch(SolutionDirectory, IsFolder ? null : Path, ProjectPaths);

    private async Task OnFilesChanged(ProjectWatcher.Changes changes)
    {
        try
        {
            var structural = changes.Directories.Concat(changes.MSBuildFiles.Select(f => System.IO.Path.GetDirectoryName(f)!)).Distinct(s_pathComparer).ToArray();
            if (structural.Length > 0)
            {
                await _events.FilesChangedAsync(new FilesChangedParams(structural)).ConfigureAwait(false);
            }

            if (changes.SolutionFileChanged && !IsSingleProject && !IsFolder)
            {
                await _solutionLock.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    var old = ProjectPaths.ToHashSet(s_pathComparer);
                    _info = await ReadSolutionAsync(_lifetime.Token).ConfigureAwait(false) with { Active = Active, Trusted = Trusted, FilterPath = FilterPath };
                    await _events.SolutionChangedAsync(_info).ConfigureAwait(false);
                    WatchNow();
                    var added = ProjectPaths.Where(p => !old.Contains(p)).ToList();
                    await EvaluateAsync(added, _lifetime.Token).ConfigureAwait(false);
                }
                finally
                {
                    _solutionLock.Release();
                }
            }

            foreach (var file in changes.MSBuildFiles)
            {
                ProjectRootElement.TryOpen(file, _collection, preserveFormatting: true)?.Reload(throwIfUnsavedChanges: false, preserveFormatting: true);
            }

            // Projects whose own folder changed, plus every project that imports a changed MSBuild file.
            var affected = ProjectPaths
                .Where(p => changes.Directories.Any(d => ProjectFileEditor.IsSameOrUnder(d, System.IO.Path.GetDirectoryName(p)!))
                            || changes.MSBuildFiles.Any(f => s_pathComparer.Equals(f, p)
                                                             || (_evaluated.TryGetValue(p, out var e) && e.Imports.Contains(f, s_pathComparer))))
                .ToList();
            await ReevaluateAsync(affected).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            await _events.LogAsync("warning", $"Could not refresh after file changes: {ex.Message}").ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ restore

    public void StartRestore(string? path)
    {
        if (!Trusted)
        {
            throw new InvalidOperationException("Restore runs only in trusted solutions.");
        }

        if (path is null && IsFolder)
        {
            if (!ProjectPaths.Any(p => !IsUnloaded(p)))
            {
                return; // a folder with no loaded project has nothing to restore
            }

            path = FolderBuildEntryPoint();
        }
        else if (path is null && !IsFolder && ProjectPaths.Any(ComposeProject.Is))
        {
            path = BuildEntryPoint(); // without the compose projects, which dotnet restore can't load
        }

        var building = _building;
        _restore = Task.WhenAll(_restore.ContinueWith(_ => { }, TaskScheduler.Default), building)
            .ContinueWith(_ => RestoreAsync(path ?? Path), TaskScheduler.Default).Unwrap();
    }

    private async Task RestoreAsync(string target)
    {
        await _events.RestoreStateAsync("running", null).ConfigureAwait(false);
        try
        {
            var code = await RunRestoreAsync(target).ConfigureAwait(false);
            var restoredAlone = 0;
            if (code != 0 && !ProjectPaths.Contains(target, s_pathComparer))
            {
                // One project that can't restore (a MAUI app without its workload, an error in its file) fails the
                // whole solution's restore before any assets file is written: the others are restored on their own,
                // so they still build and get their packages' completions.
                foreach (var project in ProjectPaths.Where(p => !IsUnloaded(p) && !ComposeProject.Is(p) && !AssetsWritten(p)).ToList())
                {
                    if (await RunRestoreAsync(project).ConfigureAwait(false) == 0)
                    {
                        restoredAlone++;
                    }
                }
            }

            var ok = code == 0;
            if (ok)
            {
                MarkRestored();
            }

            await _events.RestoreStateAsync(ok ? "succeeded" : "failed", ok ? null
                : $"dotnet restore exited with code {code}." + (restoredAlone > 0 ? $" {restoredAlone} project(s) restored on their own." : "")).ConfigureAwait(false);

            // Only projects whose assets file the restore wrote are evaluated again (M34b); the watcher handles the
            // NuGet-generated imports it rewrote.
            await EvaluateAsync([.. ProjectPaths.Where(AssetsChangedSinceEvaluation)], _lifetime.Token, DesignTimeMode.IfAssetsChanged).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            await _events.RestoreStateAsync("failed", ex.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Runs <c>dotnet restore</c> on a solution or project, its output going to the restore's output; its exit code.</summary>
    private async Task<int> RunRestoreAsync(string target)
    {
        // No --nologo: SDKs before 5.0 hand it to MSBuild, which rejects it (MSB1001). DOTNET_NOLOGO does the same.
        var psi = Packages.DotnetProcess.StartInfo(_dotnet, System.IO.Path.GetDirectoryName(target)!, ["restore", target, "--verbosity", "minimal"]);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("dotnet restore did not start.");
        var output = PumpAsync(process.StandardOutput);
        var error = PumpAsync(process.StandardError);
        await Packages.DotnetProcess.WaitOrKillAsync(process, _lifetime.Token).ConfigureAwait(false);
        await Task.WhenAll(output, error).ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>Whether the restore just run wrote the project's assets file (it has one newer than its evaluation's).</summary>
    private bool AssetsWritten(string project) => AssetsStamp(project) != DateTime.MinValue && AssetsChangedSinceEvaluation(project);

    private async Task PumpAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync(_lifetime.Token).ConfigureAwait(false) is { } line)
        {
            await _events.RestoreOutputAsync(line).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ file operations

    public FileOperationResult CreateFile(CreateFileParams parameters)
    {
        var path = System.IO.Path.GetFullPath(parameters.Path);
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException($"'{System.IO.Path.GetFileName(path)}' already exists.");
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, parameters.Contents ?? string.Empty);
        return new FileOperationResult([], [path]);
    }

    public FileOperationResult CreateFolder(CreateFolderParams parameters)
    {
        var path = System.IO.Path.GetFullPath(parameters.Path);
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException($"'{System.IO.Path.GetFileName(path)}' already exists.");
        }

        Directory.CreateDirectory(path);
        return new FileOperationResult([], [path]);
    }

    public async Task<FileOperationResult> RenameAsync(RenameParams parameters, CancellationToken cancellationToken)
    {
        var oldPath = System.IO.Path.GetFullPath(parameters.OldPath).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var newPath = System.IO.Path.GetFullPath(parameters.NewPath).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        MovePath(oldPath, newPath, copy: false);
        var updated = new List<string>(_editor.Renamed(ProjectPathsAfter(oldPath, newPath), oldPath, newPath));
        updated.AddRange(await UpdateSolutionPathsAsync(oldPath, newPath, cancellationToken).ConfigureAwait(false));
        return new FileOperationResult([.. updated.Distinct(s_pathComparer)], [newPath]);
    }

    public async Task<FileOperationResult> MoveAsync(MoveParams parameters, CancellationToken cancellationToken)
    {
        var target = System.IO.Path.GetFullPath(parameters.TargetFolder);
        var updated = new List<string>();
        var created = new List<string>();
        foreach (var source in parameters.Sources.Select(s => System.IO.Path.GetFullPath(s).TrimEnd(System.IO.Path.DirectorySeparatorChar)))
        {
            var destination = System.IO.Path.Combine(target, System.IO.Path.GetFileName(source));
            if (ProjectFileEditor.IsSameOrUnder(target, source))
            {
                throw new IOException($"Can't move '{System.IO.Path.GetFileName(source)}' into itself.");
            }

            MovePath(source, destination, parameters.Copy);
            created.Add(destination);

            // Into another project: its explicit items (metadata, build action) go with it (ADR 0033).
            updated.AddRange(CarryItems(source, destination, parameters.Copy));
            if (!parameters.Copy)
            {
                updated.AddRange(_editor.Renamed(ProjectPathsAfter(source, destination), source, destination));
                updated.AddRange(await UpdateSolutionPathsAsync(source, destination, cancellationToken).ConfigureAwait(false));
            }
        }

        return new FileOperationResult([.. updated.Distinct(s_pathComparer)], [.. created]);
    }

    public async Task<FileOperationResult> DeleteAsync(DeleteParams parameters, CancellationToken cancellationToken)
    {
        var updated = new List<string>();
        foreach (var path in parameters.Paths.Select(p => System.IO.Path.GetFullPath(p).TrimEnd(System.IO.Path.DirectorySeparatorChar)))
        {
            Trash.Move(path);
            updated.AddRange(_editor.Deleted(ProjectPaths.Where(File.Exists), path));
            if (IsFolder && ProjectPaths.Any(p => ProjectFileEditor.IsSameOrUnder(p, path)))
            {
                await ChangeFolderProjectsAsync([.. ProjectPaths.Where(p => !ProjectFileEditor.IsSameOrUnder(p, path))], cancellationToken).ConfigureAwait(false);
            }
            else if (!IsSingleProject && !IsFolder && ProjectPaths.Any(p => ProjectFileEditor.IsSameOrUnder(p, path)))
            {
                await EditSolutionAsync(model =>
                {
                    foreach (var project in model.SolutionProjects.Where(p => ProjectFileEditor.IsSameOrUnder(Full(SolutionDirectory, p.FilePath), path)).ToList())
                    {
                        model.RemoveProject(project);
                    }
                }, cancellationToken).ConfigureAwait(false);
                updated.Add(Path);
            }
        }

        return new FileOperationResult([.. updated.Distinct(s_pathComparer)], []);
    }

    public ListDirectoryResult ListDirectory(ListDirectoryParams parameters)
    {
        var directory = new DirectoryInfo(parameters.Path);
        if (!directory.Exists)
        {
            return new ListDirectoryResult([]);
        }

        return new ListDirectoryResult([.. directory.EnumerateFileSystemInfos()
            .Where(e => (e.Attributes & FileAttributes.Hidden) == 0)
            .Select(e => new DirectoryEntry(e.FullName, e is DirectoryInfo))]);
    }

    /// <summary>The projects as they are after a rename (a renamed project file or folder moves projects along).</summary>
    private List<string> ProjectPathsAfter(string oldPath, string newPath) =>
        [.. ProjectPaths.Select(p => ProjectFileEditor.IsSameOrUnder(p, oldPath) ? newPath + p[oldPath.Length..] : p)];

    private static void MovePath(string source, string destination, bool copy)
    {
        if (!File.Exists(source) && !Directory.Exists(source))
        {
            throw new FileNotFoundException($"'{System.IO.Path.GetFileName(source)}' no longer exists.", source);
        }

        var caseOnly = string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) && source != destination;
        if (!caseOnly && (File.Exists(destination) || Directory.Exists(destination)))
        {
            throw new IOException($"'{System.IO.Path.GetFileName(destination)}' already exists.");
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
        if (Directory.Exists(source))
        {
            if (copy)
            {
                CopyDirectory(source, destination);
            }
            else if (caseOnly)
            {
                var temp = destination + ".~" + Guid.NewGuid().ToString("N")[..8];
                Retry(() => Directory.Move(source, temp));
                Retry(() => Directory.Move(temp, destination));
            }
            else
            {
                Retry(() => Directory.Move(source, destination));
            }
        }
        else if (copy)
        {
            File.Copy(source, destination);
        }
        else
        {
            Retry(() => File.Move(source, destination));
        }
    }

    /// <summary>
    /// Retries a move that failed with a sharing violation: on Windows a file inside may be open for a moment (a
    /// design-time build reading it, an indexer, antivirus).
    /// </summary>
    private static void Retry(Action move)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                move();
                return;
            }
            catch (Exception ex) when (attempt < 10 && OperatingSystem.IsWindows() && (ex is UnauthorizedAccessException || (ex is IOException io && (io.HResult & 0xFFFF) is 32 or 33 or 5)))
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(directory)));
        }
    }

    // ------------------------------------------------------------------ solution edits

    /// <summary>The workspace folder: the folder in folder mode, else the solution (or project) file's folder.</summary>
    private string SolutionDirectory => IsFolder ? Path : System.IO.Path.GetDirectoryName(Path)!;

    /// <summary>Points solution entries at projects that moved with a file or folder rename.</summary>
    private async Task<IReadOnlyList<string>> UpdateSolutionPathsAsync(string oldPath, string newPath, CancellationToken cancellationToken)
    {
        if (IsFolder)
        {
            if (ProjectPaths.Any(p => ProjectFileEditor.IsSameOrUnder(p, oldPath)))
            {
                await ChangeFolderProjectsAsync([.. ProjectPathsAfter(oldPath, newPath)], cancellationToken).ConfigureAwait(false);
            }

            return [];
        }

        if (IsSingleProject)
        {
            if (ProjectFileEditor.IsSameOrUnder(Path, oldPath))
            {
                Path = newPath + Path[oldPath.Length..];
            }

            return [];
        }

        var moved = false;
        await EditSolutionAsync(model =>
        {
            foreach (var project in model.SolutionProjects)
            {
                var full = Full(SolutionDirectory, project.FilePath);
                if (ProjectFileEditor.IsSameOrUnder(full, oldPath))
                {
                    project.FilePath = System.IO.Path.GetRelativePath(SolutionDirectory, newPath + full[oldPath.Length..]);
                    moved = true;
                }
            }

            foreach (var folder in model.SolutionFolders)
            {
                foreach (var file in (folder.Files ?? []).ToList())
                {
                    var full = Full(SolutionDirectory, file);
                    if (ProjectFileEditor.IsSameOrUnder(full, oldPath))
                    {
                        folder.RemoveFile(file);
                        folder.AddFile(System.IO.Path.GetRelativePath(SolutionDirectory, newPath + full[oldPath.Length..]));
                        moved = true;
                    }
                }
            }
        }, cancellationToken, saveOnlyIf: () => moved).ConfigureAwait(false);
        return moved ? [Path] : [];
    }

    public Task AddProjectAsync(AddProjectParams parameters, CancellationToken cancellationToken) =>
        IsFolder
            ? ChangeFolderProjectsAsync([.. ProjectPaths.Append(System.IO.Path.GetFullPath(parameters.ProjectPath)).Distinct(s_pathComparer)], cancellationToken)
            : EditSolutionAsync(model =>
        {
            var relative = System.IO.Path.GetRelativePath(SolutionDirectory, System.IO.Path.GetFullPath(parameters.ProjectPath));
            if (model.FindProject(relative) is not null)
            {
                return;
            }

            var folder = parameters.SolutionFolder is { } id ? model.FindFolder(id) ?? model.AddFolder(id) : null;
            model.AddProject(relative, null, folder);
        }, cancellationToken);

    public Task RemoveProjectAsync(ProjectPathParams parameters, CancellationToken cancellationToken) =>
        IsFolder
            ? ChangeFolderProjectsAsync([.. ProjectPaths.Where(p => !s_pathComparer.Equals(p, System.IO.Path.GetFullPath(parameters.Path)))], cancellationToken)
            : EditSolutionAsync(model =>
        {
            if (FindProject(model, parameters.Path) is { } project)
            {
                model.RemoveProject(project);
            }
        }, cancellationToken);

    public Task AddFolderAsync(SolutionFolderParams parameters, CancellationToken cancellationToken) =>
        EditSolutionAsync(model => model.AddFolder($"{parameters.Parent?.TrimEnd('/') ?? string.Empty}/{parameters.Name}/"), cancellationToken);

    public Task RenameFolderAsync(RenameSolutionFolderParams parameters, CancellationToken cancellationToken) =>
        EditSolutionAsync(model =>
        {
            var folder = model.FindFolder(parameters.Id) ?? throw new InvalidOperationException($"No solution folder '{parameters.Id}'.");
            folder.Name = parameters.NewName;
        }, cancellationToken);

    public Task RemoveFolderAsync(RemoveSolutionFolderParams parameters, CancellationToken cancellationToken) =>
        EditSolutionAsync(model =>
        {
            if (model.FindFolder(parameters.Id) is { } folder)
            {
                model.RemoveFolder(folder);
            }
        }, cancellationToken);

    public Task MoveInSolutionAsync(MoveInSolutionParams parameters, CancellationToken cancellationToken) =>
        EditSolutionAsync(model =>
        {
            SolutionItemModel item = (IsFolderId(parameters.Item) ? model.FindFolder(parameters.Item) : (SolutionItemModel?)FindProject(model, parameters.Item))
                                     ?? throw new InvalidOperationException($"'{parameters.Item}' is not in the solution.");
            var target = parameters.TargetFolder is { } id ? model.FindFolder(id) ?? throw new InvalidOperationException($"No solution folder '{id}'.") : null;
            item.MoveToFolder(target);
        }, cancellationToken);

    public bool AddProjectReference(ProjectReferenceParams parameters) =>
        _editor.AddProjectReference(System.IO.Path.GetFullPath(parameters.ProjectPath), System.IO.Path.GetFullPath(parameters.ReferencePath));

    public bool RemoveProjectReference(ProjectReferenceParams parameters) =>
        _editor.RemoveProjectReference(System.IO.Path.GetFullPath(parameters.ProjectPath), System.IO.Path.GetFullPath(parameters.ReferencePath));

    /// <summary>Solution folder ids start and end with '/' (<c>/src/</c>); absolute Unix paths only start with one.</summary>
    private static bool IsFolderId(string item) => item.Length > 1 && item[0] == '/' && item[^1] == '/';

    private SolutionProjectModel? FindProject(SolutionModel model, string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        return model.SolutionProjects.FirstOrDefault(p => s_pathComparer.Equals(Full(SolutionDirectory, p.FilePath), full));
    }

    /// <summary>Applies <paramref name="edit"/> to the solution file (kept in its format) and reports the new structure.</summary>
    private async Task EditSolutionAsync(Action<SolutionModel> edit, CancellationToken cancellationToken, Func<bool>? saveOnlyIf = null)
    {
        if (_model is null || _serializer is null)
        {
            throw new InvalidOperationException(IsFolder ? "A folder opened without a solution has no solution file to edit." : "A project opened on its own has no solution file to edit.");
        }

        await _solutionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-read first: the file may have been edited outside Codalus.
            _model = await _serializer.OpenAsync(Path, cancellationToken).ConfigureAwait(false);
            var old = ProjectPaths.ToHashSet(s_pathComparer);
            edit(_model);
            if (saveOnlyIf is not null && !saveOnlyIf())
            {
                return;
            }

            _watcher?.Suppress(Path);
            await _serializer.SaveAsync(Path, _model, cancellationToken).ConfigureAwait(false);
            _info = Describe(_model) with { FilterPath = FilterPath };
            await _events.SolutionChangedAsync(_info).ConfigureAwait(false);
            if (Trusted)
            {
                WatchNow();
                _ = EvaluateAsync([.. ProjectPaths.Where(p => !old.Contains(p))], _lifetime.Token);
            }
        }
        finally
        {
            _solutionLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _watcher?.Dispose();
        try
        {
            await _restore.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        CancelDesignTimeBuilds();
        try
        {
            await _designTimeWorker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _designTime.Dispose();
        _designTimeBatch.Dispose();
        _builder.Dispose();

        _evaluation.Dispose();
        _lifetime.Dispose();
        _solutionLock.Dispose();
        _collection.Dispose();
        _editCollection.Dispose();
    }
}
