using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DaedalusStudio.Workbench.Previewer;

/// <summary>A window of the running app.</summary>
public sealed record LiveWindow(int Id, string Title, string Type);

/// <summary>An element of the app's logical or visual tree; <see cref="ParentId"/> is 0 for the tree's root.</summary>
public sealed record LiveNode(int Id, int ParentId, string Type, string Name, bool Templated);

/// <summary>An Avalonia property of an element: its value as text, its type, where the value comes from.</summary>
public sealed record LiveProperty(string Name, string Value, string Type, string Source, bool IsReadOnly);

/// <summary>The window's class and the element's logical path from it (the agent's <c>path</c>).</summary>
public sealed record LivePath(string WindowType, IReadOnlyList<PreviewPathStep> Steps);

/// <summary>
/// The IDE's side of the live agent (M19b, ADR 0052) in a running Avalonia app: a loopback listener whose port and token
/// the app gets with the agent (<see cref="Environment"/>), the app's connection once it starts, its requests and its
/// events (<see cref="Picked"/>). One session per run; the app's exit ends the connection. Events arrive on any thread.
/// </summary>
public sealed class LiveSession : IAsyncDisposable
{
    public const string PortVariable = "DAEDALUS_LIVE_AGENT_PORT";
    public const string TokenVariable = "DAEDALUS_LIVE_AGENT_TOKEN";

    private const char Record = '\u001e';
    private const char Field = '\u001f';
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    private readonly TcpListener _listener;
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private readonly string _agent;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private TcpClient? _client;
    private StreamWriter? _writer;
    private int _nextId;

    private LiveSession(string agent)
    {
        _agent = agent;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = AcceptAsync();
    }

    /// <summary>A session for the next run, or null without the agent (<see cref="PreviewService.ResolveAgent"/>).</summary>
    public static LiveSession? Start(string? agent = null) =>
        (agent ?? PreviewService.ResolveAgent()) is { } path ? new LiveSession(path) : null;

    /// <summary>The app connected (any thread).</summary>
    public event EventHandler? Connected;

    /// <summary>The app's connection ended: it exited (any thread).</summary>
    public event EventHandler? Disconnected;

    /// <summary>Pick mode's click: the element hit and the nearest element of the logical tree (any thread).</summary>
    public event EventHandler<(int Hit, int Element)>? Picked;

    public bool IsConnected => _writer is not null;

