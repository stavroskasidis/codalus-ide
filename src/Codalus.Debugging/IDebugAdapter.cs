namespace Codalus.Debugging;

/// <summary>
/// A debug session's adapter as <c>DebugService</c> sees it: netcoredbg (<see cref="DebugAdapter"/>, ADR 0007) or
/// vscode-js-debug for browsers (<see cref="JsDebugAdapter"/>, ADR 0026). Launching and attaching are on the
/// implementations, because their arguments differ.
/// </summary>
public interface IDebugAdapter : IAsyncDisposable
{
    Capabilities Capabilities { get; }

    /// <summary>Completes on the adapter's <c>initialized</c> event: breakpoints can be sent, then <c>configurationDone</c>.</summary>
    Task Initialized { get; }

    /// <summary>Completes when the adapter exits or closes its connection.</summary>
    Task Closed { get; }

    /// <summary>Adapter events, in order.</summary>
    event Func<DapEvent, Task>? EventReceived;

    /// <summary>The adapter's own diagnostics (stderr).</summary>
    event EventHandler<string>? Log;

    /// <summary>What the adapter and its program are doing, for a session that stopped making progress (empty when it can't say).</summary>
    string Describe() => string.Empty;

    /// <summary>The adapters the session talks to: itself, or a compound session's live parts (M55: each Codalus debugger takes an edit).</summary>
    IReadOnlyList<IDebugAdapter> Adapters => [this];

    Task ConfigurationDoneAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Whether the adapter that stopped <paramref name="threadId"/> applies a breakpoint's hit condition, log message,
    /// filter and dependency itself (the Codalus debugger, M58): then a stop at a breakpoint is one to show. Otherwise
    /// the adapter is sent the breakpoint without them, stops at every hit, and the session decides (netcoredbg, the
    /// browser's adapter).
    /// </summary>
    bool AppliesBreakpointBehaviors(int threadId) => Capabilities.AppliesBreakpointBehaviors;

    /// <summary>The capabilities of the adapter <paramref name="threadId"/> belongs to (a compound session's parts differ).</summary>
    Capabilities CapabilitiesOf(int threadId) => Capabilities;

    Task<Breakpoint[]> SetBreakpointsAsync(string path, SourceBreakpoint[] breakpoints, CancellationToken cancellationToken);

    Task<Breakpoint[]> SetFunctionBreakpointsAsync(FunctionBreakpoint[] breakpoints, CancellationToken cancellationToken);

    Task SetExceptionBreakpointsAsync(string[] filters, CancellationToken cancellationToken, ExceptionFilterOptions[]? options = null);

    Task ContinueAsync(int threadId, CancellationToken cancellationToken);

    Task NextAsync(int threadId, CancellationToken cancellationToken);

    Task StepInAsync(int threadId, CancellationToken cancellationToken);

    Task StepOutAsync(int threadId, CancellationToken cancellationToken);

    /// <summary>The calls of the rest of a frame's line that a step could go into (Step Into Specific); none when the adapter can't say.</summary>
    Task<StepInTarget[]> StepInTargetsAsync(int frameId, CancellationToken cancellationToken) => Task.FromResult<StepInTarget[]>([]);

    /// <summary>Steps into one of <see cref="StepInTargetsAsync"/>'s calls.</summary>
    Task StepInAsync(int threadId, int targetId, CancellationToken cancellationToken) => StepInAsync(threadId, cancellationToken);

    /// <summary>Where <paramref name="threadId"/>'s next statement could be set on a line (Set Next Statement); none when it can't be.</summary>
    Task<GotoTarget[]> GotoTargetsAsync(int threadId, string path, int line, CancellationToken cancellationToken) => Task.FromResult<GotoTarget[]>([]);

    /// <summary>Moves <paramref name="threadId"/>'s next statement to one of <see cref="GotoTargetsAsync"/>'s targets; a <c>stopped</c> event with the reason <c>goto</c> follows.</summary>
    Task GotoAsync(int threadId, int targetId, CancellationToken cancellationToken) => throw new DapException("goto", "This debugger can't set the next statement.");

    /// <summary>
    /// Make Object ID (<see cref="Capabilities.SupportsObjectIds"/>): the object <paramref name="expression"/> evaluates to
    /// in the frame gets a name (<c>$1</c>) expressions reach it by for the rest of the session.
    /// </summary>
    Task<string> MakeObjectIdAsync(string expression, int? frameId, CancellationToken cancellationToken) => throw new DapException("codalus/makeObjectId", "This debugger doesn't make object IDs.");

    /// <summary>Lets go of an object ID.</summary>
    Task DeleteObjectIdAsync(string name, int? frameId, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Keeps a thread from running when the program runs on, or lets it run again (<see cref="Capabilities.SupportsFreezingThreads"/>).</summary>
    Task FreezeThreadAsync(int threadId, bool frozen, CancellationToken cancellationToken) => throw new DapException("codalus/freezeThread", "This debugger can't freeze threads.");

    Task PauseAsync(int threadId, CancellationToken cancellationToken);

    Task<DapThread[]> ThreadsAsync(CancellationToken cancellationToken);

    Task<StackTraceResponse> StackTraceAsync(int threadId, int levels, CancellationToken cancellationToken);

    Task<Scope[]> ScopesAsync(int frameId, CancellationToken cancellationToken);

    Task<Variable[]> VariablesAsync(int variablesReference, CancellationToken cancellationToken);

    /// <summary>A page of the children (a long array's range, M56); an adapter that doesn't page returns them all.</summary>
    Task<Variable[]> VariablesAsync(int variablesReference, VariablesPage page, CancellationToken cancellationToken) => VariablesAsync(variablesReference, cancellationToken);

    Task<EvaluateResponse> EvaluateAsync(string expression, int? frameId, string context, CancellationToken cancellationToken);

    Task<SetVariableResponse> SetVariableAsync(int variablesReference, string name, string value, CancellationToken cancellationToken);

    Task<SetExpressionResponse> SetExpressionAsync(string expression, string value, int? frameId, CancellationToken cancellationToken);

    Task<ExceptionInfoResponse> ExceptionInfoAsync(int threadId, CancellationToken cancellationToken);

    /// <summary>Ends the session: terminates a launched program, or detaches from an attached one.</summary>
    Task DisconnectAsync(bool terminateDebuggee, CancellationToken cancellationToken);
}
