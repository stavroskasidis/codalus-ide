using DaedalusStudio.Diagnostics.Protocol;
using DaedalusStudio.Rpc;

namespace DaedalusStudio.Workbench.Diagnostics;

/// <summary>
/// A GC heap snapshot opened in the diagnostics host (ADR 0046): its summary, its types (or their growth against
/// another snapshot) and the paths to roots, a page at a time. Opened again when the host has restarted since.
/// </summary>
public sealed class HeapSnapshot : IAsyncDisposable
{
    private readonly DiagnosticsService _service;
    private readonly SemaphoreSlim _opening = new(1, 1);
    private string? _id;
    private int _generation = -1;

    internal HeapSnapshot(DiagnosticsService service, string path)
    {
        _service = service;
        Path = path;
    }

    public string Path { get; }

    public HeapSummary Summary { get; private set; } = null!;

    private static DiagnosticsJsonContext Json => DiagnosticsService.Json;

    internal async Task OpenAsync(CancellationToken cancellationToken)
    {
        await _opening.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var generation = _service.Generation;
            Summary = await _service.InvokeAsync(DiagnosticsMethods.HeapOpen, new HeapOpenParams(Path), Json.HeapOpenParams, Json.HeapSummary, cancellationToken).ConfigureAwait(false);
            _id = Summary.Snapshot;
            _generation = Math.Max(generation, _service.Generation);
        }
        finally
        {
            _opening.Release();
        }
    }

    private async Task<string> IdAsync(CancellationToken cancellationToken)
    {
        if (_id is null || _generation != _service.Generation)
        {
            await OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        return _id!;
    }

    private async Task<T> RetryAsync<T>(Func<Task<T>> request, CancellationToken cancellationToken)
    {
        try
        {
            return await request().ConfigureAwait(false);
        }
        catch (JsonRpcRemoteException ex) when (ex.Code == DiagnosticsErrorCodes.UnknownTrace)
        {
            await OpenAsync(cancellationToken).ConfigureAwait(false);
            return await request().ConfigureAwait(false);
        }
    }

    /// <summary>Types by size, or by growth since <paramref name="baseline"/>.</summary>
    public Task<HeapTypesResult> TypesAsync(HeapSnapshot? baseline, string? search, int offset, int count, CancellationToken cancellationToken) =>
        RetryAsync(
            async () =>
            {
                var baselineId = baseline is null ? null : await baseline.IdAsync(cancellationToken).ConfigureAwait(false);
                var id = await IdAsync(cancellationToken).ConfigureAwait(false);
                return await _service.InvokeAsync(DiagnosticsMethods.HeapTypes, new HeapTypesParams(id, baselineId, search, offset, count), Json.HeapTypesParams, Json.HeapTypesResult, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>What holds a type's objects: the referrers of <paramref name="path"/> (type ids from the type up).</summary>
    public Task<HeapRootsResult> ReferrersAsync(IReadOnlyList<int> path, int offset, int count, CancellationToken cancellationToken) =>
        RetryAsync(
            async () =>
            {
                var id = await IdAsync(cancellationToken).ConfigureAwait(false);
                return await _service.InvokeAsync(DiagnosticsMethods.HeapRoots, new HeapRootsParams(id, path, offset, count), Json.HeapRootsParams, Json.HeapRootsResult, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_id is { } id && _generation == _service.Generation)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _service.InvokeAsync(DiagnosticsMethods.HeapClose, new HeapIdParams(id), Json.HeapIdParams, Json.DiagnosticsEmpty, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }

        _id = null;
        _opening.Dispose();
    }
}
