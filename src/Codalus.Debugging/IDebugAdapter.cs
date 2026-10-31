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

    Task<Breakpoint[]> SetBreakpointsAsync(string path, SourceBreakpoint[] breakpoints, CancellationToken cancellationToken);

    Task<Breakpoint[]> SetFunctionBreakpointsAsync(FunctionBreakpoint[] breakpoints, CancellationToken cancellationToken);

    Task SetExceptionBreakpointsAsync(string[] filters, CancellationToken cancellationToken, ExceptionFilterOptions[]? options = null);

    Task ContinueAsync(int threadId, CancellationToken cancellationToken);

    Task NextAsync(int threadId, CancellationToken cancellationToken);

    Task StepInAsync(int threadId, CancellationToken cancellationToken);

    Task StepOutAsync(int threadId, CancellationToken cancellationToken);

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
