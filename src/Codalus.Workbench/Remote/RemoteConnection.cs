using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Codalus.Remote.Protocol;
using Codalus.Rpc;
using Codalus.Rpc.Multiplexing;

namespace Codalus.Workbench.Remote;

/// <summary>How to start <c>codalus-server</c>: a program whose stdin and stdout are the server's (ssh, wsl.exe, or the server here).</summary>
public sealed record RemoteTransport(string Program, IReadOnlyList<string> Arguments, string Display)
{
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

/// <summary>
/// The connection to a running <c>codalus-server</c> (M21, ADR 0050 and 0051): the transport process, the
/// multiplexer on its stdio, and the control channel. Channels for hosts, terminals and tunnels are opened here.
/// The server never listens on a port: its stdio, through the user's ssh or wsl.exe, is the only way in.
/// </summary>
public sealed class RemoteConnection : IAsyncDisposable
{
    private static readonly RemoteJsonContext Json = RemoteJsonContext.Default;
    private Process _transport;
    private readonly ChannelMultiplexer _multiplexer;
    private readonly JsonRpcConnection _control;
    private readonly ConcurrentDictionary<uint, ChannelProcess> _processes = new();
    private readonly ConcurrentDictionary<int, Action<IReadOnlyList<FileChange>>> _watches = new();
    private readonly StringBuilder _stderr = new();
    private int _disposed;

    private RemoteConnection(Process transport, ChannelMultiplexer multiplexer, JsonRpcConnection control, string display)
    {
        _transport = transport;
        _multiplexer = multiplexer;
        _control = control;
        Display = display;
    }

    public string Display { get; }

    private LatencyStream? _latency;

    /// <summary>
    /// The simulated round trip of a connection started with a latency (budgets, tests): changing it applies to what
    /// arrives from now on. Null when the connection has none.
    /// </summary>
    public TimeSpan? SimulatedLatency
    {
        get => _latency?.Delay;
        set
        {
            if (_latency is { } stream && value is { } delay)
            {
                stream.Delay = delay;
            }
        }
    }

    public HelloResult Server { get; private set; } = null!;

    public JsonRpcConnection Control => _control;

    public ChannelMultiplexer Multiplexer => _multiplexer;

    public bool IsConnected => _multiplexer.IsConnected && Volatile.Read(ref _disposed) == 0;

    /// <summary>Lines the transport and the server wrote to stderr (ssh's messages, the server's log).</summary>
    public event EventHandler<string>? Log;

    /// <summary>The connection ended (the server exited, ssh lost the network…).</summary>
    public event EventHandler<Exception?>? Disconnected;

    /// <summary>The server's listening ports changed (after <see cref="WatchPortsAsync"/>).</summary>
    public event EventHandler<IReadOnlyList<ListeningPort>>? PortsChanged;

    /// <summary>
    /// Starts the server through <paramref name="transport"/>, waits for its preamble (skipping what a login shell
    /// printed first) and says hello. <paramref name="latency"/> simulates a slower network (budgets, tests). A server
    /// started with <c>--relay</c> is resumable: when the transport drops, it is started again and the session goes on.
    /// </summary>
    public static async Task<RemoteConnection> StartAsync(RemoteTransport transport, string clientVersion, CancellationToken cancellationToken, TimeSpan? latency = null, TimeSpan? timeout = null)
    {
        var link = await OpenLinkAsync(transport, timeout ?? TimeSpan.FromSeconds(60), null, cancellationToken).ConfigureAwait(false);
        Stream input = link.Frames;
        Stream output = link.Process.StandardInput.BaseStream;
        ResumableStream? resumable = null;
        if (link.Instance is not null)
        {
            resumable = new ResumableStream();
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wait.CancelAfter(TimeSpan.FromSeconds(30));
                await resumable.AttachAsync(input, output, wait.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException)
            {
                resumable.Close();
                Kill(link.Process, tree: false);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new InvalidOperationException($"The remote server on {transport.Display} didn't answer: {ex.Message}", ex);
            }

            input = output = resumable;
        }

        LatencyStream? simulated = null;
        if (latency is { } delay)
        {
            input = simulated = new LatencyStream(input, delay);
        }

        var multiplexer = new ChannelMultiplexer(input, output);
        var control = multiplexer.Start();
        var connection = new JsonRpcConnection(control.Stream, control.Stream);
        var remote = new RemoteConnection(link.Process, multiplexer, connection, transport.Display)
        {
            _latency = simulated,
            _transportSpec = transport,
            _resumable = resumable,
            _instance = link.Instance,
        };
        link.Log = line => remote.Log?.Invoke(remote, line);
        remote.AppendStderr(link.Stderr);
        remote.Configure();
        multiplexer.Disconnected += (_, error) => remote.OnDisconnected(error);
        if (resumable is not null)
        {
            resumable.LinkLost += (_, error) => remote.OnLinkLost(error);
            _ = remote.WatchLinkAsync();
        }

        connection.Start();
        try
        {
            remote.Server = await connection.InvokeAsync(RemoteMethods.Hello, new HelloParams(clientVersion, RemoteMethods.ProtocolVersion), Json.HelloParams, Json.HelloResult, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonRpcRemoteException or JsonRpcConnectionLostException or IOException)
        {
            await remote.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"The remote server on {transport.Display} didn't answer: {ex.Message}", ex);
        }

        return remote;
    }

