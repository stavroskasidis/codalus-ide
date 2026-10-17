using Codalus.Debugging.Host.Mono;
namespace Codalus.Debugging.Host.NetFx;

/// <summary><c>codalus-debughost --netfx-dap</c>: the desktop-CLR debugger adapter's process (M43b, ADR 0076), until the session ends.</summary>
internal static class NetFxAdapter
{
    public static async Task<int> RunAsync(Stream input, Stream output, TextWriter log)
    {
        if (OperatingSystem.IsWindows())
        {
            NativeMethods.DoNotInheritStandardHandles();
        }

        using var server = new DapServer(input, output);
        var session = new NetFxDebugSession(server, log, OperatingSystem.IsWindows() ? new CorDebugger() : new UnavailableDebugger());
        server.Handler = session.HandleAsync;
        using var stop = new CancellationTokenSource();
        var reading = server.RunAsync(stop.Token);
        await Task.WhenAny(reading, session.Ended).ConfigureAwait(false);

        // The session ended (or the client left): let the last events go out, then stop.
        await Task.Delay(200).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>Off Windows: the desktop CLR runs only there (D31).</summary>
    private sealed class UnavailableDebugger : IClrDebugger
    {
        private const string Message = ".NET Framework programs run and debug only on Windows.";

        public IClrProcess Launch(NetFxDebugArguments arguments, Action<string, string> output) => throw new DapRequestException(Message);

        public IClrProcess Attach(int processId) => throw new DapRequestException(Message);
    }
}
