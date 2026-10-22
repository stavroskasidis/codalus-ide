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
/// <para>
/// Sign-in modes (ADR 0083), for the Connect flow and terminal sign-in:
/// <list type="bullet">
/// <item><c>ACP_FAKE_INIT_FILE</c>: answer <c>initialize</c> with a recorded answer (tests/Shared/AcpRecordings); its
/// methods count as offered, and <c>authenticate</c> with one of them signs in.</item>
/// <item><c>ACP_FAKE_REQUIRE_ENV=NAME=VALUE</c>: <c>session/new</c> answers <c>auth_required</c> unless that variable has that value
/// (or, for <c>sha256:HEX</c>, a value with that SHA-256).</item>
/// <item><c>ACP_FAKE_AUTH_ON_PROMPT=1</c>: <c>session/new</c> succeeds and <c>session/prompt</c> answers <c>auth_required</c> until signed in (Claude's adapter).</item>
/// <item><c>ACP_FAKE_AUTH_STATUS=none|account|silent</c>: the <c>authStatus</c> marker, <c>auth.logout</c>, and a
/// <c>_auth/status_update</c> push after <c>initialize</c> (none: signed out; account: signed in; silent: no push).</item>
/// <item><c>ACP_FAKE_TERMINAL_AUTH=1</c>: a terminal method <c>fake-terminal-login</c> (<c>--login</c>, with
/// <c>ACP_FAKE_LOGIN_MODE=1</c>) when the client advertises <c>auth.terminal</c>.</item>
/// <item><c>--login</c> on the command line: the terminal sign-in itself: it writes <c>ACP_FAKE_LOGIN_MARKER</c> (unless
/// <c>ACP_FAKE_LOGIN_FAILS=1</c>) and exits with <c>ACP_FAKE_LOGIN_EXIT</c> (0); a start that finds the marker is signed in.</item>
/// <item><c>ACP_FAKE_PROBE_CLIENT=1</c>: before answering <c>session/new</c>, it asks the client to read a file, run a command and allow
/// something, and logs the answers (the connection test refuses them all).</item>
/// <item><c>ACP_FAKE_STDERR</c>: a line on standard error at start; <c>ACP_FAKE_START_DELAY</c>: milliseconds before it reads.</item>
/// <item><c>logout</c> deletes the marker and pushes <c>none</c>.</item>
/// </list>
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
        // The terminal sign-in (a terminal method's --login): it signs in by writing the marker, then exits.
        if (Environment.GetCommandLineArgs().Contains("--login"))
        {
            if (Environment.GetEnvironmentVariable("ACP_FAKE_LOGIN_MARKER") is { Length: > 0 } marker && Environment.GetEnvironmentVariable("ACP_FAKE_LOGIN_FAILS") != "1")
            {
                await File.WriteAllTextAsync(marker, Environment.GetEnvironmentVariable("ACP_FAKE_LOGIN_MODE") ?? "").ConfigureAwait(false);
            }

            if (Environment.GetEnvironmentVariable("ACP_FAKE_LOG") is { Length: > 0 } loginLog)
            {
                await File.AppendAllTextAsync(loginLog, new JsonObject { ["login"] = string.Join(' ', Environment.GetCommandLineArgs().Skip(1)), ["cwd"] = Environment.CurrentDirectory, ["mode"] = Environment.GetEnvironmentVariable("ACP_FAKE_LOGIN_MODE") }.ToJsonString() + "\n").ConfigureAwait(false);
            }

            return int.TryParse(Environment.GetEnvironmentVariable("ACP_FAKE_LOGIN_EXIT"), CultureInfo.InvariantCulture, out var code) ? code : 0;
        }

        if (Environment.GetEnvironmentVariable("ACP_FAKE_STDERR") is { Length: > 0 } stderr)
        {
            await Console.Error.WriteLineAsync(stderr).ConfigureAwait(false);
            await Console.Error.FlushAsync().ConfigureAwait(false);
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("ACP_FAKE_START_DELAY"), CultureInfo.InvariantCulture, out var delay))
        {
            await Task.Delay(delay).ConfigureAwait(false);
        }

        var agent = new Agent(output, Environment.GetEnvironmentVariable("ACP_FAKE_LOG"));

        // Where the IDE started it (a neutral folder, not the workspace: a cloned repository's .npmrc mustn't steer npx),
        // and the variables a test looks for (secretEnv's, unsetEnv's): present or not, never their values in a real agent.
        agent.Log(new JsonObject
        {
            ["cwd"] = Environment.CurrentDirectory,
            ["env"] = new JsonObject(((Environment.GetEnvironmentVariable("ACP_FAKE_REPORT_ENV") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                .Select(n => KeyValuePair.Create(n, (JsonNode?)(Environment.GetEnvironmentVariable(n) is { } v ? JsonValue.Create(v) : null)))),
        }.ToJsonString());
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
        private string[] _offered = ["fake-login"];

        private static string? Marker => Environment.GetEnvironmentVariable("ACP_FAKE_LOGIN_MARKER") is { Length: > 0 } m ? m : null;

        private static string? StatusMode => Environment.GetEnvironmentVariable("ACP_FAKE_AUTH_STATUS") is { Length: > 0 } m ? m : null;

        /// <summary>Signed in: authenticated, or the terminal sign-in's marker is there.</summary>
        private bool SignedIn => _authenticated || (Marker is { } m && File.Exists(m));

        private Task PushStatusAsync(bool signedIn) =>
            SendAsync(new JsonObject
            {
                ["method"] = "_auth/status_update",
                ["params"] = new JsonObject
                {
                    ["authStatus"] = signedIn
                        ? new JsonObject { ["kind"] = "subscription", ["label"] = "Claude Max", ["detail"] = "tester@example.com" }
                        : new JsonObject { ["kind"] = "none", ["label"] = "Not logged in" },
                },
            });

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

        /// <summary>A variable's value against the expected one, or its SHA-256 (<c>sha256:HEX</c>, so a test's settings never hold the key).</summary>
        private static bool Matches(string? actual, string expected) =>
            actual is not null && (expected.StartsWith("sha256:", StringComparison.Ordinal)
                ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(actual))) == expected["sha256:".Length..]
                : actual == expected);

        private static string ErrorText(JsonObject answer) => answer["error"]?["message"]?.GetValue<string>() ?? "";

        public async Task HandleAsync(JsonObject message)
        {
            var id = message["id"];
            var parameters = message["params"] as JsonObject ?? [];
            switch (message["method"]!.GetValue<string>())
            {
                case "initialize":
                    if (Environment.GetEnvironmentVariable("ACP_FAKE_INIT_FILE") is { Length: > 0 } recorded)
                    {
                        var answer = JsonNode.Parse(await File.ReadAllTextAsync(recorded).ConfigureAwait(false))!.AsObject();
                        answer.Remove("_source");
                        _offered = [.. (answer["authMethods"]?.AsArray() ?? []).Select(m => m!["id"]!.GetValue<string>())];
                        await RespondAsync(id, answer).ConfigureAwait(false);
                    }
                    else
                    {
                        var protocol = int.TryParse(Environment.GetEnvironmentVariable("ACP_FAKE_PROTOCOL"), CultureInfo.InvariantCulture, out var p) ? p : 1;
                        var auth = new JsonArray();
                        if (Environment.GetEnvironmentVariable("ACP_FAKE_AUTH") == "1" || Environment.GetEnvironmentVariable("ACP_FAKE_AUTH_ON_PROMPT") == "1")
                        {
                            auth.Add(new JsonObject { ["id"] = "fake-login", ["name"] = "Fake login", ["description"] = "Signs in to nothing." });
                        }

                        if (Environment.GetEnvironmentVariable("ACP_FAKE_TERMINAL_AUTH") == "1" && parameters["clientCapabilities"]?["auth"]?["terminal"]?.GetValue<bool>() == true)
                        {
                            auth.Add(new JsonObject { ["id"] = "fake-terminal-login", ["name"] = "Fake terminal login", ["type"] = "terminal", ["args"] = new JsonArray("--login"), ["env"] = new JsonObject { ["ACP_FAKE_LOGIN_MODE"] = "1" } });
                        }

                        var capabilities = new JsonObject { ["loadSession"] = false, ["promptCapabilities"] = new JsonObject { ["embeddedContext"] = true } };
                        if (StatusMode is not null)
                        {
                            capabilities["_meta"] = new JsonObject { ["authStatus"] = new JsonObject() };
                            capabilities["auth"] = new JsonObject { ["logout"] = new JsonObject() };
                        }

                        await RespondAsync(id, new JsonObject
                        {
                            ["protocolVersion"] = protocol,
                            ["agentCapabilities"] = capabilities,
                            ["agentInfo"] = new JsonObject { ["name"] = "stand-in", ["title"] = "Stand-in agent", ["version"] = "1.0.0" },
                            ["authMethods"] = auth,
                        }).ConfigureAwait(false);
                    }

                    // Claude's adapter: who is signed in, pushed after the answer (never before it).
                    if (StatusMode is { } mode && (mode != "silent" || SignedIn))
                    {
                        await PushStatusAsync(SignedIn || mode == "account").ConfigureAwait(false);
                    }

                    break;
                case "authenticate":
                    var method = parameters["methodId"]?.GetValue<string>();
                    _authenticated = method is not null && _offered.Contains(method);
                    await RespondAsync(id, new JsonObject()).ConfigureAwait(false);
                    break;
                case "logout":
                    _authenticated = false;
                    if (Marker is { } signedIn && File.Exists(signedIn))
                    {
                        File.Delete(signedIn);
                    }

                    await RespondAsync(id, new JsonObject()).ConfigureAwait(false);
                    if (StatusMode is not null)
                    {
                        await PushStatusAsync(false).ConfigureAwait(false);
                    }

                    break;
                case "session/new":
                    if (Environment.GetEnvironmentVariable("ACP_FAKE_AUTH") == "1" && !SignedIn)
                    {
                        await ErrorAsync(id, -32000, "Authentication required").ConfigureAwait(false);
                        break;
                    }

                    if (Environment.GetEnvironmentVariable("ACP_FAKE_REQUIRE_ENV") is { Length: > 0 } required
                        && required.Split('=', 2) is [var name, var value] && !Matches(Environment.GetEnvironmentVariable(name), value) && !_authenticated)
                    {
                        await ErrorAsync(id, -32000, "Authentication required").ConfigureAwait(false);
                        break;
                    }

                    _sessionId = "sess_stand_in";
                    if (Environment.GetEnvironmentVariable("ACP_FAKE_PROBE_CLIENT") == "1")
                    {
                        // What the connection test must refuse, asked before the answer so the client is still there: each
                        // answer is logged (as a response) for the test to read.
                        await RequestAsync("fs/read_text_file", new JsonObject { ["sessionId"] = _sessionId, ["path"] = Path.Combine(Environment.CurrentDirectory, "probe.txt") }).ConfigureAwait(false);
                        await RequestAsync("terminal/create", new JsonObject { ["sessionId"] = _sessionId, ["command"] = "echo", ["args"] = new JsonArray("probe") }).ConfigureAwait(false);
                        await PermissionAsync("perm_probe").ConfigureAwait(false);
                    }

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
                    if (Environment.GetEnvironmentVariable("ACP_FAKE_AUTH_ON_PROMPT") == "1" && !SignedIn)
                    {
                        await ErrorAsync(id, -32000, "Authentication required. Please run /login").ConfigureAwait(false);
                        break;
                    }

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
                case "runenv":
                    // runenv NAME=VALUE <command>: the command with an environment variable the agent supplies.
                    var request = new JsonObject { ["sessionId"] = _sessionId, ["outputByteLimit"] = 10_000 };
                    if (word == "runenv")
                    {
                        var pair = rest.Split(' ', 2);
                        var variable = pair[0].Split('=', 2);
                        request["env"] = new JsonArray(new JsonObject { ["name"] = variable[0], ["value"] = variable[1] });
                        rest = pair[1];
                    }

                    request["command"] = rest;
                    var created = await RequestAsync("terminal/create", request).ConfigureAwait(false);
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
