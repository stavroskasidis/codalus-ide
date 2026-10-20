using System.Collections.Concurrent;
using Codalus.Diagnostics.Protocol;
using Codalus.Rpc;

namespace Codalus.Workbench.Diagnostics;

/// <summary>Locates the diagnostics host (ADR 0044).</summary>
public static class DiagnosticsHostLaunch
{
    public const string EnvironmentVariable = "CODALUS_DIAGHOST";

    /// <summary>Resolution order: <c>CODALUS_DIAGHOST</c> (an apphost or a .dll), then <c>diaghost/</c> next to the shell.</summary>
    public static ProcessLaunchInfo? Resolve()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv)
        {
            candidates.Add(fromEnv);
        }

        var exe = OperatingSystem.IsWindows() ? "codalus-diaghost.exe" : "codalus-diaghost";
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "diaghost", exe));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "diaghost", "codalus-diaghost.dll"));
        return candidates.FirstOrDefault(File.Exists) is { } path ? ProcessLaunchInfo.FromPath(path) : null;
    }
}

/// <summary>
/// Profiling and diagnostics (ADR 0044): the diagnostics host, started on the first request and supervised, and what
/// runs in it: counter monitors (live values pushed by the host), CPU trace recordings, and opened traces, which stay in
/// the host and are asked for a view at a time. After a host crash, monitors and recordings end; an opened trace is
/// opened again on its next request. UI-agnostic; events arrive on any thread.
/// </summary>
public sealed class DiagnosticsService : IAsyncDisposable
{
    private static readonly SupervisionPolicy s_policy = new() { MaxRestarts = 5 };

    private readonly Func<ProcessLaunchInfo?> _launch;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, CounterMonitor> _monitors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TraceRecordingHandle> _recordings = new(StringComparer.Ordinal);
    private SupervisedProcess? _process;
    private int _generation;

    public DiagnosticsService(Func<ProcessLaunchInfo?> launch) => _launch = launch;

    internal static DiagnosticsJsonContext Json => DiagnosticsJsonContext.Default;

    /// <summary>The host restarted after a crash (true) or gave up (false).</summary>
    public event EventHandler<bool>? HostCrashed;

    public event EventHandler<string>? Log;

    public bool IsAvailable => _launch() is not null;

    public int? HostProcessId => _process?.ProcessId;

    /// <summary>Hot lines for the editor gutter, from the profiling report in front.</summary>
    public HotLineStore HotLines { get; } = new();

    /// <summary>Bumped on every host (re)start: opened traces compare it to know they must be opened again.</summary>
    internal int Generation => Volatile.Read(ref _generation);

    public IReadOnlyList<CounterMonitor> Monitors => [.. _monitors.Values];

    public async Task<IReadOnlyList<DotnetProcess>> ProcessesAsync(CancellationToken cancellationToken)
    {
        var host = await HostAsync(cancellationToken).ConfigureAwait(false);
        var result = await host.InvokeAsync(DiagnosticsMethods.Processes, new DiagnosticsEmpty(), Json.DiagnosticsEmpty, Json.ProcessesResult, cancellationToken).ConfigureAwait(false);
        return result.Processes;
    }

