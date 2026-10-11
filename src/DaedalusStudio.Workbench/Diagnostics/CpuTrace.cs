using System.Text.Json.Serialization.Metadata;
using DaedalusStudio.Diagnostics.Protocol;
using DaedalusStudio.Rpc;

namespace DaedalusStudio.Workbench.Diagnostics;

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
