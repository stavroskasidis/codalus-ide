using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codalus.LspConformance.Server;

/// <summary>
/// <c>--dap</c>: a scripted debug adapter for the debugger contribution tests (plugin API 1.0, M22a). It answers
/// <c>initialize</c>, <c>launch</c> (echoing the launch configuration to the Debug Console), <c>configurationDone</c>
/// (after which the "program" prints and exits) and <c>disconnect</c>, as any DAP adapter would.
/// A launch configuration with <c>threads</c> (see <see cref="Threads"/>) is a program that keeps running until a
/// <c>pause</c>, which stops it on the thread named, as netcoredbg does (Break All tests). Started with netcoredbg's
/// arguments (<c>--interpreter=vscode</c>), it stands in for netcoredbg: <c>configurationDone</c> without a launch, then
/// an attach (a program started in the terminal, see <see cref="AttachScript"/>); or an attach, then a
/// <c>configurationDone</c> that runs past netcoredbg's 15 seconds (<see cref="AttachLateAsync"/>).
/// A launch configuration with <c>inspect</c> (see <see cref="ScriptedInspection"/>) is a program paused at a line with
/// the variables, evaluations and edits its script lists (the data tip and variable window tests).
/// </summary>
internal static class ScriptedDap
{
    private static int s_seq;

    /// <summary>How many requests arrived (<c>runtimeAfter</c> with <c>+request</c>).</summary>
    private static int s_requests;

    /// <summary>
    /// Where each request is written once an attach named a process that has a request log: the file
    /// <c>codalus-scripted-requests-&lt;process id&gt;</c> in the temp folder, when it exists (#75's owned attach tests).
    /// </summary>
    private static string? s_requestLog;

