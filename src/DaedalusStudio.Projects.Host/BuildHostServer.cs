using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Rpc;

namespace DaedalusStudio.Projects.Host;

/// <summary>
/// The build host's side of the protocol (docs/project-protocol.md). One solution session at a time; the shell
/// starts one host per solution. Notifications to the shell go through one ordered queue.
/// </summary>
internal sealed class BuildHostServer : IAsyncDisposable, IHostEvents
{
    private readonly JsonRpcConnection _connection;
    private readonly string _msbuildPath;
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<Func<Task>> _outgoing = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _outgoingLoop;
    private SolutionSession? _session;

    public BuildHostServer(JsonRpcConnection connection, string msbuildPath)
    {
        _connection = connection;
        _msbuildPath = msbuildPath;
        _outgoingLoop = Task.Run(PumpOutgoingAsync);
    }

    public Task ExitRequested => _exit.Task;

    /// <summary><c>&lt;dotnet root&gt;/sdk/&lt;version&gt;/</c> → <c>&lt;dotnet root&gt;/dotnet</c>.</summary>
    private string DotnetExecutable
    {
        get
        {
            var root = Directory.GetParent(Path.TrimEndingDirectorySeparator(_msbuildPath))?.Parent?.FullName;
            var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
            return root is not null && File.Exists(Path.Combine(root, exe)) ? Path.Combine(root, exe) : exe;
        }
    }

    private SolutionSession Session => _session ?? throw new JsonRpcHandlerException(ProjectErrorCodes.NoSolution, "No solution is open.");

