using System.Collections.Concurrent;
using DaedalusStudio.Plugins.Proposed;
using DaedalusStudio.Plugins.Protocol;

namespace DaedalusStudio.Plugins.Host.Proposed;

/// <summary>
/// The host-wide side of the proposed APIs: the test registry, and the shell's workspace, build and debug
/// notifications, fanned out to each plugin's facade.
/// </summary>
internal sealed class ProposedHost : IDisposable
{
    private readonly PluginHostServer _server;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<int?>> _debugSessions = new(StringComparer.Ordinal);

    public ProposedHost(PluginHostServer server)
    {
        _server = server;
        Tests = new HostTestRegistry(server);
    }

    public HostTestRegistry Tests { get; }

    public event EventHandler? SolutionChanged;

    public event EventHandler<DocumentsChangedEventArgs>? DocumentsChanged;

    public event EventHandler<BuildFinishedEventArgs>? BuildFinished;

    public void RegisterHandlers()
    {
        var json = PluginJsonContext.Default;
        var connection = _server.Connection;
        connection.OnRequest(ProposedMethods.TestsRun, json.TestRunParams, json.TestRunResultDto, async (p, ct) =>
            new TestRunResultDto([.. await Tests.RunAsync(p, ct).ConfigureAwait(false)]));
        connection.OnRequest(ProposedMethods.TestsRefresh, json.TestControllerIdParams, json.EmptyResult, async (p, ct) =>
        {
            await Tests.RefreshAsync(p.ControllerId, ct).ConfigureAwait(false);
            return new EmptyResult();
        });
        connection.OnNotification(ProposedMethods.WorkspaceSolutionChanged, json.EmptyParams, _ => SolutionChanged?.Invoke(this, EventArgs.Empty));
        connection.OnNotification(ProposedMethods.WorkspaceDocumentsChanged, json.DocumentUrisParams, p => DocumentsChanged?.Invoke(this, new DocumentsChangedEventArgs(p.Uris)));
        connection.OnNotification(ProposedMethods.BuildFinished, json.BuildFinishedParams, p =>
            BuildFinished?.Invoke(this, new BuildFinishedEventArgs(p.Target, p.Project, ToResult(p.Result))));
        connection.OnNotification(ProposedMethods.DebugEnded, json.DebugEndedParams, p =>
        {
            if (_debugSessions.TryRemove(p.SessionId, out var ended))
            {
                ended.TrySetResult(p.ExitCode);
            }
        });
    }

    internal static BuildResult ToResult(BuildResultDto dto) => new(dto.Succeeded, dto.Canceled, dto.Errors, dto.Warnings);

    internal Task<T> RequestAsync<TParams, T>(string method, TParams parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TParams> paramsInfo, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> resultInfo, CancellationToken cancellationToken) =>
        _server.RequestAsync(method, parameters, paramsInfo, resultInfo, cancellationToken);

    internal IDebugSession TrackSession(DebugSessionDto session)
    {
        var ended = _debugSessions.GetOrAdd(session.SessionId, _ => new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously));
        return new DebugSession(this, session.SessionId, session.ProcessId, ended.Task);
    }

    public void Dispose()
    {
        Tests.Dispose();
        foreach (var session in _debugSessions.Values)
        {
            session.TrySetResult(null);
        }
    }

    private sealed class DebugSession(ProposedHost host, string id, int processId, Task<int?> ended) : IDebugSession
    {
        public int ProcessId { get; } = processId;

        public Task<int?> Ended { get; } = ended;

        public Task StopAsync(CancellationToken cancellationToken = default) =>
            host.RequestAsync(ProposedMethods.DebugStop, new DebugSessionParams(id), PluginJsonContext.Default.DebugSessionParams, PluginJsonContext.Default.EmptyResult, cancellationToken);
    }
}

/// <summary>One plugin's view of the proposed APIs. Its event subscriptions end when the plugin is deactivated.</summary>
internal sealed class PluginProposedApi : IProposedApi, ITests, IWorkspace, IBuild, IDebug, IDisposable
{
    private readonly ProposedHost _host;
    private readonly string _pluginId;
    private readonly List<HostTestController> _controllers = [];

    public PluginProposedApi(ProposedHost host, string pluginId)
    {
        _host = host;
        _pluginId = pluginId;
        _host.SolutionChanged += OnSolutionChanged;
        _host.DocumentsChanged += OnDocumentsChanged;
        _host.BuildFinished += OnBuildFinished;
    }

    public ITests Tests => this;

    public IWorkspace Workspace => this;

    public IBuild Build => this;

    public IDebug Debug => this;

    public event EventHandler? SolutionChanged;

    public event EventHandler<DocumentsChangedEventArgs>? DocumentsChanged;

    public event EventHandler<BuildFinishedEventArgs>? Finished;

    private void OnSolutionChanged(object? sender, EventArgs e) => SolutionChanged?.Invoke(this, e);

    private void OnDocumentsChanged(object? sender, DocumentsChangedEventArgs e) => DocumentsChanged?.Invoke(this, e);

    private void OnBuildFinished(object? sender, BuildFinishedEventArgs e) => Finished?.Invoke(this, e);

    // ------------------------------------------------------------------ tests

