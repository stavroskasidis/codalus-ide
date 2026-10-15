namespace Codalus.Debugging.Host.Mono;

/// <summary><c>codalus-debughost --mono-dap</c>: the Mono debugger adapter's process (M20b), until the session ends.</summary>
internal static class MonoAdapter
{
    public static async Task<int> RunAsync(Stream input, Stream output, TextWriter log)
    {
        using var server = new DapServer(input, output);
        var session = new MonoDebugSession(server, log);
        server.Handler = session.HandleAsync;
        using var stop = new CancellationTokenSource();
        var reading = server.RunAsync(stop.Token);
        await Task.WhenAny(reading, session.Ended).ConfigureAwait(false);

        // The session ended (or the client left): let the last events go out, then stop.
        await Task.Delay(200).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        return 0;
    }
}
