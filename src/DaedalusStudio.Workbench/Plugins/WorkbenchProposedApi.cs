using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Plugins.Protocol;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Debugging;
using DaedalusStudio.Workbench.Languages;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Workbench.Plugins;

/// <summary>
/// The proposed workspace, build and debug APIs on the workbench's services (ADR 0010). Solution changes are
/// coalesced (<see cref="SolutionDebounce"/>), edited documents batched (<see cref="DocumentDebounce"/>).
/// </summary>
public sealed class WorkbenchProposedApi : IProposedApiHost, IDisposable
{
    public static readonly TimeSpan SolutionDebounce = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan DocumentDebounce = TimeSpan.FromMilliseconds(500);

    private readonly SolutionService _solutions;
    private readonly BuildService _builds;
    private readonly DebugService _debug;
    private readonly LanguageService _languages;
    private readonly Func<CancellationToken, Task>? _beforeBuild;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _changedDocuments = new(StringComparer.Ordinal);
    private readonly Timer _solutionTimer;
    private readonly Timer _documentTimer;
    private (string Id, Action<DebugEndedParams> Ended)? _session;

    /// <param name="beforeBuild">Runs before plugin-requested builds (the shell saves open documents).</param>
    public WorkbenchProposedApi(SolutionService solutions, BuildService builds, DebugService debug, LanguageService languages, Func<CancellationToken, Task>? beforeBuild = null)
    {
        _solutions = solutions;
        _builds = builds;
        _debug = debug;
        _languages = languages;
        _beforeBuild = beforeBuild;
        _solutionTimer = new Timer(_ => Notification?.Invoke(this, new ProposedNotification.SolutionChanged()));
        _documentTimer = new Timer(_ => FlushDocuments());
        _solutions.SolutionChanged += OnSolutionChanged;
        _solutions.ProjectChanged += OnProjectChanged;
        _languages.DocumentChanged += OnDocumentChanged;
        _builds.Finished += OnBuildFinished;
        _debug.Ended += OnDebugEnded;
    }

    public event EventHandler<ProposedNotification>? Notification;

    private void OnSolutionChanged(object? sender, EventArgs e) => _solutionTimer.Change(SolutionDebounce, Timeout.InfiniteTimeSpan);

    private void OnProjectChanged(object? sender, ProjectInfo e) => _solutionTimer.Change(SolutionDebounce, Timeout.InfiniteTimeSpan);

    private void OnDocumentChanged(object? sender, string path)
    {
        lock (_gate)
        {
            _changedDocuments.Add(DocumentUris.FromPath(path));
        }

        _documentTimer.Change(DocumentDebounce, Timeout.InfiniteTimeSpan);
    }

    private void FlushDocuments()
    {
        string[] uris;
        lock (_gate)
        {
            uris = [.. _changedDocuments];
            _changedDocuments.Clear();
        }

        if (uris.Length > 0)
        {
            Notification?.Invoke(this, new ProposedNotification.DocumentsChanged(uris));
        }
    }

    private void OnBuildFinished(object? sender, BuildCompleted e) =>
        Notification?.Invoke(this, new ProposedNotification.BuildFinished(new BuildFinishedParams(e.Request.Target, e.Request.Project, ToDto(e.Result))));

    private static BuildResultDto ToDto(BuildResultInfo result) => new(result.Succeeded, result.Canceled, result.Errors, result.Warnings);

    // ------------------------------------------------------------------ workspace

    public Task<WorkspaceSolutionDto?> GetSolutionAsync(CancellationToken cancellationToken)
    {
        if (_solutions.Solution is not { } solution)
        {
            return Task.FromResult<WorkspaceSolutionDto?>(null);
        }

        var projects = solution.Projects.Select(entry =>
        {
            if (_solutions.Projects.TryGetValue(entry.Path, out var info))
            {
                return new WorkspaceProjectDto(
                    info.Path,
                    info.Name,
                    info.State,
                    info.TargetFrameworks,
                    info.OutputType,
                    info.IsTestProject,
                    info.TestPlatform,
                    [.. info.Items.Where(i => i.Kind == "Compile").Select(i => i.Path).Distinct(SolutionService.PathComparer)],
                    [.. info.References.Where(r => r.Kind == ReferenceKinds.Project && r.Path is not null).Select(r => r.Path!).Distinct(SolutionService.PathComparer)]);
            }

            return new WorkspaceProjectDto(entry.Path, entry.Name, ProjectStates.Loading, [], null, false, null, [], []);
        });
        return Task.FromResult<WorkspaceSolutionDto?>(new WorkspaceSolutionDto(
            solution.Path, solution.Trusted, solution.Active?.Configuration, solution.Active?.Platform, _solutions.SdkPath, [.. projects]));
    }

    public async Task<ProjectOutputDto> GetProjectOutputAsync(string projectPath, string? targetFramework, CancellationToken cancellationToken)
    {
        var info = await _solutions.GetRunInfoAsync(projectPath, cancellationToken, targetFramework).ConfigureAwait(false);
        return new ProjectOutputDto(info.Path, info.TargetFramework, info.TargetPath, info.RunCommand, info.RunArguments, info.RunWorkingDirectory);
    }

