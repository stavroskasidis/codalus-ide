using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codalus.Debugging.Mono;

/// <summary>A failed request: its message goes back in the response.</summary>
internal sealed class DapError(string message) : Exception(message);

/// <summary>
/// The Debug Adapter Protocol's server side on a pair of streams: <c>Content-Length</c> framing, requests handled in
/// order (as adapters do), responses and events written under one lock.
/// </summary>
internal sealed class DapServer(Stream input, Stream output)
{
    private readonly Lock _write = new();
    private int _seq;

    public async Task RunAsync(Func<string, JsonObject, Task<JsonNode?>> handle, Action<string>? responded, CancellationToken cancellationToken)
    {
        while (await ReadAsync(cancellationToken).ConfigureAwait(false) is { } message)
        {
            if (message["type"]?.GetValue<string>() != "request")
            {
                continue;
            }

            var command = message["command"]?.GetValue<string>() ?? string.Empty;
            var seq = message["seq"]?.GetValue<int>() ?? 0;
            var arguments = message["arguments"] as JsonObject ?? [];
            try
            {
                var body = await handle(command, arguments).ConfigureAwait(false);
                Send(new JsonObject { ["type"] = "response", ["request_seq"] = seq, ["success"] = true, ["command"] = command, ["body"] = body });
            }
#pragma warning disable CA1031 // An adapter answers every request: whatever the soft debugger throws becomes the error response.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Send(new JsonObject { ["type"] = "response", ["request_seq"] = seq, ["success"] = false, ["command"] = command, ["message"] = ex.Message });
            }

            responded?.Invoke(command);
            if (command == "disconnect")
            {
                return;
            }
        }
    }

    public void Event(string name, JsonObject? body = null) =>
        Send(new JsonObject { ["type"] = "event", ["event"] = name, ["body"] = body ?? [] });

    private void Send(JsonObject message)
    {
        lock (_write)
        {
            message["seq"] = ++_seq;
            var payload = Encoding.UTF8.GetBytes(message.ToJsonString());
            var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
            try
            {
                output.Write(header);
                output.Write(payload);
                output.Flush();
            }
            catch (IOException)
            {
                // The client went away; the read loop ends too.
            }
        }
    }

    private async Task<JsonObject?> ReadAsync(CancellationToken cancellationToken)
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
                length = int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        if (length < 0)
        {
            return null;
        }

        var buffer = new byte[length];
        await input.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonNode.Parse(buffer) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
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
