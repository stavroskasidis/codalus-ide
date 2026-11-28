using Codalus.Debugging.Host.Mono;
namespace Codalus.Debugging.Host.NetFx;

/// <summary>
/// <c>codalus-debughost --netfx-dap</c>: the desktop-CLR debugger adapter's process (M43b, ADR 0076), until the session
/// ends; with <c>--clr-dap</c>, the same session over dbgshim for CoreCLR (M54, ADR 0086).
/// </summary>
internal static class NetFxAdapter
{
    /// <param name="debugger">CoreCLR's (<see cref="CoreClr.DbgShimDebugger"/>); the desktop CLR's when null.</param>
    /// <param name="trace">A log of the DAP traffic and the session's messages (<see cref="CoreClrAdapter.LogVariable"/>).</param>
    /// <param name="configured">Given the session's <see cref="NetFxDebugSession.Configured"/>, as it starts: the in-app debugger holds the app's start until then (ADR 0117).</param>
    public static async Task<int> RunAsync(Stream input, Stream output, TextWriter log, IClrDebugger? debugger = null, TextWriter? trace = null, Action<Task>? configured = null)
    {
        if (OperatingSystem.IsWindows())
        {
            NativeMethods.DoNotInheritStandardHandles();
        }

        // A crash says so in the log before the process goes, and the adapter's end says which end it was. On Linux CI
        // (PR #530's run 45335957581, DebuggerBundleTests) a CoreCLR session's log ended with "the launcher says the
        // program ended with exit code 15" and nothing after it: no exited, no terminated, and the IDE saw the connection
        // close with the adapter's process down to one thread. Whether it crashed, its client's stream ended or a read
        // threw, nothing said; now the log does (docs/ci-flakes.md).
        UnhandledExceptionEventHandler? crashed = trace is null ? null : (_, e) => Log(trace, $"[adapter] unhandled exception, the process ends: {e.ExceptionObject}");
        if (crashed is not null)
        {
            AppDomain.CurrentDomain.UnhandledException += crashed;
        }

        try
        {
            using var server = new DapServer(input, output) { Trace = trace };
            using var session = new NetFxDebugSession(server, trace is null ? log : new CoreClr.TeeWriter(log, trace), debugger ?? CreateDebugger()) { Trace = trace };
            server.Handler = session.HandleAsync;
            configured?.Invoke(session.Configured);
            using var stop = new CancellationTokenSource();
            var reading = server.RunAsync(stop.Token);
            await Task.WhenAny(reading, session.Ended).ConfigureAwait(false);
            if (trace is not null)
            {
                Log(trace, "[adapter] ends: " + WhyItEnds(reading, session.Ended));
            }

            // The session ended, its exited and terminated written (or the client left): let the last answer (disconnect's)
            // go out, then stop.
            await Task.Delay(200).ConfigureAwait(false);
            await stop.CancelAsync().ConfigureAwait(false);
            return 0;
        }
        finally
        {
            if (crashed is not null)
            {
                AppDomain.CurrentDomain.UnhandledException -= crashed;
            }
        }
    }

    /// <summary>Which of the adapter's two ends came: its session's, or its client's stream's (and how).</summary>
    internal static string WhyItEnds(Task reading, Task ended) =>
        ended.IsCompleted ? "the session ended (exited and terminated written, or their wait over)"
        : reading.IsFaulted ? $"reading the client's stream failed: {reading.Exception?.GetBaseException().GetType().Name}: {reading.Exception?.GetBaseException().Message}"
        : reading.IsCanceled ? "reading the client's stream was cancelled"
        : "the client's stream ended";

    private static void Log(TextWriter trace, string line)
    {
        try
        {
            trace.WriteLine(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The log went first: nothing to tell.
        }
    }

    /// <summary>The desktop CLR's debugger on Windows; off Windows one that says .NET Framework runs only there.</summary>
    internal static IClrDebugger CreateDebugger() => OperatingSystem.IsWindows() ? new CorDebugger() : new UnavailableDebugger();

    /// <summary>Off Windows: the desktop CLR runs only there (D31).</summary>
    private sealed class UnavailableDebugger : IClrDebugger
    {
        private const string Message = ".NET Framework programs run and debug only on Windows.";

        public IClrProcess Launch(NetFxDebugArguments arguments, Action<string, string> output) => throw new DapRequestException(Message);

        public IClrProcess Attach(int processId) => throw new DapRequestException(Message);
    }
}
