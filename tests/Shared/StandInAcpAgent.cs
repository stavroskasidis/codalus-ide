using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codalus.Tests;

/// <summary>
/// A stand-in Agent Client Protocol agent (M48) that plays scripted sessions over newline-delimited JSON-RPC, so tests never
/// reach a real agent or model. It is a console app (tests/Codalus.AcpAgent.Fake) started like a real agent.
/// <para>
/// Every message it receives is appended to the file in <c>ACP_FAKE_LOG</c> (one JSON line each). <c>ACP_FAKE_AUTH=1</c>
/// makes <c>session/new</c> answer <c>auth_required</c> until <c>authenticate</c> with <c>fake-login</c>;
/// <c>ACP_FAKE_PROTOCOL</c> changes the version it answers <c>initialize</c> with.
/// </para>
/// The prompt's last line picks the script:
/// <list type="bullet">
/// <item><c>hello</c>: a thought, two message chunks and a plan.</item>
/// <item><c>read &lt;path&gt; [line limit]</c>: a read tool call with a location, then <c>fs/read_text_file</c>; the message has what it got.</item>
/// <item><c>write &lt;path&gt;</c>: an edit tool call, then <c>fs/write_text_file</c> of "written by the agent\n".</item>
/// <item><c>permission</c>: <c>session/request_permission</c> with all four option kinds; the message says the outcome.</item>
/// <item><c>run &lt;command line&gt;</c>: <c>terminal/create</c>, a tool call that shows it, wait, output, release.</item>
/// <item><c>diff &lt;path&gt;</c>: writes the file itself and reports a completed edit with its diff.</item>
/// <item><c>slow</c>: a tool call that runs until <c>session/cancel</c>, with a permission request pending.</item>
/// <item><c>crash</c>: a chunk, a line on standard error, then the process exits mid-turn.</item>
/// <item><c>compact</c>: a compaction that completes, and a notice.</item>
/// <item><c>mode</c>: switches its mode to <c>code</c>.</item>
/// </list>
/// </summary>
public static class StandInAcpAgent
{
    private static readonly JsonSerializerOptions s_line = new() { WriteIndented = false };

    public static async Task<int> RunAsync(Stream input, Stream output)
    {
        var agent = new Agent(output, Environment.GetEnvironmentVariable("ACP_FAKE_LOG"));
        using var reader = new StreamReader(input, new UTF8Encoding(false));
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            agent.Log(line);
            var message = JsonNode.Parse(line)!.AsObject();
            if (message["method"] is null)
            {
                agent.OnResponse(message);
                continue;
            }

            _ = Task.Run(() => agent.HandleAsync(message));
        }

