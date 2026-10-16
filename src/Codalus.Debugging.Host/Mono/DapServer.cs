using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Codalus.Debugging.Host.Mono;

/// <summary>A request the adapter refuses, with the message the client shows.</summary>
internal sealed class DapRequestException(string message) : Exception(message);

/// <summary>
/// The server side of the Debug Adapter Protocol over a pair of streams (<c>Content-Length</c> framing, M20b): requests
/// are handed to <see cref="Handler"/> one at a time, in order, and answered with its body or its error; events can be
/// sent from any thread. Source-generated JSON throughout.
/// </summary>
internal sealed class DapServer(Stream input, Stream output) : IDisposable
{
    private readonly SemaphoreSlim _write = new(1, 1);
    private int _seq;

    /// <summary>Answers a request: its command and arguments → the response body (null for none).</summary>
    public Func<string, JsonElement, CancellationToken, Task<JsonElement?>>? Handler { get; set; }

    /// <summary>Reads and answers requests until the input ends or <paramref name="cancellationToken"/> fires.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && await ReadAsync(cancellationToken).ConfigureAwait(false) is { } message)
        {
            using var document = message;
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "request")
            {
                continue;
            }

            var seq = root.TryGetProperty("seq", out var s) ? s.GetInt32() : 0;
            var command = root.TryGetProperty("command", out var c) ? c.GetString() ?? string.Empty : string.Empty;
            var arguments = root.TryGetProperty("arguments", out var a) ? a.Clone() : default;
            try
            {
                var body = Handler is null ? null : await Handler(command, arguments, cancellationToken).ConfigureAwait(false);
                await RespondAsync(seq, command, success: true, body, null).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DapRequestException or InvalidOperationException or ArgumentException or IOException or global::Mono.Debugger.Soft.VMDisconnectedException or global::Mono.Debugger.Soft.InvalidStackFrameException or global::Mono.Debugger.Soft.AbsentInformationException or global::Mono.Debugger.Soft.CommandException or global::Mono.Debugger.Soft.ObjectCollectedException or NotSupportedException or TimeoutException)
            {
                await RespondAsync(seq, command, success: false, null, ex.Message).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Sends an event with a body of <typeparamref name="T"/>.</summary>
    public Task SendEventAsync<T>(string name, T body, JsonTypeInfo<T> info) =>
        SendEventAsync(name, JsonSerializer.SerializeToElement(body, info));

    public Task SendEventAsync(string name, JsonElement? body = null) =>
        WriteAsync(w =>
        {
            w.WriteNumber("seq", Interlocked.Increment(ref _seq));
            w.WriteString("type", "event");
            w.WriteString("event", name);
            if (body is { } value)
            {
                w.WritePropertyName("body");
                value.WriteTo(w);
            }
        });

    private Task RespondAsync(int requestSeq, string command, bool success, JsonElement? body, string? message) =>
        WriteAsync(w =>
        {
            w.WriteNumber("seq", Interlocked.Increment(ref _seq));
            w.WriteString("type", "response");
            w.WriteNumber("request_seq", requestSeq);
            w.WriteBoolean("success", success);
            w.WriteString("command", command);
            if (message is not null)
            {
                w.WriteString("message", message);
            }

            if (body is { } value)
            {
                w.WritePropertyName("body");
                value.WriteTo(w);
            }
        });

    private async Task WriteAsync(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        var header = Encoding.ASCII.GetBytes("Content-Length: " + buffer.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
        await _write.WaitAsync().ConfigureAwait(false);
        try
        {
            await output.WriteAsync(header).ConfigureAwait(false);
            await output.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length)).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The client is gone; the read loop ends too.
        }
        finally
        {
            _write.Release();
        }
    }

    public void Dispose() => _write.Dispose();

    /// <summary>One message, or null when the input ends.</summary>
    private async Task<JsonDocument?> ReadAsync(CancellationToken cancellationToken)
    {
        var length = -1;
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                break;
            }

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                length = int.Parse(line["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture);
            }
        }

        if (length < 0)
        {
            return null;
        }

        var body = new byte[length];
        await input.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(body);
    }

    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await input.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
            {
                return bytes.Count == 0 ? null : Encoding.ASCII.GetString([.. bytes]);
            }

            if (one[0] == '\n')
            {
                return Encoding.ASCII.GetString([.. bytes]).TrimEnd('\r');
            }

            bytes.Add(one[0]);
        }
    }
}
