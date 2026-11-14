using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Codalus.Workbench.Previewer;

/// <summary>A window of the running app.</summary>
public sealed record LiveWindow(int Id, string Title, string Type);

/// <summary>An element of the app's logical or visual tree; <see cref="ParentId"/> is 0 for the tree's root.</summary>
public sealed record LiveNode(int Id, int ParentId, string Type, string Name, bool Templated);

/// <summary>An Avalonia property of an element: its value as text, its type, where the value comes from.</summary>
public sealed record LiveProperty(string Name, string Value, string Type, string Source, bool IsReadOnly);

/// <summary>The window's class and the element's logical path from it (the agent's <c>path</c>).</summary>
public sealed record LivePath(string WindowType, IReadOnlyList<PreviewPathStep> Steps);

/// <summary>
/// A binding that failed in the running app (M64): the target property, the binding's path, the framework's message,
/// the element's type (its full name when the element was found, else as the log named it), its name, and the classes
/// it sits in, nearest first (Avalonia only).
/// </summary>
public sealed record LiveBindingFailure(string Property, string Path, string Message, string ElementType, string ElementName, IReadOnlyList<string> Ancestors);

/// <summary>What a XAML Hot Reload did: applied to <see cref="Count"/> instances, failed (at a line when known), or nothing to reload live (no <c>x:Class</c>).</summary>
public sealed record LiveReloadResult(LiveReloadOutcome Outcome, int Count, int Line, int Column, string Message);

public enum LiveReloadOutcome
{
    Applied,
    Failed,
    NotApplicable,
}

/// <summary>
/// Why the app stopped at the agent's break (M64b): <see cref="Kind"/> is <c>element</c> (created), <c>attribute</c>
/// (its property set), <c>value</c> (Break When Value Changes) or <c>binding</c> (a binding failed); the breakpoint's
/// id, the XAML file, line and column (1-based; 0 when the element has no source information), the element, the
/// property, its old and new values, a failed binding's message and path, and the classes its element sits in.
/// </summary>
public sealed record LiveXamlStop(
    string Kind,
    string BreakpointId,
    string File,
    int Line,
    int Column,
    string ElementType,
    string ElementName,
    string Property,
    string OldValue,
    string NewValue,
    string Message,
    string BindingPath,
    IReadOnlyList<string> Ancestors);

/// <summary>What the agent said about a XAML breakpoint: whether it can stop, and why not.</summary>
public sealed record LiveBreakpointState(string Id, bool Bound, string Message);

public enum LiveStyleState
{
    /// <summary>The style applies to the element now.</summary>
    Applies,

    /// <summary>Its selector matches the element's type, but a condition (a class, a pseudo-class, a trigger) is off now.</summary>
    Inactive,

    /// <summary>It doesn't apply: <see cref="LiveStyle.Reason"/> names the part that fails.</summary>
    No,
}

/// <summary>
/// A style of the element's scope and why it applies or doesn't (M64b): its kind (Avalonia's <c>style</c>,
/// <c>theme</c>, <c>nested</c>; WPF's <c>style</c>, <c>basedOn</c>, <c>trigger</c>), its selector or trigger, its
/// state, the reason (the part that fails), its setters (with those a higher priority overrides), and where the XAML
/// declares it (Avalonia's source information; empty and 0 when unknown).
/// </summary>
public sealed record LiveStyle(string Kind, string Selector, LiveStyleState State, string Reason, IReadOnlyList<string> Setters, string File, int Line);

/// <summary>
/// The IDE's side of the live agent (M19b, ADR 0052) in a running Avalonia app, or WPF app (M40b, ADR 0072: the WPF
/// previewer's assembly is its agent, with the same protocol): a loopback listener whose port and token
/// the app gets with the agent (<see cref="Environment"/>), the app's connection once it starts, its requests and its
/// events (<see cref="Picked"/>). One session per run; the app's exit ends the connection. Events arrive on any thread.
/// </summary>
public sealed class LiveSession : IAsyncDisposable
{
    public const string PortVariable = "CODALUS_LIVE_AGENT_PORT";
    public const string TokenVariable = "CODALUS_LIVE_AGENT_TOKEN";

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
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    /// <summary>The loopback port the app's agent connects to (kept after the session ends).</summary>
    public int Port { get; }

