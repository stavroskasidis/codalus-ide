using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codalus.LspConformance.Server;

/// <summary>
/// <c>--dap</c>: a scripted debug adapter for the debugger contribution tests (plugin API 1.0, M22a). It answers
/// <c>initialize</c>, <c>launch</c> (echoing the launch configuration to the Debug Console), <c>configurationDone</c>
/// (after which the "program" prints and exits) and <c>disconnect</c>, as any DAP adapter would.
/// </summary>
internal static class ScriptedDap
{
    private static int s_seq;

    public static async Task RunAsync(Stream input, Stream output)
    {
        JsonElement? launch = null;
        while (await ReadAsync(input) is { } message)
        {
            if (message.GetProperty("type").GetString() != "request")
            {
                continue;
            }

            var command = message.GetProperty("command").GetString();
            var requestSeq = message.GetProperty("seq").GetInt32();
            JsonNode? body = null;
            switch (command)
            {
                case "initialize":
                    body = new JsonObject { ["supportsConfigurationDoneRequest"] = true };
                    await RespondAsync(output, requestSeq, command, body);
                    await EventAsync(output, "initialized", null);
                    continue;
                case "launch":
                    launch = message.TryGetProperty("arguments", out var arguments) ? arguments.Clone() : null;
                    break;
                case "configurationDone" when FailsToStart(launch):
                    // As netcoredbg answers when the program's runtime didn't report within its 5 seconds.
                    await WriteAsync(output, new JsonObject
                    {
                        ["seq"] = ++s_seq,
                        ["type"] = "response",
                        ["request_seq"] = requestSeq,
                        ["success"] = false,
                        ["command"] = command,
                        ["message"] = "Failed command 'configurationDone' : 0x80004005",
                    });
                    continue;
                case "configurationDone":
                    await RespondAsync(output, requestSeq, command, null);
                    await EventAsync(output, "output", new JsonObject { ["category"] = "console", ["output"] = "scripted adapter: launch " + launch?.GetRawText() + "\n" });
                    await EventAsync(output, "output", new JsonObject { ["category"] = "stdout", ["output"] = "Hello from the scripted program\n" });
                    await EventAsync(output, "exited", new JsonObject { ["exitCode"] = 7 });
                    await EventAsync(output, "terminated", new JsonObject());
                    continue;
                case "threads":
                    body = new JsonObject { ["threads"] = new JsonArray() };
                    break;
                case "disconnect":
                    await RespondAsync(output, requestSeq, command, null);
                    return;
            }

            await RespondAsync(output, requestSeq, command!, body);
        }
    }

    /// <summary>
    /// A launch configuration with <c>failStarts</c> (a count) and <c>startsFile</c> (a file the adapters share) fails
    /// that many starts, one per adapter process, before one succeeds (the debug service starts again, as after netcoredbg's startup timeout).
    /// </summary>
    private static bool FailsToStart(JsonElement? launch)
    {
        if (launch is not { ValueKind: JsonValueKind.Object } arguments
            || !arguments.TryGetProperty("failStarts", out var fail) || !arguments.TryGetProperty("startsFile", out var file))
        {
            return false;
        }

        var path = file.GetString()!;
        var failed = File.Exists(path) ? int.Parse(File.ReadAllText(path), System.Globalization.CultureInfo.InvariantCulture) : 0;
        if (failed >= fail.GetInt32())
        {
            return false;
        }

        File.WriteAllText(path, (failed + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }

    private static Task RespondAsync(Stream output, int requestSeq, string command, JsonNode? body) =>
        WriteAsync(output, new JsonObject
        {
            ["seq"] = ++s_seq,
            ["type"] = "response",
            ["request_seq"] = requestSeq,
            ["success"] = true,
            ["command"] = command,
            ["body"] = body ?? new JsonObject(),
        });

    private static Task EventAsync(Stream output, string name, JsonNode? body) =>
        WriteAsync(output, new JsonObject { ["seq"] = ++s_seq, ["type"] = "event", ["event"] = name, ["body"] = body ?? new JsonObject() });

    private static async Task WriteAsync(Stream output, JsonObject message)
    {
        var json = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {json.Length}\r\n\r\n");
        await output.WriteAsync(header);
        await output.WriteAsync(json);
        await output.FlushAsync();
    }

    private static async Task<JsonElement?> ReadAsync(Stream input)
    {
        var length = -1;
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await input.ReadAsync(one) == 0)
            {
                return null;
            }

            if (one[0] == '\n')
            {
                var text = line.ToString().TrimEnd('\r');
                line.Clear();
                if (text.Length == 0)
                {
                    break;
                }

                if (text.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    length = int.Parse(text["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            else
            {
                line.Append((char)one[0]);
            }
        }

        var buffer = new byte[length];
        await input.ReadExactlyAsync(buffer);
        using var document = JsonDocument.Parse(buffer);
        return document.RootElement.Clone();
    }
}
