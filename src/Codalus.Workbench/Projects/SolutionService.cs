using System.Collections.Concurrent;
using Codalus.Projects.Protocol;
using Codalus.Rpc;

namespace Codalus.Workbench.Projects;

/// <summary>Locates <c>codalus-buildhost</c>: <c>CODALUS_BUILDHOST</c>, then <c>buildhost/</c> next to the shell.</summary>
public static class BuildHostLocator
{
    public const string EnvironmentVariable = "CODALUS_BUILDHOST";

    public static string? Resolve()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv)
        {
            candidates.Add(fromEnv);
        }

        var exe = OperatingSystem.IsWindows() ? "codalus-buildhost.exe" : "codalus-buildhost";
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "buildhost", exe));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "buildhost", "codalus-buildhost.dll"));
        return candidates.FirstOrDefault(File.Exists);
    }
}

public sealed class RestoreStateChangedEventArgs(string state, string? message) : EventArgs
{
    /// <summary><c>running</c>, <c>succeeded</c> or <c>failed</c>.</summary>
    public string State { get; } = state;

    public string? Message { get; } = message;
}

/// <summary>
/// The open solution, on the shell side (ADR 0004). Starts a build host per solution, keeps the latest structure
/// and evaluated projects, and offers file and solution operations. Events are raised on the host's thread.
/// A crashed host is restarted and the solution reopened.
/// </summary>
public sealed class SolutionService : IAsyncDisposable
{
    private static readonly TimeSpan s_operationTimeout = TimeSpan.FromMinutes(2);

    private readonly Func<string?> _resolveHost;
    private readonly Func<string, CancellationToken, Task<IReadOnlyDictionary<string, string>?>> _hostEnvironment;
    private readonly SupervisionPolicy? _policy;

    /// <summary>The build host's exit code when it finds no usable SDK (none, or not the one global.json asks for).</summary>
    public const int NoSdkExitCode = 3;

    /// <summary>The build host's last line of output: why it stopped, when it did.</summary>
    public string? LastHostMessage { get; private set; }
    private readonly ConcurrentDictionary<string, ProjectInfo> _projects = new(PathComparer);
    private readonly ConcurrentDictionary<string, CompilerInputsInfo> _compilerInputs = new(PathComparer);
    private readonly ConcurrentDictionary<string, string> _activeFrameworks = new(PathComparer);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SupervisedProcess? _host;

    /// <summary>
    /// The build host whose notifications the service takes: it changes when a solution opens and when it closes. A
    /// host that is shutting down still delivers what it sent before it stopped (the design-time build it finished
    /// first), and that belongs to the solution that closed.
    /// </summary>
    private int _hostGeneration;
    private SolutionInfo? _solution;
    private OpenSolutionParams? _openParams;

    /// <param name="hostEnvironment">The build host's environment for a solution folder; by default <see cref="BuildHostRuntime.EnvironmentAsync"/>.</param>
    public SolutionService(Func<string?> resolveHost, SupervisionPolicy? policy = null, Func<string, CancellationToken, Task<IReadOnlyDictionary<string, string>?>>? hostEnvironment = null)
    {
        _resolveHost = resolveHost;
        _policy = policy;
        _hostEnvironment = hostEnvironment ?? BuildHostRuntime.EnvironmentAsync;
    }

    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Where the build host runs: null here, or a remote window's server (M21, ADR 0050).</summary>
    public Func<ProcessLaunchInfo, ProcessLaunchInfo?>? Placement { get; set; }

    /// <summary>The open solution's structure, or null.</summary>
    public SolutionInfo? Solution => _solution;

    public bool IsOpen => _solution is not null;

    public IReadOnlyDictionary<string, ProjectInfo> Projects => _projects;

    public EvaluationProgressParams? Progress { get; private set; }

    /// <summary>Design-time build results by project path (the active target framework of each project).</summary>
    public IReadOnlyDictionary<string, CompilerInputsInfo> CompilerInputs => _compilerInputs;

