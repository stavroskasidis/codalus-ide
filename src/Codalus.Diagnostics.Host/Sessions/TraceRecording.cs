using System.Diagnostics;
using Codalus.Diagnostics.Host.Trace;
using Codalus.Diagnostics.Protocol;
using Codalus.Rpc;
using Microsoft.Diagnostics.NETCore.Client;

namespace Codalus.Diagnostics.Host.Sessions;

/// <summary>
/// Records a trace (ADR 0044, 0046): the tools' events (EventPipe's sample profiler, allocation ticks, contention, the
/// thread pool, exceptions, the TPL, HTTP and DiagnosticSource; <see cref="DiagnosticsPort.TraceProviders"/>) with their
/// stacks and the runtime's method and module events, with rundown at the end so methods compiled before the session
/// have names, streamed to a <c>.nettrace</c> file; with File I/O, the process's I/O counters next to it. It ends with
/// <see cref="StopAsync"/> or when the process exits.
/// </summary>
internal sealed class TraceRecording : IAsyncDisposable
{
    private readonly TraceStartParams _parameters;
    private readonly Stopwatch _clock = new();
    private EventPipeSession? _session;
    private Task _copy = Task.CompletedTask;
    private IoSampler? _io;

    public TraceRecording(TraceStartParams parameters) => _parameters = parameters;

    public string Id => _parameters.Session;

    /// <summary>Completes when the stream ends (stopped, or the process exited).</summary>
    public Task Completion => _copy;

    public async Task<string> StartAsync(CancellationToken cancellationToken)
    {
        var name = DiagnosticsPort.NameOf(_parameters.ProcessId);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_parameters.OutputPath))!);
        var file = new FileStream(_parameters.OutputPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, useAsync: true);
        try
        {
            var tools = _parameters.Tools is { Count: > 0 } named ? named : [TraceTools.Cpu];
            _session = await DiagnosticsPort.StartSessionAsync(_parameters.ProcessId, DiagnosticsPort.TraceProviders(tools.ToList(), _parameters.Providers), rundown: true, stacks: true, bufferMegabytes: 256, slowStart: false, cancellationToken).ConfigureAwait(false);
            if (tools.Contains(TraceTools.FileIo))
            {
                _io = new IoSampler(_parameters.ProcessId);
                _io.Start();
            }
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _clock.Start();
        var stream = _session.EventStream;
        _copy = Task.Run(async () =>
        {
            await using (file.ConfigureAwait(false))
            {
                try
                {
                    await stream.CopyToAsync(file, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is EndOfStreamException or ObjectDisposedException)
                {
                    // The process went away mid-stream: what arrived is kept.
                }
            }

            _clock.Stop();
        }, CancellationToken.None);
        return name;
    }

    public async Task<TraceStopResult> StopAsync(CancellationToken cancellationToken)
    {
        if (_session is { } session && !_copy.IsCompleted)
        {
            try
            {
                // Rundown happens now: the stream ends once the runtime has written it.
                await session.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ServerNotAvailableException or IOException or EndOfStreamException or ServerErrorException or UnsupportedCommandException)
            {
                // Exited meanwhile.
            }
        }

        try
        {
            await _copy.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new JsonRpcHandlerException(DiagnosticsErrorCodes.TraceError, "The trace did not finish within two minutes of stopping.");
        }
        catch (IOException ex)
        {
            throw new JsonRpcHandlerException(DiagnosticsErrorCodes.TraceError, "The trace could not be written: " + ex.Message);
        }

        if (Interlocked.Exchange(ref _io, null) is { } io)
        {
            try
            {
                await io.WriteAsync(_parameters.OutputPath).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The trace is kept without its I/O counters.
            }

            await io.DisposeAsync().ConfigureAwait(false);
        }

        var bytes = File.Exists(_parameters.OutputPath) ? new FileInfo(_parameters.OutputPath).Length : 0;
        return new TraceStopResult(_parameters.OutputPath, bytes, _clock.Elapsed.TotalSeconds);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or JsonRpcHandlerException)
        {
        }

        _session?.Dispose();
    }
}
