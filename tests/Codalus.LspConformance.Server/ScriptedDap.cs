using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codalus.LspConformance.Server;

/// <summary>
/// <c>--dap</c>: a scripted debug adapter for the debugger contribution tests (plugin API 1.0, M22a). It answers
/// <c>initialize</c>, <c>launch</c> (echoing the launch configuration to the Debug Console), <c>configurationDone</c>
/// (after which the "program" prints and exits) and <c>disconnect</c>, as any DAP adapter would.
/// A launch configuration with <c>threads</c> (see <see cref="Threads"/>) is a program that keeps running until a
/// <c>pause</c>, which stops it on the thread named, as netcoredbg does (Break All tests).
/// </summary>
internal static class ScriptedDap
{
    private static int s_seq;

    /// <summary>How many requests arrived (<c>runtimeAfter</c> with <c>+request</c>).</summary>
    private static int s_requests;

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
            Interlocked.Increment(ref s_requests);
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
                    {
                        // As netcoredbg answers when the program's runtime didn't report within its 5 seconds. Like
                        // netcoredbg, it started the program first, as its child, and doesn't end it on disconnect: its id
                        // is listed next to the starts file.
                        var program = StartProgram(StartsFile(launch!.Value) + ".programs");
                        await FailAsync(output, requestSeq, command, "Failed command 'configurationDone' : 0x80004005");
                        _ = AfterTimeoutAsync(output, program, Option(launch.Value, "runtimeAfter"), launch.Value.GetRawText());
                        continue;
                    }
                case "configurationDone" when Threads(launch) is not null:
                    // The program runs until a pause.
                    break;
                case "configurationDone":
                    await RespondAsync(output, requestSeq, command, null);
                    await EventAsync(output, "output", new JsonObject { ["category"] = "console", ["output"] = "scripted adapter: launch " + launch?.GetRawText() + "\n" });
                    await EventAsync(output, "output", new JsonObject { ["category"] = "stdout", ["output"] = "Hello from the scripted program\n" });
                    await EventAsync(output, "exited", new JsonObject { ["exitCode"] = 7 });
                    await EventAsync(output, "terminated", new JsonObject());
                    continue;
                case "threads":
                    body = new JsonObject
                    {
                        ["threads"] = new JsonArray([.. (Threads(launch) ?? []).Select(t => (JsonNode)new JsonObject { ["id"] = t.GetProperty("id").GetInt32(), ["name"] = t.GetProperty("name").GetString() })]),
                    };
                    break;
                case "pause":
                    {
                        // As netcoredbg: every thread stops, and the event names the thread the pause named (or, with
                        // pauseNamesNoThread, none).
                        await RespondAsync(output, requestSeq, command, null);
                        var stopped = new JsonObject { ["reason"] = "pause", ["allThreadsStopped"] = true };
                        if (!(launch?.TryGetProperty("pauseNamesNoThread", out var none) == true && none.GetBoolean()))
                        {
                            stopped["threadId"] = message.GetProperty("arguments").GetProperty("threadId").GetInt32();
                        }

                        await EventAsync(output, "stopped", stopped);
                        continue;
                    }

                case "stackTrace":
                    {
                        var id = message.GetProperty("arguments").GetProperty("threadId").GetInt32();
                        var thread = (Threads(launch) ?? []).FirstOrDefault(t => t.GetProperty("id").GetInt32() == id);
                        if (thread.ValueKind != JsonValueKind.Object || thread.GetProperty("frames").ValueKind != JsonValueKind.Array)
                        {
                            await FailAsync(output, requestSeq, command, "Failed command 'stackTrace' : 0x80070057");
                            continue;
                        }

                        var frames = new JsonArray();
                        foreach (var frame in thread.GetProperty("frames").EnumerateArray())
                        {
                            frames.Add(new JsonObject
                            {
                                ["id"] = id * 1000 + frames.Count,
                                ["name"] = frame.GetProperty("name").GetString(),
                                ["source"] = frame.TryGetProperty("path", out var path) ? new JsonObject { ["path"] = path.GetString() } : null,
                                ["line"] = frame.TryGetProperty("line", out var line) ? line.GetInt32() : 0,
                                ["column"] = 0,
                            });
                        }

                        body = new JsonObject { ["stackFrames"] = frames, ["totalFrames"] = frames.Count };
                        break;
                    }

