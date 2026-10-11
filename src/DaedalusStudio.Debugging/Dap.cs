using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaedalusStudio.Debugging;

// The subset of the Debug Adapter Protocol (https://microsoft.github.io/debug-adapter-protocol/) that Daedalus uses,
// as source-generated contracts. Lines and columns are 1-based (initialize sets linesStartAt1/columnsStartAt1).

public sealed record InitializeArguments(
    string ClientId,
    string ClientName,
    string AdapterId,
    bool LinesStartAt1 = true,
    bool ColumnsStartAt1 = true,
    string PathFormat = "path",
    bool SupportsVariableType = true,
    bool SupportsVariablePaging = false,
    bool SupportsRunInTerminalRequest = false,
    string Locale = "en-US",
    bool SupportsStartDebuggingRequest = false)
{
    [JsonPropertyName("clientID")]
    public string ClientId { get; init; } = ClientId;

    [JsonPropertyName("adapterID")]
    public string AdapterId { get; init; } = AdapterId;
}

public sealed record ExceptionBreakpointsFilter(string Filter, string Label, bool? Default = null, string? Description = null);

public sealed record Capabilities
{
    public bool SupportsConfigurationDoneRequest { get; init; }

    public bool SupportsFunctionBreakpoints { get; init; }

    public bool SupportsConditionalBreakpoints { get; init; }

    public bool SupportsHitConditionalBreakpoints { get; init; }

    public bool SupportsLogPoints { get; init; }

    public bool SupportsSetVariable { get; init; }

    public bool SupportsExceptionInfoRequest { get; init; }

    public bool SupportsTerminateRequest { get; init; }

    public bool SupportsExceptionOptions { get; init; }

    public bool SupportsExceptionFilterOptions { get; init; }

    public bool SupportTerminateDebuggee { get; init; }

    public ExceptionBreakpointsFilter[]? ExceptionBreakpointFilters { get; init; }
}

/// <summary>netcoredbg's <c>launch</c> arguments.</summary>
public sealed record LaunchArguments(
    string Program,
    string[] Args,
    string Cwd,
    Dictionary<string, string> Env,
    bool StopAtEntry = false,
    bool JustMyCode = true,
    bool EnableStepFiltering = true,
    string Name = ".NET Launch",
    string Type = "coreclr",
    string Request = "launch");

public sealed record AttachArguments(int ProcessId, bool JustMyCode = true, string Name = ".NET Attach", string Type = "coreclr", string Request = "attach");

/// <summary>
/// vscode-js-debug's browser <c>launch</c> arguments (ADR 0026). <see cref="Type"/> is <c>pwa-chrome</c> or
/// <c>pwa-msedge</c>; <see cref="InspectUri"/> routes the connection through the Blazor WebAssembly debugging proxy;
/// <see cref="UserDataDir"/> true gives the browser a temporary profile.
/// </summary>
public sealed record BrowserLaunchArguments(
    string Type,
    string Url,
    string WebRoot,
    string? InspectUri = null,
    string? RuntimeExecutable = null,
    string[]? RuntimeArgs = null,
    bool UserDataDir = true,
    int Timeout = 60000,
    string Name = "Browser",
    string Request = "launch",
    JsDebugTrace? Trace = null);

/// <summary>js-debug's own log (<c>trace</c>): written to <see cref="LogFile"/>, for diagnosing a browser session.</summary>
public sealed record JsDebugTrace(string LogFile);

/// <summary>The <c>startDebugging</c> reverse request: start a child session with <see cref="Configuration"/>.</summary>
public sealed record StartDebuggingArguments(string Request, JsonElement Configuration);

public sealed record Source(string? Name = null, string? Path = null, int? SourceReference = null);

public sealed record SourceBreakpoint(int Line, int? Column = null, string? Condition = null, string? HitCondition = null, string? LogMessage = null);

public sealed record SetBreakpointsArguments(Source Source, SourceBreakpoint[] Breakpoints, bool SourceModified = false);

public sealed record Breakpoint(int? Id = null, bool Verified = false, string? Message = null, Source? Source = null, int? Line = null, int? Column = null);

public sealed record SetBreakpointsResponse(Breakpoint[] Breakpoints);

public sealed record FunctionBreakpoint(string Name, string? Condition = null, string? HitCondition = null);

public sealed record SetFunctionBreakpointsArguments(FunctionBreakpoint[] Breakpoints);

/// <summary>
/// A filter with a condition. netcoredbg's condition is a comma-separated list of exception type names (matched
/// exactly); a leading <c>!</c> negates the whole list.
/// </summary>
public sealed record ExceptionFilterOptions(string FilterId, string? Condition = null);

public sealed record SetExceptionBreakpointsArguments(string[] Filters, ExceptionFilterOptions[]? FilterOptions = null);

public sealed record EmptyArguments;

public sealed record ThreadArguments(int ThreadId);

public sealed record ContinueResponse(bool? AllThreadsContinued = null);

public sealed record DisconnectArguments(bool? TerminateDebuggee = null, bool Restart = false);

public sealed record DapThread(int Id, string Name);

public sealed record ThreadsResponse(DapThread[] Threads);

public sealed record StackTraceArguments(int ThreadId, int? StartFrame = null, int? Levels = null);

public sealed record StackFrame(int Id, string Name, Source? Source, int Line, int Column, int? EndLine = null, int? EndColumn = null, string? PresentationHint = null, System.Text.Json.JsonElement? ModuleId = null);

public sealed record StackTraceResponse(StackFrame[] StackFrames, int? TotalFrames = null);

