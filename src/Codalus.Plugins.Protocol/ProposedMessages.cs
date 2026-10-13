namespace Codalus.Plugins.Protocol;

#pragma warning disable CA1819 // Properties should not return arrays: wire DTOs.

// Proposed APIs (docs/plugin-protocol.md#proposed-apis, ADR 0010). Only plugins loaded with enableProposedApi use them.

/// <summary>Method names of the proposed APIs. Since protocol 0.2.</summary>
public static class ProposedMethods
{
    // host → shell (requests)
    public const string WorkspaceGetSolution = "workspace/getSolution";
    public const string WorkspaceProjectOutput = "workspace/projectOutput";
    public const string WorkspaceTestCandidates = "workspace/testCandidates";
    public const string WorkspaceTypeOutline = "workspace/typeOutline";
    public const string WorkspacePackages = "workspace/packages";
    public const string WorkspaceProperties = "workspace/properties";
    public const string BuildStart = "build/start";
    public const string DebugLaunch = "debug/launch";
    public const string DebugAttach = "debug/attach";
    public const string DebugStop = "debug/stop";
    public const string TerminalCreate = "terminal/create";
    public const string TerminalSendText = "terminal/sendText";
    public const string TerminalKill = "terminal/kill";
    public const string DebugEvaluate = "debug/evaluate";

    // host → shell (notifications)
    public const string DebugRegisterVisualizer = "debug/registerVisualizer";
    public const string DebugUnregisterVisualizer = "debug/unregisterVisualizer";
    public const string TestsCreateController = "tests/createController";
    public const string TestsDisposeController = "tests/disposeController";
    public const string TestsUpdateItems = "tests/updateItems";
    public const string TestsUpdateProfiles = "tests/updateProfiles";
    public const string TestsRunStarted = "tests/runStarted";
    public const string TestsRunUpdate = "tests/runUpdate";
    public const string TestsRunCoverage = "tests/runCoverage";
    public const string TestsRunEnded = "tests/runEnded";

    // shell → host (requests)
    public const string TestsRun = "tests/run";
    public const string TestsRefresh = "tests/refresh";
    public const string DebugVisualize = "debug/visualize";

    // shell → host (notifications)
    public const string WorkspaceSolutionChanged = "workspace/solutionChanged";
    public const string WorkspaceDocumentsChanged = "workspace/documentsChanged";
    public const string BuildFinished = "build/finished";
    public const string DebugEnded = "debug/ended";
    public const string TerminalOutput = "terminal/output";
    public const string TerminalExited = "terminal/exited";
}

// ------------------------------------------------------------------ workspace

public sealed record WorkspaceProjectDto(
    string Path,
    string Name,
    string State,
    string[] TargetFrameworks,
    string? OutputType,
    bool IsTestProject,
    string? TestPlatform,
    string[] CompileFiles,
    string[] ProjectReferences);

public sealed record WorkspaceSolutionDto(string Path, bool Trusted, string? Configuration, string? Platform, string? SdkPath, WorkspaceProjectDto[] Projects);

public sealed record GetSolutionResult(WorkspaceSolutionDto? Solution);

public sealed record ProjectOutputParams(string Path, string? TargetFramework);

public sealed record ProjectOutputDto(string Path, string? TargetFramework, string? TargetPath, string? RunCommand, string? RunArguments, string? RunWorkingDirectory);

public sealed record DocumentUrisParams(string[] Uris);

/// <summary><c>workspace/packages</c> (since protocol 0.2, API 0.2.2).</summary>
public sealed record WorkspacePackagesParams(string Path);

public sealed record WorkspacePackageDto(string Id, string? RequestedVersion, string? ResolvedVersion, bool IsTransitive, string[] TargetFrameworks);

public sealed record WorkspacePackagesResult(WorkspacePackageDto[] Packages);

/// <summary><c>workspace/properties</c> (since protocol 0.2, API 0.2.2).</summary>
public sealed record WorkspacePropertiesParams(string Path, string[] Names);

public sealed record WorkspacePropertiesResult(Dictionary<string, string> Values);

/// <summary>Zero-based, LSP style.</summary>
public sealed record RangeDto(int StartLine, int StartCharacter, int EndLine, int EndCharacter);

public sealed record TestCandidateDto(string Kind, string Name, string Type, string? Namespace, RangeDto Range, RangeDto SelectionRange, bool Parameterized);

public sealed record DocumentTestCandidatesDto(string Uri, TestCandidateDto[] Candidates);

public sealed record TestCandidatesResultDto(DocumentTestCandidatesDto[] Documents);

public sealed record TypeOutlineParamsDto(string Uri, int Line, int Character);

public sealed record ParameterOutlineDto(string Name, string Type);

public sealed record MethodOutlineDto(string Name, string ReturnType, ParameterOutlineDto[] Parameters, bool IsStatic, bool IsAsync, int Line);

public sealed record TypeOutlineDto(string Name, string? Namespace, string NestedName, bool IsStatic, bool HasParameterlessConstructor, MethodOutlineDto[] Methods, string? SelectedMethod);

/// <summary>Wraps the outline: null when there is no type at the position.</summary>
public sealed record TypeOutlineResult(TypeOutlineDto? Outline);

// ------------------------------------------------------------------ build and debug

/// <summary>Builds <see cref="Projects"/>, or the solution when null.</summary>
public sealed record BuildStartParams(string[]? Projects);