    /// <summary>
    /// What the app's process gets: the agent as a startup hook (after any it already has) and where to connect.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment(IReadOnlyDictionary<string, string>? existing)
    {
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
        var hooks = existing?.GetValueOrDefault(PreviewerSession.StartupHooksVariable) ?? System.Environment.GetEnvironmentVariable(PreviewerSession.StartupHooksVariable);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PreviewerSession.StartupHooksVariable] = string.IsNullOrEmpty(hooks) ? _agent : hooks + Path.PathSeparator + _agent,
            [PortVariable] = port,
            [TokenVariable] = _token,
        };
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
                client.NoDelay = true;
                var stream = client.GetStream();
                var reader = new StreamReader(stream, new UTF8Encoding(false));
                using var hello = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                hello.CancelAfter(s_timeout);
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(hello.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    line = null;
                }

                if (line != "hello\t" + _token)
                {
                    client.Dispose(); // not our app's agent
                    continue;
                }

                // A run of the app per session; a new run (a restart) replaces the old connection.
                _client?.Dispose();
                _client = client;
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
                Connected?.Invoke(this, EventArgs.Empty);
                await ReadAsync(reader).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task ReadAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync(_lifetime.Token).ConfigureAwait(false) is { } line)
            {
                var parts = line.Split('\t');
                if (parts[0] == "event")
                {
                    if (parts.Length >= 4 && parts[1] == "picked" && Int(parts[2]) is var hit && Int(parts[3]) is var element)
                    {
                        Picked?.Invoke(this, (hit, element));
                    }

                    continue;
                }

                if (parts.Length >= 2 && _pending.TryRemove(parts[0], out var request))
                {
                    var payload = line.Split('\t', 3) is { Length: 3 } split ? split[2] : string.Empty;
                    if (parts[1] == "ok")
                    {
                        request.TrySetResult(payload);
                    }
                    else
                    {
                        request.TrySetException(new PreviewerException(payload.Length > 0 ? payload : "The app's agent couldn't do that."));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }

        _writer = null;
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new PreviewerException("The app ended."));
        }

        _pending.Clear();
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    private async Task<string> RequestAsync(CancellationToken cancellationToken, params string[] request)
    {
        if (_writer is not { } writer)
        {
            throw new PreviewerException("The app isn't running with the live tools.");
        }

        var id = Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync((id + "\t" + string.Join('\t', request)).AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _pending.TryRemove(id, out _);
            throw new PreviewerException("The app ended.", ex);
        }
        finally
        {
            _writeGate.Release();
        }

        try
        {
            return await reply.Task.WaitAsync(s_timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(id, out _);
            throw new PreviewerException($"The app didn't answer '{request[0]}' within {s_timeout.TotalSeconds:0} s.");
        }
    }

    private static int Int(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static IEnumerable<string[]> Records(string reply) =>
        reply.Length == 0 ? [] : reply.Split(Record).Select(r => r.Split(Field));

    public async Task<IReadOnlyList<LiveWindow>> WindowsAsync(CancellationToken cancellationToken) =>
        [.. Records(await RequestAsync(cancellationToken, "windows").ConfigureAwait(false)).Where(r => r.Length >= 3).Select(r => new LiveWindow(Int(r[0]), r[1], r[2]))];

    public async Task<IReadOnlyList<LiveNode>> TreeAsync(int root, bool visual, CancellationToken cancellationToken) =>
        [.. Records(await RequestAsync(cancellationToken, "tree", Id(root), visual ? "visual" : "logical").ConfigureAwait(false))
            .Where(r => r.Length >= 5).Select(r => new LiveNode(Int(r[0]), Int(r[1]), r[2], r[3], r[4] == "1"))];

    public async Task<IReadOnlyList<LiveProperty>> PropertiesAsync(int element, CancellationToken cancellationToken) =>
        [.. Records(await RequestAsync(cancellationToken, "props", Id(element)).ConfigureAwait(false))
            .Where(r => r.Length >= 5).Select(r => new LiveProperty(r[0], r[1], r[2], r[3], r[4] == "1"))];

    /// <summary>Sets a local value from text, parsed as XAML reads it; throws <see cref="PreviewerException"/> with the agent's reason.</summary>
    public Task SetAsync(int element, string property, string value, CancellationToken cancellationToken) =>
        RequestAsync(cancellationToken, "set", Id(element), property, value.Replace('\t', ' ').Replace('\n', ' '));

    public Task HighlightAsync(int? element, CancellationToken cancellationToken) =>
        RequestAsync(cancellationToken, "highlight", element is { } id ? Id(id) : "none");

    public Task PickAsync(bool on, CancellationToken cancellationToken) => RequestAsync(cancellationToken, "pick", on ? "on" : "off");

    public async Task<LivePath> PathAsync(int element, CancellationToken cancellationToken)
    {
        var reply = (await RequestAsync(cancellationToken, "path", Id(element)).ConfigureAwait(false)).Split('\t', 2);
        var steps = reply.Length < 2 ? [] : reply[1].Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split(':')).Select(p => new PreviewPathStep(
            p[0],
            p.Length > 1 ? Int(p[1]) : 0,
            p.Length > 2 && p[2].Length > 0 ? p[2] : null)).ToList();
        return new LivePath(reply[0], steps);
    }

    private static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);

    public ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _listener.Stop();
        _client?.Dispose();
        _lifetime.Dispose();
        return ValueTask.CompletedTask;
    }
}