    public DesignTimeProgressParams? DesignTimeProgress { get; private set; }

    public string? RestoreState { get; private set; }

    /// <summary>The build host's process id while it runs (diagnostics and tests).</summary>
    public int? HostProcessId => _host?.ProcessId;

    /// <summary>The .NET SDK folder the build host's MSBuild comes from, once it started.</summary>
    public string? SdkPath { get; private set; }

    /// <summary>Why the SDK the solution's folder resolves to isn't the one in use (ADR 0025), or null.</summary>
    public string? SdkWarning { get; private set; }

    /// <summary>A solution was opened, closed (null) or its structure changed.</summary>
    public event EventHandler? SolutionChanged;

    /// <summary>What a credential provider says during a package source sign-in (a device code); raised on any thread.</summary>
    public event EventHandler<PackageSignInMessage>? PackageSignInMessage;

    public event EventHandler<ProjectInfo>? ProjectChanged;

    public event EventHandler<EvaluationProgressParams>? ProgressChanged;

    /// <summary>A design-time build finished for a project (new compiler inputs for the language service).</summary>
    public event EventHandler<CompilerInputsInfo>? CompilerInputsChanged;

    public event EventHandler<DesignTimeProgressParams>? DesignTimeProgressChanged;

    public event EventHandler<string>? RestoreOutput;

    public event EventHandler<RestoreStateChangedEventArgs>? RestoreStateChanged;

    /// <summary>A line of a running build's output.</summary>
    public event EventHandler<string>? BuildOutput;

    /// <summary>An error or warning of a running build.</summary>
    public event EventHandler<BuildDiagnosticInfo>? BuildDiagnostic;

    /// <summary>Build host messages and process output, for the Output window.</summary>
    public event EventHandler<string>? Log;

    /// <summary>Folders whose set of files changed on disk (Show All Files and the folder view refresh; ADR 0033).</summary>
    public event EventHandler<IReadOnlyList<string>>? FilesChanged;
    /// <summary>A message of a <c>dotnet ef</c> command the build host runs (ADR 0036).</summary>
    public event EventHandler<EfOutputParams>? EfOutput;

    /// <summary>A line of a <c>dotnet aspnet-codegenerator</c> run the build host runs (ADR 0041).</summary>
    public event EventHandler<ScaffoldOutputParams>? ScaffoldOutput;

    /// <summary>A line of a <c>dotnet-svcutil</c> run the build host runs (ADR 0069).</summary>
    public event EventHandler<ScaffoldOutputParams>? WcfOutput;

    /// <summary>The build host crashed; <see cref="ProcessCrashedEventArgs.WillRestart"/> tells whether it comes back.</summary>
    public event EventHandler<ProcessCrashedEventArgs>? HostCrashed;

    public event EventHandler? HostRestarted;

    // ------------------------------------------------------------------ open and close

