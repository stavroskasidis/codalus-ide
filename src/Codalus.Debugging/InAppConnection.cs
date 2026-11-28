using System.Net.Sockets;
using Codalus.Rpc;

namespace Codalus.Debugging;

/// <summary>
/// The IDE's end of the in-app debugger's DAP (ADR 0117): a TCP connection to the app on the device, used as a debug
/// adapter's process (<see cref="DebugAdapterStartInfo.Launcher"/>), so the session is the same as with any adapter.
/// The app serves only once it runs, after its build and install, so <see cref="ConnectAsync"/> tries until it answers.
/// </summary>
public sealed class InAppConnection : IHostProcess, IProcessLauncher
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private InAppConnection(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    /// <summary>
    /// Connects to <paramref name="port"/> at the address <paramref name="address"/> gives (asked again on each try: a
    /// device's tunnel comes up with the install), every <paramref name="interval"/>, until it answers. Throws
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled (the run ended,
    /// the session was stopped), and <see cref="TimeoutException"/> after <paramref name="limit"/>.
    /// </summary>
    public static async Task<InAppConnection> ConnectAsync(Func<CancellationToken, Task<string?>> address, int port, TimeSpan interval, TimeSpan limit, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        string? last = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await address(cancellationToken).ConfigureAwait(false) is { Length: > 0 } host)
            {
                var client = new TcpClient(System.Net.IPAddress.TryParse(host, out var ip) ? ip.AddressFamily : AddressFamily.InterNetworkV6);
                try
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    attempt.CancelAfter(TimeSpan.FromSeconds(5));
                    await client.ConnectAsync(host, port, attempt.Token).ConfigureAwait(false);
                    client.NoDelay = true;
                    return new InAppConnection(client);
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    client.Dispose();
                    last = ex.Message;
                }
            }
            else
            {
                last = "the device has no tunnel address";
            }

            if (started.Elapsed >= limit)
            {
                throw new TimeoutException($"The app's debugger didn't answer on port {port} in {limit.TotalMinutes:0} minutes ({last}).");
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    public Stream Output => _stream;

    public Stream Input => _stream;

    public int? Id => null;

    public Task<int?> Exited => _exited.Task;

    public event Action<string>? ErrorLine
    {
        add { }
        remove { }
    }

    public void BeginErrorRead()
    {
    }

    /// <summary>Closes the connection: the app's debugger waits for the next one, or ends with the app.</summary>
    public void Kill()
    {
        _client.Dispose();
        _exited.TrySetResult(null);
    }

    public ValueTask DisposeAsync()
    {
        Kill();
        return ValueTask.CompletedTask;
    }

    /// <summary>The connection, as the adapter's "process": it is already there.</summary>
    Task<IHostProcess> IProcessLauncher.StartAsync(ProcessLaunchInfo launch, CancellationToken cancellationToken) =>
        Task.FromResult<IHostProcess>(this);

    /// <summary>The adapter's start info for this connection: the Codalus debugger, in the app on <paramref name="device"/>.</summary>
    public DebugAdapterStartInfo Adapter(string device) =>
        new("codalus-in-app-debugger", [], Transport: device) { Launcher = this, IsCodalus = true };
}
