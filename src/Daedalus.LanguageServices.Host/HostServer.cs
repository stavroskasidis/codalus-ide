using System.Text.Json;
using Daedalus.LanguageServices.Protocol;
using StreamJsonRpc;

namespace Daedalus.LanguageServices.Host;

/// <summary>Wires a <see cref="LanguageServer"/> to a JSON-RPC connection with LSP header framing.</summary>
public sealed class HostServer : IAsyncDisposable
{
    private readonly JsonRpc _rpc;
    private readonly LanguageServer _server;

    public HostServer(Stream sendingStream, Stream receivingStream)
    {
        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = CreateSerializerOptions(),
        };
        var handler = new HeaderDelimitedMessageHandler(sendingStream, receivingStream, formatter);
        _rpc = new JsonRpc(handler)
        {
            CancelLocallyInvokedMethodsWhenConnectionIsClosed = true,
            // Handlers start strictly in arrival order, so a request always observes every edit sent before it.
            // Async handlers leave the context at their first ConfigureAwait(false), so long requests still overlap.
            SynchronizationContext = new Microsoft.VisualStudio.Threading.NonConcurrentSynchronizationContext(sticky: false),
        };
        if (Environment.GetEnvironmentVariable("DAEDALUS_TRACE") is "1" or "true")
        {
            _rpc.TraceSource = new System.Diagnostics.TraceSource("daedalus-lshost", System.Diagnostics.SourceLevels.Warning);
            _rpc.TraceSource.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Error));
        }

        _server = new LanguageServer(_rpc.Dispose);
        _rpc.AddLocalRpcTarget(_server, new JsonRpcTargetOptions
        {
            UseSingleObjectParameterDeserialization = true,
            AllowNonPublicInvocation = false,
        });
    }

    public static JsonSerializerOptions CreateSerializerOptions() => new(ProtocolJsonContext.Default.Options);

    public async Task RunAsync()
    {
        _rpc.StartListening();
        await _rpc.Completion.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _rpc.Dispose();
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
