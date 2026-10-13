using System.Text.Json.Serialization.Metadata;
using Codalus.Diagnostics.Protocol;
using Codalus.Rpc;

namespace Codalus.Workbench.Diagnostics;

/// <summary>
/// A CPU trace opened in the diagnostics host (ADR 0044, 0045): its summary, and its views asked for a page or a level
/// at a time. When the host has restarted since (or forgot the trace), the trace is opened again and the request retried.
/// </summary>
public sealed class CpuTrace : IAsyncDisposable
{
    private readonly DiagnosticsService _service;
    private readonly SemaphoreSlim _opening = new(1, 1);
    private string? _id;
    private int _generation = -1;

    internal CpuTrace(DiagnosticsService service, string path)
    {
        _service = service;
        Path = path;
    }

    public string Path { get; }

    public TraceSummary Summary { get; private set; } = null!;

    private static DiagnosticsJsonContext Json => DiagnosticsService.Json;

    internal async Task OpenAsync(CancellationToken cancellationToken)
    {
        await _opening.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var generation = _service.Generation;
            Summary = await _service.InvokeAsync(DiagnosticsMethods.TraceOpen, new TraceOpenParams(Path), Json.TraceOpenParams, Json.TraceSummary, cancellationToken).ConfigureAwait(false);
            _id = Summary.Trace;
            _generation = Math.Max(generation, _service.Generation);
        }
        finally
        {
            _opening.Release();
        }
    }

    private async Task<TResult> RequestAsync<TParams, TResult>(string method, Func<string, TParams> parameters, JsonTypeInfo<TParams> parametersInfo, JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken)
    {
        if (_id is null || _generation != _service.Generation)
        {
            await OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await _service.InvokeAsync(method, parameters(_id!), parametersInfo, resultInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcRemoteException ex) when (ex.Code == DiagnosticsErrorCodes.UnknownTrace)
        {
            await OpenAsync(cancellationToken).ConfigureAwait(false);
            return await _service.InvokeAsync(method, parameters(_id!), parametersInfo, resultInfo, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<CpuTreeResult> TopDownAsync(CpuFilter filter, int node, int offset, int count, CancellationToken cancellationToken) =>
        RequestAsync(DiagnosticsMethods.CpuTree, id => new CpuTreeParams(id, filter, node, offset, count), Json.CpuTreeParams, Json.CpuTreeResult, cancellationToken);

    public Task<CpuTreeResult> BottomUpAsync(CpuFilter filter, IReadOnlyList<int> path, int offset, int count, CancellationToken cancellationToken) =>
        RequestAsync(DiagnosticsMethods.CpuCallers, id => new CpuCallersParams(id, filter, path, offset, count), Json.CpuCallersParams, Json.CpuTreeResult, cancellationToken);

    public Task<CpuFunctionsResult> FunctionsAsync(CpuFilter filter, string? search, int offset, int count, CancellationToken cancellationToken) =>
        RequestAsync(DiagnosticsMethods.CpuFunctions, id => new CpuFunctionsParams(id, filter, search, offset, count), Json.CpuFunctionsParams, Json.CpuFunctionsResult, cancellationToken);

    public Task<CpuCallerCalleeResult> CallerCalleeAsync(CpuFilter filter, int function, CancellationToken cancellationToken) =>
        RequestAsync(DiagnosticsMethods.CpuCallerCallee, id => new CpuCallerCalleeParams(id, filter, function), Json.CpuCallerCalleeParams, Json.CpuCallerCalleeResult, cancellationToken);

    public Task<CpuFlameResult> FlameAsync(CpuFilter filter, int root, double minFraction, int maxFrames, CancellationToken cancellationToken) =>
        RequestAsync(DiagnosticsMethods.CpuFlame, id => new CpuFlameParams(id, filter, root, minFraction, maxFrames), Json.CpuFlameParams, Json.CpuFlameResult, cancellationToken);

    public Task<CpuTimelineResult> TimelineAsync(CpuFilter filter, int buckets, CancellationToken cancellationToken) =>
        RequestAsync(DiagnosticsMethods.CpuTimeline, id => new CpuTimelineParams(id, filter, buckets), Json.CpuTimelineParams, Json.CpuTimelineResult, cancellationToken);

    public Task<CpuFilesResult> FilesAsync(CpuFilter filter, CancellationToken cancellationToken) =>
        RequestAsync(DiagnosticsMethods.CpuFiles, id => new CpuFilesParams(id, filter), Json.CpuFilesParams, Json.CpuFilesResult, cancellationToken);

    public Task<CpuLinesResult> LinesAsync(CpuFilter filter, string file, CancellationToken cancellationToken) =>
        RequestAsync(DiagnosticsMethods.CpuLines, id => new CpuLinesParams(id, filter, file), Json.CpuLinesParams, Json.CpuLinesResult, cancellationToken);

    /// <summary>The trace's functions against a baseline trace's (both open; comparing sessions).</summary>
    public async Task<CpuCompareResult> CompareAsync(CpuTrace baseline, CpuFilter filter, int count, CancellationToken cancellationToken)
    {
        if (baseline._id is null || baseline._generation != _service.Generation)
        {
            await baseline.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        return await RequestAsync(DiagnosticsMethods.CpuCompare, id => new CpuCompareParams(baseline._id!, id, filter, count), Json.CpuCompareParams, Json.CpuCompareResult, cancellationToken).ConfigureAwait(false);
    }

    public Task<AsyncTasksResult> AsyncTasksAsync(CpuFilter filter, string? search, int offset, int count, CancellationToken cancellationToken) =>
        RequestAsync(DiagnosticsMethods.AsyncTasks, id => new AsyncTasksParams(id, filter, search, offset, count), Json.AsyncTasksParams, Json.AsyncTasksResult, cancellationToken);

    public Task<ThreadsResult> ThreadsAsync(int buckets, CancellationToken cancellationToken) =>
        RequestAsync(DiagnosticsMethods.Threads, id => new ThreadsParams(id, buckets), Json.ThreadsParams, Json.ThreadsResult, cancellationToken);

    public Task<ExceptionsResult> ExceptionsAsync(CpuFilter filter, CancellationToken cancellationToken) =>
        RequestAsync(DiagnosticsMethods.Exceptions, id => new ExceptionsParams(id, filter), Json.ExceptionsParams, Json.ExceptionsResult, cancellationToken);

    public Task<ActivitiesResult> ActivitiesAsync(string kind, CpuFilter filter, bool byTime, int offset, int count, CancellationToken cancellationToken) =>
        RequestAsync(DiagnosticsMethods.Activities, id => new ActivitiesParams(id, kind, filter, byTime, offset, count), Json.ActivitiesParams, Json.ActivitiesResult, cancellationToken);

    public Task<FileIoResult> FileIoAsync(CancellationToken cancellationToken) =>
        RequestAsync(DiagnosticsMethods.FileIo, id => new TraceIdParams(id), Json.TraceIdParams, Json.FileIoResult, cancellationToken);

    public Task<EventStatsResult> EventStatsAsync(CancellationToken cancellationToken) =>
        RequestAsync(DiagnosticsMethods.EventStats, id => new TraceIdParams(id), Json.TraceIdParams, Json.EventStatsResult, cancellationToken);

    public Task<EventsResult> EventsAsync(EventFilter filter, int offset, int count, CancellationToken cancellationToken) =>
        RequestAsync(DiagnosticsMethods.Events, id => new EventsParams(id, filter, offset, count), Json.EventsParams, Json.EventsResult, cancellationToken);

    /// <summary>Writes the filter's profile as Speedscope or Chromium trace JSON (<see cref="ExportFormats"/>).</summary>
    public Task<ExportResult> ExportAsync(CpuFilter filter, string format, string outputPath, CancellationToken cancellationToken) =>
        RequestAsync(DiagnosticsMethods.Export, id => new ExportParams(id, filter, format, outputPath), Json.ExportParams, Json.ExportResult, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_id is { } id && _generation == _service.Generation)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _service.InvokeAsync(DiagnosticsMethods.TraceClose, new TraceIdParams(id), Json.TraceIdParams, Json.DiagnosticsEmpty, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }

        _id = null;
        _opening.Dispose();
    }
}
