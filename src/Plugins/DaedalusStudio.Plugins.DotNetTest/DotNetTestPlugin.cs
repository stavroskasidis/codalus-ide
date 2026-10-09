using DaedalusStudio.Plugins.Proposed;

namespace DaedalusStudio.Plugins.DotNetTest;

/// <summary>
/// The built-in .NET test provider (ADR 0010, 0011). Shows tests from source as soon as a solution's test projects are
/// evaluated, asks the runners (VSTest or Microsoft.Testing.Platform) after builds, and runs, debugs and measures coverage
/// for what the Test Explorer asks for.
/// </summary>
public sealed class DotNetTestPlugin : IDaedalusPlugin, IDisposable
{
    private static readonly TimeSpan s_rescanDelay = TimeSpan.FromMilliseconds(200);

    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ProjectTests> _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _discoveredBuilds = new(StringComparer.OrdinalIgnoreCase);
    private IPluginContext _context = null!;
    private IProposedApi _api = null!;
    private ITestController _controller = null!;
    private TestTree _tree = null!;
    private VsTestRunner _vstest = null!;
    private MtpRunner _mtp = null!;
    private WorkspaceSolution? _solution;
    private CancellationTokenSource _lifetime = new();
    private Timer? _rescanTimer;

    /// <summary>Completes when the first source scan after activation is done (tests).</summary>
    internal Task Initialized { get; private set; } = Task.CompletedTask;