                case "continue":
                    await RespondAsync(output, requestSeq, command, new JsonObject { ["allThreadsContinued"] = true });
                    await EventAsync(output, "continued", new JsonObject { ["threadId"] = message.GetProperty("arguments").GetProperty("threadId").GetInt32(), ["allThreadsContinued"] = true });
                    continue;
                case "disconnect":
                    await RespondAsync(output, requestSeq, command, null);
                    return;
            }

            await RespondAsync(output, requestSeq, command!, body);
        }
    }

    /// <summary>
    /// The launch configuration's <c>threads</c>: <c>{ "id", "name", "frames" }</c>, the frames <c>{ "name", "path"?,
    /// "line"? }</c> top first, or not an array for a thread whose stack netcoredbg can't read (an error).
    /// </summary>
    private static JsonElement[]? Threads(JsonElement? launch) =>
        launch is { ValueKind: JsonValueKind.Object } arguments && arguments.TryGetProperty("threads", out var threads) ? [.. threads.EnumerateArray()] : null;

    /// <summary>
    /// A launch configuration with <c>failStarts</c> (a count) and <c>startsFile</c> (a file the adapters share) fails
    /// that many starts, one per adapter process, before one succeeds (the debug service starts again, as after netcoredbg's startup timeout).
    /// A netcoredbg launch (a debug service's own <c>LaunchArguments</c>) carries the two in its <c>env</c>.
    /// </summary>
    private static bool FailsToStart(JsonElement? launch)
    {
        if (launch is not { ValueKind: JsonValueKind.Object } arguments)
        {
            return false;
        }

        if (arguments.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
        {
            arguments = env;
        }

        if (!arguments.TryGetProperty("failStarts", out var fail) || !arguments.TryGetProperty("startsFile", out var file))
        {
            return false;
        }

        var path = file.GetString()!;
        var failed = File.Exists(path) ? int.Parse(File.ReadAllText(path), System.Globalization.CultureInfo.InvariantCulture) : 0;
        var count = fail.ValueKind == JsonValueKind.String ? int.Parse(fail.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : fail.GetInt32();
        if (failed >= count)
        {
            return false;
        }

        File.WriteAllText(path, (failed + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }

    private static string StartsFile(JsonElement launch) =>
        (launch.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object ? env : launch).GetProperty("startsFile").GetString()!;

    /// <summary>An option of the launch configuration (or of a netcoredbg launch's <c>env</c>), as text.</summary>
    private static string? Option(JsonElement launch, string name) =>
        (launch.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object ? env : launch).TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;

    /// <summary>
    /// What a timed-out start's program does next, by the launch's <c>runtimeAfter</c>: its runtime reports that many
    /// milliseconds later (netcoredbg then reports its thread and module, and the program runs: it prints and exits
    /// with 7); <c>never</c>: it runs until it is killed; none: it ends at once, before its runtime started (a host
    /// that fails). With <c>+request</c> after the milliseconds, netcoredbg on macOS: it reports the runtime only once
    /// a request arrives after it started (its CreateProcess callback waits for the stdin its main thread holds).
    /// </summary>
    private static async Task AfterTimeoutAsync(Stream output, int program, string? runtimeAfter, string launch)
    {
        if (runtimeAfter == "never")
        {
            return;
        }

        if (runtimeAfter is null)
        {
            await Task.Delay(200);
            Kill(program);
            return;
        }

        var afterRequest = runtimeAfter.EndsWith("+request", StringComparison.Ordinal);
        await Task.Delay(int.Parse(afterRequest ? runtimeAfter[..^"+request".Length] : runtimeAfter, System.Globalization.CultureInfo.InvariantCulture));
        if (afterRequest)
        {
            var requests = Volatile.Read(ref s_requests);
            while (Volatile.Read(ref s_requests) == requests)
            {
                await Task.Delay(20);
            }
        }

        await EventAsync(output, "thread", new JsonObject { ["reason"] = "started", ["threadId"] = 1 });
        await EventAsync(output, "module", new JsonObject { ["reason"] = "new", ["module"] = new JsonObject { ["id"] = "1", ["name"] = "program.dll" } });
        await Task.Delay(1000);
        await EventAsync(output, "output", new JsonObject { ["category"] = "console", ["output"] = "scripted adapter: launch " + launch + "\n" });
        await EventAsync(output, "output", new JsonObject { ["category"] = "stdout", ["output"] = "Hello from the scripted program\n" });
        Kill(program);
        await EventAsync(output, "exited", new JsonObject { ["exitCode"] = 7 });
        await EventAsync(output, "terminated", new JsonObject());
    }

    private static void Kill(int program)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(program);
            process.Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Killed already.
        }
    }

    private static int StartProgram(string programs)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!) == "dotnet")
        {
            start.ArgumentList.Add(typeof(ScriptedDap).Assembly.Location);
        }

        start.ArgumentList.Add("--program");
        using var program = System.Diagnostics.Process.Start(start)!;
        File.AppendAllText(programs, program.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
        return program.Id;
    }

    private static Task FailAsync(Stream output, int requestSeq, string command, string error) =>
        WriteAsync(output, new JsonObject
        {
            ["seq"] = ++s_seq,
            ["type"] = "response",
            ["request_seq"] = requestSeq,
            ["success"] = false,
            ["command"] = command,
            ["message"] = error,
        });

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

    private static readonly SemaphoreSlim s_write = new(1, 1);

    private static async Task WriteAsync(Stream output, JsonObject message)
    {
        // A timed-out start's program writes its events beside the answers to requests.
        await s_write.WaitAsync();
        try
        {
            message["seq"] = Interlocked.Increment(ref s_seq);
            var json = Encoding.UTF8.GetBytes(message.ToJsonString());
            var header = Encoding.ASCII.GetBytes($"Content-Length: {json.Length}\r\n\r\n");
            await output.WriteAsync(header);
            await output.WriteAsync(json);
            await output.FlushAsync();
        }
        finally
        {
            s_write.Release();
        }
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
