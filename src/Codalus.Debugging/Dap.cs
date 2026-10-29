using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codalus.Debugging;

// The subset of the Debug Adapter Protocol (https://microsoft.github.io/debug-adapter-protocol/) that Codalus uses,
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

/// <summary>
/// The Mono debugger adapter's <c>launch</c> arguments (M20b, ADR 0065): <see cref="Mode"/> <c>connect</c> attaches to
/// a runtime whose agent listens at <see cref="Address"/>:<see cref="Port"/> (Android through adb's forward, a desktop
/// test program), retrying until it answers or <see cref="TimeoutSeconds"/> pass; <c>listen</c> waits there for the
/// runtime to connect (iOS simulators, Mac Catalyst).
/// </summary>
public sealed record MonoDebugArguments(
    string Mode = MonoDebugArguments.Connect,
    string Address = "127.0.0.1",
    int Port = 10000,
    int TimeoutSeconds = 300,
    bool JustMyCode = true,
    string Name = "Mono",
    string Type = "mono",
    string Request = "attach")
{
    public const string Connect = "connect";
    public const string Listen = "listen";
}

/// <summary>
/// The desktop-CLR (.NET Framework) debugger adapter's <c>launch</c> and <c>attach</c> arguments (M43b, ADR 0076,
/// <c>codalus-debughost --netfx-dap</c>): <see cref="Program"/> (a managed <c>.exe</c>) with its arguments, working
/// directory and environment, or <see cref="ProcessId"/> to attach to. <see cref="NewConsole"/> gives the program a
/// console window of its own (with input); otherwise its output comes through the Debug Console. CoreCLR's adapter
/// (<c>--clr-dap</c>, M54, ADR 0086) takes the same arguments, and <see cref="Console"/> (DAP's own: <c>integratedTerminal</c>
/// or <c>externalTerminal</c>) starts its launcher there through the client's <c>runInTerminal</c>.
/// </summary>
public sealed record NetFxDebugArguments(
    string? Program = null,
    string[]? Args = null,
    string? Cwd = null,
    Dictionary<string, string>? Env = null,
    int? ProcessId = null,
    bool JustMyCode = true,
    bool EnableStepFiltering = true,
    bool NewConsole = false,
    string Name = ".NET Framework",
    string Type = "clr",
    string Request = "launch",
    string? Console = null);

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

public sealed record AttachArguments(int ProcessId, bool JustMyCode = true, string Name = ".NET Attach", string Type = "coreclr", string Request = "attach", bool EnableStepFiltering = true);

/// <summary>
/// The <c>runInTerminal</c> reverse request (M14c): start <see cref="Args"/> (the program first) in a terminal.
/// <see cref="Kind"/> is <c>integrated</c> or <c>external</c>; an <see cref="Env"/> value of null unsets the variable.
/// </summary>
public sealed record RunInTerminalRequestArguments(string[] Args, string Cwd, string? Kind = null, string? Title = null, Dictionary<string, string?>? Env = null);

/// <summary>The answer to <c>runInTerminal</c>: the program's process id, when the terminal knows it.</summary>
public sealed record RunInTerminalResponse(int? ProcessId = null, int? ShellProcessId = null);

/// <summary>
/// The Codalus debugger's <c>codalus/applyChanges</c> request (M55, ADR 0086): an edit's deltas, one per module Roslyn
/// emitted for (<see cref="ModuleDelta.ModuleId"/> is its MVID). Modules the program hasn't loaded are skipped.
/// </summary>
public sealed record ApplyChangesArguments(ModuleDelta[] Updates);

/// <summary>Roslyn's deltas for one module: metadata, IL and PDB (base64 in JSON).</summary>
public sealed record ModuleDelta(Guid ModuleId, byte[] MetadataDelta, byte[] IlDelta, byte[] PdbDelta);

/// <summary><c>codalus/applyChanges</c>'s answer: what happened to each module's delta.</summary>
public sealed record ApplyChangesResponse(ModuleDeltaResult[] Results);

/// <summary>
/// <see cref="Status"/>: <c>applied</c>; <c>notLoaded</c> (the program doesn't have the module); <c>failed</c>, with the
/// runtime's reason in <see cref="Message"/>.
/// </summary>
public sealed record ModuleDeltaResult(Guid ModuleId, string Status, string? Message = null)
{
    public const string Applied = "applied";
    public const string NotLoaded = "notLoaded";
    public const string Failed = "failed";
}

/// <summary>
/// <c>codalus/hotReloadCapabilities</c>'s answer (M55): what the debugged runtime can apply (Roslyn's capability names,
/// <c>Baseline</c> first), empty when it can't take edits.
/// </summary>
public sealed record HotReloadCapabilitiesResponse(string[] Capabilities);

/// <summary>
/// vscode-js-debug's browser <c>launch</c> arguments (ADR 0026). <see cref="Type"/> is <c>pwa-chrome</c> or
/// <c>pwa-msedge</c>; <see cref="InspectUri"/> routes the connection through the Blazor WebAssembly debugging proxy;
/// <see cref="UserDataDir"/> true gives the browser a temporary profile. <see cref="SkipNavigateForTest"/> (js-debug's
/// own name for it) leaves the page at <c>about:blank</c> instead of opening <see cref="Url"/>: <see cref="JsDebugAdapter"/>
/// sets it behind the proxy and opens the page itself.
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
    JsDebugTrace? Trace = null,
    bool? SkipNavigateForTest = null);

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
[JsonSerializable(typeof(MonoDebugArguments))]
[JsonSerializable(typeof(NetFxDebugArguments))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(RunInTerminalRequestArguments))]
[JsonSerializable(typeof(RunInTerminalResponse))]
[JsonSerializable(typeof(ApplyChangesArguments))]
[JsonSerializable(typeof(ApplyChangesResponse))]
[JsonSerializable(typeof(HotReloadCapabilitiesResponse))]
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