public sealed record BuildResultDto(bool Succeeded, bool Canceled, int Errors, int Warnings);

public sealed record BuildFinishedParams(string Target, string? Project, BuildResultDto Result);

public sealed record DebugLaunchParams(string Program, string[] Arguments, string WorkingDirectory, Dictionary<string, string>? Environment);

public sealed record DebugAttachParams(int ProcessId);

public sealed record DebugSessionDto(string SessionId, int ProcessId);

public sealed record DebugSessionParams(string SessionId);

public sealed record DebugEndedParams(string SessionId, int? ExitCode);

/// <summary>A plugin's visualizer; <see cref="VisualizerId"/> is <c>pluginId/id</c>. Since API 0.2.7.</summary>
public sealed record DebugVisualizerDto(string VisualizerId, string PluginId, string Title, string[] Types);

public sealed record DebugVisualizerIdParams(string VisualizerId);

public sealed record DebugVisualizeParams(string VisualizerId, string Expression, string? Type, string Value);

/// <summary>Text (with an optional language) or a table.</summary>
public sealed record DebugVisualizerViewDto(string? Text, string? LanguageId, string[]? Columns, string[][]? Rows);

public sealed record DebugEvaluateParams(string Expression);

public sealed record DebugValueDto(string Value, string? Type);

// ------------------------------------------------------------------ terminals (since API 0.2.3)

/// <summary>
/// <c>terminal/create</c>: a shell (<see cref="Profile"/>, null for the default) or, with <see cref="Program"/>, that
/// program in a terminal. <see cref="ReportOutput"/> asks for <c>terminal/output</c> notifications.
/// </summary>
public sealed record TerminalCreateParams(
    string? Name,
    string? Profile,
    string? WorkingDirectory,
    string? Program,
    string[]? Arguments,
    Dictionary<string, string>? Environment,
    bool Show,
    bool ReportOutput);

public sealed record TerminalDto(string TerminalId, int ProcessId);

public sealed record TerminalIdParams(string TerminalId);

public sealed record TerminalTextParams(string TerminalId, string Text);

/// <summary>Text the program wrote (UTF-8 decoded, escape sequences included).</summary>
public sealed record TerminalOutputParams(string TerminalId, string Text);

public sealed record TerminalExitedParams(string TerminalId, int? ExitCode);

// ------------------------------------------------------------------ tests

public sealed record TestControllerParams(string PluginId, string ControllerId, string Label);

public sealed record TestControllerIdParams(string ControllerId);

public sealed record TestTraitDto(string Name, string? Value);

/// <summary>An item, sent after its parent. <see cref="ParentId"/> null means a root.</summary>
public sealed record TestItemDto(
    string Id,
    string? ParentId,
    string Label,
    string? Description,
    string? Uri,
    RangeDto? Range,
    TestTraitDto[]? Traits,
    string? SortText,
    string? Error);

/// <summary>Items added or changed (parents first), then removed ids (with their descendants).</summary>
public sealed record TestItemsUpdateParams(string ControllerId, TestItemDto[] Items, string[] Removed);

/// <summary><see cref="Kind"/> is <c>run</c>, <c>debug</c> or <c>coverage</c>.</summary>
public sealed record TestProfileDto(string Id, string Label, string Kind, bool IsDefault);

public sealed record TestProfilesParams(string ControllerId, TestProfileDto[] Profiles, bool CanRefresh);

/// <summary>
/// Runs a profile. <see cref="Include"/> null means everything. <see cref="RequestId"/> is chosen by the shell and
/// echoed in <c>tests/runStarted</c> for the runs the handler creates.
/// </summary>
public sealed record TestRunParams(string ControllerId, string ProfileId, string RequestId, string[]? Include, string[] Exclude);

/// <summary>
/// The result of <c>tests/run</c>: the runs the profile's handler created. Their <c>tests/runEnded</c> notifications may
/// be processed after this response, so callers wait for them.
/// </summary>
public sealed record TestRunResultDto(string[] RunIds);

public sealed record TestRunStartedParams(string ControllerId, string RunId, string Name, string? RequestId, string? ProfileKind);

public static class TestStates
{
    public const string Enqueued = "enqueued";
    public const string Started = "started";
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Errored = "errored";
    public const string Skipped = "skipped";
}

public sealed record TestMessageDto(string Message, string? Expected, string? Actual, string? StackTrace, string? Uri, RangeDto? Range);

public sealed record TestStateDto(string ItemId, string State, double? DurationMs, TestMessageDto[]? Messages);

public sealed record TestOutputDto(string? ItemId, string Text);

public sealed record TestAttachmentDto(string Uri, string? DisplayName);

/// <summary>A batch of state changes and output, in the order the plugin reported them.</summary>
public sealed record TestRunUpdateParams(string RunId, TestStateDto[] States, TestOutputDto[] Output, TestAttachmentDto[] Attachments);

public sealed record LineCoverageDto(int Line, int Hits, int BranchesCovered, int BranchesTotal);

public sealed record FileCoverageDto(string Uri, LineCoverageDto[] Lines);

public sealed record MethodCoverageDto(string Assembly, string? Namespace, string Class, string Method, string? Uri, int Line, int LinesCovered, int LinesTotal, int BranchesCovered, int BranchesTotal);

public sealed record TestRunCoverageParams(string RunId, FileCoverageDto[] Files, MethodCoverageDto[] Methods);

public sealed record TestRunIdParams(string RunId);

#pragma warning restore CA1819