    public void RegisterHandlers()
    {
        var json = ProjectJsonContext.Default;
        _connection.OnRequest(ProjectMethods.Initialize, json.InitializeParams, json.InitializeResult, (_, _) =>
        {
            var version = typeof(BuildHostServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            var msbuild = typeof(Microsoft.Build.Evaluation.Project).Assembly.GetName().Version?.ToString() ?? "?";
            return Task.FromResult(new InitializeResult("daedalus-buildhost", version, ProjectMethods.ProtocolVersion, msbuild, _msbuildPath));
        });
        _connection.OnRequest(ProjectMethods.OpenSolution, json.OpenSolutionParams, json.SolutionInfo, async (p, ct) =>
        {
            if (_session is { } previous)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            _session = new SolutionSession(this, DotnetExecutable);
            return await Guard(() => _session.OpenAsync(p, ct)).ConfigureAwait(false);
        });
        _connection.OnRequest(ProjectMethods.CloseSolution, json.EmptyParams, json.EmptyResult, async (_, _) =>
        {
            if (_session is { } session)
            {
                _session = null;
                await session.DisposeAsync().ConfigureAwait(false);
            }

            return new EmptyResult();
        });
        _connection.OnRequest(ProjectMethods.Trust, json.TrustParams, json.EmptyResult, (p, _) => Sync(() => Session.Trust(p.Restore)));
        _connection.OnRequest(ProjectMethods.SetConfiguration, json.SetConfigurationParams, json.EmptyResult, (p, _) => Sync(() => Session.SetConfiguration(p)));
        _connection.OnRequest(ProjectMethods.Reevaluate, json.ProjectPathParams, json.EmptyResult, async (p, _) =>
        {
            await Session.ReevaluateAsync([Path.GetFullPath(p.Path)]).ConfigureAwait(false);
            return new EmptyResult();
        });
        _connection.OnRequest(ProjectMethods.Restore, json.RestoreParams, json.EmptyResult, (p, _) => Sync(() => Session.StartRestore(p.Path)));
        _connection.OnRequest(ProjectMethods.ListDirectory, json.ListDirectoryParams, json.ListDirectoryResult, (p, _) => Guard(() => Task.FromResult(Session.ListDirectory(p))));
        _connection.OnRequest(ProjectMethods.CreateFile, json.CreateFileParams, json.FileOperationResult, (p, _) => Guard(() => Task.FromResult(Session.CreateFile(p))));
        _connection.OnRequest(ProjectMethods.CreateFolder, json.CreateFolderParams, json.FileOperationResult, (p, _) => Guard(() => Task.FromResult(Session.CreateFolder(p))));
        _connection.OnRequest(ProjectMethods.Rename, json.RenameParams, json.FileOperationResult, (p, ct) => Guard(() => Session.RenameAsync(p, ct)));
        _connection.OnRequest(ProjectMethods.Move, json.MoveParams, json.FileOperationResult, (p, ct) => Guard(() => Session.MoveAsync(p, ct)));
        _connection.OnRequest(ProjectMethods.Delete, json.DeleteParams, json.FileOperationResult, (p, ct) => Guard(() => Session.DeleteAsync(p, ct)));
        _connection.OnRequest(ProjectMethods.AddProject, json.AddProjectParams, json.EmptyResult, (p, ct) => GuardEmpty(() => Session.AddProjectAsync(p, ct)));
        _connection.OnRequest(ProjectMethods.RemoveProject, json.ProjectPathParams, json.EmptyResult, (p, ct) => GuardEmpty(() => Session.RemoveProjectAsync(p, ct)));
        _connection.OnRequest(ProjectMethods.AddSolutionFolder, json.SolutionFolderParams, json.EmptyResult, (p, ct) => GuardEmpty(() => Session.AddFolderAsync(p, ct)));
        _connection.OnRequest(ProjectMethods.RenameSolutionFolder, json.RenameSolutionFolderParams, json.EmptyResult, (p, ct) => GuardEmpty(() => Session.RenameFolderAsync(p, ct)));
        _connection.OnRequest(ProjectMethods.RemoveSolutionFolder, json.RemoveSolutionFolderParams, json.EmptyResult, (p, ct) => GuardEmpty(() => Session.RemoveFolderAsync(p, ct)));
        _connection.OnRequest(ProjectMethods.MoveInSolution, json.MoveInSolutionParams, json.EmptyResult, (p, ct) => GuardEmpty(() => Session.MoveInSolutionAsync(p, ct)));
        _connection.OnRequest(ProjectMethods.AddProjectReference, json.ProjectReferenceParams, json.EmptyResult, (p, _) => Sync(() => Session.AddProjectReference(p)));
        _connection.OnRequest(ProjectMethods.RemoveProjectReference, json.ProjectReferenceParams, json.EmptyResult, (p, _) => Sync(() => Session.RemoveProjectReference(p)));
        _connection.OnRequest(ProjectMethods.SetActiveFramework, json.SetActiveFrameworkParams, json.EmptyResult, (p, _) => Sync(() => Session.SetActiveFramework(p)));
        _connection.OnRequest(ProjectMethods.DesignTimeBuild, json.DesignTimeBuildParams, json.EmptyResult, (p, _) => Sync(() => Session.RequestDesignTimeBuild(p.Path)));
        _connection.OnRequest(ProjectMethods.Build, json.BuildParams, json.BuildResultInfo, async (p, ct) =>
        {
            var result = await Guard(() => Session.BuildAsync(p, this, ct)).ConfigureAwait(false);
            await FlushAsync().ConfigureAwait(false);
            return result;
        });
        _connection.OnRequest(ProjectMethods.CancelBuild, json.EmptyParams, json.EmptyResult, (_, _) => Sync(() => _session?.CancelBuild()));
        _connection.OnRequest(ProjectMethods.RunInfo, json.RunInfoParams, json.RunInfo, (p, _) => Guard(() => Task.Run(() => Session.GetRunInfo(p.Path, p.TargetFramework))));
        _connection.OnRequest(ProjectMethods.Shutdown, json.EmptyParams, json.EmptyResult, async (_, _) =>
        {
            if (_session is { } session)
            {
                _session = null;
                await session.DisposeAsync().ConfigureAwait(false);
            }

            return new EmptyResult();
        });
        _connection.OnNotification(ProjectMethods.Exit, json.EmptyParams, _ => _exit.TrySetResult());
    }

    /// <summary>Maps expected failures (missing files, conflicts, invalid edits) to protocol errors with readable messages.</summary>
    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new JsonRpcHandlerException(ProjectErrorCodes.FileSystem, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Microsoft.Build.Exceptions.InvalidProjectFileException)
        {
            throw new JsonRpcHandlerException(ProjectErrorCodes.InvalidOperation, ex.Message);
        }
    }

