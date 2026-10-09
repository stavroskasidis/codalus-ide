using System.Collections.Concurrent;
using Daedalus.Projects.Protocol;
using Daedalus.Rpc;

namespace Daedalus.Workbench.Projects;

/// <summary>Locates <c>daedalus-buildhost</c>: <c>DAEDALUS_BUILDHOST</c>, then <c>buildhost/</c> next to the shell.</summary>
public static class BuildHostLocator
{
    public const string EnvironmentVariable = "DAEDALUS_BUILDHOST";

    public static string? Resolve()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv)
        {
            candidates.Add(fromEnv);
        }

        var exe = OperatingSystem.IsWindows() ? "daedalus-buildhost.exe" : "daedalus-buildhost";
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "buildhost", exe));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "buildhost", "daedalus-buildhost.dll"));
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
    private readonly SupervisionPolicy? _policy;
    private readonly ConcurrentDictionary<string, ProjectInfo> _projects = new(PathComparer);
    private readonly ConcurrentDictionary<string, CompilerInputsInfo> _compilerInputs = new(PathComparer);
    private readonly ConcurrentDictionary<string, string> _activeFrameworks = new(PathComparer);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SupervisedProcess? _host;
    private SolutionInfo? _solution;
    private OpenSolutionParams? _openParams;

    public SolutionService(Func<string?> resolveHost, SupervisionPolicy? policy = null)
    {
        _resolveHost = resolveHost;
        _policy = policy;
    }

    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

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

    /// <summary>A solution was opened, closed (null) or its structure changed.</summary>
    public event EventHandler? SolutionChanged;

    public event EventHandler<ProjectInfo>? ProjectChanged;

    public event EventHandler<EvaluationProgressParams>? ProgressChanged;

    /// <summary>A design-time build finished for a project (new compiler inputs for the language service).</summary>
    public event EventHandler<CompilerInputsInfo>? CompilerInputsChanged;

    public event EventHandler<DesignTimeProgressParams>? DesignTimeProgressChanged;

    public event EventHandler<string>? RestoreOutput;

    public event EventHandler<RestoreStateChangedEventArgs>? RestoreStateChanged;

    /// <summary>Build host messages and process output, for the Output window.</summary>
    public event EventHandler<string>? Log;

    /// <summary>The build host crashed; <see cref="ProcessCrashedEventArgs.WillRestart"/> tells whether it comes back.</summary>
    public event EventHandler<ProcessCrashedEventArgs>? HostCrashed;

    public event EventHandler? HostRestarted;

    // ------------------------------------------------------------------ open and close

    /// <summary>
    /// Opens <paramref name="path"/> (<c>.sln</c>, <c>.slnx</c> or a project file) in a new build host. Untrusted
    /// solutions show their structure only. Returns the structure from the solution file; projects follow as
    /// <see cref="ProjectChanged"/> events.
    /// </summary>
    public async Task<SolutionInfo> OpenAsync(string path, bool trusted, bool restore, CancellationToken cancellationToken, string? configuration = null, string? platform = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
            var full = Path.GetFullPath(path);
            var hostPath = _resolveHost() ?? throw new InvalidOperationException("The build host (daedalus-buildhost) was not found.");
            _openParams = new OpenSolutionParams(full, trusted, configuration, platform, restore);
            var host = new SupervisedProcess("build host", ProcessLaunchInfo.FromPath(hostPath, "--solution-dir", Path.GetDirectoryName(full)!), _policy)
            {
                ConfigureConnection = Configure,
                Initialize = InitializeAsync,
            };
            host.Crashed += (_, e) => HostCrashed?.Invoke(this, e);
            host.Restarted += (_, _) => HostRestarted?.Invoke(this, EventArgs.Empty);
            host.Log += (_, line) => Log?.Invoke(this, line);
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

    private void Configure(JsonRpcConnection connection)
    {
        var json = ProjectJsonContext.Default;
        connection.OnNotification(ProjectMethods.SolutionChanged, json.SolutionInfo, solution =>
        {
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
            _projects[project.Path] = project;
            ProjectChanged?.Invoke(this, project);
        });
        connection.OnNotification(ProjectMethods.EvaluationProgress, json.EvaluationProgressParams, progress =>
        {
            Progress = progress;
            ProgressChanged?.Invoke(this, progress);
        });
        connection.OnNotification(ProjectMethods.CompilerInputs, json.CompilerInputsInfo, inputs =>
        {
            _compilerInputs[inputs.Path] = inputs;
            CompilerInputsChanged?.Invoke(this, inputs);
        });
        connection.OnNotification(ProjectMethods.DesignTimeProgress, json.DesignTimeProgressParams, progress =>
        {
            DesignTimeProgress = progress;
            DesignTimeProgressChanged?.Invoke(this, progress);
        });
        connection.OnNotification(ProjectMethods.RestoreOutput, json.RestoreOutputParams, p => RestoreOutput?.Invoke(this, p.Line));
        connection.OnNotification(ProjectMethods.RestoreState, json.RestoreStateParams, p =>
        {
            RestoreState = p.State;
            RestoreStateChanged?.Invoke(this, new RestoreStateChangedEventArgs(p.State, p.Message));
        });
        connection.OnNotification(ProjectMethods.Log, json.LogParams, p => Log?.Invoke(this, $"[{p.Level}] {p.Message}"));
    }

    /// <summary>Runs for the first start and after every restart: (re)opens the solution with the latest settings.</summary>
    private async Task InitializeAsync(JsonRpcConnection connection, bool isRestart, CancellationToken cancellationToken)
    {
        var json = ProjectJsonContext.Default;
        await connection.InvokeAsync(
            ProjectMethods.Initialize, new InitializeParams(Environment.ProcessId, "daedalus", ProjectMethods.ProtocolVersion),
            json.InitializeParams, json.InitializeResult, cancellationToken).ConfigureAwait(false);
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
    public string? ActiveFramework(string projectPath) =>
        _activeFrameworks.TryGetValue(projectPath, out var framework) ? framework
        : _projects.TryGetValue(projectPath, out var project) ? project.TargetFrameworks.FirstOrDefault() : null;

    public async Task SetActiveFrameworkAsync(string projectPath, string targetFramework, CancellationToken cancellationToken)
    {
        await InvokeAsync(ProjectMethods.SetActiveFramework, new SetActiveFrameworkParams(projectPath, targetFramework), ProjectJsonContext.Default.SetActiveFrameworkParams, cancellationToken).ConfigureAwait(false);
        _activeFrameworks[projectPath] = targetFramework;
    }

    /// <summary>Runs design-time builds again (one project, or every project when null).</summary>
    public Task DesignTimeBuildAsync(string? projectPath, CancellationToken cancellationToken) =>
        InvokeAsync(ProjectMethods.DesignTimeBuild, new DesignTimeBuildParams(projectPath), ProjectJsonContext.Default.DesignTimeBuildParams, cancellationToken);

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
