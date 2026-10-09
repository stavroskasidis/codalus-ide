using DaedalusStudio.Plugins.Protocol;

namespace DaedalusStudio.Workbench.Plugins;

/// <summary>
/// What the shell offers plugins through the proposed APIs (workspace, build, debug; ADR 0010). Set on
/// <see cref="PluginManager.ProposedApi"/>; without it those requests fail.
/// </summary>
public interface IProposedApiHost
{
    Task<WorkspaceSolutionDto?> GetSolutionAsync(CancellationToken cancellationToken);

    Task<ProjectOutputDto> GetProjectOutputAsync(string projectPath, string? targetFramework, CancellationToken cancellationToken);

    Task<TestCandidatesResultDto> FindTestCandidatesAsync(IReadOnlyList<string> uris, CancellationToken cancellationToken);

    Task<TypeOutlineDto?> GetTypeOutlineAsync(string uri, int line, int character, CancellationToken cancellationToken);

    Task<WorkspacePackagesResult> GetPackagesAsync(string projectPath, CancellationToken cancellationToken);

    Task<WorkspacePropertiesResult> GetPropertiesAsync(string projectPath, IReadOnlyList<string> names, CancellationToken cancellationToken);

    /// <summary>Builds the projects (null: the solution), after a build in progress.</summary>
    Task<BuildResultDto> BuildAsync(IReadOnlyList<string>? projects, CancellationToken cancellationToken);

    /// <summary>Starts a debug session; <paramref name="ended"/> is called when it ends.</summary>
    Task<DebugSessionDto> DebugLaunchAsync(DebugLaunchParams launch, Action<DebugEndedParams> ended, CancellationToken cancellationToken);

    Task<DebugSessionDto> DebugAttachAsync(int processId, Action<DebugEndedParams> ended, CancellationToken cancellationToken);

    Task DebugStopAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Opens a terminal; <paramref name="output"/> (with <c>ReportOutput</c>) and <paramref name="exited"/> report on it.</summary>
    Task<TerminalDto> TerminalCreateAsync(TerminalCreateParams create, Action<TerminalOutputParams> output, Action<TerminalExitedParams> exited, CancellationToken cancellationToken);

    Task TerminalSendTextAsync(string terminalId, string text, CancellationToken cancellationToken);

    Task TerminalKillAsync(string terminalId, CancellationToken cancellationToken);

    /// <summary>Raised to tell plugins about the workspace (solution changes, edited documents, finished builds).</summary>
    event EventHandler<ProposedNotification>? Notification;
}

/// <summary>A notification for every plugin host with proposed-API plugins.</summary>
public abstract record ProposedNotification
{
    public sealed record SolutionChanged : ProposedNotification;

    public sealed record DocumentsChanged(string[] Uris) : ProposedNotification;

    public sealed record BuildFinished(BuildFinishedParams Build) : ProposedNotification;
}

/// <summary>Receives the test controllers' messages (the shell's TestService).</summary>
public interface ITestSink
{
    void ControllerCreated(TestControllerParams controller, ITestControllerConnection connection);

    void ControllerDisposed(string controllerId);

    void ItemsUpdated(TestItemsUpdateParams update);

    void ProfilesUpdated(TestProfilesParams profiles);

    void RunStarted(TestRunStartedParams run);

    void RunUpdated(TestRunUpdateParams update);

    void RunCoverage(TestRunCoverageParams coverage);

    void RunEnded(string runId);
}

/// <summary>Requests to a test controller's plugin.</summary>
public interface ITestControllerConnection
{
    /// <summary>
    /// Runs a profile; completes when the plugin's handler completes, with the ids of the runs it created. Cancelling
    /// cancels the run.
    /// </summary>
    Task<IReadOnlyList<string>> RunAsync(TestRunParams request, CancellationToken cancellationToken);

    Task RefreshAsync(CancellationToken cancellationToken);
}
