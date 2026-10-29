namespace Codalus.Debugging;

/// <summary>
/// CoreCLR's debugger adapter of our own (M54, ADR 0086): <c>codalus-debughost --clr-dap</c>, ADR 0076's adapter over
/// dbgshim. A launched program starts through <c>codalus-debughost --launch</c>, which keeps it a child of its own (for
/// its exit code) and starts it suspended until the adapter has registered for its runtime's startup.
/// </summary>
public static class CoreClrAdapter
{
    /// <summary>The debug companion host's argument that runs CoreCLR's debugger adapter over DAP on stdio.</summary>
    public const string AdapterArgument = "--clr-dap";

    /// <summary>
    /// The debug companion host's first argument for the launcher: <c>--launch &lt;pipe&gt; &lt;program&gt; [args]</c>
    /// starts the program for the adapter listening on the named pipe.
    /// </summary>
    public const string LaunchArgument = "--launch";

    /// <summary>
    /// A folder for the adapter's logs: each session writes its DAP traffic and its own messages to
    /// <c>clr-dap-&lt;time&gt;-&lt;id&gt;.log</c> there, to diagnose it (as <c>CODALUS_NETCOREDBG_LOG</c> does for netcoredbg).
    /// </summary>
    public const string LogVariable = "CODALUS_CLR_DEBUGGER_LOG";
}
