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
/// sent from any thread. A handler can send the client a request of its own (<see cref="RequestAsync"/>, the
/// <c>runInTerminal</c> reverse request, M54): the requests run on a chain of their own while the input is read, so the
/// answer is read while the handler waits for it. Source-generated JSON throughout.
/// </summary>
internal sealed class DapServer(Stream input, Stream output) : IDisposable
{
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Dictionary<int, TaskCompletionSource<JsonElement?>> _pending = [];
    private int _seq;

    /// <summary>Where every message read and written goes too, when set (CoreCLR's adapter's log, M54).</summary>
    public TextWriter? Trace { get; init; }

    /// <summary>Answers a request: its command and arguments → the response body (null for none).</summary>
    public Func<string, JsonElement, CancellationToken, Task<JsonElement?>>? Handler { get; set; }

    /// <summary>Reads and answers requests until the input ends or <paramref name="cancellationToken"/> fires.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var requests = Task.CompletedTask;
        try
        {
            while (!cancellationToken.IsCancellationRequested && await ReadAsync(cancellationToken).ConfigureAwait(false) is { } message)
            {
                using var document = message;
                var root = document.RootElement;
                Trace?.WriteLine("<- " + root.GetRawText());
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "response")
                {
                    Answered(root);
                    continue;
                }

                if (type != "request")
                {
                    continue;
                }

                var seq = root.TryGetProperty("seq", out var s) ? s.GetInt32() : 0;
                var command = root.TryGetProperty("command", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                var arguments = root.TryGetProperty("arguments", out var a) ? a.Clone() : default;
                var previous = requests;
                requests = AnswerAsync(previous, seq, command, arguments, cancellationToken);
            }
        }
        finally
        {
            lock (_pending)
            {
                foreach (var pending in _pending.Values)
                {
                    pending.TrySetException(new IOException("The client went away."));
                }

                _pending.Clear();
            }
        }

        await requests.ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the client a request (a reverse request: <c>runInTerminal</c>) and returns its response's body; throws
    /// <see cref="DapRequestException"/> when the client refused it.
    /// </summary>
    public async Task<JsonElement?> RequestAsync<T>(string command, T arguments, JsonTypeInfo<T> info, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seq = Interlocked.Increment(ref _seq);
        lock (_pending)
        {
            _pending[seq] = answer;
        }

        try
        {
            var body = JsonSerializer.SerializeToElement(arguments, info);
            await WriteAsync(w =>
            {
                w.WriteNumber("seq", seq);
                w.WriteString("type", "request");
                w.WriteString("command", command);
                w.WritePropertyName("arguments");
                body.WriteTo(w);
            }).ConfigureAwait(false);
            return await answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_pending)
            {
                _pending.Remove(seq);
            }
        }
    }

    private void Answered(JsonElement response)
    {
        var seq = response.TryGetProperty("request_seq", out var s) ? s.GetInt32() : 0;
        TaskCompletionSource<JsonElement?>? answer;
        lock (_pending)
        {
            _pending.TryGetValue(seq, out answer);
        }

        if (answer is null)
        {
            return;
        }

        if (response.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
        {
            answer.TrySetException(new DapRequestException(response.TryGetProperty("message", out var m) ? m.GetString() ?? "The client refused." : "The client refused."));
            return;
        }

        answer.TrySetResult(response.TryGetProperty("body", out var body) ? body.Clone() : null);
    }

    /// <summary>Answers one request once the ones before it are answered: they run in order, as they arrived.</summary>
    private async Task AnswerAsync(Task previous, int seq, string command, JsonElement arguments, CancellationToken cancellationToken)
    {
        // Off the read loop's thread even when the request before has been answered (an await of a finished task goes on
        // synchronously): a handler that waits for the client's answer to a request of its own would block the loop
        // that reads that answer.
        await Task.Yield();
        await previous.ConfigureAwait(false);
        try
        {
            var body = Handler is null ? null : await Handler(command, arguments, cancellationToken).ConfigureAwait(false);
            await RespondAsync(seq, command, success: true, body, null).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DapRequestException or InvalidOperationException or ArgumentException or IOException or global::Mono.Debugger.Soft.VMDisconnectedException or global::Mono.Debugger.Soft.InvalidStackFrameException or global::Mono.Debugger.Soft.AbsentInformationException or global::Mono.Debugger.Soft.CommandException or global::Mono.Debugger.Soft.ObjectCollectedException or NotSupportedException or TimeoutException or System.Runtime.InteropServices.COMException)
        {
            await RespondAsync(seq, command, success: false, null, ex.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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

        Trace?.WriteLine("-> " + Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
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