    public async Task<TestCandidatesResultDto> FindTestCandidatesAsync(IReadOnlyList<string> uris, CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, DocumentTestCandidatesDto>(StringComparer.Ordinal);
        foreach (var group in uris.GroupBy(u => DocumentUris.ToPath(u) is { } path ? _languages.LanguageIdOf(path) : null))
        {
            if (group.Key is { } languageId && await _languages.ClientAsync(languageId).ConfigureAwait(false) is { } client)
            {
                var found = await client.TestCandidatesAsync([.. group], cancellationToken).ConfigureAwait(false);
                foreach (var document in found.Documents)
                {
                    results[document.Uri] = new DocumentTestCandidatesDto(document.Uri, [.. document.Candidates.Select(c =>
                        new TestCandidateDto(c.Kind, c.Name, c.Type, c.Namespace, ToDto(c.Range), ToDto(c.SelectionRange), c.Parameterized))]);
                }
            }
        }

        return new TestCandidatesResultDto([.. uris.Select(u => results.GetValueOrDefault(u) ?? new DocumentTestCandidatesDto(u, []))]);
    }

    public async Task<TypeOutlineDto?> GetTypeOutlineAsync(string uri, int line, int character, CancellationToken cancellationToken)
    {
        if (DocumentUris.ToPath(uri) is not { } path || _languages.LanguageIdOf(path) is not { } languageId
            || await _languages.ClientAsync(languageId).ConfigureAwait(false) is not { } client
            || await client.TypeOutlineAsync(uri, new Position(line, character), cancellationToken).ConfigureAwait(false) is not { } outline)
        {
            return null;
        }

        return new TypeOutlineDto(
            outline.Name,
            outline.Namespace,
            outline.NestedName,
            outline.IsStatic,
            outline.HasParameterlessConstructor,
            [.. outline.Methods.Select(m => new MethodOutlineDto(m.Name, m.ReturnType, [.. m.Parameters.Select(p => new ParameterOutlineDto(p.Name, p.Type))], m.IsStatic, m.IsAsync, m.Line))],
            outline.SelectedMethod);
    }

    private static RangeDto ToDto(LanguageServices.Protocol.Range range) => new(range.Start.Line, range.Start.Character, range.End.Line, range.End.Character);

    // ------------------------------------------------------------------ build

    public async Task<BuildResultDto> BuildAsync(IReadOnlyList<string>? projects, CancellationToken cancellationToken)
    {
        if (_beforeBuild is not null)
        {
            await _beforeBuild(cancellationToken).ConfigureAwait(false);
        }

        // One project builds that project; several build the solution (MSBuild orders and deduplicates them).
        var project = projects is [var single] ? single : null;
        while (true)
        {
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnFinished(object? sender, BuildCompleted e) => finished.TrySetResult();
            _builds.Finished += OnFinished;
            try
            {
                if (!_builds.IsBuilding)
                {
                    try
                    {
                        return ToDto(await _builds.BuildAsync(BuildTargets.Build, project, cancellationToken).ConfigureAwait(false));
                    }
                    catch (InvalidOperationException) when (_builds.IsBuilding)
                    {
                        // Someone else started a build between the check and ours; wait for it.
                    }
                }

                await finished.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _builds.Finished -= OnFinished;
            }
        }
    }

    // ------------------------------------------------------------------ debug

    public async Task<DebugSessionDto> DebugLaunchAsync(DebugLaunchParams launch, Action<DebugEndedParams> ended, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        SetSession(id, ended);
        try
        {
            await _debug.LaunchAsync(launch.Program, launch.Arguments, launch.WorkingDirectory, launch.Environment ?? [], cancellationToken).ConfigureAwait(false);
            var pid = await _debug.WaitForProcessIdAsync(cancellationToken).ConfigureAwait(false);
            return new DebugSessionDto(id, pid);
        }
        catch
        {
            ClearSession(id);
            throw;
        }
    }

    public async Task<DebugSessionDto> DebugAttachAsync(int processId, Action<DebugEndedParams> ended, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        SetSession(id, ended);
        try
        {
            await _debug.AttachAsync(processId, cancellationToken).ConfigureAwait(false);
            return new DebugSessionDto(id, processId);
        }
        catch
        {
            ClearSession(id);
            throw;
        }
    }

    public async Task DebugStopAsync(string sessionId, CancellationToken cancellationToken)
    {
        bool current;
        lock (_gate)
        {
            current = _session?.Id == sessionId;
        }

        if (current)
        {
            await _debug.StopAsync().ConfigureAwait(false);
        }
    }

    private void SetSession(string id, Action<DebugEndedParams> ended)
    {
        lock (_gate)
        {
            if (_debug.IsActive)
            {
                throw new InvalidOperationException("A debug session is already running.");
            }

            _session = (id, ended);
        }
    }

    private void ClearSession(string id)
    {
        lock (_gate)
        {
            if (_session?.Id == id)
            {
                _session = null;
            }
        }
    }

    private void OnDebugEnded(object? sender, int? exitCode)
    {
        (string Id, Action<DebugEndedParams> Ended)? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        session?.Ended(new DebugEndedParams(session.Value.Id, exitCode));
    }

    public void Dispose()
    {
        _solutions.SolutionChanged -= OnSolutionChanged;
        _solutions.ProjectChanged -= OnProjectChanged;
        _languages.DocumentChanged -= OnDocumentChanged;
        _builds.Finished -= OnBuildFinished;
        _debug.Ended -= OnDebugEnded;
        _solutionTimer.Dispose();
        _documentTimer.Dispose();
    }
}