    /// <summary>
    /// Opens <paramref name="path"/> (<c>.sln</c>, <c>.slnx</c> or a project file) in a new build host. Untrusted
    /// solutions show their structure only. Returns the structure from the solution file; projects follow as
    /// <see cref="ProjectChanged"/> events. <paramref name="configuration"/> and <paramref name="platform"/> are used
    /// when the solution has them, and <paramref name="activeFrameworks"/> for the projects that still target them
    /// (the session kept with the solution, ADR 0070).
    /// </summary>
    public async Task<SolutionInfo> OpenAsync(string path, bool trusted, bool restore, CancellationToken cancellationToken, string? configuration = null, string? platform = null, IReadOnlyDictionary<string, string>? activeFrameworks = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
            foreach (var (project, framework) in activeFrameworks ?? new Dictionary<string, string>())
            {
                _activeFrameworks[project] = framework;
            }

            var full = Path.GetFullPath(path);
            var hostPath = _resolveHost() ?? throw new InvalidOperationException("The build host (codalus-buildhost) was not found.");
            _openParams = new OpenSolutionParams(full, trusted, configuration, platform, restore);
            var directory = await FileSystem.FileSystems.DirectoryExistsAsync(full, cancellationToken).ConfigureAwait(false) ? Path.TrimEndingDirectorySeparator(full) : Path.GetDirectoryName(full)!;

            // A remote window's build host runs on the server (M21): its SDK is the remote machine's.
            var environment = Placement is null ? await _hostEnvironment(directory, cancellationToken).ConfigureAwait(false) : null;
            if (environment is not null)
            {
                Log?.Invoke(this, "[info] The solution's SDK is newer than .NET " + BuildHostRuntime.HostMajor + ": the build host rolls forward (" + string.Join(", ", environment.Select(e => e.Key + "=" + e.Value)) + ").");
            }

            var launch = ProcessLaunchInfo.FromPath(hostPath, "--solution-dir", directory) with { Environment = environment };
            if (Placement is { } place)
            {
                launch = place(launch) ?? throw new InvalidOperationException("The build host can't run on the remote machine.");
            }

            var generation = Interlocked.Increment(ref _hostGeneration);
            var host = new SupervisedProcess("build host", launch, (_policy ?? SupervisionPolicy.Default) with { FinalExitCodes = [NoSdkExitCode] })
            {
                ConfigureConnection = connection => Configure(connection, generation),
                Initialize = InitializeAsync,
            };
            host.Crashed += (_, e) => HostCrashed?.Invoke(this, e);
            host.Restarted += (_, _) => HostRestarted?.Invoke(this, EventArgs.Empty);
            host.Log += (_, line) =>
            {
                if (!line.StartsWith("[" + host.Name + "]", StringComparison.Ordinal))
                {
                    LastHostMessage = line; // the host's own words, not the supervisor's summary of its exit (M49)
                }

                Log?.Invoke(this, line);
            };
            _host = host;
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
            return _solution!;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CloseCoreAsync()
    {
        var hadSolution = _solution is not null;
        Interlocked.Increment(ref _hostGeneration); // what the closing host still sends is dropped
        _solution = null;
        _openParams = null;
        _projects.Clear();
        _compilerInputs.Clear();
        _activeFrameworks.Clear();
        Progress = null;
        DesignTimeProgress = null;
        RestoreState = null;
        if (_host is { } host)
        {
            _host = null;
            await host.DisposeAsync().ConfigureAwait(false);
        }

        if (hadSolution)
        {
            SolutionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Configure(JsonRpcConnection connection, int generation)
    {
        var json = ProjectJsonContext.Default;

        // The solution's state comes from the open solution's host only. A closed solution's host finishes the
        // design-time build it was running before it answers the shutdown, and sends the results: taken, they brought a
        // closed folder's projects back after the close (the smoke test's EF step after the folder step, on macOS).
        bool Current() => Volatile.Read(ref _hostGeneration) == generation;
        connection.OnNotification(ProjectMethods.SolutionChanged, json.SolutionInfo, solution =>
        {
            if (!Current())
            {
                return;
            }

            _solution = solution;
            foreach (var removed in _projects.Keys.Where(k => !solution.Projects.Any(p => PathComparer.Equals(p.Path, k))).ToList())
            {
                _projects.TryRemove(removed, out _);
                _compilerInputs.TryRemove(removed, out _);
            }

            SolutionChanged?.Invoke(this, EventArgs.Empty);
        });
        connection.OnNotification(ProjectMethods.ProjectEvaluated, json.ProjectInfo, project =>
        {
            if (!Current())
            {
                return;
            }

            _projects[project.Path] = project;
            ProjectChanged?.Invoke(this, project);
        });
        connection.OnNotification(ProjectMethods.EvaluationProgress, json.EvaluationProgressParams, progress =>
        {
            if (!Current())
            {
                return;
            }

            Progress = progress;
            ProgressChanged?.Invoke(this, progress);
        });
        connection.OnNotification(ProjectMethods.CompilerInputs, json.CompilerInputsInfo, inputs =>
        {
            if (!Current())
            {
                return;
            }

            _compilerInputs[inputs.Path] = inputs;
            CompilerInputsChanged?.Invoke(this, inputs);
        });
        connection.OnNotification(ProjectMethods.DesignTimeProgress, json.DesignTimeProgressParams, progress =>
        {
            if (!Current())
            {
                return;
            }

            DesignTimeProgress = progress;
            DesignTimeProgressChanged?.Invoke(this, progress);
        });
        connection.OnNotification(ProjectMethods.RestoreOutput, json.RestoreOutputParams, p => RestoreOutput?.Invoke(this, p.Line));
        connection.OnNotification(ProjectMethods.RestoreState, json.RestoreStateParams, p =>
        {
            if (!Current())
            {
                return;
            }

            RestoreState = p.State;
            RestoreStateChanged?.Invoke(this, new RestoreStateChangedEventArgs(p.State, p.Message));
        });
        connection.OnNotification(ProjectMethods.Log, json.LogParams, p => Log?.Invoke(this, $"[{p.Level}] {p.Message}"));
        connection.OnNotification(ProjectMethods.BuildOutput, json.BuildOutputParams, p => BuildOutput?.Invoke(this, p.Line));
        connection.OnNotification(ProjectMethods.BuildDiagnostic, json.BuildDiagnosticInfo, d => BuildDiagnostic?.Invoke(this, d));
        connection.OnNotification(ItemMethods.FilesChanged, ItemJsonContext.Default.FilesChangedParams, p => FilesChanged?.Invoke(this, p.Directories));
        connection.OnNotification(EfMethods.Output, EntityFrameworkJsonContext.Default.EfOutputParams, o => EfOutput?.Invoke(this, o));
        connection.OnNotification(ScaffoldMethods.Output, WebToolsJsonContext.Default.ScaffoldOutputParams, o => ScaffoldOutput?.Invoke(this, o));
        connection.OnNotification(WcfReferenceMethods.Output, WcfJsonContext.Default.ScaffoldOutputParams, o => WcfOutput?.Invoke(this, o));
        connection.OnNotification(PackageMethods.SignInMessage, PackageJsonContext.Default.PackageSignInMessage, m => PackageSignInMessage?.Invoke(this, m));
    }

    /// <summary>Runs for the first start and after every restart: (re)opens the solution with the latest settings.</summary>
    private async Task InitializeAsync(JsonRpcConnection connection, bool isRestart, CancellationToken cancellationToken)
    {
        var json = ProjectJsonContext.Default;
        var host = await connection.InvokeAsync(
            ProjectMethods.Initialize, new InitializeParams(Environment.ProcessId, "codalus", ProjectMethods.ProtocolVersion),
            json.InitializeParams, json.InitializeResult, cancellationToken).ConfigureAwait(false);
        SdkPath = host.SdkPath;
        SdkWarning = host.SdkWarning;
        if (host.SdkWarning is { } warning && !isRestart)
        {
            Log?.Invoke(this, "[warning] " + warning);
        }

        if (_openParams is not { } open)
        {
            return;
        }

        if (isRestart && _solution is { } previous)
        {
            // Keep the user's choices across the restart, but don't restore again.
            open = open with { Trusted = previous.Trusted, Configuration = previous.Active?.Configuration, Platform = previous.Active?.Platform, Restore = false };
        }

        open = open with { ActiveFrameworks = _activeFrameworks.IsEmpty ? null : new Dictionary<string, string>(_activeFrameworks) };
        _projects.Clear();
        _compilerInputs.Clear();
        _solution = await connection.InvokeAsync(ProjectMethods.OpenSolution, open, json.OpenSolutionParams, json.SolutionInfo, cancellationToken).ConfigureAwait(false);
        SolutionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ solution-wide operations

    public async Task TrustAsync(bool restore, CancellationToken cancellationToken)
    {
        await InvokeAsync(ProjectMethods.Trust, new TrustParams(restore), ProjectJsonContext.Default.TrustParams, cancellationToken).ConfigureAwait(false);
        _solution = _solution! with { Trusted = true };
        _openParams = _openParams! with { Trusted = true };
        SolutionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetConfigurationAsync(SolutionConfigurationInfo configuration, CancellationToken cancellationToken)
    {
        await InvokeAsync(ProjectMethods.SetConfiguration, new SetConfigurationParams(configuration.Configuration, configuration.Platform), ProjectJsonContext.Default.SetConfigurationParams, cancellationToken).ConfigureAwait(false);
        _solution = _solution! with { Active = configuration };
        SolutionChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task RestoreAsync(string? path, CancellationToken cancellationToken) =>
        InvokeAsync(ProjectMethods.Restore, new RestoreParams(path), ProjectJsonContext.Default.RestoreParams, cancellationToken);

    /// <summary>The target framework the language service sees for a multi-targeted project.</summary>
    public string? ActiveFramework(string projectPath)
    {
        var frameworks = _projects.TryGetValue(projectPath, out var project) ? project.TargetFrameworks : [];
        return _activeFrameworks.TryGetValue(projectPath, out var framework) && (frameworks.Length == 0 || frameworks.Contains(framework, StringComparer.OrdinalIgnoreCase))
            ? framework // a kept choice the project no longer targets falls back to its first framework
            : frameworks.FirstOrDefault();
    }

    /// <summary>The target frameworks chosen for multi-targeted projects (kept with the solution).</summary>
    public IReadOnlyDictionary<string, string> ActiveFrameworks => new Dictionary<string, string>(_activeFrameworks, PathComparer);

    public async Task SetActiveFrameworkAsync(string projectPath, string targetFramework, CancellationToken cancellationToken)
    {
        await InvokeAsync(ProjectMethods.SetActiveFramework, new SetActiveFrameworkParams(projectPath, targetFramework), ProjectJsonContext.Default.SetActiveFrameworkParams, cancellationToken).ConfigureAwait(false);
        _activeFrameworks[projectPath] = targetFramework;
    }

    /// <summary>Runs design-time builds again (one project, or every project when null).</summary>
    public Task DesignTimeBuildAsync(string? projectPath, CancellationToken cancellationToken) =>
        InvokeAsync(ProjectMethods.DesignTimeBuild, new DesignTimeBuildParams(projectPath), ProjectJsonContext.Default.DesignTimeBuildParams, cancellationToken);

    /// <summary>
    /// Builds the solution (<paramref name="projectPath"/> null) or a project, and completes when the build has finished:
    /// after every <see cref="BuildOutput"/> and <see cref="BuildDiagnostic"/> of it. Cancelling the token asks the
    /// host to stop the build; the result then reports <see cref="BuildResultInfo.Canceled"/>.
    /// </summary>
    public async Task<BuildResultInfo> BuildAsync(string target, string? projectPath, CancellationToken cancellationToken, BuildOptions? options = null)
    {
        var connection = Connection;
        await using var registration = cancellationToken.Register(() => _ = CancelBuildAsync(connection)).ConfigureAwait(false);
        var result = await connection.InvokeAsync(
            ProjectMethods.Build, new BuildParams(target, projectPath, Options: options), ProjectJsonContext.Default.BuildParams, ProjectJsonContext.Default.BuildResultInfo, CancellationToken.None).ConfigureAwait(false);

        // The host sends the build's output and diagnostics before its result, but the result completes this call as
        // soon as it is read: wait until they have been raised (a failed build's errors were missing: Windows CI).
        await connection.WhenDispatchedAsync().ConfigureAwait(false);
        return result;
    }

    /// <summary>Batch Build (ADR 0034): each entry in turn, output and diagnostics as for a build; cancellable the same way.</summary>
    public async Task<BatchBuildResult> BatchBuildAsync(BatchBuildParams parameters, CancellationToken cancellationToken)
    {
        var connection = Connection;
        await using var registration = cancellationToken.Register(() => _ = CancelBuildAsync(connection)).ConfigureAwait(false);
        var result = await connection.InvokeAsync(
            BuildDepthMethods.Batch, parameters, BuildDepthJsonContext.Default.BatchBuildParams, BuildDepthJsonContext.Default.BatchBuildResult, CancellationToken.None).ConfigureAwait(false);
        await connection.WhenDispatchedAsync().ConfigureAwait(false); // its output and diagnostics first, as for a build
        return result;
    }

    private static async Task CancelBuildAsync(JsonRpcConnection connection)
    {
        try
        {
            await connection.InvokeAsync(ProjectMethods.CancelBuild, new EmptyParams(), ProjectJsonContext.Default.EmptyParams, ProjectJsonContext.Default.EmptyResult, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonRpcConnectionLostException or JsonRpcRemoteException or ObjectDisposedException)
        {
            // The build ends with the host.
        }
    }

    /// <summary>
    /// How to start a project (the SDK's run properties and its launch settings file), for <paramref name="targetFramework"/>
    /// or the active framework.
    /// </summary>
    public async Task<RunInfo> GetRunInfoAsync(string projectPath, CancellationToken cancellationToken, string? targetFramework = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(s_operationTimeout);
        return await Connection.InvokeAsync(ProjectMethods.RunInfo, new RunInfoParams(projectPath, targetFramework), ProjectJsonContext.Default.RunInfoParams, ProjectJsonContext.Default.RunInfo, timeout.Token).ConfigureAwait(false);
    }

    public Task ReevaluateAsync(string projectPath, CancellationToken cancellationToken) =>
        InvokeAsync(ProjectMethods.Reevaluate, new ProjectPathParams(projectPath), ProjectJsonContext.Default.ProjectPathParams, cancellationToken);

    // ------------------------------------------------------------------ file operations

    public async Task<IReadOnlyList<DirectoryEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken) =>
        (await Connection.InvokeAsync(ProjectMethods.ListDirectory, new ListDirectoryParams(path), ProjectJsonContext.Default.ListDirectoryParams, ProjectJsonContext.Default.ListDirectoryResult, cancellationToken).ConfigureAwait(false)).Entries;

    public Task<FileOperationResult> CreateFileAsync(string path, string? contents, CancellationToken cancellationToken) =>
        FileOperationAsync(ProjectMethods.CreateFile, new CreateFileParams(path, contents), ProjectJsonContext.Default.CreateFileParams, cancellationToken);

    public Task<FileOperationResult> CreateFolderAsync(string path, CancellationToken cancellationToken) =>
        FileOperationAsync(ProjectMethods.CreateFolder, new CreateFolderParams(path), ProjectJsonContext.Default.CreateFolderParams, cancellationToken);

    public Task<FileOperationResult> RenameAsync(string oldPath, string newPath, CancellationToken cancellationToken) =>
        FileOperationAsync(ProjectMethods.Rename, new RenameParams(oldPath, newPath), ProjectJsonContext.Default.RenameParams, cancellationToken);

    public Task<FileOperationResult> MoveAsync(IReadOnlyList<string> sources, string targetFolder, bool copy, CancellationToken cancellationToken) =>
        FileOperationAsync(ProjectMethods.Move, new MoveParams([.. sources], targetFolder, copy), ProjectJsonContext.Default.MoveParams, cancellationToken);

    /// <summary>Moves <paramref name="paths"/> to the OS trash and updates project and solution files.</summary>
    public Task<FileOperationResult> DeleteAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
        FileOperationAsync(ProjectMethods.Delete, new DeleteParams([.. paths]), ProjectJsonContext.Default.DeleteParams, cancellationToken);

    // ------------------------------------------------------------------ solution edits

    public Task AddProjectAsync(string projectPath, string? solutionFolder, CancellationToken cancellationToken) =>
        InvokeAsync(ProjectMethods.AddProject, new AddProjectParams(projectPath, solutionFolder), ProjectJsonContext.Default.AddProjectParams, cancellationToken);

    public Task RemoveProjectAsync(string projectPath, CancellationToken cancellationToken) =>
        InvokeAsync(ProjectMethods.RemoveProject, new ProjectPathParams(projectPath), ProjectJsonContext.Default.ProjectPathParams, cancellationToken);

    public Task AddSolutionFolderAsync(string name, string? parent, CancellationToken cancellationToken) =>
        InvokeAsync(ProjectMethods.AddSolutionFolder, new SolutionFolderParams(name, parent), ProjectJsonContext.Default.SolutionFolderParams, cancellationToken);

    public Task RenameSolutionFolderAsync(string id, string newName, CancellationToken cancellationToken) =>
        InvokeAsync(ProjectMethods.RenameSolutionFolder, new RenameSolutionFolderParams(id, newName), ProjectJsonContext.Default.RenameSolutionFolderParams, cancellationToken);

    public Task RemoveSolutionFolderAsync(string id, CancellationToken cancellationToken) =>
        InvokeAsync(ProjectMethods.RemoveSolutionFolder, new RemoveSolutionFolderParams(id), ProjectJsonContext.Default.RemoveSolutionFolderParams, cancellationToken);

    public Task MoveInSolutionAsync(string item, string? targetFolder, CancellationToken cancellationToken) =>
        InvokeAsync(ProjectMethods.MoveInSolution, new MoveInSolutionParams(item, targetFolder), ProjectJsonContext.Default.MoveInSolutionParams, cancellationToken);

    public Task AddProjectReferenceAsync(string projectPath, string referencePath, CancellationToken cancellationToken) =>
        InvokeAsync(ProjectMethods.AddProjectReference, new ProjectReferenceParams(projectPath, referencePath), ProjectJsonContext.Default.ProjectReferenceParams, cancellationToken);

    public Task RemoveProjectReferenceAsync(string projectPath, string referencePath, CancellationToken cancellationToken) =>
        InvokeAsync(ProjectMethods.RemoveProjectReference, new ProjectReferenceParams(projectPath, referencePath), ProjectJsonContext.Default.ProjectReferenceParams, cancellationToken);

    // ------------------------------------------------------------------ plumbing

    private JsonRpcConnection Connection => _host is { IsRunning: true } host
        ? host.Connection
        : throw new InvalidOperationException(_host is null ? "No solution is open." : "The build host is not running.");

    /// <summary>
    /// Sends a request to the build host, for the services built on its connection (packages, project properties…).
    /// It completes after the notifications the host sent before its result (a tool's output: Entity Framework,
    /// scaffolding, WCF), and times out after <paramref name="timeout"/> (default two minutes).
    /// </summary>
    public async Task<TResult> RequestAsync<TParams, TResult>(
        string method,
        TParams parameters,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TParams> parametersInfo,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultInfo,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? s_operationTimeout);
        var connection = Connection;
        var result = await connection.InvokeAsync(method, parameters, parametersInfo, resultInfo, limit.Token).ConfigureAwait(false);
        await connection.WhenDispatchedAsync().ConfigureAwait(false); // as for a build: the output sent before the result first
        return result;
    }

    private async Task InvokeAsync<T>(string method, T parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(s_operationTimeout);
        await Connection.InvokeAsync(method, parameters, info, ProjectJsonContext.Default.EmptyResult, timeout.Token).ConfigureAwait(false);
    }

    private async Task<FileOperationResult> FileOperationAsync<T>(string method, T parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(s_operationTimeout);
        return await Connection.InvokeAsync(method, parameters, info, ProjectJsonContext.Default.FileOperationResult, timeout.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