public sealed record ScopesArguments(int FrameId);

public sealed record Scope(string Name, int VariablesReference, bool Expensive = false, string? PresentationHint = null);

public sealed record ScopesResponse(Scope[] Scopes);

public sealed record VariablesArguments(int VariablesReference);

public sealed record Variable(string Name, string Value, string? Type = null, int VariablesReference = 0, string? EvaluateName = null);

public sealed record VariablesResponse(Variable[] Variables);

/// <summary><see cref="Context"/> is <c>watch</c>, <c>repl</c>, <c>hover</c> or <c>clipboard</c>.</summary>
public sealed record EvaluateArguments(string Expression, int? FrameId = null, string? Context = null);

public sealed record EvaluateResponse(string Result, string? Type = null, int VariablesReference = 0);

public sealed record SetVariableArguments(int VariablesReference, string Name, string Value);

public sealed record SetVariableResponse(string Value, string? Type = null, int? VariablesReference = null);

public sealed record SetExpressionArguments(string Expression, string Value, int? FrameId = null);

public sealed record SetExpressionResponse(string Value, string? Type = null, int? VariablesReference = null);

public sealed record ExceptionDetails(
    string? Message = null,
    string? TypeName = null,
    string? FullTypeName = null,
    string? StackTrace = null,
    string? EvaluateName = null,
    ExceptionDetails[]? InnerException = null);

public sealed record ExceptionInfoResponse(string ExceptionId, string? Description = null, string? BreakMode = null, ExceptionDetails? Details = null);

// ------------------------------------------------------------------ events

public sealed record StoppedEvent(
    string Reason,
    string? Description = null,
    int? ThreadId = null,
    bool? AllThreadsStopped = null,
    string? Text = null,
    int[]? HitBreakpointIds = null);

public sealed record ContinuedEvent(int ThreadId, bool? AllThreadsContinued = null);

public sealed record ExitedEvent(int ExitCode);

public sealed record OutputEvent(string Output, string? Category = null, Source? Source = null, int? Line = null);

public sealed record ThreadEvent(string Reason, int ThreadId);

public sealed record BreakpointEvent(string Reason, Breakpoint Breakpoint);

/// <summary>A loaded module. <see cref="Id"/> is a number or a string, depending on the adapter.</summary>
public sealed record DapModule(JsonElement Id, string Name, string? Path = null, string? SymbolStatus = null, string? SymbolFilePath = null, string? Version = null, bool? IsOptimized = null, bool? IsUserCode = null);

/// <summary><see cref="Reason"/> is <c>new</c>, <c>changed</c> or <c>removed</c>.</summary>
public sealed record ModuleEvent(string Reason, DapModule Module);

/// <summary>The debuggee started (launch) or was attached to; <see cref="SystemProcessId"/> is its process id.</summary>
public sealed record ProcessEvent(string Name, int? SystemProcessId = null, bool? IsLocalProcess = null, string? StartMethod = null);

public static class DapEvents
{
    public const string Initialized = "initialized";
    public const string Stopped = "stopped";
    public const string Continued = "continued";
    public const string Exited = "exited";
    public const string Terminated = "terminated";
    public const string Output = "output";
    public const string Thread = "thread";
    public const string Breakpoint = "breakpoint";
    public const string Module = "module";
    public const string Process = "process";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InitializeArguments))]
[JsonSerializable(typeof(Capabilities))]
[JsonSerializable(typeof(LaunchArguments))]
[JsonSerializable(typeof(AttachArguments))]
[JsonSerializable(typeof(BrowserLaunchArguments))]
[JsonSerializable(typeof(StartDebuggingArguments))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(SetBreakpointsArguments))]
[JsonSerializable(typeof(SetBreakpointsResponse))]
[JsonSerializable(typeof(SetFunctionBreakpointsArguments))]
[JsonSerializable(typeof(SetExceptionBreakpointsArguments))]
[JsonSerializable(typeof(EmptyArguments))]
[JsonSerializable(typeof(ThreadArguments))]
[JsonSerializable(typeof(ContinueResponse))]
[JsonSerializable(typeof(DisconnectArguments))]
[JsonSerializable(typeof(ThreadsResponse))]
[JsonSerializable(typeof(StackTraceArguments))]
[JsonSerializable(typeof(StackTraceResponse))]
[JsonSerializable(typeof(ScopesArguments))]
[JsonSerializable(typeof(ScopesResponse))]
[JsonSerializable(typeof(VariablesArguments))]
[JsonSerializable(typeof(VariablesResponse))]
[JsonSerializable(typeof(EvaluateArguments))]
[JsonSerializable(typeof(EvaluateResponse))]
[JsonSerializable(typeof(SetVariableArguments))]
[JsonSerializable(typeof(SetVariableResponse))]
[JsonSerializable(typeof(SetExpressionArguments))]
[JsonSerializable(typeof(SetExpressionResponse))]
[JsonSerializable(typeof(ExceptionInfoResponse))]
[JsonSerializable(typeof(StoppedEvent))]
[JsonSerializable(typeof(ContinuedEvent))]
[JsonSerializable(typeof(ExitedEvent))]
[JsonSerializable(typeof(OutputEvent))]
[JsonSerializable(typeof(ThreadEvent))]
[JsonSerializable(typeof(BreakpointEvent))]
[JsonSerializable(typeof(ModuleEvent))]
[JsonSerializable(typeof(ProcessEvent))]
public sealed partial class DapJsonContext : JsonSerializerContext;