    private static string? RequestLog(JsonElement attach)
    {
        var processId = attach.GetProperty("arguments").GetProperty("processId").GetInt32();
        var log = Path.Combine(Path.GetTempPath(), "codalus-scripted-requests-" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return File.Exists(log) ? log : null;
    }

    /// <summary>The attached program's threads, once a slow attach has the process (<see cref="AttachLateAsync"/>).</summary>
    private static JsonElement[]? s_attachedThreads;

    public static async Task RunAsync(Stream input, Stream output)
    {
        JsonElement? launch = null;
        var configured = false;
        (int ProcessId, string? RuntimeAfter, bool Slow)? slowAttach = null;
        var evaluations = 0;
        ScriptedInspection? inspection = null;
        while (await ReadAsync(input) is { } message)
        {
            if (message.GetProperty("type").GetString() != "request")
            {
                continue;
            }

            var command = message.GetProperty("command").GetString();
            var requestSeq = message.GetProperty("seq").GetInt32();
            Interlocked.Increment(ref s_requests);
            if (command == "attach" && RequestLog(message) is { } log)
            {
                s_requestLog = log;
            }

            if (s_requestLog is { } logging)
            {
                await File.AppendAllTextAsync(logging, command + " " + (message.TryGetProperty("arguments", out var logged) ? logged.GetRawText() : "{}") + "\n");
            }
            JsonNode? body = null;
            if (inspection is not null && await inspection.HandleAsync(output, message, command!, requestSeq))
            {
                continue;
            }

            switch (command)
            {
                case "initialize":
                    body = new JsonObject { ["supportsConfigurationDoneRequest"] = true, ["supportsSetVariable"] = true };
                    await RespondAsync(output, requestSeq, command, body);
                    await EventAsync(output, "initialized", null);
                    continue;
                case "launch":
                    launch = message.TryGetProperty("arguments", out var arguments) ? arguments.Clone() : null;
                    inspection = ScriptedInspection.From(launch);
                    break;
                case "attach" when !configured && AttachScript(message) is { RuntimeAfter: "fail" }:
                    // An attach netcoredbg refuses (not its startup timeout: the start isn't made again).
                    await FailAsync(output, requestSeq, command, "Failed command 'attach' : 0x80070057");
                    continue;
                case "attach" when !configured && AttachScript(message) is { Slow: true } slow:
                    // As netcoredbg: it only takes the process id, and attaches in configurationDone.
                    slowAttach = slow;
                    break;
                case "configurationDone" when launch is null && slowAttach is { } attaching:
                    {
                        // netcoredbg reads no request until it has answered configurationDone, and answers it with its
                        // command timeout once the attach runs past its fixed 15 seconds; the attach goes on (the packaged
                        // WinUI app on a loaded Windows runner, PR #97's CI). A request sent meanwhile is read after.
                        await Task.Delay(1000);
                        await FailAsync(output, requestSeq, command, "Command execution timed out.");
                        _ = AttachLateAsync(output, int.Parse(attaching.RuntimeAfter!, System.Globalization.CultureInfo.InvariantCulture));
                        continue;
                    }

                case "threads" when slowAttach is not null && Volatile.Read(ref s_attachedThreads) is null:
                    // As netcoredbg answers before it has handled the process's CreateProcess callback (its
                    // CheckDebugProcess).
                    await FailAsync(output, requestSeq, command, "Failed command 'threads' : 0x80004005");
                    continue;
                case "configurationDone" when launch is null:
                    // An attach comes next: a program started in the terminal.
                    configured = true;
                    break;
                case "attach" when configured && AttachScript(message) is { Slow: false } attach:
                    {
                        // An attach after configurationDone carries netcoredbg's start, and its 5-second wait for the
                        // runtime: it fails as netcoredbg's does, and the program goes on as the script says.
                        await FailAsync(output, requestSeq, command, "Failed command 'attach' : 0x80004005");
                        _ = AfterTimeoutAsync(output, attach.ProcessId, attach.RuntimeAfter, $"{{\"attach\":{attach.ProcessId}}}");
                        continue;
                    }

                case "evaluate" when launch is { } evaluating && Option(evaluating, "evaluateTimesOut") is { } timesOut
                                     && ++evaluations <= int.Parse(timesOut, System.Globalization.CultureInfo.InvariantCulture):
                    // As netcoredbg answers a request that runs past its fixed 15 seconds.
                    await FailAsync(output, requestSeq, command, "Command execution timed out.");
                    continue;
                case "evaluate":
                    body = new JsonObject
                    {
                        ["result"] = $"{message.GetProperty("arguments").GetProperty("expression").GetString()} (request {evaluations})",
                        ["type"] = "string",
                        ["variablesReference"] = 0,
                    };
                    break;
                case "configurationDone" when FailsToStart(launch):
                    {
                        // As netcoredbg answers when the program's runtime didn't report within its 5 seconds (with
                        // startTimesOut, when the start ran past its 15 seconds: its command timeout). Like netcoredbg, it
                        // started the program first, as its child, and doesn't end it on disconnect: its id is listed next
                        // to the starts file.
                        var program = StartProgram(StartsFile(launch!.Value) + ".programs");
                        await FailAsync(output, requestSeq, command, Option(launch.Value, "startTimesOut") == "true" ? "Command execution timed out." : "Failed command 'configurationDone' : 0x80004005");
                        _ = AfterTimeoutAsync(output, program, Option(launch.Value, "runtimeAfter"), launch.Value.GetRawText());
                        continue;
                    }
                case "configurationDone" when Threads(launch) is not null:
                    // The program runs until a pause.
                    break;
                case "configurationDone" when launch is { } running && Option(running, "keepsRunning") == "true":
                    // The program runs until the session ends it (a multi-program session's first one, while others join).
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
                        ["threads"] = new JsonArray([.. (Threads(launch) ?? Volatile.Read(ref s_attachedThreads) ?? []).Select(t => (JsonNode)new JsonObject { ["id"] = t.GetProperty("id").GetInt32(), ["name"] = t.GetProperty("name").GetString() })]),
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
                        var thread = (Threads(launch) ?? Volatile.Read(ref s_attachedThreads) ?? []).FirstOrDefault(t => t.GetProperty("id").GetInt32() == id);
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
    /// What an attached process does when netcoredbg's attach times out, as its script says: the file
    /// <c>codalus-scripted-attach-&lt;process id&gt;</c> in the temp folder holds <see cref="AfterTimeoutAsync"/>'s
    /// <c>runtimeAfter</c> (empty: the program ends before its runtime starts). Without one, the attach works. With
    /// <c>slow &lt;milliseconds&gt;</c>, an attach sent before <c>configurationDone</c> runs past netcoredbg's 15 seconds
    /// there, and its runtime reports that many milliseconds after the timeout (<see cref="AttachLateAsync"/>).
    /// </summary>
    private static (int ProcessId, string? RuntimeAfter, bool Slow)? AttachScript(JsonElement request)
    {
        var processId = request.GetProperty("arguments").GetProperty("processId").GetInt32();
        var script = Path.Combine(Path.GetTempPath(), "codalus-scripted-attach-" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!File.Exists(script))
        {
            return null;
        }

        var runtimeAfter = File.ReadAllText(script).Trim();
        var slow = runtimeAfter.StartsWith("slow ", StringComparison.Ordinal);
        if (slow)
        {
            runtimeAfter = runtimeAfter["slow ".Length..].Trim();
        }

        return (processId, runtimeAfter.Length == 0 ? null : runtimeAfter, slow);
    }

    /// <summary>
    /// A slow attach going on after netcoredbg's command timeout: <paramref name="runtimeAfter"/> milliseconds later the
    /// runtime reports (netcoredbg sends the attach's thread and module events), and a moment later netcoredbg handles
    /// the process's CreateProcess callback, which it gets after them. From then on <c>threads</c> answers, with one
    /// thread in the program's code, and the program runs until a pause.
    /// </summary>
    private static async Task AttachLateAsync(Stream output, int runtimeAfter)
    {
        await Task.Delay(runtimeAfter);
        await EventAsync(output, "thread", new JsonObject { ["reason"] = "started", ["threadId"] = 1 });
        await EventAsync(output, "module", new JsonObject { ["reason"] = "new", ["module"] = new JsonObject { ["id"] = "1", ["name"] = "program.dll" } });
        await Task.Delay(500);
        using var threads = JsonDocument.Parse("""[{ "id": 1, "name": "Main Thread", "frames": [{ "name": "App.Program.Main" }] }]""");
        Volatile.Write(ref s_attachedThreads, [.. threads.RootElement.Clone().EnumerateArray()]);
    }

    /// <summary>
    /// A launch configuration with <c>failStarts</c> (a count) and <c>startsFile</c> (a file the adapters share) fails
    /// that many starts, one per adapter process, before one succeeds (the debug service starts again, as after netcoredbg's startup timeout).
    /// With <c>startTimesOut</c> (true), a failed start is answered with netcoredbg's command timeout instead.
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

    internal static Task FailAsync(Stream output, int requestSeq, string command, string error) =>
        WriteAsync(output, new JsonObject
        {
            ["seq"] = ++s_seq,
            ["type"] = "response",
            ["request_seq"] = requestSeq,
            ["success"] = false,
            ["command"] = command,
            ["message"] = error,
        });

    internal static Task RespondAsync(Stream output, int requestSeq, string command, JsonNode? body) =>
        WriteAsync(output, new JsonObject
        {
            ["seq"] = ++s_seq,
            ["type"] = "response",
            ["request_seq"] = requestSeq,
            ["success"] = true,
            ["command"] = command,
            ["body"] = body ?? new JsonObject(),
        });

    internal static Task EventAsync(Stream output, string name, JsonNode? body) =>
        WriteAsync(output, new JsonObject { ["seq"] = ++s_seq, ["type"] = "event", ["event"] = name, ["body"] = body ?? new JsonObject() });

    private static readonly SemaphoreSlim s_write = new(1, 1);

    internal static async Task WriteAsync(Stream output, JsonObject message)
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

    internal static async Task<JsonElement?> ReadAsync(Stream input)
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
