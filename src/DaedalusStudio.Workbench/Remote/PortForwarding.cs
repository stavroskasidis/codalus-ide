using System.Globalization;
using System.Net;
using System.Net.Sockets;
using DaedalusStudio.Remote.Protocol;

namespace DaedalusStudio.Workbench.Remote;

/// <summary>A forwarded port: <see cref="LocalPort"/> on this machine's loopback reaches <see cref="RemotePort"/> there.</summary>
/// <param name="Origin"><c>auto</c> (detected), <c>user</c> (added) or <c>debug</c> (a debug session's).</param>
public sealed record ForwardedPort(int RemotePort, int LocalPort, string Origin, string? ProcessName = null)
{
    public string LocalAddress => "localhost:" + LocalPort.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Port forwarding for a remote session (M21, ADR 0049): a listener on this machine's loopback for each forwarded
/// port; every connection to it is a tunnel channel that the server connects to the port on its machine. With
/// <see cref="AutoForward"/>, ports that start listening there after the session began are forwarded as they appear
/// (a web app's, a debug session's), on the same number when it is free here.
/// </summary>
public sealed class PortForwarder : IAsyncDisposable
{
    private readonly RemoteConnection _connection;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, (ForwardedPort Port, TcpListener Listener, CancellationTokenSource Stop)> _forwards = [];
    private readonly HashSet<int> _initial = [];
    private readonly HashSet<int> _dismissed = [];
    private IReadOnlyList<ListeningPort> _listening = [];
    private bool _watching;

    public PortForwarder(RemoteConnection connection)
    {
        _connection = connection;
        connection.PortsChanged += (_, ports) => OnPortsChanged(ports);
    }

    /// <summary>Forward ports that start listening on the remote machine (setting <c>remote.autoForwardPorts</c>).</summary>
    public bool AutoForward { get; set; } = true;

    public IReadOnlyList<ForwardedPort> Forwarded
    {
        get
        {
            lock (_gate)
            {
                return [.. _forwards.Values.Select(f => f.Port).OrderBy(p => p.RemotePort)];
            }
        }
    }

    /// <summary>The remote machine's listening ports, as last reported.</summary>
    public IReadOnlyList<ListeningPort> Listening => _listening;

    /// <summary>The forwarded ports or the listening ones changed (any thread).</summary>
    public event EventHandler? Changed;

    /// <summary>A port was forwarded automatically (for a notification).</summary>
    public event EventHandler<ForwardedPort>? AutoForwarded;

    /// <summary>Starts watching the remote machine's ports; the ones listening now are left alone by automatic forwarding.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var now = await _connection.ListPortsAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _initial.UnionWith(now.Select(p => p.Port));
            _listening = now;
            _watching = true;
        }

        await _connection.WatchPortsAsync(true, TimeSpan.FromMilliseconds(1500), cancellationToken).ConfigureAwait(false);
    }

    private void OnPortsChanged(IReadOnlyList<ListeningPort> ports)
    {
        List<ListeningPort> fresh;
        lock (_gate)
        {
            _listening = ports;
            // Our own listeners are left out: a server on this machine (the stand-in) sees them listening too.
            var ours = _forwards.Values.Select(f => f.Port.LocalPort).ToHashSet();
            fresh = !_watching || !AutoForward ? [] : [.. ports.Where(p => !_initial.Contains(p.Port) && !_dismissed.Contains(p.Port) && !_forwards.ContainsKey(p.Port) && !ours.Contains(p.Port)).DistinctBy(p => p.Port)];
        }

        foreach (var port in fresh)
        {
            try
            {
                if (TryForward(port.Port, null, "auto", port.ProcessName, automatic: true) is { } forwarded)
                {
                    AutoForwarded?.Invoke(this, forwarded);
                }
            }
            catch (SocketException)
            {
            }
        }

        // A forwarded port whose program stopped listening stays forwarded (it may restart); its row shows it.
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Forwards <paramref name="remotePort"/>: on the same number here when it is free (so <c>localhost:5000</c> in a
    /// launch profile works), else on <paramref name="localPort"/> or any free one.
    /// </summary>
    public ForwardedPort Forward(int remotePort, int? localPort = null, string origin = "user", string? processName = null) =>
        TryForward(remotePort, localPort, origin, processName, automatic: false)!;

    /// <summary>Forwards a port; an automatic forward of a port the user stopped (or one already forwarded) is skipped (null).</summary>
    private ForwardedPort? TryForward(int remotePort, int? localPort, string origin, string? processName, bool automatic)
    {
        ForwardedPort forwarded;
        TcpListener listener;
        CancellationTokenSource stop;
        lock (_gate)
        {
            if (_forwards.TryGetValue(remotePort, out var existing))
            {
                return automatic ? null : existing.Port;
            }

            if (automatic && _dismissed.Contains(remotePort))
            {
                return null;
            }

            listener = Listen(localPort ?? remotePort) ?? (localPort is null ? Listen(0) : null)
                ?? throw new SocketException((int)SocketError.AddressAlreadyInUse);
            forwarded = new ForwardedPort(remotePort, ((IPEndPoint)listener.LocalEndpoint).Port, origin, processName);
            stop = new CancellationTokenSource();
            _forwards[remotePort] = (forwarded, listener, stop);
            _dismissed.Remove(remotePort);
        }

        _ = AcceptAsync(listener, remotePort, stop.Token);
        Changed?.Invoke(this, EventArgs.Empty);
        return forwarded;
    }

    private static TcpListener? Listen(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
            return listener;
        }
        catch (SocketException)
        {
            listener.Dispose();
            return null;
        }
    }

    /// <summary>Stops forwarding <paramref name="remotePort"/> (and keeps automatic forwarding from adding it back).</summary>
    public void Stop(int remotePort)
    {
        (ForwardedPort Port, TcpListener Listener, CancellationTokenSource Stop) entry;
        lock (_gate)
        {
            if (!_forwards.Remove(remotePort, out entry))
            {
                return;
            }

            _dismissed.Add(remotePort);
        }

        entry.Stop.Cancel();
        entry.Listener.Stop();
        entry.Listener.Dispose();
        entry.Stop.Dispose();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task AcceptAsync(TcpListener listener, int remotePort, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = TunnelAsync(client, remotePort, cancellationToken);
        }
    }

    private async Task TunnelAsync(TcpClient client, int remotePort, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            Rpc.Multiplexing.MuxChannel channel;
            try
            {
                channel = await _connection.OpenTunnelAsync("localhost", remotePort, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or IOException or OperationCanceledException)
            {
                return; // nothing listens there (any more): the connection here just closes
            }

            var socket = client.GetStream();
            var up = Task.Run(async () =>
            {
                try
                {
                    await socket.CopyToAsync(channel.Stream, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                {
                }

                await channel.CloseOutputAsync().ConfigureAwait(false);
            }, CancellationToken.None);
            var down = Task.Run(async () =>
            {
                try
                {
                    await channel.Stream.CopyToAsync(socket, cancellationToken).ConfigureAwait(false);
                    client.Client.Shutdown(SocketShutdown.Send);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
                {
                }
            }, CancellationToken.None);
            await Task.WhenAll(up, down).ConfigureAwait(false);
            await channel.CloseAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var port in Forwarded)
        {
            Stop(port.RemotePort);
        }

        if (_connection.IsConnected)
        {
            try
            {
                await _connection.WatchPortsAsync(false, TimeSpan.Zero, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or IOException)
            {
            }
        }
    }
}
