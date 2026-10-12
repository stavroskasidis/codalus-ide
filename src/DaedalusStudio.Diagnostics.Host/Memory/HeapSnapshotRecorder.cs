using System.Diagnostics.Tracing;
using DaedalusStudio.Diagnostics.Host.Sessions;
using DaedalusStudio.Diagnostics.Protocol;
using DaedalusStudio.Rpc;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace DaedalusStudio.Diagnostics.Host.Memory;

/// <summary>
/// Takes a GC heap snapshot the way dotnet-gcdump does (ADR 0046): an EventPipe session with the runtime's
/// <c>GCHeapSnapshot</c> keywords makes the runtime run a full, blocking GC that reports every live object, its
/// references, the roots and the types; the session stops once that GC ends. The events are kept as a
/// <c>.nettrace</c> file (the snapshot), read by <see cref="HeapGraph"/>.
/// </summary>
internal static class HeapSnapshotRecorder
{
    /// <summary>GC | GCHeapCollect | GCHeapDump | GCHeapAndTypeNames | Type.</summary>
    private const long HeapSnapshotKeywords = 0x1 | 0x800000 | 0x100000 | 0x1000000 | 0x80000;

    public static async Task<HeapSnapshotResult> RecordAsync(HeapSnapshotParams parameters, CancellationToken cancellationToken)
    {
        DiagnosticsPort.NameOf(parameters.ProcessId);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(parameters.OutputPath))!);
        var session = await DiagnosticsPort.StartSessionAsync(
            parameters.ProcessId,
            [new EventPipeProvider(DiagnosticsPort.RuntimeProvider, EventLevel.Verbose, HeapSnapshotKeywords)],
            rundown: false,
            stacks: false,
            bufferMegabytes: 512,
            cancellationToken).ConfigureAwait(false);
        using (session)
        {
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var file = new FileStream(parameters.OutputPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            var reading = Task.Run(() =>
            {
                using (file)
                using (var tee = new TeeStream(session.EventStream, file))
                using (var source = new EventPipeEventSource(tee))
                {
                    var clr = new ClrTraceEventParser(source);
                    int? induced = null;
                    clr.GCStart += e =>
                    {
                        if (induced is null && e.Depth == 2 && e.Reason == GCReason.Induced)
                        {
                            induced = e.Count;
                        }
                    };
                    clr.GCStop += e =>
                    {
                        if (induced == e.Count)
                        {
                            finished.TrySetResult();
                        }
                    };
                    try
                    {
                        source.Process();
                    }
                    catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException)
                    {
                        // The process exited, or the session ended: what arrived is kept.
                    }
                }

                finished.TrySetResult();
            }, CancellationToken.None);

            try
            {
                await finished.Task.WaitAsync(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new JsonRpcHandlerException(DiagnosticsErrorCodes.TraceError, "The heap snapshot didn't finish within five minutes.");
            }
            finally
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await session.StopAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ServerNotAvailableException or IOException or EndOfStreamException or ServerErrorException or UnsupportedCommandException or OperationCanceledException or TimeoutException)
                {
                    // Exited meanwhile.
                }

                await reading.WaitAsync(TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
            }
        }

        return new HeapSnapshotResult(parameters.OutputPath, new FileInfo(parameters.OutputPath).Length);
    }

    /// <summary>A stream read by the event parser that writes what it reads to a file.</summary>
    private sealed class TeeStream(Stream source, Stream copy) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            copy.Write(buffer, offset, read);
            return read;
        }

        public override void Flush() => copy.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