        return 0;
    }

    private sealed class Agent(Stream output, string? logFile)
    {
        private readonly Lock _write = new();
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
        private readonly Lock _logGate = new();
        private TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _next = 100;
        private bool _authenticated;
        private string _sessionId = "";

        public void Log(string line)
        {
            if (logFile is null)
            {
                return;
            }

            lock (_logGate)
            {
                File.AppendAllText(logFile, line + "\n");
            }
        }

        public void OnResponse(JsonObject message)
        {
            if (message["id"]?.GetValue<long>() is { } id && _pending.TryRemove(id, out var pending))
            {
                pending.TrySetResult(message);
            }
        }

        private async Task SendAsync(JsonObject message)
        {
            message["jsonrpc"] = "2.0";
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString(s_line) + "\n");
            lock (_write)
            {
                output.Write(bytes);
                output.Flush();
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private Task RespondAsync(JsonNode? id, JsonNode? result) => SendAsync(new JsonObject { ["id"] = id?.DeepClone(), ["result"] = result });

        private Task ErrorAsync(JsonNode? id, int code, string text) =>
            SendAsync(new JsonObject { ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = text } });

        private Task UpdateAsync(JsonObject update) =>
            SendAsync(new JsonObject { ["method"] = "session/update", ["params"] = new JsonObject { ["sessionId"] = _sessionId, ["update"] = update } });

        private Task ChunkAsync(string kind, string text) =>
            UpdateAsync(new JsonObject { ["sessionUpdate"] = kind, ["content"] = new JsonObject { ["type"] = "text", ["text"] = text } });

        /// <summary>A request to the client; its result, or its error as <c>{"error": …}</c>.</summary>
        private async Task<JsonObject> RequestAsync(string method, JsonObject parameters)
        {
            var id = Interlocked.Increment(ref _next);
            var pending = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = pending;
            await SendAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }).ConfigureAwait(false);
            var answer = await pending.Task.ConfigureAwait(false);
            return answer["error"] is JsonObject error ? new JsonObject { ["error"] = error.DeepClone() } : answer["result"] as JsonObject ?? [];
        }

        private static string ErrorText(JsonObject answer) => answer["error"]?["message"]?.GetValue<string>() ?? "";

        public async Task HandleAsync(JsonObject message)
        {
            var id = message["id"];
            var parameters = message["params"] as JsonObject ?? [];
            switch (message["method"]!.GetValue<string>())
            {
                case "initialize":
                    var protocol = int.TryParse(Environment.GetEnvironmentVariable("ACP_FAKE_PROTOCOL"), CultureInfo.InvariantCulture, out var p) ? p : 1;
                    var auth = Environment.GetEnvironmentVariable("ACP_FAKE_AUTH") == "1"
                        ? new JsonArray(new JsonObject { ["id"] = "fake-login", ["name"] = "Fake login", ["description"] = "Signs in to nothing." })
                        : [];
                    await RespondAsync(id, new JsonObject
                    {
                        ["protocolVersion"] = protocol,
                        ["agentCapabilities"] = new JsonObject { ["loadSession"] = false, ["promptCapabilities"] = new JsonObject { ["embeddedContext"] = true } },
                        ["agentInfo"] = new JsonObject { ["name"] = "stand-in", ["title"] = "Stand-in agent", ["version"] = "1.0.0" },
                        ["authMethods"] = auth,
                    }).ConfigureAwait(false);
                    break;
                case "authenticate":
                    _authenticated = parameters["methodId"]?.GetValue<string>() == "fake-login";
                    await RespondAsync(id, new JsonObject()).ConfigureAwait(false);
                    break;
                case "session/new":
                    if (Environment.GetEnvironmentVariable("ACP_FAKE_AUTH") == "1" && !_authenticated)
                    {
                        await ErrorAsync(id, -32000, "Authentication required").ConfigureAwait(false);
                        break;
                    }

                    _sessionId = "sess_stand_in";
                    await RespondAsync(id, new JsonObject
                    {
                        ["sessionId"] = _sessionId,
                        ["modes"] = new JsonObject
                        {
                            ["currentModeId"] = "ask",
                            ["availableModes"] = new JsonArray(new JsonObject { ["id"] = "ask", ["name"] = "Ask" }, new JsonObject { ["id"] = "code", ["name"] = "Code" }),
                        },
                    }).ConfigureAwait(false);
                    await UpdateAsync(new JsonObject
                    {
                        ["sessionUpdate"] = "available_commands_update",
                        ["availableCommands"] = new JsonArray(
                            new JsonObject { ["name"] = "review", ["description"] = "Review the changes" },
                            new JsonObject { ["name"] = "web", ["description"] = "Search the web", ["input"] = new JsonObject { ["hint"] = "query" } }),
                    }).ConfigureAwait(false);
                    break;
                case "session/set_mode":
                    await RespondAsync(id, new JsonObject()).ConfigureAwait(false);
                    break;
                case "session/cancel":
                    _cancelled.TrySetResult();
                    break;
                case "session/prompt":
                    _cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var text = parameters["prompt"]?.AsArray().Select(b => b?["text"]?.GetValue<string>() ?? "").LastOrDefault() ?? "";
                    var command = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Last().Trim();
                    var stop = await PromptAsync(command).ConfigureAwait(false);
                    await RespondAsync(id, new JsonObject { ["stopReason"] = stop }).ConfigureAwait(false);
                    break;
                default:
                    if (id is not null)
                    {
                        await ErrorAsync(id, -32601, "Method not found").ConfigureAwait(false);
                    }

                    break;
            }
        }

        private async Task<string> PromptAsync(string command)
        {
            var word = command.Split(' ', 2)[0];
            var rest = command.Length > word.Length ? command[(word.Length + 1)..] : "";
            switch (word)
            {
                case "hello":
                    await ChunkAsync("agent_thought_chunk", "Thinking about it.").ConfigureAwait(false);
                    await ChunkAsync("agent_message_chunk", "Hello ").ConfigureAwait(false);
                    await ChunkAsync("agent_message_chunk", "from the stand-in.").ConfigureAwait(false);
                    await UpdateAsync(new JsonObject
                    {
                        ["sessionUpdate"] = "plan",
                        ["entries"] = new JsonArray(
                            new JsonObject { ["content"] = "Say hello", ["priority"] = "high", ["status"] = "completed" },
                            new JsonObject { ["content"] = "Wait for more", ["priority"] = "low", ["status"] = "pending" }),
                    }).ConfigureAwait(false);
                    return "end_turn";

                case "read":
                    var parts = rest.Split(' ');
                    await ToolCallAsync("read_1", "read", "Read " + parts[0], "in_progress", new JsonArray(new JsonObject { ["path"] = parts[0], ["line"] = 2 })).ConfigureAwait(false);
                    var read = new JsonObject { ["sessionId"] = _sessionId, ["path"] = parts[0] };
                    if (parts.Length >= 3)
                    {
                        read["line"] = int.Parse(parts[1], CultureInfo.InvariantCulture);
                        read["limit"] = int.Parse(parts[2], CultureInfo.InvariantCulture);
                    }

                    var content = await RequestAsync("fs/read_text_file", read).ConfigureAwait(false);
                    await ChunkAsync("agent_message_chunk", content["error"] is null ? "Read: " + content["content"]!.GetValue<string>() : "Read failed: " + ErrorText(content)).ConfigureAwait(false);
                    await StatusAsync("read_1", content["error"] is null ? "completed" : "failed").ConfigureAwait(false);
                    return "end_turn";

                case "write":
                    await ToolCallAsync("write_1", "edit", "Write " + rest, "pending", new JsonArray(new JsonObject { ["path"] = rest })).ConfigureAwait(false);
                    var written = await RequestAsync("fs/write_text_file", new JsonObject { ["sessionId"] = _sessionId, ["path"] = rest, ["content"] = "written by the agent\n" }).ConfigureAwait(false);
                    await ChunkAsync("agent_message_chunk", written["error"] is null ? "Wrote it." : "Write failed: " + ErrorText(written)).ConfigureAwait(false);
                    await UpdateAsync(new JsonObject
                    {
                        ["sessionUpdate"] = "tool_call_update",
                        ["toolCallId"] = "write_1",
                        ["status"] = written["error"] is null ? "completed" : "failed",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "diff", ["path"] = rest, ["oldText"] = null, ["newText"] = "written by the agent\n" }),
                    }).ConfigureAwait(false);
                    return "end_turn";

                case "permission":
                    var outcome = await PermissionAsync("perm_1").ConfigureAwait(false);
                    await ChunkAsync("agent_message_chunk", "Permission: " + outcome).ConfigureAwait(false);
                    return "end_turn";

                case "run":
                    var created = await RequestAsync("terminal/create", new JsonObject { ["sessionId"] = _sessionId, ["command"] = rest, ["outputByteLimit"] = 10_000 }).ConfigureAwait(false);
                    if (created["error"] is not null)
                    {
                        await ChunkAsync("agent_message_chunk", "Run failed: " + ErrorText(created)).ConfigureAwait(false);
                        return "end_turn";
                    }

                    var terminal = created["terminalId"]!.GetValue<string>();
                    await UpdateAsync(new JsonObject
                    {
                        ["sessionUpdate"] = "tool_call",
                        ["toolCallId"] = "run_1",
                        ["title"] = "Run " + rest,
                        ["kind"] = "execute",
                        ["status"] = "in_progress",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "terminal", ["terminalId"] = terminal }),
                    }).ConfigureAwait(false);
                    var exit = await RequestAsync("terminal/wait_for_exit", new JsonObject { ["sessionId"] = _sessionId, ["terminalId"] = terminal }).ConfigureAwait(false);
                    var shown = await RequestAsync("terminal/output", new JsonObject { ["sessionId"] = _sessionId, ["terminalId"] = terminal }).ConfigureAwait(false);
                    await RequestAsync("terminal/release", new JsonObject { ["sessionId"] = _sessionId, ["terminalId"] = terminal }).ConfigureAwait(false);
                    var after = await RequestAsync("terminal/output", new JsonObject { ["sessionId"] = _sessionId, ["terminalId"] = terminal }).ConfigureAwait(false);
                    await StatusAsync("run_1", "completed").ConfigureAwait(false);
                    await ChunkAsync("agent_message_chunk", $"Exit {exit["exitCode"]?.ToJsonString()}: {shown["output"]?.GetValue<string>().Trim()} (after release: {(after["error"] is null ? "still there" : "gone")})").ConfigureAwait(false);
                    return "end_turn";

                case "diff":
                    var oldText = File.Exists(rest) ? await File.ReadAllTextAsync(rest).ConfigureAwait(false) : null;
                    await File.WriteAllTextAsync(rest, "changed by the agent itself\n").ConfigureAwait(false);
                    await UpdateAsync(new JsonObject
                    {
                        ["sessionUpdate"] = "tool_call",
                        ["toolCallId"] = "diff_1",
                        ["title"] = "Edit " + Path.GetFileName(rest),
                        ["kind"] = "edit",
                        ["status"] = "completed",
                        ["locations"] = new JsonArray(new JsonObject { ["path"] = rest, ["line"] = 1 }),
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "diff", ["path"] = rest, ["oldText"] = oldText, ["newText"] = "changed by the agent itself\n" }),
                    }).ConfigureAwait(false);
                    return "end_turn";

                case "slow":
                    await ToolCallAsync("slow_1", "execute", "Think for a long time", "in_progress", null).ConfigureAwait(false);
                    var waiting = PermissionAsync("perm_slow");
                    await _cancelled.Task.ConfigureAwait(false);
                    var answered = await waiting.ConfigureAwait(false);
                    await ChunkAsync("agent_message_chunk", "Permission: " + answered).ConfigureAwait(false);
                    return "cancelled";

                case "crash":
                    await ChunkAsync("agent_message_chunk", "Starting…").ConfigureAwait(false);
                    await Console.Error.WriteLineAsync("the stand-in agent crashed on purpose").ConfigureAwait(false);
                    await Console.Error.FlushAsync().ConfigureAwait(false);
                    await Task.Delay(200).ConfigureAwait(false);
                    Environment.Exit(3);
                    return "end_turn";

                case "compact":
                    await UpdateAsync(new JsonObject { ["sessionUpdate"] = "compaction_update", ["compactionId"] = "cmp_1", ["status"] = "in_progress" }).ConfigureAwait(false);
                    await UpdateAsync(new JsonObject { ["sessionUpdate"] = "compaction_update", ["compactionId"] = "cmp_1", ["status"] = "completed" }).ConfigureAwait(false);
                    await UpdateAsync(new JsonObject { ["sessionUpdate"] = "notice", ["severity"] = "warning", ["title"] = "The context is nearly full." }).ConfigureAwait(false);
                    await ChunkAsync("agent_message_chunk", "Compacted.").ConfigureAwait(false);
                    return "end_turn";

                case "mode":
                    await UpdateAsync(new JsonObject { ["sessionUpdate"] = "current_mode_update", ["currentModeId"] = "code" }).ConfigureAwait(false);
                    return "end_turn";

                default:
                    await ChunkAsync("agent_message_chunk", "You said: " + command).ConfigureAwait(false);
                    return "end_turn";
            }
        }

        private Task ToolCallAsync(string id, string kind, string title, string status, JsonArray? locations) =>
            UpdateAsync(new JsonObject
            {
                ["sessionUpdate"] = "tool_call",
                ["toolCallId"] = id,
                ["title"] = title,
                ["kind"] = kind,
                ["status"] = status,
                ["locations"] = locations ?? [],
                ["rawInput"] = new JsonObject { ["title"] = title },
            });

        private Task StatusAsync(string id, string status) =>
            UpdateAsync(new JsonObject { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = id, ["status"] = status });

        private async Task<string> PermissionAsync(string toolCallId)
        {
            var answer = await RequestAsync("session/request_permission", new JsonObject
            {
                ["sessionId"] = _sessionId,
                ["toolCall"] = new JsonObject
                {
                    ["toolCallId"] = toolCallId,
                    ["title"] = "Delete the build output",
                    ["kind"] = "delete",
                    ["rawInput"] = new JsonObject { ["path"] = "bin/" },
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "diff", ["path"] = "notes.txt", ["oldText"] = "old line\n", ["newText"] = "new line\n" }),
                },
                ["options"] = new JsonArray(
                    new JsonObject { ["optionId"] = "allow", ["name"] = "Allow", ["kind"] = "allow_once" },
                    new JsonObject { ["optionId"] = "always", ["name"] = "Always allow", ["kind"] = "allow_always" },
                    new JsonObject { ["optionId"] = "reject", ["name"] = "Reject", ["kind"] = "reject_once" },
                    new JsonObject { ["optionId"] = "never", ["name"] = "Never", ["kind"] = "reject_always" }),
            }).ConfigureAwait(false);
            var outcome = answer["outcome"];
            return outcome?["outcome"]?.GetValue<string>() == "selected" ? "selected " + outcome["optionId"]!.GetValue<string>() : outcome?["outcome"]?.GetValue<string>() ?? "error";
        }
    }
}
