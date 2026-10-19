using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Codalus.Platform;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Acp;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Plugins;
using Codalus.Workbench.Settings;

namespace Codalus.Workbench.Tests;

/// <summary>
/// External agents over the Agent Client Protocol (M48, ADR 0079), against the stand-in agent (tests/Codalus.AcpAgent.Fake)
/// and a stand-in host: negotiation, the turn's updates, the IDE's checks on what the agent asks for (paths, exclusions,
/// redaction, the preview, permissions, commands), cancelling, a crash, sign-in, and who may start an agent.
/// </summary>
public sealed class AcpAgentTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-acp-" + Guid.NewGuid().ToString("N"));
    private readonly string _log;
    private readonly WorkbenchServices _workbench;
    private readonly AiService _ai;
    private readonly AcpAgents _agents;
    private readonly FakeHost _host = new();
    private readonly AiChat _chat;
    private bool _trusted = true;
    private string _workspace;

    public AcpAgentTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "obj/\n");
        File.WriteAllText(Path.Combine(_root, "src", "A.cs"), "class A\n{\n    int X() => 1;\n}\n");
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), "{ \"Password\": \"hunter2\" }\n");
        File.WriteAllText(Path.Combine(_root, ".env"), "TOKEN=abc\n");
        var workspaceFile = Path.Combine(_root, ".codalus", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(workspaceFile)!);
        File.WriteAllText(workspaceFile, "{}");
        _log = Path.Combine(_root, ".user-agent-log.jsonl");
        _workbench = new WorkbenchServices(AppPaths.Resolve(PlatformInfo.Current, _ => null, Path.Combine(_root, ".user")), PlatformInfo.Current, workspaceFile, watchFiles: false);
        _workbench.Settings.Register(AiSettings.Descriptors(ContributionSource.Shell));
        var models = new LanguageModelService(new PluginManager(_workbench, _ => Codalus.Rpc.ProcessLaunchInfo.FromPath("none"), "test", new Codalus.Rpc.SupervisionPolicy()));
        _workspace = _root;
        _ai = new AiService(_workbench.Settings, models, _workbench.Output, () => _workspace, () => _trusted);
        _agents = new AcpAgents(_workbench.Settings, _ai);
        _chat = new AiChat(_ai);
    }

    public async ValueTask DisposeAsync()
    {
        await _agents.DisposeAsync();
        _workbench.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Metadata(string key) =>
        typeof(AcpAgentTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    /// <summary>The stand-in agent's assembly (run with dotnet, as a user's agent CLI is run).</summary>
    public static string StandInAgent => Path.Combine(Metadata("CodalusRepoRoot"), "tests", "Codalus.AcpAgent.Fake", "bin", Metadata("CodalusConfiguration"), Codalus.Tests.RepoBuild.TargetFramework, "codalus-fake-acp-agent.dll");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The dotnet that runs the tests, by its absolute path (it needn't be on the PATH).</summary>
    private static string Dotnet =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host
        : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")).First(File.Exists);

    private async Task ConfigureAsync(string extraEnv = "")
    {
        await _workbench.Settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, Ct);
        var env = new Dictionary<string, string> { ["ACP_FAKE_LOG"] = _log };
        foreach (var pair in extraEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            env[pair.Split('=')[0]] = pair.Split('=')[1];
        }

        var agents = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["stand-in"] = new { command = Dotnet, args = new[] { StandInAgent }, env } });
        await _workbench.Settings.SetAsync(AcpAgents.AgentsSetting, agents, SettingScope.User, Ct);
    }

    private Task<AiChatTurn> AskAsync(string question, CancellationToken? cancellationToken = null) =>
        _chat.SendExternalAsync(question, [], _agents, "stand-in", _host, cancellationToken ?? Ct);

    /// <summary>The messages the stand-in received with this method, as JSON.</summary>
    private List<JsonElement> Received(string method) =>
        [.. File.ReadAllLines(_log).Select(l => JsonElement.Parse(l)).Where(m => m.TryGetProperty("method", out var name) && name.GetString() == method)];

    private string AiLog => _workbench.Output.GetOrCreate(AiService.OutputChannelId).GetText();

    [Fact]
    public async Task Initialize_negotiates_version_1_and_the_capabilities_and_the_session_gets_the_workspace_and_codalus_mcp()
    {
        await ConfigureAsync();
        var turn = await AskAsync("hello");
        Assert.Null(turn.Error);
        var session = _agents.Current!;
        Assert.Equal(("stand-in", 1), (session.Info!.AgentInfo!.Name, session.Info.ProtocolVersion));

        var initialize = Received("initialize").Single().GetProperty("params");
        Assert.Equal(1, initialize.GetProperty("protocolVersion").GetInt32());
        var capabilities = initialize.GetProperty("clientCapabilities");
        Assert.True(capabilities.GetProperty("fs").GetProperty("readTextFile").GetBoolean());
        Assert.True(capabilities.GetProperty("fs").GetProperty("writeTextFile").GetBoolean());
        Assert.True(capabilities.GetProperty("terminal").GetBoolean());
        Assert.False(capabilities.GetProperty("auth").GetProperty("terminal").GetBoolean());
        Assert.False(capabilities.TryGetProperty("elicitation", out _)); // not advertised: TODO(issue)
        Assert.Equal(JsonValueKind.Object, capabilities.GetProperty("session").GetProperty("compaction").ValueKind);
        Assert.Equal(JsonValueKind.Object, capabilities.GetProperty("session").GetProperty("notices").ValueKind);
        Assert.Equal("codalus", initialize.GetProperty("clientInfo").GetProperty("name").GetString());

        var created = Received("session/new").Single().GetProperty("params");
        Assert.Equal(_root, created.GetProperty("cwd").GetString());
        Assert.False(created.TryGetProperty("additionalDirectories", out _));
        var mcp = Assert.Single(created.GetProperty("mcpServers").EnumerateArray());
        Assert.Equal("codalus", mcp.GetProperty("name").GetString());
        Assert.True(Path.IsPathRooted(mcp.GetProperty("command").GetString()));
        Assert.Equal(["mcp", "--agent-session", "token-1"], mcp.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(1, _host.McpStarted);

        // The same session answers the next question; New Chat stops it, and the MCP pipe with it.
        await AskAsync("hello again");
        Assert.Same(session, _agents.Current);
        Assert.Single(Received("initialize"));
        await _agents.StopAsync();
        Assert.False(session.IsAlive);
        Assert.Equal(1, _host.McpStopped);
        Assert.Contains("external agent stand-in: session/prompt", AiLog, StringComparison.Ordinal);
        Assert.Contains("agent stand-in: stopped", AiLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_of_another_protocol_version_is_refused()
    {
        await ConfigureAsync("ACP_FAKE_PROTOCOL=2");
        var turn = await AskAsync("hello");
        Assert.Contains("speaks ACP version 2", turn.Error, StringComparison.Ordinal);
        Assert.Null(_agents.Current);
    }

    [Fact]
    public void Codalus_mcp_is_passed_by_the_absolute_path_of_the_running_executable()
    {
        var native = AcpAgents.CodalusMcpServer(Path.Combine(_root, "codalus"), null, Path.Combine(_root, "data"), "abc");
        Assert.Equal(Path.Combine(_root, "codalus"), native.Command);
        Assert.Equal(["mcp", "--user-data-dir", Path.Combine(_root, "data"), "--agent-session", "abc"], native.Args);

        // Run as dotnet codalus.dll (development, tests): dotnet and the shell's assembly, both absolute.
        var dotnet = AcpAgents.CodalusMcpServer(Path.Combine(_root, "dotnet"), "codalus.dll", null, "abc");
        Assert.True(Path.IsPathRooted(dotnet.Command));
        Assert.True(Path.IsPathRooted(dotnet.Args[0]));
        Assert.Equal(["mcp", "--agent-session", "abc"], dotnet.Args.Skip(1));
    }

    [Fact]
    public async Task A_turn_streams_messages_thoughts_plans_commands_modes_and_notices()
    {
        await ConfigureAsync();
        var turn = await AskAsync("hello");
        Assert.Equal(("Hello from the stand-in.", "Thinking about it.", "stop"), (turn.Answer, turn.Thought, turn.FinishReason));
        Assert.Equal([new AiPlanEntry("Say hello", "completed"), new AiPlanEntry("Wait for more", "pending")], turn.Plan);
        Assert.Equal("stand-in", turn.ExternalAgent);
        var session = _agents.Current!;
        Assert.Equal(["review", "web"], session.Commands.Select(c => c.Name));
        Assert.Equal("query", session.Commands[1].Input?.Hint);
        Assert.Equal(["ask", "code"], session.Modes.Select(m => m.Id));
        Assert.Equal("ask", session.CurrentModeId);

        await AskAsync("mode");
        Assert.Equal("code", session.CurrentModeId);
        await session.SetModeAsync("ask", Ct);
        Assert.Equal("ask", session.CurrentModeId);
        Assert.Equal("ask", Received("session/set_mode").Single().GetProperty("params").GetProperty("modeId").GetString());

        turn = await AskAsync("compact");
        Assert.Equal(
            [new AiTurnNotice("info", "The agent compacted the conversation's context."), new AiTurnNotice("warning", "The context is nearly full.")],
            turn.Notices);
    }

    [Fact]
    public async Task Reading_stays_in_the_workspace_away_from_excluded_files_and_is_redacted()
    {
        await ConfigureAsync();
        var turn = await AskAsync("read src/A.cs 2 2");
        Assert.Equal("Read: {\n    int X() => 1;", turn.Answer);
        var step = Assert.Single(turn.Steps);
        Assert.Equal(("read", "Read src/A.cs", "completed"), (step.Kind, step.Target, step.Status));
        Assert.Equal(new AiAgentLocation("src/A.cs", 2), Assert.Single(step.Locations));

        Assert.Contains("Read: class A", (await AskAsync("read " + Path.Combine(_root, "src", "A.cs"))).Answer, StringComparison.Ordinal);
        Assert.Contains("outside the workspace", (await AskAsync("read ../outside.txt")).Answer, StringComparison.Ordinal);
        Assert.Contains("outside the workspace", (await AskAsync("read " + Path.Combine(Path.GetTempPath(), "x.txt"))).Answer, StringComparison.Ordinal);
        Assert.Contains("excluded from AI (holds secrets)", (await AskAsync("read .env")).Answer, StringComparison.Ordinal);
        Assert.Equal("Read: { \"Password\": \"[redacted]\" }\n", (await AskAsync("read appsettings.json")).Answer);
        Assert.Contains("doesn't exist", (await AskAsync("read src/Missing.cs")).Answer, StringComparison.Ordinal);
        Assert.Equal("failed", (await AskAsync("read .env")).Steps[0].Status);
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "escape"), Path.GetTempPath());
            Assert.Contains("leads outside the workspace", (await AskAsync("read escape/x.txt")).Answer, StringComparison.Ordinal);
        }

        // The editor's text, not the disk's, when the file is open.
        _host.Open[Path.Combine(_root, "src", "A.cs")] = "class Unsaved {}\n";
        Assert.Equal("Read: class Unsaved {}\n", (await AskAsync("read src/A.cs")).Answer);

        Assert.Contains("fs/read_text_file src/A.cs (lines 2–3)", AiLog, StringComparison.Ordinal);
        Assert.Contains("fs/read_text_file .env → refused: excluded (holds secrets)", AiLog, StringComparison.Ordinal);
        Assert.Contains("1 secret(s) redacted", AiLog, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", AiLog, StringComparison.Ordinal);
        Assert.DoesNotContain("int X()", AiLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_write_goes_through_the_preview_and_a_rejection_is_an_error_for_the_agent()
    {
        await ConfigureAsync();
        var file = Path.Combine(_root, "src", "A.cs");
        _host.Apply = false;
        var turn = await AskAsync("write src/A.cs");
        Assert.Equal("Write failed: The user rejected the change to src/A.cs. Don't make it again; ask what they want instead.", turn.Answer);
        Assert.Equal((file, "class A\n{\n    int X() => 1;\n}\n", "written by the agent\n"), _host.Proposed.Single());
        Assert.Equal("failed", turn.Steps.Single().Status);

        _host.Apply = true;
        turn = await AskAsync("write src/New.cs");
        Assert.Equal("Wrote it.", turn.Answer);
        Assert.Equal((Path.Combine(_root, "src", "New.cs"), (string?)null, "written by the agent\n"), _host.Proposed[^1]);
        Assert.True(Assert.Single(turn.Steps.Single().Diffs).Reviewed);
        Assert.Empty(_host.Reloaded); // reviewed: the buffer has it

        Assert.Contains("excluded from AI", (await AskAsync("write .env")).Answer, StringComparison.Ordinal);
        Assert.Contains("outside the workspace", (await AskAsync("write ../x.cs")).Answer, StringComparison.Ordinal);
        Assert.Equal(2, _host.Proposed.Count);
        Assert.Contains("fs/write_text_file src/A.cs → rejected", AiLog, StringComparison.Ordinal);
        Assert.Contains("fs/write_text_file src/New.cs → applied", AiLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_change_the_agent_makes_itself_shows_read_only_and_the_open_editor_reloads()
    {
        await ConfigureAsync();
        var file = Path.Combine(_root, "src", "A.cs");
        var turn = await AskAsync("diff " + file);
        var diff = Assert.Single(turn.Steps.Single().Diffs);
        Assert.Equal((false, "changed by the agent itself\n"), (diff.Reviewed, diff.NewText));
        Assert.StartsWith("class A", diff.OldText, StringComparison.Ordinal);
        Assert.Equal([file], _host.Reloaded);
        Assert.Contains("changed src/A.cs itself (not reviewed in the preview)", AiLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_permission_offers_no_allow_always_and_reject_is_the_default()
    {
        await ConfigureAsync();
        _host.Permission = _ => null; // the dialog's default, Reject
        var turn = await AskAsync("permission");
        Assert.Equal("Permission: selected reject", turn.Answer);
        var prompt = _host.Permissions.Single();
        Assert.Equal(["allow_once", "reject_once", "reject_always"], prompt.Options.Select(o => o.Kind));
        Assert.Equal(("Delete the build output", "delete"), (prompt.Title, prompt.Kind));
        Assert.Contains("\"path\": \"bin/\"", prompt.RawInput, StringComparison.Ordinal);
        Assert.Equal(new AiStepDiff("notes.txt", "old line\n", "new line\n", false), Assert.Single(prompt.Diffs));

        _host.Permission = p => p.Options.Single(o => o.Kind == "allow_once").OptionId;
        Assert.Equal("Permission: selected allow", (await AskAsync("permission")).Answer);
        _host.Permission = _ => "always"; // not offered: treated as Reject
        Assert.Equal("Permission: selected reject", (await AskAsync("permission")).Answer);
        Assert.Contains("session/request_permission delete \"Delete the build output\"", AiLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_terminal_command_asks_first_shows_its_output_and_is_released()
    {
        await ConfigureAsync();
        _host.AllowCommands = false;
        var refused = await AskAsync("run echo agent-ran");
        Assert.Equal("Run failed: The user didn't allow the command. Don't run it again; ask what they want instead.", refused.Answer);
        Assert.Equal(("echo agent-ran", _root), _host.Commands.Single());

        _host.AllowCommands = true;
        var turn = await AskAsync("run echo agent-ran password=hunter2");
        Assert.Equal("Exit 0: agent-ran password=[redacted] (after release: gone)", turn.Answer);
        var step = turn.Steps.Single();
        Assert.Equal(("execute", "completed"), (step.Kind, step.Status));
        Assert.Contains("agent-ran password=[redacted]", step.Output, StringComparison.Ordinal);
        Assert.Contains("terminal/create echo agent-ran password=hunter2 in the workspace", AiLog, StringComparison.Ordinal);
        Assert.Contains("terminal/release", AiLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_stops_at_the_timeout_and_its_output_is_cut_from_the_start()
    {
        var terminals = new AcpTerminals { Timeout = TimeSpan.FromSeconds(1) };
        await using (terminals)
        {
            var (display, launch) = AcpTerminals.CommandLine(OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1" : "sleep 30", [], _root, null);
            var terminal = await terminals.StartAsync(display, launch, null, Ct);
            var status = await terminal.Exited.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Equal((null, "SIGKILL"), (status.ExitCode, status.Signal));
            Assert.Contains("stopped the command after 1 seconds", terminal.Output.Text, StringComparison.Ordinal);

            var (_, many) = AcpTerminals.CommandLine(Dotnet, ["--help"], _root, null);
            var cut = await terminals.StartAsync("dotnet --help", many, 64, Ct);
            await cut.Exited.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            Assert.True(cut.Output.Truncated);
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(cut.Output.Text) <= 64);
        }

        Assert.Equal(("dotnet \"two words\"", "dotnet"), (AcpTerminals.CommandLine("dotnet", ["two words"], _root, null).Display, AcpTerminals.CommandLine("dotnet", ["two words"], _root, null).Launch.FileName));
    }

    [Fact]
    public async Task Cancelling_mid_turn_sends_session_cancel_and_answers_the_pending_permission_cancelled()
    {
        await ConfigureAsync();
        var asked = new TaskCompletionSource();
        _host.PermissionAsync = async (_, ct) =>
        {
            asked.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var asking = AskAsync("slow", stop.Token);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await stop.CancelAsync();
        var turn = await asking.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(("stopped", "Permission: cancelled"), (turn.FinishReason, turn.Answer));
        Assert.Equal("cancelled", turn.Steps.Single().Status);
        Assert.Single(Received("session/cancel"));
        Assert.True(_agents.Current!.IsAlive);
        Assert.Contains("session/request_permission → cancelled", AiLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_crash_mid_turn_ends_the_turn_with_the_agents_last_words_and_the_next_question_starts_it_again()
    {
        await ConfigureAsync();
        var turn = await AskAsync("crash");
        Assert.Equal("Starting…", turn.Answer);
        Assert.Contains("The agent 'stand-in' stopped.", turn.Error, StringComparison.Ordinal);
        Assert.Contains("crashed on purpose", turn.Error, StringComparison.Ordinal);
        var dead = _agents.Current!;
        Assert.False(dead.IsAlive);

        turn = await AskAsync("hello");
        Assert.Null(turn.Error);
        Assert.NotSame(dead, _agents.Current);
        Assert.Equal(1, _host.McpStopped); // the dead session's pipe closed with it
    }

    [Fact]
    public async Task An_agent_that_needs_a_sign_in_shows_its_ways_and_runs_the_chosen_one()
    {
        await ConfigureAsync("ACP_FAKE_AUTH=1");
        _host.SignIn = _ => null;
        Assert.Equal("stand-in needs you to sign in first.", (await AskAsync("hello")).Error);
        Assert.Equal("fake-login", _host.SignIns.Single().Single().Id);

        _host.SignIn = methods => methods[0];
        var turn = await AskAsync("hello");
        Assert.Null(turn.Error);
        Assert.Equal("fake-login", Received("authenticate").Last().GetProperty("params").GetProperty("methodId").GetString());
    }

    [Fact]
    public async Task Agents_come_from_the_users_settings_only()
    {
        await _workbench.Settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, Ct);
        var agents = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["evil"] = new { command = Dotnet, args = new[] { StandInAgent } } });
        await _workbench.Settings.SetAsync(AcpAgents.AgentsSetting, agents, SettingScope.Workspace, Ct);
        Assert.Empty(_agents.Configured);
        var turn = await _chat.SendExternalAsync("hello", [], _agents, "evil", _host, Ct);
        Assert.Equal("There is no agent 'evil' in your settings (ai.agents).", turn.Error);
        Assert.False(File.Exists(_log));

        using var parsed = JsonDocument.Parse("""{"pi":{"command":"npx","args":["pi-acp@0.0.34"],"env":{"K":"V"}},"broken":{"args":[]}}""");
        var pi = Assert.Single(AcpAgents.Parse(parsed.RootElement));
        Assert.Equal(("pi", "npx", "pi-acp@0.0.34", "V"), (pi.Key, pi.Value.Command, pi.Value.Args.Single(), pi.Value.Env["K"]));
    }

    [Fact]
    public async Task A_command_shows_the_variables_the_agent_sets_and_refuses_ones_that_change_what_runs()
    {
        await ConfigureAsync();
        _host.AllowCommands = true;

        // An ordinary variable is shown in the approval with the command line (its value redacted when it's a secret).
        var shown = await AskAsync("runenv GREETING=hello echo with-env");
        Assert.StartsWith("Exit 0:", shown.Answer, StringComparison.Ordinal);
        var prompt = _host.CommandPrompts.Single();
        Assert.Equal("echo with-env", prompt.CommandLine);
        Assert.Equal(["GREETING=hello"], prompt.Environment);
        await AskAsync("runenv API_TOKEN=ghp_0123456789abcdefghijklmnopqrstuvwxyzAB echo secret");
        Assert.DoesNotContain("ghp_0123456789abcdefghijklmnopqrstuvwxyzAB", _host.CommandPrompts[^1].Environment[0], StringComparison.Ordinal);

        // One that changes what an allowed command runs is refused before anyone is asked.
        foreach (var variable in new[] { "PATH=/tmp/evil", "GIT_CONFIG_PARAMETERS='core.pager=sh'", "NODE_OPTIONS=--require=/tmp/x.js", "LD_PRELOAD=/tmp/x.so", "dyld_insert_libraries=/tmp/x" })
        {
            var count = _host.CommandPrompts.Count;
            var refused = await AskAsync("runenv " + variable + " echo should-not-run");
            Assert.StartsWith("Run failed: The command sets " + variable.Split('=')[0] + ", which changes what a command runs.", refused.Answer, StringComparison.Ordinal);
            Assert.Equal(count, _host.CommandPrompts.Count);
        }

        Assert.Equal("NPM_CONFIG_REGISTRY", AcpTerminals.RefusedVariable(["HELLO", "NPM_CONFIG_REGISTRY"]));
        Assert.Null(AcpTerminals.RefusedVariable(["HELLO", "RUST_LOG", "CI"]));
    }

    [Fact]
    public async Task Turning_ai_off_stops_a_running_agent_and_its_requests_are_refused_while_it_may_not_run()
    {
        await ConfigureAsync();
        Assert.Null((await AskAsync("hello")).Error);
        var session = _agents.Current!;

        // Trust gone (the folder reopened untrusted): the agent's requests are refused, whatever it asks.
        _trusted = false;
        var read = await Assert.ThrowsAsync<Codalus.Rpc.JsonRpcHandlerException>(() => session.ReadTextFileAsync(new AcpReadTextFileRequest { SessionId = "s", Path = Path.Combine(_root, "src", "A.cs") }, Ct));
        Assert.Contains("only in trusted folders", read.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<Codalus.Rpc.JsonRpcHandlerException>(() => session.CreateTerminalAsync(new AcpCreateTerminalRequest { SessionId = "s", Command = "echo", Args = ["hi"] }, Ct));
        await _agents.EnforceAsync();
        Assert.Null(_agents.Current);
        Assert.False(session.IsAlive);

        // AI turned off while one runs: it stops.
        _trusted = true;
        Assert.Null((await AskAsync("hello")).Error);
        var second = _agents.Current!;
        await _workbench.Settings.SetAsync(AiService.EnabledSetting, false, SettingScope.User, Ct);
        await WaitUntilAsync(() => _agents.Current is null && !second.IsAlive);
    }

    [Fact]
    public async Task A_remote_window_refuses_to_start_an_agent()
    {
        await ConfigureAsync();
        _agents.IsRemoteWindow = () => true;
        var turn = await AskAsync("hello");
        Assert.Contains("don't run in a remote window", turn.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(_log)); // never started
    }

    [Fact]
    public async Task A_mistyped_entry_is_skipped_never_a_crash()
    {
        using var parsed = JsonDocument.Parse("""{"number":{"command":5},"object":{"command":{"x":1}},"text":"npx","array":[],"ok":{"command":"npx","args":"pi-acp","env":{"K":1,"L":"v"}}}""");
        var ok = Assert.Single(AcpAgents.Parse(parsed.RootElement));
        Assert.Equal(("ok", "npx", 0, 1), (ok.Key, ok.Value.Command, ok.Value.Args.Count, ok.Value.Env.Count));
        await _workbench.Settings.SetAsync(AcpAgents.AgentsSetting, parsed.RootElement.Clone(), SettingScope.User, Ct);
        Assert.Equal(["ok"], _agents.Configured.Keys);
    }

    [Fact]
    public async Task An_agent_started_while_the_folder_changed_doesnt_stay_for_the_old_folder()
    {
        await ConfigureAsync();
        var other = Path.Combine(_root, "src");
        _host.OnStart = () => _workspace = other;
        var turn = await AskAsync("hello");
        Assert.Contains("The folder changed while the agent started", turn.Error, StringComparison.Ordinal);
        Assert.Null(_agents.Current);
        Assert.Equal(_host.McpStarted, _host.McpStopped);
    }

    [Fact]
    public async Task The_agent_starts_in_a_neutral_folder_and_the_session_carries_the_workspace()
    {
        var neutral = Path.Combine(_root, ".user", "external-agents");
        _agents.ProcessDirectory = neutral;
        await ConfigureAsync();
        Assert.Null((await AskAsync("hello")).Error);
        var started = File.ReadAllLines(_log).Select(l => JsonElement.Parse(l)).First(m => m.TryGetProperty("cwd", out _));
        // The stand-in reports its working directory with links resolved (macOS: /var is /private/var).
        static string Real(string path) => Codalus.Platform.RealPath.Resolve(Path.GetFullPath(path)).TrimEnd(Path.DirectorySeparatorChar);
        Assert.Equal(Real(neutral), Real(started.GetProperty("cwd").GetString()!));
        Assert.Equal(_root, Received("session/new").Single().GetProperty("params").GetProperty("cwd").GetString());
    }

    [Fact]
    public async Task Output_limits_are_clamped_reports_are_throttled_and_only_the_shown_tail_is_redacted()
    {
        var terminals = new AcpTerminals();
        var reports = 0;
        terminals.Changed += (_, _) => Interlocked.Increment(ref reports);
        await using (terminals)
        {
            var (display, launch) = OperatingSystem.IsWindows()
                ? AcpTerminals.CommandLine("for /l %i in (1,1,3000) do @echo line %i", [], _root, null)
                : AcpTerminals.CommandLine("i=0; while [ $i -lt 3000 ]; do echo line $i; i=$((i+1)); done", [], _root, null);
            var terminal = await terminals.StartAsync(display, launch, long.MaxValue, Ct);
            Assert.Equal(AcpTerminals.MaxOutputLimit, terminal.OutputLimit);
            await terminal.Exited.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            Assert.Contains("line 2999", terminal.Output.Text, StringComparison.Ordinal);
            Assert.True(reports < 1000, $"{reports} reports for 3000 lines");
        }

        // The step's output: the end, redacted; a secret far before the end isn't in it, one near the end is redacted.
        var raw = "password=early-secret\n" + new string('x', 40_000) + "\nlast line password=hunter2\n";
        var shown = AcpAgentSession.ShownOutput(raw);
        Assert.StartsWith("…", shown, StringComparison.Ordinal);
        Assert.Contains("last line password=[redacted]", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("early-secret", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", shown, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), "Timed out.");
            await Task.Delay(20, Ct);
        }
    }

    [Fact]
    public async Task An_untrusted_workspace_or_ai_turned_off_refuses_to_start_one()
    {
        await ConfigureAsync();
        _trusted = false;
        var turn = await AskAsync("hello");
        Assert.Contains("only in trusted folders", turn.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(_log)); // never started

        _trusted = true;
        await _workbench.Settings.SetAsync(AiService.EnabledSetting, false, SettingScope.User, Ct);
        Assert.Contains("Turn on AI assistance first", (await AskAsync("hello")).Error, StringComparison.Ordinal);
        Assert.False(File.Exists(_log));
    }

    private sealed class FakeHost : IAcpHost
    {
        private int _tokens;

        public bool Apply { get; set; }

        public bool AllowCommands { get; set; }

        public ConcurrentDictionary<string, string> Open { get; } = new();

        public List<(string Path, string? Old, string New)> Proposed { get; } = [];

        public List<AcpPermissionPrompt> Permissions { get; } = [];

        public Func<AcpPermissionPrompt, string?> Permission { get; set; } = _ => null;

        public Func<AcpPermissionPrompt, CancellationToken, Task<string?>>? PermissionAsync { get; set; }

        public List<(string CommandLine, string Folder)> Commands { get; } = [];

        public List<AcpCommandPrompt> CommandPrompts { get; } = [];

        /// <summary>Runs when the session asks for its <c>codalus mcp</c> entry, during its start (tests change the folder there).</summary>
        public Action? OnStart { get; set; }

        public List<IReadOnlyList<AcpAuthMethod>> SignIns { get; } = [];

        public Func<IReadOnlyList<AcpAuthMethod>, AcpAuthMethod?> SignIn { get; set; } = _ => null;

        public List<string> Reloaded { get; } = [];

        public int McpStarted { get; private set; }

        public int McpStopped { get; private set; }

        public async Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken) =>
            Open.TryGetValue(path, out var open) ? open : File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;

        public Task<bool> ProposeWriteAsync(string path, string? oldText, string newText, string agent, CancellationToken cancellationToken)
        {
            lock (Proposed)
            {
                Proposed.Add((path, oldText, newText));
            }

            return Task.FromResult(Apply);
        }

        public Task<string?> RequestPermissionAsync(AcpPermissionPrompt prompt, CancellationToken cancellationToken)
        {
            lock (Permissions)
            {
                Permissions.Add(prompt);
            }

            return PermissionAsync is { } ask ? ask(prompt, cancellationToken) : Task.FromResult(Permission(prompt));
        }

        public Task<bool> ApproveCommandAsync(AcpCommandPrompt prompt, CancellationToken cancellationToken)
        {
            lock (Commands)
            {
                Commands.Add((prompt.CommandLine, prompt.Folder));
                CommandPrompts.Add(prompt);
            }

            return Task.FromResult(AllowCommands);
        }

        public Task<AcpAuthMethod?> ChooseSignInAsync(string agent, IReadOnlyList<AcpAuthMethod> methods, CancellationToken cancellationToken)
        {
            SignIns.Add(methods);
            return Task.FromResult(SignIn(methods));
        }

        public Task ReloadFromDiskAsync(string path)
        {
            lock (Reloaded)
            {
                Reloaded.Add(path);
            }

            return Task.CompletedTask;
        }

        public (AcpMcpServerStdio Server, IDisposable Lifetime)? StartMcpServer(string agent)
        {
            OnStart?.Invoke();
            McpStarted++;
            var token = "token-" + (++_tokens).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return (AcpAgents.CodalusMcpServer(Path.Combine(Path.GetTempPath(), "codalus"), null, null, token), new Stop(() => McpStopped++));
        }

        private sealed class Stop(Action stop) : IDisposable
        {
            public void Dispose() => stop();
        }
    }
}
