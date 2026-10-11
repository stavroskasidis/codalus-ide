using System.Collections.Concurrent;
using System.Text.Json;
using DaedalusStudio.Diagnostics.Host.Cpu;
using DaedalusStudio.Diagnostics.Host.Sessions;
using DaedalusStudio.Diagnostics.Protocol;
using DaedalusStudio.Rpc;

namespace DaedalusStudio.Diagnostics.Host;

/// <summary>
/// The diagnostics host's request handlers (ADR 0044, docs/diagnostics-protocol.md): live counter sessions, CPU trace
/// recordings, and opened traces with their views. Work runs on the thread pool, never on the RPC loop; long requests
/// (opening a large trace) are cancellable.
/// </summary>
internal sealed class DiagnosticsServer : IAsyncDisposable
{
    private readonly JsonRpcConnection _connection;
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, CounterSession> _counters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TraceRecording> _recordings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OpenedTrace> _traces = new(StringComparer.Ordinal);
    private int _shellProcess = -1;

    public DiagnosticsServer(JsonRpcConnection connection) => _connection = connection;

    public Task ExitRequested => _exit.Task;

    private static DiagnosticsJsonContext Json => DiagnosticsJsonContext.Default;

    public void Register()
    {
        var json = Json;
        _connection.OnRequest(DiagnosticsMethods.Initialize, json.DiagnosticsInitializeParams, json.DiagnosticsInitializeResult, (p, _) =>
        {
            _shellProcess = p.ProcessId;
            return Task.FromResult(new DiagnosticsInitializeResult(
                "daedalus-diaghost",
                typeof(DiagnosticsServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                DiagnosticsMethods.ProtocolVersion));
        });
        _connection.OnRequest(DiagnosticsMethods.Processes, json.DiagnosticsEmpty, json.ProcessesResult, (_, _) => Task.Run(() => new ProcessesResult(DiagnosticsPort.Processes(_shellProcess))));
        _connection.OnRequest(DiagnosticsMethods.Resume, json.ProcessIdParams, json.DiagnosticsEmpty, (p, ct) => Guard(async () =>
        {
            await DiagnosticsPort.ResumeAsync(p.ProcessId, ct).ConfigureAwait(false);
            return new DiagnosticsEmpty();
        }));
        _connection.OnRequest(DiagnosticsMethods.CountersStart, json.CountersStartParams, json.SessionStartResult, (p, ct) => Guard(() => StartCountersAsync(p, ct)));
        _connection.OnRequest(DiagnosticsMethods.CountersStop, json.SessionParams, json.DiagnosticsEmpty, (p, ct) => Guard(() => StopCountersAsync(p.Session, ct)));
        _connection.OnRequest(DiagnosticsMethods.TraceStart, json.TraceStartParams, json.SessionStartResult, (p, ct) => Guard(() => StartTraceAsync(p, ct)));
        _connection.OnRequest(DiagnosticsMethods.TraceStop, json.SessionParams, json.TraceStopResult, (p, ct) => Guard(() => StopTraceAsync(p.Session, ct)));
        _connection.OnRequest(DiagnosticsMethods.TraceOpen, json.TraceOpenParams, json.TraceSummary, (p, ct) => Guard(() => OpenTraceAsync(p.Path, ct)));
        _connection.OnRequest(DiagnosticsMethods.TraceClose, json.TraceIdParams, json.DiagnosticsEmpty, (p, _) => Guard(() =>
        {
            if (_traces.TryRemove(p.Trace, out var trace))
            {
                trace.Dispose();
            }

            return Task.FromResult(new DiagnosticsEmpty());
        }));
        _connection.OnRequest(DiagnosticsMethods.CpuTree, json.CpuTreeParams, json.CpuTreeResult, (p, _) => View(p.Trace, v => v.TopDown(p.Filter, p.Node, p.Offset, p.Count)));
        _connection.OnRequest(DiagnosticsMethods.CpuCallers, json.CpuCallersParams, json.CpuTreeResult, (p, _) => View(p.Trace, v => v.BottomUp(p.Filter, p.Path, p.Offset, p.Count)));
        _connection.OnRequest(DiagnosticsMethods.CpuFunctions, json.CpuFunctionsParams, json.CpuFunctionsResult, (p, _) => View(p.Trace, v => v.Functions(p.Filter, p.Search, p.Offset, p.Count)));
        _connection.OnRequest(DiagnosticsMethods.CpuCallerCallee, json.CpuCallerCalleeParams, json.CpuCallerCalleeResult, (p, _) => View(p.Trace, v => v.CallerCallee(p.Filter, p.Function)));
        _connection.OnRequest(DiagnosticsMethods.CpuFlame, json.CpuFlameParams, json.CpuFlameResult, (p, _) => View(p.Trace, v => v.Flame(p.Filter, p.Root, p.MinFraction, p.MaxFrames)));
        _connection.OnRequest(DiagnosticsMethods.CpuTimeline, json.CpuTimelineParams, json.CpuTimelineResult, (p, _) => View(p.Trace, v => v.Timeline(p.Filter, p.Buckets)));
        _connection.OnRequest(DiagnosticsMethods.CpuFiles, json.CpuFilesParams, json.CpuFilesResult, (p, ct) => Guard(async () =>
        {
            var trace = Trace(p.Trace);
            await trace.WaitForLinesAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            return trace.Views.Files(p.Filter);
        }));
        _connection.OnRequest(DiagnosticsMethods.CpuLines, json.CpuLinesParams, json.CpuLinesResult, (p, ct) => Guard(async () =>
        {
            var trace = Trace(p.Trace);
            await trace.WaitForLinesAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            return trace.Views.Lines(p.Filter, p.File);
        }));
        _connection.OnRequest(DiagnosticsMethods.Shutdown, (_, _) => Task.FromResult<JsonElement?>(null));
        _connection.OnNotification(DiagnosticsMethods.Exit, json.DiagnosticsEmpty, _ => _exit.TrySetResult());
    }

    /// <summary>Runs a handler on the thread pool and maps argument errors to <see cref="DiagnosticsErrorCodes.TraceError"/>.</summary>
    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await Task.Run(action).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or FormatException or NotSupportedException)
        {
            throw new JsonRpcHandlerException(DiagnosticsErrorCodes.TraceError, ex.Message);
        }
    }

    private Task<T> View<T>(string trace, Func<CpuViews, T> view) => Guard(() => Task.FromResult(view(Trace(trace).Views)));

    private OpenedTrace Trace(string id) =>
        _traces.TryGetValue(id, out var trace) ? trace : throw new JsonRpcHandlerException(DiagnosticsErrorCodes.UnknownTrace, "The trace isn't open. Open it again.");

    // ------------------------------------------------------------------ counters

    private async Task<SessionStartResult> StartCountersAsync(CountersStartParams parameters, CancellationToken cancellationToken)
    {
        await StopCountersAsync(parameters.Session, cancellationToken).ConfigureAwait(false);
        var json = Json;
        var session = new CounterSession(
            parameters,
            update => _connection.NotifyAsync(DiagnosticsMethods.CountersUpdate, update, json.CountersUpdate, CancellationToken.None),
            events => _connection.NotifyAsync(DiagnosticsMethods.RuntimeEvents, events, json.RuntimeEventsUpdate, CancellationToken.None),
            async error =>
            {
                _counters.TryRemove(new KeyValuePair<string, CounterSession>(parameters.Session, null!));
                await NotifyEndedAsync(parameters.Session, error).ConfigureAwait(false);
            });
        var name = await session.StartAsync(cancellationToken).ConfigureAwait(false);
        _counters[parameters.Session] = session;
        return new SessionStartResult(name);
    }

    private async Task<DiagnosticsEmpty> StopCountersAsync(string id, CancellationToken cancellationToken)
    {
        if (_counters.TryRemove(id, out var session))
        {
            try
            {
                await session.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A process held by a debugger answers later; the session is forgotten either way.
            }

            await session.DisposeAsync().ConfigureAwait(false);
        }

        return new DiagnosticsEmpty();
    }

    private async Task NotifyEndedAsync(string session, string? error)
    {
        try
        {
            await _connection.NotifyAsync(DiagnosticsMethods.SessionEnded, new SessionEndedParams(session, error), Json.SessionEndedParams, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonRpcConnectionLostException)
        {
        }
    }

    // ------------------------------------------------------------------ traces

    private async Task<SessionStartResult> StartTraceAsync(TraceStartParams parameters, CancellationToken cancellationToken)
    {
        var recording = new TraceRecording(parameters);
        var name = await recording.StartAsync(cancellationToken).ConfigureAwait(false);
        _recordings[parameters.Session] = recording;
        _ = recording.Completion.ContinueWith(
            _ =>
            {
                // The process exited before Stop: say so (Stop still returns the file).
                if (_recordings.ContainsKey(parameters.Session))
                {
                    _ = NotifyEndedAsync(parameters.Session, null);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        return new SessionStartResult(name);
    }

    private async Task<TraceStopResult> StopTraceAsync(string id, CancellationToken cancellationToken)
    {
        if (!_recordings.TryRemove(id, out var recording))
        {
            throw new JsonRpcHandlerException(DiagnosticsErrorCodes.UnknownSession, "No trace is being recorded in that session.");
        }

        try
        {
            return await recording.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await recording.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<TraceSummary> OpenTraceAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new JsonRpcHandlerException(DiagnosticsErrorCodes.TraceError, $"{path} doesn't exist.");
        }

        OpenedTrace trace;
        try
        {
            trace = await Task.Run(() => OpenedTrace.Open(path, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not JsonRpcHandlerException)
        {
            throw new JsonRpcHandlerException(DiagnosticsErrorCodes.TraceError, $"{Path.GetFileName(path)} could not be read: {ex.Message}");
        }

        var id = Guid.NewGuid().ToString("N");
        _traces[id] = trace;
        var profile = trace.Views.Profile;
        var managed = profile.Samples.Count(s => !s.External);
        return new TraceSummary(
            id,
            path,
            profile.ProcessName,
            profile.ProcessId,
            profile.Duration,
            managed,
            profile.Samples.Count - managed,
            profile.FunctionCount,
            trace.Views.Threads());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _counters.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var recording in _recordings.Values)
        {
            await recording.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var trace in _traces.Values)
        {
            trace.Dispose();
        }

        _counters.Clear();
        _recordings.Clear();
        _traces.Clear();
    }
}

/// <summary>An opened trace: its profile and views, and the source lines being resolved in the background.</summary>
internal sealed class OpenedTrace : IDisposable
{
    private readonly NettraceReader _reader;
    private readonly CancellationTokenSource _lifetime = new();

    private OpenedTrace(CpuProfile profile, NettraceReader reader)
    {
        _reader = reader;
        Views = new CpuViews(profile);
        Lines = Task.Run(() => _reader.ResolveLines(profile, _lifetime.Token), _lifetime.Token);
    }

    public CpuViews Views { get; }

    public Task Lines { get; }

    public static OpenedTrace Open(string path, CancellationToken cancellationToken)
    {
        var (profile, reader) = NettraceReader.Read(path, cancellationToken);
        return new OpenedTrace(profile, reader);
    }

    /// <summary>Waits a while for source lines; views answer without them after that (and say they aren't resolved).</summary>
    public async Task WaitForLinesAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await Lines.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Lines that couldn't be read: the views answer without them.
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        try
        {
            Lines.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _reader.Dispose();
        _lifetime.Dispose();
    }
}