    private static Task<EmptyResult> GuardEmpty(Func<Task> action) => Guard(async () =>
    {
        await action().ConfigureAwait(false);
        return new EmptyResult();
    });

    private static Task<EmptyResult> Sync(Action action) => GuardEmpty(() =>
    {
        action();
        return Task.CompletedTask;
    });

    private static Task<EmptyResult> Sync(Func<bool> action) => Sync(() => { _ = action(); });

    // ------------------------------------------------------------------ events to the shell

    public Task SolutionChangedAsync(SolutionInfo solution) => Post(ProjectMethods.SolutionChanged, solution, ProjectJsonContext.Default.SolutionInfo);

    public Task ProjectEvaluatedAsync(ProjectInfo project) => Post(ProjectMethods.ProjectEvaluated, project, ProjectJsonContext.Default.ProjectInfo);

    public Task ProgressAsync(EvaluationProgressParams progress) => Post(ProjectMethods.EvaluationProgress, progress, ProjectJsonContext.Default.EvaluationProgressParams);

    public Task RestoreOutputAsync(string line) => Post(ProjectMethods.RestoreOutput, new RestoreOutputParams(line), ProjectJsonContext.Default.RestoreOutputParams);

    public Task RestoreStateAsync(string state, string? message) => Post(ProjectMethods.RestoreState, new RestoreStateParams(state, message), ProjectJsonContext.Default.RestoreStateParams);

    public Task CompilerInputsAsync(CompilerInputsInfo inputs) => Post(ProjectMethods.CompilerInputs, inputs, ProjectJsonContext.Default.CompilerInputsInfo);

    public Task DesignTimeProgressAsync(DesignTimeProgressParams progress) => Post(ProjectMethods.DesignTimeProgress, progress, ProjectJsonContext.Default.DesignTimeProgressParams);

    public Task BuildOutputAsync(string line) => Post(ProjectMethods.BuildOutput, new BuildOutputParams(line), ProjectJsonContext.Default.BuildOutputParams);

    public Task BuildDiagnosticAsync(BuildDiagnosticInfo diagnostic) => Post(ProjectMethods.BuildDiagnostic, diagnostic, ProjectJsonContext.Default.BuildDiagnosticInfo);

    /// <summary>Completes once every notification queued so far has been sent (a response must not overtake them).</summary>
    private Task FlushAsync()
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_outgoing.Writer.TryWrite(() =>
            {
                sent.TrySetResult();
                return Task.CompletedTask;
            }))
        {
            sent.TrySetResult();
        }

        return sent.Task;
    }

    public Task LogAsync(string level, string message) => Post(ProjectMethods.Log, new LogParams(level, message), ProjectJsonContext.Default.LogParams);

    private Task Post<T>(string method, T parameters, JsonTypeInfo<T> info)
    {
        _outgoing.Writer.TryWrite(() => _connection.NotifyAsync(method, parameters, info, CancellationToken.None));
        return Task.CompletedTask;
    }

    private async Task PumpOutgoingAsync()
    {
        await foreach (var send in _outgoing.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await send().ConfigureAwait(false);
            }
            catch (JsonRpcConnectionLostException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is { } session)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _outgoing.Writer.TryComplete();
        await _outgoingLoop.ConfigureAwait(false);
    }
}