    /// <summary>Resumes a process started with <see cref="ProfilingLaunch.SuspendVariable"/>.</summary>
    public async Task ResumeAsync(int processId, CancellationToken cancellationToken)
    {
        var host = await HostAsync(cancellationToken).ConfigureAwait(false);
        await host.InvokeAsync(DiagnosticsMethods.Resume, new ProcessIdParams(processId), Json.ProcessIdParams, Json.DiagnosticsEmpty, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts a counters session on a process; its values arrive on the monitor.</summary>
    public async Task<CounterMonitor> StartCountersAsync(int processId, IReadOnlyList<CounterProvider> providers, double intervalSeconds, bool runtimeEvents, CancellationToken cancellationToken)
    {
        var host = await HostAsync(cancellationToken).ConfigureAwait(false);
        var id = "counters-" + Guid.NewGuid().ToString("N");
        var monitor = new CounterMonitor(this, id, processId, providers, intervalSeconds);
        _monitors[id] = monitor;
        try
        {
            var started = await host.InvokeAsync(DiagnosticsMethods.CountersStart, new CountersStartParams(id, processId, providers, intervalSeconds, runtimeEvents), Json.CountersStartParams, Json.SessionStartResult, cancellationToken).ConfigureAwait(false);
            monitor.ProcessName = started.ProcessName;
            return monitor;
        }
        catch
        {
            _monitors.TryRemove(id, out _);
            throw;
        }
    }

    internal async Task StopCountersAsync(CounterMonitor monitor, CancellationToken cancellationToken)
    {
        if (!_monitors.TryRemove(monitor.Id, out _))
        {
            return;
        }

        try
        {
            if (_process is { IsRunning: true } running)
            {
                await running.Connection.InvokeAsync(DiagnosticsMethods.CountersStop, new SessionParams(monitor.Id), Json.SessionParams, Json.DiagnosticsEmpty, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is JsonRpcRemoteException or JsonRpcConnectionLostException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            monitor.OnEnded(null);
        }
    }

    /// <summary>Starts recording a CPU trace of a process into <paramref name="outputPath"/>.</summary>
    public Task<TraceRecordingHandle> StartTraceAsync(int processId, string outputPath, CancellationToken cancellationToken) =>
        StartTraceAsync(processId, outputPath, null, null, cancellationToken);

    /// <summary>
    /// Starts recording a trace of a process into <paramref name="outputPath"/>: the <paramref name="tools"/>
    /// (<see cref="TraceTools"/>; null: CPU only) and any extra <paramref name="providers"/>.
    /// </summary>
    public async Task<TraceRecordingHandle> StartTraceAsync(int processId, string outputPath, IReadOnlyList<string>? tools, IReadOnlyList<TraceProvider>? providers, CancellationToken cancellationToken)
    {
        var host = await HostAsync(cancellationToken).ConfigureAwait(false);
        var id = "trace-" + Guid.NewGuid().ToString("N");
        var recording = new TraceRecordingHandle(this, id, processId, outputPath);
        _recordings[id] = recording;
        try
        {
            var started = await host.InvokeAsync(DiagnosticsMethods.TraceStart, new TraceStartParams(id, processId, outputPath, tools, providers), Json.TraceStartParams, Json.SessionStartResult, cancellationToken).ConfigureAwait(false);
            recording.ProcessName = started.ProcessName;
            return recording;
        }
        catch
        {
            _recordings.TryRemove(id, out _);
            throw;
        }
    }

    internal async Task<TraceStopResult> StopTraceAsync(TraceRecordingHandle recording, CancellationToken cancellationToken)
    {
        try
        {
            var host = await HostAsync(cancellationToken).ConfigureAwait(false);
            return await host.InvokeAsync(DiagnosticsMethods.TraceStop, new SessionParams(recording.Id), Json.SessionParams, Json.TraceStopResult, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _recordings.TryRemove(recording.Id, out _);
        }
    }

    /// <summary>Opens a <c>.nettrace</c> file in the host (it is read there, symbols and all; large files take a while).</summary>
    public async Task<CpuTrace> OpenTraceAsync(string path, CancellationToken cancellationToken)
    {
        var trace = new CpuTrace(this, path);
        await trace.OpenAsync(cancellationToken).ConfigureAwait(false);
        return trace;
    }

    /// <summary>Takes a GC heap snapshot of a process (an induced full GC) into <paramref name="outputPath"/>.</summary>
    public async Task<HeapSnapshotResult> TakeHeapSnapshotAsync(int processId, string outputPath, CancellationToken cancellationToken) =>
        await InvokeAsync(DiagnosticsMethods.HeapSnapshot, new HeapSnapshotParams(processId, outputPath), Json.HeapSnapshotParams, Json.HeapSnapshotResult, cancellationToken).ConfigureAwait(false);

    /// <summary>Opens a heap snapshot in the host (it stays there; its views are asked for a page at a time).</summary>
    public async Task<HeapSnapshot> OpenHeapSnapshotAsync(string path, CancellationToken cancellationToken)
    {
        var snapshot = new HeapSnapshot(this, path);
        await snapshot.OpenAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    internal async Task<TResult> InvokeAsync<TParams, TResult>(string method, TParams parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TParams> parametersInfo, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken)
    {
        var host = await HostAsync(cancellationToken).ConfigureAwait(false);
        return await host.InvokeAsync(method, parameters, parametersInfo, resultInfo, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ the host

    private async Task<JsonRpcConnection> HostAsync(CancellationToken cancellationToken)
    {
        if (_process is { IsRunning: true } running)
        {
            return running.Connection;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is null)
            {
                var launch = _launch() ?? throw new InvalidOperationException("The diagnostics host (codalus-diaghost) was not found.");
                var process = new SupervisedProcess("diagnostics host", launch, s_policy)
                {
                    Initialize = InitializeAsync,
                    ConfigureConnection = Configure,
                };
                process.Crashed += (_, e) =>
                {
                    Log?.Invoke(this, $"The diagnostics host exited (code {e.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}); {(e.WillRestart ? "restarting" : "giving up")}.");
                    EndAll("The diagnostics host stopped.");
                    if (e is not { DuringStartup: true, IsRestart: false })
                    {
                        // Not a first start that failed (M49): StartAsync's caller reports that one, and it isn't a crash loop.
                        HostCrashed?.Invoke(this, e.WillRestart);
                    }
                };
                process.Log += (_, line) => Log?.Invoke(this, line);
                _process = process;
            }

            await _process.StartAsync(cancellationToken).ConfigureAwait(false);
            return _process.Connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Configure(JsonRpcConnection connection)
    {
        connection.OnNotification(DiagnosticsMethods.CountersUpdate, Json.CountersUpdate, update =>
        {
            if (_monitors.TryGetValue(update.Session, out var monitor))
            {
                monitor.OnUpdate(update);
            }
        });
        connection.OnNotification(DiagnosticsMethods.RuntimeEvents, Json.RuntimeEventsUpdate, update =>
        {
            if (_monitors.TryGetValue(update.Session, out var monitor))
            {
                monitor.OnEvents(update.Events);
            }
        });
        connection.OnNotification(DiagnosticsMethods.SessionEnded, Json.SessionEndedParams, ended =>
        {
            if (_monitors.TryRemove(ended.Session, out var monitor))
            {
                monitor.OnEnded(ended.Error);
            }
            else if (_recordings.TryGetValue(ended.Session, out var recording))
            {
                recording.OnProcessExited();
            }
        });
    }

    private async Task InitializeAsync(JsonRpcConnection connection, bool isRestart, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _generation);
        await connection.InvokeAsync(DiagnosticsMethods.Initialize, new DiagnosticsInitializeParams(Environment.ProcessId, DiagnosticsMethods.ProtocolVersion), Json.DiagnosticsInitializeParams, Json.DiagnosticsInitializeResult, cancellationToken).ConfigureAwait(false);
    }

    private void EndAll(string error)
    {
        foreach (var id in _monitors.Keys.ToList())
        {
            if (_monitors.TryRemove(id, out var monitor))
            {
                monitor.OnEnded(error);
            }
        }

        foreach (var id in _recordings.Keys.ToList())
        {
            if (_recordings.TryRemove(id, out var recording))
            {
                recording.OnProcessExited();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var monitor in _monitors.Values.ToList())
        {
            monitor.OnEnded(null);
        }

        _monitors.Clear();
        if (Interlocked.Exchange(ref _process, null) is { } process)
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }
}

/// <summary>What a profiling launch adds to a program's environment (ADR 0044).</summary>
public static class ProfilingLaunch
{
    /// <summary>Holds the runtime before <c>Main</c> until the diagnostics host has started its sessions and resumes it.</summary>
    public const string SuspendVariable = "DOTNET_DefaultDiagnosticPortSuspend";
}

/// <summary>A CPU trace being recorded. <see cref="ProcessExited"/> fires when the process ends before it is stopped.</summary>
public sealed class TraceRecordingHandle
{
    private readonly DiagnosticsService _service;

    internal TraceRecordingHandle(DiagnosticsService service, string id, int processId, string outputPath)
    {
        _service = service;
        Id = id;
        ProcessId = processId;
        OutputPath = outputPath;
    }

    public string Id { get; }

    public int ProcessId { get; }

    public string ProcessName { get; internal set; } = string.Empty;

    public string OutputPath { get; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public bool HasProcessExited { get; private set; }

    public event EventHandler? ProcessExited;

    internal void OnProcessExited()
    {
        HasProcessExited = true;
        ProcessExited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops recording (rundown happens now) and returns the file.</summary>
    public Task<TraceStopResult> StopAsync(CancellationToken cancellationToken) => _service.StopTraceAsync(this, cancellationToken);
}
