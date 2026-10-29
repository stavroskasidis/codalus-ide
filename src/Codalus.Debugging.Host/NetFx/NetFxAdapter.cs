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
    public static async Task<int> RunAsync(Stream input, Stream output, TextWriter log, IClrDebugger? debugger = null, TextWriter? trace = null)
    {
        if (OperatingSystem.IsWindows())
        {
            NativeMethods.DoNotInheritStandardHandles();
        }

        using var server = new DapServer(input, output) { Trace = trace };
        var session = new NetFxDebugSession(server, trace is null ? log : new CoreClr.TeeWriter(log, trace), debugger ?? CreateDebugger());
        server.Handler = session.HandleAsync;
        using var stop = new CancellationTokenSource();
        var reading = server.RunAsync(stop.Token);
        await Task.WhenAny(reading, session.Ended).ConfigureAwait(false);

        // The session ended (or the client left): let the last events go out, then stop.
        await Task.Delay(200).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        return 0;
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