    /// <summary>A session for the next run, or null without the agent (<see cref="PreviewService.ResolveAgent"/>).</summary>
    public static LiveSession? Start(string? agent = null) =>
        (agent ?? PreviewService.ResolveAgent()) is { } path ? new LiveSession(path) : null;

    /// <summary>The app connected (any thread).</summary>
    public event EventHandler? Connected;

    /// <summary>The app's connection ended: it exited (any thread).</summary>
    public event EventHandler? Disconnected;

    /// <summary>Pick mode's click: the element hit and the nearest element of the logical tree (any thread).</summary>
    public event EventHandler<(int Hit, int Element)>? Picked;

    /// <summary>Bindings failed in the app since the last <see cref="BindingFailuresAsync"/> (M64; any thread).</summary>
    public event EventHandler? BindingsFailed;

    /// <summary>The agent's XAML breakpoints' states changed: ask them again (M64b; any thread).</summary>
    public event EventHandler? XamlBreakpointsChanged;

    /// <summary>The agent is about to break into the debugger, and says why (M64b; any thread).</summary>
    public event EventHandler<LiveXamlStop>? XamlStopping;

    /// <summary>The kinds of the agent's events so far, in order (diagnostics for a failed test).</summary>
    internal ConcurrentQueue<string> EventLog { get; } = new();

    private readonly ConcurrentQueue<LiveXamlStop> _stops = new();
    private readonly SemaphoreSlim _stopSignal = new(0);

    public bool IsConnected => _writer is not null;

