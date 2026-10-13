using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Codalus.Remote.Protocol;
using Codalus.Rpc;

namespace Codalus.Remote.Server;

/// <summary>
/// Listening ports and tunnels (M21, ADR 0050): the client forwards a local port by opening a channel per connection,
/// and the server connects it to the port here. The server itself never listens.
/// </summary>
public sealed partial class RemoteServer
{
    private CancellationTokenSource? _portWatch;

    private void RegisterPorts()
    {
        connection.OnRequest(RemoteMethods.ListPorts, Json.EmptyResult, Json.PortsResult, (_, _) => Task.FromResult(new PortsResult(ListeningPorts())));
        connection.OnRequest(RemoteMethods.WatchPorts, Json.WatchPortsParams, Json.EmptyResult, (p, ct) =>
        {
            Interlocked.Exchange(ref _portWatch, null)?.Cancel();
            if (p.Enabled)
            {
                var watch = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _portWatch = watch;
                _ = WatchPortsAsync(TimeSpan.FromMilliseconds(Math.Clamp(p.IntervalMilliseconds, 200, 60000)), watch.Token);
            }

            return Task.FromResult(new EmptyResult());
        });
        connection.OnRequest(RemoteMethods.OpenTunnel, Json.TunnelParams, Json.EmptyResult, async (p, ct) =>
        {
            await OpenTunnelAsync(p, ct).ConfigureAwait(false);
            return new EmptyResult();
        });
    }

    private async Task WatchPortsAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        string? last = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var ports = ListeningPorts();
                var key = string.Join(',', ports.Select(p => p.Address + ":" + p.Port.ToString(CultureInfo.InvariantCulture)));
                if (key != last)
                {
                    last = key;
                    await Notify(RemoteMethods.PortsChanged, new PortsResult(ports), Json.PortsResult).ConfigureAwait(false);
                }

                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>TCP ports in the LISTEN state: <c>/proc/net/tcp*</c> on Linux (with their processes when they are the user's), <c>lsof</c> on macOS, the OS's table elsewhere.</summary>
    internal static IReadOnlyList<ListeningPort> ListeningPorts()
    {
        List<ListeningPort> ports;
        if (OperatingSystem.IsLinux() && File.Exists("/proc/net/tcp"))
        {
            ports = LinuxPorts();
        }
        else if (OperatingSystem.IsMacOS())
        {
            ports = MacPorts();
        }
        else
        {
            ports = [.. IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => new ListeningPort(e.Port, e.Address.ToString()))];
        }

        return [.. ports.GroupBy(p => (p.Port, p.Address)).Select(g => g.First()).OrderBy(p => p.Port).ThenBy(p => p.Address, StringComparer.Ordinal)];
    }

    private static List<ListeningPort> LinuxPorts()
    {
        var sockets = new Dictionary<string, (int Port, string Address)>(StringComparer.Ordinal);
        foreach (var (file, v6) in (ReadOnlySpan<(string, bool)>)[("/proc/net/tcp", false), ("/proc/net/tcp6", true)])
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var line in lines.Skip(1))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 10 || fields[3] != "0A")
                {
                    continue;
                }

                var local = fields[1].Split(':');
                if (local.Length != 2 || !int.TryParse(local[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var port))
                {
                    continue;
                }

                sockets[fields[9]] = (port, ParseProcAddress(local[0], v6));
            }
        }

        // Which process owns each socket: the user's own processes' descriptors (others' aren't readable).
        var owners = new Dictionary<string, (int Pid, string Name)>(StringComparer.Ordinal);
        if (sockets.Count > 0)
        {
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                {
                    continue;
                }

                try
                {
                    foreach (var fd in Directory.EnumerateFileSystemEntries(Path.Combine(directory, "fd")))
                    {
                        if (new FileInfo(fd).LinkTarget is { } target && target.StartsWith("socket:[", StringComparison.Ordinal))
                        {
                            var inode = target["socket:[".Length..^1];
                            if (sockets.ContainsKey(inode) && !owners.ContainsKey(inode))
                            {
                                owners[inode] = (pid, File.ReadAllText(Path.Combine(directory, "comm")).Trim());
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return [.. sockets.Select(s => owners.TryGetValue(s.Key, out var owner)
            ? new ListeningPort(s.Value.Port, s.Value.Address, owner.Pid, owner.Name)
            : new ListeningPort(s.Value.Port, s.Value.Address))];
    }

    /// <summary>An address of <c>/proc/net/tcp</c>: hex, each 32-bit word in host (little-endian) order.</summary>
    internal static string ParseProcAddress(string hex, bool v6)
    {
        var bytes = Convert.FromHexString(hex);
        for (var i = 0; i + 4 <= bytes.Length; i += 4)
        {
            Array.Reverse(bytes, i, 4);
        }

        return (bytes.Length == (v6 ? 16 : 4) ? new IPAddress(bytes) : IPAddress.None).ToString();
    }

    private static List<ListeningPort> MacPorts()
    {
        var ports = new List<ListeningPort>();
        try
        {
            using var process = Process.Start(new ProcessStartInfo("lsof", "-nP -iTCP -sTCP:LISTEN -Fpcn") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            int? pid = null;
            string? name = null;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                switch (line[0])
                {
                    case 'p':
                        pid = int.TryParse(line.AsSpan(1), CultureInfo.InvariantCulture, out var p) ? p : null;
                        break;
                    case 'c':
                        name = line[1..];
                        break;
                    case 'n':
                        var colon = line.LastIndexOf(':');
                        if (colon > 0 && int.TryParse(line.AsSpan(colon + 1), CultureInfo.InvariantCulture, out var port))
                        {
                            ports.Add(new ListeningPort(port, line[1..colon].Trim('[', ']'), pid, name));
                        }

                        break;
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ports.AddRange(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => new ListeningPort(e.Port, e.Address.ToString())));
        }

        return ports;
    }

    private async Task OpenTunnelAsync(TunnelParams p, CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(p.Host, p.Port, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            client.Dispose();
            throw new JsonRpcHandlerException(JsonRpcConnection.InternalError, $"Nothing accepts connections on {p.Host}:{p.Port.ToString(CultureInfo.InvariantCulture)}: {ex.Message}");
        }

        var channel = multiplexer.Open(p.Channel);
        var socket = client.GetStream();
        _ = Task.Run(
            async () =>
        {
            var toClient = Task.Run(async () =>
            {
                try
                {
                    await socket.CopyToAsync(channel.Stream, _lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                {
                }

                await channel.CloseOutputAsync().ConfigureAwait(false);
            });
            var toServer = Task.Run(async () =>
            {
                try
                {
                    await channel.Stream.CopyToAsync(socket, _lifetime.Token).ConfigureAwait(false);
                    client.Client.Shutdown(SocketShutdown.Send);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
                {
                }
            });
            await Task.WhenAll(toClient, toServer).ConfigureAwait(false);
            await channel.CloseAsync().ConfigureAwait(false);
            client.Dispose();
        },
            CancellationToken.None);
    }
}