    internal ITestController Controller => _controller;

    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _api = context.Proposed();
        _lifetime = new CancellationTokenSource();
        _controller = _api.Tests.CreateController("dotnet", ".NET");
        _tree = new TestTree(_controller);
        _vstest = new VsTestRunner(_api.Debug, Log);
        _mtp = new MtpRunner(_api.Debug, Log);
        context.Subscriptions.Add(_controller);
        context.Subscriptions.Add(_controller.CreateRunProfile("Run", TestRunProfileKind.Run, (r, ct) => RunAsync(r, RunMode.Run, ct)));
        context.Subscriptions.Add(_controller.CreateRunProfile("Debug", TestRunProfileKind.Debug, (r, ct) => RunAsync(r, RunMode.Debug, ct)));
        context.Subscriptions.Add(_controller.CreateRunProfile("Run with Coverage", TestRunProfileKind.Coverage, (r, ct) => RunAsync(r, RunMode.Coverage, ct)));
        _controller.RefreshHandler = ct => RefreshAsync(ct);
        _rescanTimer = new Timer(_ => _ = ScanAsync(_lifetime.Token));
        _api.Workspace.SolutionChanged += OnSolutionChanged;
        _api.Workspace.DocumentsChanged += OnDocumentsChanged;
        _api.Build.Finished += OnBuildFinished;
        context.Commands.Register("dotnettest.createUnitTests", (args, ct) => CreateUnitTests.RunAsync(_context, _api, args, ct));
        Initialized = ScanAsync(_lifetime.Token);
        return Task.CompletedTask;
    }

    public async Task DeactivateAsync(CancellationToken cancellationToken)
    {
        _api.Workspace.SolutionChanged -= OnSolutionChanged;
        _api.Workspace.DocumentsChanged -= OnDocumentsChanged;
        _api.Build.Finished -= OnBuildFinished;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_rescanTimer is not null)
        {
            await _rescanTimer.DisposeAsync().ConfigureAwait(false);
        }

        _vstest.Dispose();
    }

    public void Dispose()
    {
        _lifetime.Dispose();
        _rescanTimer?.Dispose();
        _scanGate.Dispose();
        _vstest?.Dispose();
    }

    private void Log(string message) => _context.Log.Info(message);

    // ------------------------------------------------------------------ source discovery

    private void OnSolutionChanged(object? sender, EventArgs e) => _rescanTimer?.Change(s_rescanDelay, Timeout.InfiniteTimeSpan);

    private void OnDocumentsChanged(object? sender, DocumentsChangedEventArgs e)
    {
        List<(ProjectTests Project, string Uri)> affected;
        lock (_gate)
        {
            affected = [.. e.Uris.SelectMany(uri => _projects.Values.Where(p => p.Candidates.ContainsKey(uri)).Select(p => (p, uri)))];
        }

        if (affected.Count > 0)
        {
            _ = RescanDocumentsAsync(affected, _lifetime.Token);
        }
    }

    private async Task RescanDocumentsAsync(IReadOnlyList<(ProjectTests Project, string Uri)> documents, CancellationToken cancellationToken)
    {
        try
        {
            var found = await _api.Workspace.FindTestCandidatesAsync([.. documents.Select(d => d.Uri).Distinct()], cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                foreach (var document in found)
                {
                    foreach (var (project, _) in documents.Where(d => d.Uri == document.Uri))
                    {
                        project.Candidates[document.Uri] = document.Candidates;
                    }
                }

                _tree.Update(_projects.Values.ToList());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"Rescanning changed documents failed: {ex.Message}");
        }
    }

    /// <summary>Reads the solution, keeps its test projects, and finds their tests in source.</summary>
    internal async Task ScanAsync(CancellationToken cancellationToken)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var solution = await _api.Workspace.GetSolutionAsync(cancellationToken).ConfigureAwait(false);
            var platformSetting = _context.Settings.GetValue("testing.platform", "auto");
            var frameworkSetting = _context.Settings.GetValue("testing.targetFramework", string.Empty);
            var testProjects = solution?.Projects.Where(p => p.IsTestProject).ToList() ?? [];
            var uris = new Dictionary<string, List<ProjectTests>>(StringComparer.OrdinalIgnoreCase);
            lock (_gate)
            {
                _solution = solution;
                foreach (var stale in _projects.Keys.Where(k => testProjects.All(p => !string.Equals(p.Path, k, StringComparison.OrdinalIgnoreCase))).ToList())
                {
                    _projects.Remove(stale);
                }

                foreach (var project in testProjects)
                {
                    if (!_projects.TryGetValue(project.Path, out var tests))
                    {
                        _projects[project.Path] = tests = new ProjectTests(project.Path, project.Name);
                    }

                    tests.Name = project.Name;
                    tests.Platform = platformSetting switch
                    {
                        "vstest" => "vstest",
                        "mtp" when project.TestPlatform == "mtp" => "mtp",
                        _ => project.TestPlatform ?? "vstest",
                    };
                    tests.Frameworks = project.TargetFrameworks.Count <= 1
                        ? project.TargetFrameworks
                        : frameworkSetting.Length > 0 && project.TargetFrameworks.Contains(frameworkSetting, StringComparer.OrdinalIgnoreCase)
                            ? [frameworkSetting]
                            : project.TargetFrameworks;
                    var files = project.CompileFiles.Where(f => !f.Contains("/obj/", StringComparison.OrdinalIgnoreCase) && !f.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase))
                        .Select(f => new Uri(f).AbsoluteUri).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var removed in tests.Candidates.Keys.Where(k => !files.Contains(k)).ToList())
                    {
                        tests.Candidates.Remove(removed);
                    }

                    foreach (var uri in files)
                    {
                        if (!uris.TryGetValue(uri, out var owners))
                        {
                            uris[uri] = owners = [];
                        }

                        owners.Add(tests);
                    }
                }
            }

            if (uris.Count > 0)
            {
                var found = await _api.Workspace.FindTestCandidatesAsync([.. uris.Keys], cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    foreach (var document in found)
                    {
                        foreach (var project in uris.GetValueOrDefault(document.Uri) ?? [])
                        {
                            project.Candidates[document.Uri] = document.Candidates;
                        }
                    }
                }
            }

            lock (_gate)
            {
                _tree.Update(_projects.Values.ToList());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"Scanning for tests failed: {ex}");
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _discoveredBuilds.Clear();
        }

        await ScanAsync(cancellationToken).ConfigureAwait(false);
        var projects = Projects(null);
        if (projects.Count > 0 && await BuildAsync(projects, null, cancellationToken).ConfigureAwait(false))
        {
            await DiscoverAsync(await TargetsAsync(projects, cancellationToken).ConfigureAwait(false), RunOptions(RunMode.Run, null), cancellationToken).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ after builds

    private void OnBuildFinished(object? sender, BuildFinishedEventArgs e)
    {
        if (!e.Result.Succeeded || e.Target == "clean")
        {
            return;
        }

        var built = Projects(null).Where(p => e.Project is null || string.Equals(p.Path, e.Project, StringComparison.OrdinalIgnoreCase)).ToList();
        if (built.Count == 0)
        {
            return;
        }

        if (_context.Settings.GetValue("testing.runAfterBuild", false))
        {
            _ = RunAfterBuildAsync(built, _lifetime.Token);
        }
        else if (_context.Settings.GetValue("testing.discoverAfterBuild", true))
        {
            _ = DiscoverQuietlyAsync(built, _lifetime.Token);
        }
    }

    private async Task DiscoverQuietlyAsync(List<ProjectTests> projects, CancellationToken cancellationToken)
    {
        try
        {
            await DiscoverAsync(await TargetsAsync(projects, cancellationToken).ConfigureAwait(false), RunOptions(RunMode.Run, null), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"Test discovery after the build failed: {ex.Message}");
        }
    }

    private async Task RunAfterBuildAsync(List<ProjectTests> projects, CancellationToken cancellationToken)
    {
        var items = projects.Select(p => _controller.Find(p.Path)).OfType<TestItem>().ToList();
        await RunAsync(new TestRunRequest(items, [], null), RunMode.Run, cancellationToken, build: false).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ running

    private List<ProjectTests> Projects(IReadOnlyList<TestItem>? include)
    {
        lock (_gate)
        {
            if (include is null)
            {
                return [.. _projects.Values];
            }

            var paths = include.Select(i => _tree.Info(i.Id)?.Project).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            return [.. _projects.Values.Where(p => paths.Contains(p.Path))];
        }
    }

    private RunOptions RunOptions(RunMode mode, string? coverageDirectory)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in _context.Settings.GetValue<string[]>("testing.environment", []))
        {
            var equals = entry.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                environment[entry[..equals].Trim()] = entry[(equals + 1)..];
            }
        }

        string? runSettings = null;
        if (_context.Settings.GetValue("testing.runSettingsFile", string.Empty) is { Length: > 0 } file)
        {
            var folder = _solution is { } s ? Path.GetDirectoryName(s.Path)! : Environment.CurrentDirectory;
            runSettings = Path.GetFullPath(Path.Combine(folder, file));
        }

        return new RunOptions(mode, environment, runSettings, _context.Settings.GetValue("testing.architecture", string.Empty), _context.Settings.GetValue("testing.parallel", true))
        {
            Coverage = coverageDirectory is null ? null : new CoverageOptions(
                coverageDirectory,
                _context.Settings.GetValue<string[]>("coverage.include", []),
                _context.Settings.GetValue<string[]>("coverage.exclude", [])),
        };
    }

    private async Task<bool> BuildAsync(List<ProjectTests> projects, ITestRun? run, CancellationToken cancellationToken)
    {
        var result = await _api.Build.BuildAsync(projects.Count == 1 ? [projects[0].Path] : null, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            run?.AppendOutput(result.Canceled ? "The build was cancelled.\n" : $"The build failed with {result.Errors} error(s); see the Error List.\n");
        }

        return result.Succeeded;
    }

    private async Task<List<TestTarget>> TargetsAsync(List<ProjectTests> projects, CancellationToken cancellationToken)
    {
        var targets = new List<TestTarget>();
        foreach (var project in projects)
        {
            List<string?> frameworks = project.Frameworks.Count > 1 ? [.. project.Frameworks] : [null];
            foreach (var framework in frameworks)
            {
                var output = await _api.Workspace.GetProjectOutputAsync(project.Path, framework, cancellationToken).ConfigureAwait(false);
                if (output.TargetPath is not { } assembly)
                {
                    continue;
                }

                targets.Add(new TestTarget(
                    project.Path,
                    project.Name,
                    project.Platform ?? "vstest",
                    project.Frameworks.Count > 1 ? output.TargetFramework : null,
                    assembly,
                    output.RunCommand,
                    output.RunArguments,
                    output.RunWorkingDirectory is { Length: > 0 } cwd ? cwd : Path.GetDirectoryName(assembly)!)
                {
                    Framework = output.TargetFramework,
                });
            }
        }

        return targets;
    }

    /// <summary>Asks the runners for the tests of targets whose assembly changed since they were last asked.</summary>
    private async Task DiscoverAsync(IReadOnlyList<TestTarget> targets, RunOptions options, CancellationToken cancellationToken)
    {
        var stale = new List<TestTarget>();
        lock (_gate)
        {
            foreach (var target in targets)
            {
                var stamp = File.Exists(target.AssemblyPath) ? File.GetLastWriteTimeUtc(target.AssemblyPath) : DateTime.MinValue;
                if (stamp == DateTime.MinValue || _discoveredBuilds.GetValueOrDefault(target.AssemblyPath) != stamp)
                {
                    stale.Add(target);
                }
            }
        }

        if (stale.Count == 0)
        {
            return;
        }

        var vstest = stale.Where(t => t.Platform != "mtp").ToList();
        var results = new List<(TestTarget Target, IReadOnlyList<RunnerTest>? Tests, string? Error)>();
        if (vstest.Count > 0 && _solution?.SdkPath is { } sdk)
        {
            try
            {
                var tests = await _vstest.DiscoverAsync(sdk, vstest, options, cancellationToken).ConfigureAwait(false);
                results.AddRange(vstest.Select(t => (t, (IReadOnlyList<RunnerTest>?)tests.Where(x => x.Project == t.Project && x.TargetFramework == t.TargetFramework).ToList(), (string?)null)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.AddRange(vstest.Select(t => (t, (IReadOnlyList<RunnerTest>?)null, (string?)("Discovery failed: " + ex.Message))));
            }
        }

        await Task.WhenAll(stale.Where(t => t.Platform == "mtp").Select(async target =>
        {
            try
            {
                var tests = await _mtp.DiscoverAsync(target, options, cancellationToken).ConfigureAwait(false);
                lock (results)
                {
                    results.Add((target, tests, null));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (results)
                {
                    results.Add((target, null, "Discovery failed: " + ex.Message));
                }
            }
        })).ConfigureAwait(false);

        lock (_gate)
        {
            foreach (var (target, tests, error) in results)
            {
                if (!_projects.TryGetValue(target.Project, out var project))
                {
                    continue;
                }

                project.Error = error;
                if (tests is not null)
                {
                    project.RunnerTests[target.TargetFramework ?? string.Empty] = tests;
                    _discoveredBuilds[target.AssemblyPath] = File.GetLastWriteTimeUtc(target.AssemblyPath);
                }
                else
                {
                    Log($"{project.Name}: {error}");
                }
            }

            _tree.Update(_projects.Values.ToList());
        }
    }

    internal async Task RunAsync(TestRunRequest request, RunMode mode, CancellationToken cancellationToken, bool build = true)
    {
        var projects = Projects(request.Include);
        if (projects.Count == 0)
        {
            return;
        }

        var run = _controller.CreateRun(request, mode switch { RunMode.Debug => "Debug", RunMode.Coverage => "Coverage", _ => "Run" });
        var coverageFiles = new List<string>();
        string? coverageDirectory = null;
        try
        {
            if (build && !await BuildAsync(projects, run, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (mode == RunMode.Coverage)
            {
                coverageDirectory = Path.Combine(Path.GetTempPath(), "daedalus-coverage", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(coverageDirectory);
            }

            var options = RunOptions(mode, coverageDirectory);
            var targets = await TargetsAsync(projects, cancellationToken).ConfigureAwait(false);
            await DiscoverAsync(targets, RunOptions(RunMode.Run, null), cancellationToken).ConfigureAwait(false);

            // What to run: per target, the selected runner tests (null = all of the target).
            var include = request.Include?.Select(i => _tree.Info(i.Id)).OfType<ItemInfo>().ToList();
            var exclude = request.Exclude.Select(i => _tree.Info(i.Id)).OfType<ItemInfo>().ToList();
            var selection = new Dictionary<TestTarget, IReadOnlyList<RunnerTest>?>();
            lock (_gate)
            {
                foreach (var target in targets)
                {
                    var all = _projects.TryGetValue(target.Project, out var project) && project.RunnerTests.TryGetValue(target.TargetFramework ?? string.Empty, out var tests) ? tests : [];
                    var whole = exclude.Count == 0 && (include is null || include.Any(i => i.Kind == ItemKind.Project && i.Project == target.Project));
                    var chosen = whole ? all : all.Where(t => (include is null || include.Any(i => i.Contains(t))) && !exclude.Any(e => e.Contains(t))).ToList();
                    if (chosen.Count > 0)
                    {
                        selection[target] = whole ? null : chosen;
                    }

                    foreach (var test in chosen)
                    {
                        if (_tree.ItemFor(test) is { } item)
                        {
                            run.Enqueued(item);
                        }
                    }
                }
            }

            var reporters = new Dictionary<TestTarget, Reporter>();
            Reporter ReporterFor(TestTarget target)
            {
                lock (reporters)
                {
                    return reporters.TryGetValue(target, out var reporter) ? reporter : reporters[target] = new Reporter(this, run, coverageFiles);
                }
            }

            var vstest = selection.Where(s => s.Key.Platform != "mtp").ToList();
            if (vstest.Count > 0)
            {
                if (_solution?.SdkPath is not { } sdk)
                {
                    run.AppendOutput("The .NET SDK folder is not known yet; VSTest projects can't run.\n");
                }
                else
                {
                    var everything = vstest.All(s => s.Value is null);
                    var tests = everything ? null : vstest.SelectMany(s => s.Value ?? AllTests(s.Key)).ToList();
                    await _vstest.RunAsync(sdk, [.. vstest.Select(s => s.Key)], tests, options, ReporterFor, cancellationToken).ConfigureAwait(false);
                }
            }

            var mtp = selection.Where(s => s.Key.Platform == "mtp").ToList();
            if (mode == RunMode.Debug || !options.Parallel)
            {
                foreach (var (target, tests) in mtp)
                {
                    await RunMtpAsync(target, tests, options, ReporterFor(target), run, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await Task.WhenAll(mtp.Select(s => RunMtpAsync(s.Key, s.Value, options, ReporterFor(s.Key), run, cancellationToken))).ConfigureAwait(false);
            }

            if (coverageFiles.Count > 0)
            {
                run.AddCoverage(CoverageTools.Merge(coverageFiles));
                foreach (var file in coverageFiles.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    run.AddAttachment(new TestAttachment(new Uri(file).AbsoluteUri, "Coverage (" + Path.GetFileName(file) + ")"));
                }
            }
            else if (mode == RunMode.Coverage)
            {
                run.AppendOutput("No coverage was collected.\n");
            }
        }
        catch (OperationCanceledException)
        {
            run.AppendOutput("The run was cancelled.\n");
        }
        catch (Exception ex)
        {
            run.AppendOutput($"The run failed: {ex.Message}\n");
            Log(ex.ToString());
        }
        finally
        {
            run.Complete();
        }
    }

    private IReadOnlyList<RunnerTest> AllTests(TestTarget target)
    {
        lock (_gate)
        {
            return _projects.TryGetValue(target.Project, out var project) && project.RunnerTests.TryGetValue(target.TargetFramework ?? string.Empty, out var tests) ? tests : [];
        }
    }

    private async Task RunMtpAsync(TestTarget target, IReadOnlyList<RunnerTest>? tests, RunOptions options, IRunReporter reporter, ITestRun run, CancellationToken cancellationToken)
    {
        try
        {
            await _mtp.RunAsync(target, tests, options, reporter, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.AppendOutput($"{target.ProjectName}: {ex.Message}\n");
        }
    }

    /// <summary>Turns a runner's progress into run updates on the tree's items.</summary>
    private sealed class Reporter(DotNetTestPlugin plugin, ITestRun run, List<string> coverageFiles) : IRunReporter
    {
        public void Discovered(RunnerTest test)
        {
            lock (plugin._gate)
            {
                if (plugin._projects.TryGetValue(test.Project, out var project))
                {
                    var key = test.TargetFramework ?? string.Empty;
                    var existing = project.RunnerTests.GetValueOrDefault(key) ?? [];
                    if (existing.All(t => t.Key != test.Key))
                    {
                        project.RunnerTests[key] = [.. existing, test];
                        plugin._tree.Update(plugin._projects.Values.ToList());
                    }
                }
            }
        }

        public void Started(RunnerTest test)
        {
            if (plugin._tree.ItemFor(test) is { } item)
            {
                run.Started(item);
            }
        }

        public void Finished(RunnerTest test, RunnerOutcome outcome, TimeSpan? duration, IReadOnlyList<TestMessage> messages, string? output)
        {
            if (plugin._tree.ItemFor(test) is not { } item)
            {
                Discovered(test);
                if (plugin._tree.ItemFor(test) is not { } added)
                {
                    return;
                }

                item = added;
            }

            if (output is not null)
            {
                run.AppendOutput(output, item);
            }

            switch (outcome)
            {
                case RunnerOutcome.Passed:
                    run.Passed(item, duration);
                    break;
                case RunnerOutcome.Failed:
                    run.Failed(item, messages, duration);
                    break;
                case RunnerOutcome.Errored:
                    run.Errored(item, messages, duration);
                    break;
                default:
                    run.Skipped(item, messages.Count > 0 ? messages[0].Message : null);
                    break;
            }
        }

        public void Output(string text) => run.AppendOutput(text);

        public void CoverageFile(string path)
        {
            lock (coverageFiles)
            {
                coverageFiles.Add(path);
            }
        }
    }
}