    /// <summary>
    /// What the app's process gets: the agent as a startup hook (after any it already has) and where to connect.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment(IReadOnlyDictionary<string, string>? existing, string? breakpoints = null)
    {
        var port = Port.ToString(CultureInfo.InvariantCulture);
        var hooks = existing?.GetValueOrDefault(PreviewerSession.StartupHooksVariable) ?? System.Environment.GetEnvironmentVariable(PreviewerSession.StartupHooksVariable);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PreviewerSession.StartupHooksVariable] = string.IsNullOrEmpty(hooks) ? _agent : hooks + Path.PathSeparator + _agent,
            [PortVariable] = port,
            [TokenVariable] = _token,
        };
        if (breakpoints is not null)
        {
            // XAML breakpoints (M64b): the agent arms them before the app's first XAML.
            environment[BreakpointsVariable] = breakpoints;
        }

        return environment;
    }

    /// <summary>The XAML breakpoints at launch (<see cref="XamlBreakpoints.Encode"/>'s text), which the agent reads and clears (M64b).</summary>
    public const string BreakpointsVariable = "CODALUS_LIVE_AGENT_BREAKPOINTS";

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
                    EventLog.Enqueue(parts.Length > 1 ? parts[1] : string.Empty);
                    if (parts.Length >= 4 && parts[1] == "picked" && Int(parts[2]) is var hit && Int(parts[3]) is var element)
                    {
                        Picked?.Invoke(this, (hit, element));
                    }
                    else if (parts.Length >= 2 && parts[1] == "bindings")
                    {
                        BindingsFailed?.Invoke(this, EventArgs.Empty);
                    }
                    else if (parts.Length >= 2 && parts[1] == "xbreaks")
                    {
                        XamlBreakpointsChanged?.Invoke(this, EventArgs.Empty);
                    }
                    else if (parts.Length >= 3 && parts[1] == "xstop" && ParseStop(parts[2]) is { } stop)
                    {
                        _stops.Enqueue(stop);
                        _stopSignal.Release();
                        XamlStopping?.Invoke(this, stop);
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

    private Task<string> RequestAsync(CancellationToken cancellationToken, params string[] request) => RequestAsync(s_timeout, cancellationToken, request);

    private async Task<string> RequestAsync(TimeSpan timeout, CancellationToken cancellationToken, params string[] request)
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
            return await reply.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(id, out _);
            throw new PreviewerException($"The app didn't answer '{request[0]}' within {timeout.TotalSeconds:0} s.");
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

    /// <summary>The bindings that failed in the app since the last call (M64).</summary>
    public async Task<IReadOnlyList<LiveBindingFailure>> BindingFailuresAsync(CancellationToken cancellationToken) =>
        [.. Records(await RequestAsync(cancellationToken, "bindings").ConfigureAwait(false))
            .Where(r => r.Length >= 6)
            .Select(r => new LiveBindingFailure(r[0], r[1], r[2], r[3], r[4], r[5].Split('|', StringSplitOptions.RemoveEmptyEntries)))];

    /// <summary>
    /// XAML Hot Reload (M64): the saved XAML loaded again into the app's live instances of its <c>x:Class</c>. Avalonia's
    /// agent compiles it with <paramref name="loader"/> (the app's <c>Avalonia.Designer.HostApp.dll</c>), the file's
    /// assembly and <c>avares://</c> URI; WPF's ignores them. A minute at most: the first reload loads the compiler.
    /// </summary>
    public async Task<LiveReloadResult> ReloadAsync(string loader, string assemblyName, string uri, bool compiledBindings, string xaml, CancellationToken cancellationToken, string? path = null)
    {
        // With the file's path (M64b), the reloaded elements keep their XAML lines (Avalonia 12): XAML breakpoints still stop.
        var reply = await RequestAsync(TimeSpan.FromMinutes(1), cancellationToken, "reload", loader, assemblyName, uri, compiledBindings ? "1" : "0",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(xaml)), (path ?? "-").Replace('\t', ' ')).ConfigureAwait(false);
        var parts = reply.Split('\t', 4);
        return parts[0] switch
        {
            "applied" => new LiveReloadResult(LiveReloadOutcome.Applied, parts.Length > 1 ? Int(parts[1]) : 0, 0, 0, string.Empty),
            "failed" when parts.Length == 4 => new LiveReloadResult(LiveReloadOutcome.Failed, 0, Int(parts[1]), Int(parts[2]), parts[3]),
            "none" => new LiveReloadResult(LiveReloadOutcome.NotApplicable, 0, 0, 0, string.Empty),
            _ => new LiveReloadResult(LiveReloadOutcome.Failed, 0, 0, 0, reply),
        };
    }

    private static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ XAML breakpoints (M64b)

    private static LiveXamlStop? ParseStop(string text)
    {
        var f = text.Split(Field);
        return f.Length < 13
            ? null
            : new LiveXamlStop(f[0], f[1], f[2], Int(f[3]), Int(f[4]), f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12].Split('|', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// The stop the agent said it was about to make: the last one told, once (any before it are dropped), waiting up to
    /// <paramref name="timeout"/> for one when none has come yet (the agent tells it before it breaks, but the
    /// debugger's event can still arrive first). Null when none came.
    /// </summary>
    public async Task<LiveXamlStop?> TakeStopAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!await _stopSignal.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        LiveXamlStop? last = null;
        while (_stops.TryDequeue(out var stop))
        {
            last = stop;
        }

        while (_stopSignal.CurrentCount > 0 && _stopSignal.Wait(0, CancellationToken.None))
        {
        }

        return last;
    }

    /// <summary>The XAML breakpoints (<see cref="XamlBreakpoints.Encode"/>'s text), replacing the agent's; what it says of each.</summary>
    public async Task<IReadOnlyList<LiveBreakpointState>> SetXamlBreakpointsAsync(string encoded, CancellationToken cancellationToken) =>
        [.. Records(await RequestAsync(cancellationToken, "xbreaks", encoded).ConfigureAwait(false))
            .Where(r => r.Length >= 3).Select(r => new LiveBreakpointState(r[0], r[1] == "1", r[2]))];

    /// <summary>Break When Value Changes (M64b) on the element's property, or off.</summary>
    public Task WatchAsync(int element, string property, bool on, CancellationToken cancellationToken) =>
        RequestAsync(cancellationToken, "watch", Id(element), property, on ? "on" : "off");

    /// <summary>Why each style of the element's scope applies to it or doesn't (M64b).</summary>
    public async Task<IReadOnlyList<LiveStyle>> StylesAsync(int element, CancellationToken cancellationToken) =>
        [.. Records(await RequestAsync(cancellationToken, "styles", Id(element)).ConfigureAwait(false))
            .Where(r => r.Length >= 7)
            .Select(r => new LiveStyle(r[0], r[1], r[2] switch { "applies" => LiveStyleState.Applies, "inactive" => LiveStyleState.Inactive, _ => LiveStyleState.No },
                r[3], r[4].Split('|', StringSplitOptions.RemoveEmptyEntries), r[5], Int(r[6])))];

    public ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _listener.Stop();
        _client?.Dispose();
        _lifetime.Dispose();
        _stopSignal.Dispose();
        return ValueTask.CompletedTask;
    }
}