    /// <summary>A started transport, past the server's preamble.</summary>
    private sealed class TransportLink(Process process, Stream frames, string? instance, StringBuilder stderr)
    {
        public Process Process { get; } = process;

        public Stream Frames { get; } = frames;

        /// <summary>A resumable server's instance (<c>--relay</c>); null for one on its stdio.</summary>
        public string? Instance { get; } = instance;

        public StringBuilder Stderr { get; } = stderr;

        public Action<string>? Log { get; set; }
    }

    /// <summary>Starts the transport and waits for the server's preamble; the process is killed if it doesn't come.</summary>
    private static async Task<TransportLink> OpenLinkAsync(RemoteTransport transport, TimeSpan timeout, Action<string>? log, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(transport.Program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in transport.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in Debugging.Remote.RemoteShell.Environment)
        {
            start.Environment[name] = value;
        }

        foreach (var (name, value) in transport.Environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException($"{transport.Program} did not start.");
        var stderr = new StringBuilder();
        TransportLink? link = null;
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                lock (stderr)
                {
                    if (stderr.Length < 8192)
                    {
                        stderr.AppendLine(line);
                    }
                }

                (link?.Log ?? log)?.Invoke(line);
            }
        };
        process.BeginErrorReadLine();

        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(timeout);
            var (frames, preamble) = await ReadPreambleAsync(process.StandardOutput.BaseStream, wait.Token).ConfigureAwait(false);
            var fields = preamble.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var instance = fields is [_, _, _, "resumable", var id, ..] ? id : null;
            link = new TransportLink(process, frames, instance, stderr) { Log = log };
            return link;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException)
        {
            Kill(process, tree: false);
            string said;
            lock (stderr)
            {
                said = stderr.ToString().Trim();
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new InvalidOperationException(
                $"The remote server didn't start on {transport.Display}{(ex is OperationCanceledException ? " in time" : string.Empty)}"
                + (said.Length > 0 ? ": " + Debugging.Remote.RemoteShell.Trim(said) : "."), ex);
        }
    }

    /// <summary>Reads up to and including the preamble line; returns the stream of frames that follow.</summary>
    internal static async Task<Stream> SkipToFramesAsync(Stream stream, CancellationToken cancellationToken) =>
        (await ReadPreambleAsync(stream, cancellationToken).ConfigureAwait(false)).Frames;

    /// <summary>Reads up to and including the preamble line; returns the frames that follow and the line.</summary>
    internal static async Task<(Stream Frames, string Preamble)> ReadPreambleAsync(Stream stream, CancellationToken cancellationToken)
    {
        var marker = Encoding.ASCII.GetBytes("\n" + RemoteMethods.Preamble + " ");
        var buffer = new byte[4096];
        var data = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The server's output ended before its preamble" + (data.Length > 0 ? ": " + Encoding.UTF8.GetString(data.ToArray()).Trim() : "."));
            }

            data.Write(buffer, 0, read);
            var bytes = data.GetBuffer().AsSpan(0, (int)data.Length);
            var at = bytes.IndexOf(marker);
            if (at < 0)
            {
                if (data.Length > 1024 * 1024)
                {
                    throw new InvalidDataException("The server printed a megabyte without its preamble.");
                }

                continue;
            }

            var end = bytes[(at + 1)..].IndexOf((byte)'\n');
            if (end < 0)
            {
                continue;
            }