    public ITestController CreateController(string id, string label)
    {
        var controller = _host.Tests.Create(_pluginId, id, label);
        lock (_controllers)
        {
            _controllers.Add(controller);
        }

        return controller;
    }

    // ------------------------------------------------------------------ workspace

    public async Task<WorkspaceSolution?> GetSolutionAsync(CancellationToken cancellationToken)
    {
        var result = await _host.RequestAsync(ProposedMethods.WorkspaceGetSolution, new EmptyParams(), PluginJsonContext.Default.EmptyParams, PluginJsonContext.Default.GetSolutionResult, cancellationToken).ConfigureAwait(false);
        return result.Solution is not { } s
            ? null
            : new WorkspaceSolution(s.Path, s.Trusted, s.Configuration, s.Platform, s.SdkPath, [.. s.Projects.Select(p =>
                new WorkspaceProject(p.Path, p.Name, p.State, p.TargetFrameworks, p.OutputType, p.IsTestProject, p.TestPlatform, p.CompileFiles, p.ProjectReferences))]);
    }

    public async Task<ProjectOutput> GetProjectOutputAsync(string projectPath, string? targetFramework, CancellationToken cancellationToken)
    {
        var o = await _host.RequestAsync(ProposedMethods.WorkspaceProjectOutput, new ProjectOutputParams(projectPath, targetFramework), PluginJsonContext.Default.ProjectOutputParams, PluginJsonContext.Default.ProjectOutputDto, cancellationToken).ConfigureAwait(false);
        return new ProjectOutput(o.Path, o.TargetFramework, o.TargetPath, o.RunCommand, o.RunArguments, o.RunWorkingDirectory);
    }

    public async Task<IReadOnlyList<DocumentTestCandidates>> FindTestCandidatesAsync(IReadOnlyList<string> documentUris, CancellationToken cancellationToken)
    {
        var result = await _host.RequestAsync(ProposedMethods.WorkspaceTestCandidates, new DocumentUrisParams([.. documentUris]), PluginJsonContext.Default.DocumentUrisParams, PluginJsonContext.Default.TestCandidatesResultDto, cancellationToken).ConfigureAwait(false);
        return [.. result.Documents.Select(d => new DocumentTestCandidates(d.Uri, [.. d.Candidates.Select(c =>
            new TestCandidate(c.Kind, c.Name, c.Type, c.Namespace, HostTestRegistry.ToRange(c.Range), HostTestRegistry.ToRange(c.SelectionRange), c.Parameterized))]))];
    }

    public async Task<TypeOutline?> GetTypeOutlineAsync(string documentUri, int line, int character, CancellationToken cancellationToken)
    {
        var result = await _host.RequestAsync(ProposedMethods.WorkspaceTypeOutline, new TypeOutlineParamsDto(documentUri, line, character), PluginJsonContext.Default.TypeOutlineParamsDto, PluginJsonContext.Default.TypeOutlineResult, cancellationToken).ConfigureAwait(false);
        return result.Outline is not { } o
            ? null
            : new TypeOutline(o.Name, o.Namespace, o.NestedName, o.IsStatic, o.HasParameterlessConstructor,
                [.. o.Methods.Select(m => new MethodOutline(m.Name, m.ReturnType, [.. m.Parameters.Select(p => new ParameterOutline(p.Name, p.Type))], m.IsStatic, m.IsAsync, m.Line))],
                o.SelectedMethod);
    }

    // ------------------------------------------------------------------ build and debug

    public async Task<BuildResult> BuildAsync(IReadOnlyList<string>? projects, CancellationToken cancellationToken)
    {
        var result = await _host.RequestAsync(ProposedMethods.BuildStart, new BuildStartParams(projects is null ? null : [.. projects]), PluginJsonContext.Default.BuildStartParams, PluginJsonContext.Default.BuildResultDto, cancellationToken).ConfigureAwait(false);
        return ProposedHost.ToResult(result);
    }

    public async Task<IDebugSession> LaunchAsync(DebugLaunchOptions options, CancellationToken cancellationToken)
    {
        var session = await _host.RequestAsync(
            ProposedMethods.DebugLaunch,
            new DebugLaunchParams(options.Program, [.. options.Arguments], options.WorkingDirectory, options.Environment is null ? null : new Dictionary<string, string>(options.Environment)),
            PluginJsonContext.Default.DebugLaunchParams,
            PluginJsonContext.Default.DebugSessionDto,
            cancellationToken).ConfigureAwait(false);
        return _host.TrackSession(session);
    }

    public async Task<IDebugSession> AttachAsync(int processId, CancellationToken cancellationToken)
    {
        var session = await _host.RequestAsync(ProposedMethods.DebugAttach, new DebugAttachParams(processId), PluginJsonContext.Default.DebugAttachParams, PluginJsonContext.Default.DebugSessionDto, cancellationToken).ConfigureAwait(false);
        return _host.TrackSession(session);
    }

    public void Dispose()
    {
        _host.SolutionChanged -= OnSolutionChanged;
        _host.DocumentsChanged -= OnDocumentsChanged;
        _host.BuildFinished -= OnBuildFinished;
        List<HostTestController> controllers;
        lock (_controllers)
        {
            controllers = [.. _controllers];
            _controllers.Clear();
        }

        foreach (var controller in controllers)
        {
            controller.Dispose();
        }
    }
}
