using System.Diagnostics;
using System.Net.Sockets;

namespace DaedalusStudio.Workbench.Build;

/// <summary>
/// Waits until a web app accepts connections at its launch profile's address, for programs whose output the IDE can't
/// read (a console window of their own) or that don't log "Now listening on": the browser opens when the server is up.
/// </summary>
public static class ServerProbe
{
    /// <summary>
    /// True once <paramref name="url"/>'s host and port accept a connection; false when <paramref name="timeout"/> passes
    /// or <paramref name="running"/> turns false first.
    /// </summary>
    public static async Task<bool> WaitUntilListeningAsync(Uri url, TimeSpan timeout, Func<bool> running, CancellationToken cancellationToken)
    {
        var host = url.Host is "*" or "+" or "0.0.0.0" or "[::]" or "" ? "localhost" : url.Host;
        var started = Stopwatch.StartNew();
        while (started.Elapsed < timeout && running())
        {
            try
            {
                using var client = new TcpClient();
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(TimeSpan.FromMilliseconds(500));
                await client.ConnectAsync(host, url.Port, attempt.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }
}