            var line = Encoding.ASCII.GetString(bytes.Slice(at + 1, end)).TrimEnd('\r');
            var rest = bytes[(at + 1 + end + 1)..].ToArray();
            return (new PrefixedStream(rest, stream), line);
        }
    }

    // ------------------------------------------------------------------ reconnecting (M21b, ADR 0054)

    private RemoteTransport? _transportSpec;
    private ResumableStream? _resumable;
    private string? _instance;
    private int _reconnecting;

    /// <summary>How long a dropped link is retried before the session is given up (the server's grace is the same by default).</summary>
    public TimeSpan ReconnectFor { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a link may stay silent (a ping unanswered) before it's taken for dropped.</summary>
    public TimeSpan LinkTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Whether the server outlives a dropped link (started with <c>--relay</c>).</summary>
    public bool IsResumable => _resumable is not null;

    /// <summary>False while a resumable connection's link is down and being reconnected.</summary>
    public bool IsLinked => _resumable?.IsLinked ?? IsConnected;

    /// <summary>The link dropped; the connection is reconnecting (requests wait, nothing is lost).</summary>
    public event EventHandler<Exception?>? Reconnecting;

    /// <summary>The link is back, with how long it was down.</summary>
    public event EventHandler<TimeSpan>? Reconnected;

    /// <summary>How many times the link came back.</summary>
    public int Reconnections { get; private set; }

    private void AppendStderr(StringBuilder stderr)
    {
        lock (stderr)
        {
            lock (_stderr)
            {
                _stderr.Append(stderr);
            }
        }
    }

    /// <summary>Drops the current link as a network failure would (tests, the Reconnect command).</summary>
    public void DropLink()
    {
        Kill(Volatile.Read(ref _transport), tree: false);
        _resumable?.Detach(new IOException("The link was dropped."));
    }

    private void OnLinkLost(Exception? error)
    {
        if (Volatile.Read(ref _disposed) != 0 || _resumable is not { IsClosed: false } || Interlocked.Exchange(ref _reconnecting, 1) != 0)
        {
            return;
        }

        Kill(Volatile.Read(ref _transport), tree: false);
        Reconnecting?.Invoke(this, error);
        _ = Task.Run(ReconnectAsync, CancellationToken.None);
    }

    private async Task ReconnectAsync()
    {
        var down = Stopwatch.StartNew();
        var backoff = TimeSpan.FromMilliseconds(250);
        string? failure = null;
        while (Volatile.Read(ref _disposed) == 0 && down.Elapsed < ReconnectFor)
        {
            try
            {
                using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var link = await OpenLinkAsync(_transportSpec!, TimeSpan.FromSeconds(30), line => Log?.Invoke(this, line), attempt.Token).ConfigureAwait(false);
                if (link.Instance != _instance)
                {
                    // Another server answered: the one this session ran in has ended (its grace ran out, the machine restarted).
                    Kill(link.Process, tree: false);
                    failure = "The remote server's session ended while the connection was down.";
                    break;
                }

                var previous = Interlocked.Exchange(ref _transport, link.Process);
                previous.Dispose();
                try
                {
                    await _resumable!.AttachAsync(link.Frames, link.Process.StandardInput.BaseStream, attempt.Token).ConfigureAwait(false);
                }
                catch (InvalidDataException ex)
                {
                    Kill(link.Process, tree: false);
                    failure = ex.Message;
                    break;
                }

                Volatile.Write(ref _reconnecting, 0);
                Reconnections++;
                Reconnected?.Invoke(this, down.Elapsed);
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                Log?.Invoke(this, "Reconnecting: " + ex.Message);
            }

            await Task.Delay(backoff).ConfigureAwait(false);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromSeconds(10).Ticks));
        }

        // Given up: the session ends as a dropped connection did before.
        Log?.Invoke(this, failure ?? $"Gave up reconnecting after {down.Elapsed:mm\\:ss}.");
        _resumable?.Close();
    }

    /// <summary>A link can die without an error (a sleeping laptop, a dropped Wi-Fi): unanswered pings say so.</summary>
    private async Task WatchLinkAsync()
    {
        while (Volatile.Read(ref _disposed) == 0 && _resumable is { IsClosed: false } resumable)
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (!resumable.IsLinked || Volatile.Read(ref _reconnecting) != 0)
            {
                continue;
            }

            try
            {
                using var timeout = new CancellationTokenSource(LinkTimeout);
                await _multiplexer.PingAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (resumable.IsLinked && Volatile.Read(ref _reconnecting) == 0)
            {
                Log?.Invoke(this, $"No answer from the server in {LinkTimeout.TotalSeconds:0} s; reconnecting.");
                DropLink();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonRpcConnectionLostException or InvalidOperationException)
            {
            }
        }
    }

    private void Configure()
    {
        _control.OnNotification(RemoteMethods.Stderr, Json.StderrParams, p =>
        {
            if (_processes.TryGetValue(p.Channel, out var process))
            {
                process.OnErrorLine(p.Line);
            }
        });
        _control.OnNotification(RemoteMethods.Exited, Json.ExitedParams, p =>
        {
            if (_processes.TryRemove(p.Channel, out var process))
            {
                process.OnExited(p.ExitCode);
            }
        });
        _control.OnNotification(RemoteMethods.Changed, Json.FileChangesParams, p =>
        {
            if (_watches.TryGetValue(p.Id, out var handler))
            {
                handler(p.Changes);
            }
        });
        _control.OnNotification(RemoteMethods.PortsChanged, Json.PortsResult, p => PortsChanged?.Invoke(this, p.Ports));
    }

    private void OnDisconnected(Exception? error)
    {
        foreach (var process in _processes.Values)
        {
            process.OnExited(null);
        }

        _processes.Clear();
        Disconnected?.Invoke(this, error);
    }

    /// <summary>What the transport printed on stderr while starting (ssh's warnings).</summary>
    public string StartupMessages
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToString();
            }
        }
    }

    // ------------------------------------------------------------------ processes, terminals, tunnels

    /// <summary>Starts a process on the server, its stdio on a new channel (paths already in the server's terms).</summary>
    public async Task<ChannelProcess> SpawnAsync(SpawnParams parameters, CancellationToken cancellationToken, RemotePathMap? map = null)
    {
        var id = _multiplexer.NextId();
        var channel = _multiplexer.Open(id);
        var process = new ChannelProcess(this, channel) { Map = map };
        _processes[id] = process;
        try
        {
            var result = await _control.InvokeAsync(RemoteMethods.Spawn, parameters with { Channel = id }, Json.SpawnParams, Json.SpawnResult, cancellationToken).ConfigureAwait(false);
            process.Id = result.ProcessId;
            return process;
        }
        catch
        {
            _processes.TryRemove(id, out _);
            await channel.CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Starts a program in a pseudo-terminal on the server (null: the user's login shell).</summary>
    public async Task<ChannelProcess> OpenTerminalAsync(PtyOpenParams parameters, CancellationToken cancellationToken)
    {
        var id = _multiplexer.NextId();
        var channel = _multiplexer.Open(id);
        var process = new ChannelProcess(this, channel);
        _processes[id] = process;
        try
        {
            var result = await _control.InvokeAsync(RemoteMethods.PtyOpen, parameters with { Channel = id }, Json.PtyOpenParams, Json.SpawnResult, cancellationToken).ConfigureAwait(false);
            process.Id = result.ProcessId;
            return process;
        }
        catch
        {
            _processes.TryRemove(id, out _);
            await channel.CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task ResizeTerminalAsync(uint channel, int columns, int rows) =>
        _control.InvokeAsync(RemoteMethods.PtyResize, new PtyResizeParams(channel, columns, rows), Json.PtyResizeParams, Json.EmptyResult, CancellationToken.None);

    public Task KillAsync(uint channel) =>
        _control.InvokeAsync(RemoteMethods.Kill, new ChannelParams(channel), Json.ChannelParams, Json.EmptyResult, CancellationToken.None);

    /// <summary>A TCP connection the server makes to <paramref name="host"/>:<paramref name="port"/> on its machine, as a channel.</summary>
    public async Task<MuxChannel> OpenTunnelAsync(string host, int port, CancellationToken cancellationToken)
    {
        var id = _multiplexer.NextId();
        var channel = _multiplexer.Open(id);
        try
        {
            await _control.InvokeAsync(RemoteMethods.OpenTunnel, new TunnelParams(id, host, port), Json.TunnelParams, Json.EmptyResult, cancellationToken).ConfigureAwait(false);
            return channel;
        }
        catch
        {
            await channel.CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<ListeningPort>> ListPortsAsync(CancellationToken cancellationToken) =>
        (await _control.InvokeAsync(RemoteMethods.ListPorts, new EmptyResult(), Json.EmptyResult, Json.PortsResult, cancellationToken).ConfigureAwait(false)).Ports;

    public Task WatchPortsAsync(bool enabled, TimeSpan interval, CancellationToken cancellationToken) =>
        _control.InvokeAsync(RemoteMethods.WatchPorts, new WatchPortsParams(enabled, (int)interval.TotalMilliseconds), Json.WatchPortsParams, Json.EmptyResult, cancellationToken);

    private int _nextWatch;

    internal async Task<IAsyncDisposable> WatchAsync(string remotePath, bool recursive, Action<IReadOnlyList<FileChange>> changed, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextWatch);
        _watches[id] = changed;
        try
        {
            await _control.InvokeAsync(RemoteMethods.Watch, new WatchParams(id, remotePath, recursive), Json.WatchParams, Json.EmptyResult, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _watches.TryRemove(id, out _);
            throw;
        }

        return new AsyncDisposer(async () =>
        {
            if (_watches.TryRemove(id, out _) && IsConnected)
            {
                try
                {
                    await _control.InvokeAsync(RemoteMethods.Unwatch, new UnwatchParams(id), Json.UnwatchParams, Json.EmptyResult, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is JsonRpcRemoteException or JsonRpcConnectionLostException or IOException)
                {
                }
            }
        });
    }

    /// <summary>The transport's round trip, measured with a ping frame.</summary>
    public Task<TimeSpan> PingAsync(CancellationToken cancellationToken) => _multiplexer.PingAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_multiplexer.IsConnected)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _control.InvokeAsync(RemoteMethods.Shutdown, timeout.Token).ConfigureAwait(false);
                await _control.NotifyAsync(RemoteMethods.Exit, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or JsonRpcRemoteException or JsonRpcConnectionLostException or IOException or ObjectDisposedException)
            {
            }
        }

        await _control.DisposeAsync().ConfigureAwait(false);
        _resumable?.Close();
        await _multiplexer.DisposeAsync().ConfigureAwait(false);
        var exited = _transport.WaitForExitAsync();
        if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false) != exited)
        {
            Kill(_transport, tree: _resumable is null);
        }

        _transport.Dispose();
    }

    private static void Kill(Process process, bool tree = true)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: tree);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private sealed class AsyncDisposer(Func<Task> dispose) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await dispose().ConfigureAwait(false);
    }

    /// <summary>A prefix of bytes already read, then the rest of a stream.</summary>
    private sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
    {
        private int _offset;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }

            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// A process the server started, as an <see cref="IHostProcess"/>: its stdio is a channel, its stderr and exit come
/// as notifications. Hosts' channels carry <c>Content-Length</c>-framed JSON, rewritten through
/// <see cref="Map"/> when one is set.
/// </summary>
public sealed class ChannelProcess : IHostProcess
{
    private readonly RemoteConnection _connection;
    private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private List<string>? _early = [];
    private Stream? _mapped;

    internal ChannelProcess(RemoteConnection connection, MuxChannel channel)
    {
        _connection = connection;
        Channel = channel;
    }

    public MuxChannel Channel { get; }

    public int? Id { get; internal set; }

    /// <summary>Set before the first read or write: rewrites the paths of the JSON messages on the channel.</summary>
    public RemotePathMap? Map { get; init; }

    private Stream Stream => Map is { IsIdentity: false } map ? _mapped ??= new MappedJsonStream(Channel.Stream, map) : Channel.Stream;

    public Stream Output => Stream;

    public Stream Input => Stream;

    public Task<int?> Exited => _exited.Task;

    public event Action<string>? ErrorLine;

    public void BeginErrorRead()
    {
        List<string>? early;
        lock (_gate)
        {
            early = _early;
            _early = null;
        }

        foreach (var line in early ?? [])
        {
            ErrorLine?.Invoke(line);
        }
    }

    internal void OnErrorLine(string line)
    {
        lock (_gate)
        {
            if (_early is not null)
            {
                _early.Add(line);
                return;
            }
        }

        ErrorLine?.Invoke(line);
    }

    internal void OnExited(int? code) => _exited.TrySetResult(code);

    public void Kill()
    {
        if (!_exited.Task.IsCompleted && _connection.IsConnected)
        {
            _ = _connection.KillAsync(Channel.Id).ContinueWith(static _ => { }, TaskScheduler.Default);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_mapped is { } mapped)
        {
            await mapped.DisposeAsync().ConfigureAwait(false);
        }

        await Channel.CloseAsync().ConfigureAwait(false);
    }
}
