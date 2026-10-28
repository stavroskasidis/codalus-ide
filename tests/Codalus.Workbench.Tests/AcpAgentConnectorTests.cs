using System.Text.Json;
using Codalus.Platform;
using Codalus.Rpc;
using Codalus.Tests;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Acp;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Plugins;
using Codalus.Workbench.Secrets;
using Codalus.Workbench.Settings;

namespace Codalus.Workbench.Tests;

/// <summary>
/// Connecting an agent from a template (ADR 0083, D43), against stand-in node and npx on a test search path and the
/// stand-in ACP agent behind npx: requirements, installs that resume, the download step's progress and npm's errors in
/// plain words, the connection test (an empty folder, no MCP, every request refused, the sign-in checked as the template
/// says), the unsaved consent, what is written and when, Disconnect, and a remote window refusing it all. Nothing reaches
/// npm, a real agent or the OS's credential store.
/// </summary>
public sealed class AcpAgentConnectorTests : IAsyncDisposable
{
    private const string Key = "sk-stand-in-key-9876";

    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-acp-connect-" + Guid.NewGuid().ToString("N"));
    private readonly string _log;
    private readonly WorkbenchServices _workbench;
    private readonly AiService _ai;
    private readonly AcpAgents _agents;
    private readonly FakePersistentCredentialStore _store = new();
    private readonly AcpAgentConnector _connector;
    private readonly FakeConnectHost _host = new();

    public AcpAgentConnectorTests()
    {
        Directory.CreateDirectory(_root);
        var workspaceFile = Path.Combine(_root, "ws", ".codalus", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(workspaceFile)!);
        File.WriteAllText(workspaceFile, "{}");
        _log = Path.Combine(_root, "agent-log.jsonl");
        _workbench = new WorkbenchServices(AppPaths.Resolve(PlatformInfo.Current, _ => null, Path.Combine(_root, "user")), PlatformInfo.Current, workspaceFile, watchFiles: false);
        _workbench.Settings.Register(AiSettings.Descriptors(ContributionSource.Shell));
        var models = new LanguageModelService(new PluginManager(_workbench, _ => ProcessLaunchInfo.FromPath("none"), "test", new SupervisionPolicy()));
        _ai = new AiService(_workbench.Settings, models, _workbench.Output, () => Path.Combine(_root, "ws"), () => true);
        _agents = new AcpAgents(_workbench.Settings, _ai)
        {
            Secrets = new AcpAgentSecrets(new SecretStorage(_store, "Codalus (test)")),
            ProcessDirectory = Path.Combine(_root, "state", "external-agents"),
        };
        _connector = new AcpAgentConnector(_workbench.Settings, _ai, _agents) { Platform = PlatformKind.Linux, Distribution = "ubuntu debian", StartTimeout = TimeSpan.FromSeconds(30) };
        AcpAgentConnector.AuthStatusWait = TimeSpan.FromSeconds(3);
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Dotnet =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host
        : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")).First(File.Exists);

    /// <summary>Stand-in node and npx on the connector's search path, the agent behind npx in the modes given.</summary>
    private void StandIns(string? node = "v22.11.0", StandInDownload download = StandInDownload.Ok, string modes = "")
    {
        var env = new Dictionary<string, string> { ["ACP_FAKE_LOG"] = _log };
        foreach (var pair in modes.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            env[pair.Split('=', 2)[0]] = pair.Split('=', 2)[1];
        }

        var folder = Path.Combine(_root, "bin-" + Guid.NewGuid().ToString("N")[..6]);
        _connector.SearchPath = StandInNode.Create(folder, node, Dotnet, AcpAgentTests.StandInAgent, download, agentEnv: env);
    }

    /// <summary>The result's outcome, with its failed step and message when it is another.</summary>
    private static void Outcome(AcpTestOutcome expected, AcpTestResult result) =>
        Assert.True(result.Outcome == expected, $"{result.Outcome} (expected {expected}) at {result.FailedStep?.ToString() ?? "no step"}: {result.Message}");

    /// <summary>A template for the stand-in, run through npx like a real one.</summary>
    private static AcpAgentTemplate Template(AcpSignInCheck check, int? node = 22, params AcpSignInOption[] signIn) =>
        new(new AcpKnownAgent("stand-in", "Stand-in", "A stand-in agent", "1.0.0", "npx", ["-y", "stand-in@1.0.0"], "stand-in@1.0.0", "stand-in"), "Tests", "stand-in", AcpTemplateTier.OneClick, check, signIn.Length > 0 ? signIn : [Already])
        {
            NodeMajor = node,
        };

    private static readonly AcpSignInOption Already = new("already", AcpSignInKind.AlreadySignedIn, "I'm signed in already", "");

    private List<JsonElement> Received(string method) =>
        File.Exists(_log) ? [.. File.ReadAllLines(_log).Select(l => JsonElement.Parse(l)).Where(m => m.TryGetProperty("method", out var name) && name.GetString() == method)] : [];

    private string AiLog => _workbench.Output.GetOrCreate(AiService.OutputChannelId).GetText();

    private JsonElement? Agents => _workbench.Settings.GetLayerValue(AcpAgents.AgentsSetting, SettingScope.User);

    private JsonElement? Enabled => _workbench.Settings.GetLayerValue(AiService.EnabledSetting, SettingScope.User);

    private async Task<(AcpTestResult Result, List<AcpTestProgress> Steps)> TestAsync(AcpConnectRequest request)
    {
        var steps = new List<AcpTestProgress>();
        var result = await _connector.TestAsync(request, _host, new SyncProgress<AcpTestProgress>(steps.Add), Ct);
        return (result, steps);
    }

    [Fact]
    public async Task Requirements_are_found_on_the_path_too_old_or_missing_with_this_oss_install_options()
    {
        var template = Template(AcpSignInCheck.SessionNew);
        StandIns();
        var met = await _connector.CheckAsync(template, codalusNode: false, Ct);
        Assert.True(met.Met);
        Assert.Equal(["node", "npx"], met.Rows.Select(r => r.Id));
        Assert.Equal(("22.11.0", "22"), (met.Rows[0].Version, met.Rows[0].Needed));
        Assert.Equal(Path.Combine(_connector.SearchPath!, OperatingSystem.IsWindows() ? "node.cmd" : "node"), met.Rows[0].Path);
        Assert.Equal("10.9.0", met.Rows[1].Version);
        Assert.Empty(met.Install);

        // Ubuntu's Node.js 18 for an agent that needs 22: too old; no snap or apt here, so the page only.
        StandIns(node: "v18.19.1");
        var old = await _connector.CheckAsync(template, codalusNode: false, Ct);
        Assert.Equal(AcpRequirementState.TooOld, old.Rows[0].State);
        Assert.Equal("Install Node.js 22 or later", old.InstallTitle);
        Assert.Equal(["page"], old.Install.Select(o => o.Id));
        Assert.Equal("apt isn't offered: the distribution has no nodejs package.", old.InstallNote);

        // apt-cache says the candidate is 22 and snap is there: both offered, with their exact commands.
        StandInNode.Program(_connector.SearchPath!, "apt-cache", "nodejs:\n  Installed: (none)\n  Candidate: 22.12.0-1nodesource1");
        StandInNode.Program(_connector.SearchPath!, "snap", "");
        var offered = await _connector.CheckAsync(template, codalusNode: false, Ct);
        Assert.Equal(["snap", "apt", "page"], offered.Install.Select(o => o.Id));

        StandIns(node: null);
        var missing = await _connector.CheckAsync(template, codalusNode: false, Ct);
        Assert.Equal(AcpRequirementState.Missing, missing.Rows[0].State);
        Assert.Contains("Node.js is missing", AiLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_terminal_install_waits_for_its_end_and_the_check_then_finds_what_it_installed()
    {
        StandIns(node: null);
        var template = Template(AcpSignInCheck.SessionNew);
        Assert.False((await _connector.CheckAsync(template, false, Ct)).Met);

        // A stand-in "installer" that puts node on the search path; run in the (stand-in) terminal, which waits for it.
        var installer = Path.Combine(_root, "installer");
        Directory.CreateDirectory(installer);
        var node = Path.Combine(_connector.SearchPath!, OperatingSystem.IsWindows() ? "node.cmd" : "node");
        var script = Path.Combine(installer, OperatingSystem.IsWindows() ? "install.cmd" : "install");
        File.WriteAllText(script, OperatingSystem.IsWindows() ? $"@echo off\r\n(echo @echo off& echo echo v22.1.0) > \"{node}\"\r\n" : $"#!/bin/sh\nprintf '#!/bin/sh\\necho v22.1.0\\n' > '{node}'\nchmod +x '{node}'\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var option = new AcpInstallOption("snap", "Snap", "", Setup.InstallKind.Terminal, "sudo snap install node") { Program = script };
        Assert.Equal(0, await _connector.InstallAsync("stand-in", option, _host, null, Ct));
        Assert.Equal("Snap", Assert.Single(_host.Runs).Title);
        Assert.True((await _connector.CheckAsync(template, false, Ct)).Met);
        Assert.Contains("install snap: sudo snap install node", AiLog, StringComparison.Ordinal);

        // A page only opens: nothing runs here.
        Assert.Null(await _connector.InstallAsync("stand-in", new AcpInstallOption("page", "nodejs.org", "", Setup.InstallKind.OpenPage, AcpNodeInstall.Page), _host, null, Ct));
    }

    [Fact]
    public async Task The_test_downloads_starts_in_an_empty_folder_with_no_mcp_and_reports_what_it_found_then_stops_the_agent()
    {
        await _workbench.Settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, Ct);
        StandIns(modes: "ACP_FAKE_PROBE_CLIENT=1");
        var (result, steps) = await TestAsync(new AcpConnectRequest(Template(AcpSignInCheck.SessionNew), Already));

        Assert.Equal(AcpTestOutcome.Connected, result.Outcome);
        Assert.Equal("Stand-in is connected.", result.Message);
        Assert.Equal(("stand-in 1.0.0", 1), (result.Agent, result.Protocol));
        Assert.Equal(["Ask", "Code"], result.Modes);

        // npm's http fetch lines were the download's progress; each step ran in order.
        Assert.Contains(steps, s => s is { Step: AcpTestStep.Download, State: AcpStepState.Running } && s.Detail!.StartsWith("npm http fetch GET 200", StringComparison.Ordinal));
        Assert.Equal([AcpTestStep.Requirements, AcpTestStep.Download, AcpTestStep.Start, AcpTestStep.SignIn, AcpTestStep.Check, AcpTestStep.Done], steps.Select(s => s.Step).Distinct());

        // session/new in the connect-test folder (created first), no MCP servers; every request the agent sent refused.
        var created = Received("session/new").Single().GetProperty("params");
        Assert.Equal(_agents.ConnectTestDirectory, created.GetProperty("cwd").GetString());
        Assert.Empty(created.GetProperty("mcpServers").EnumerateArray());
        Assert.Equal(3, AiLog.Split('\n').Count(l => l.Contains("refused (connection test)", StringComparison.Ordinal)));
        Assert.Empty(Received("session/prompt")); // no prompt, ever
        Assert.Contains("test agent stopped", AiLog, StringComparison.Ordinal);

        // Nothing was written by the test.
        Assert.Null(Agents);
    }

    [Theory]
    [InlineData(StandInDownload.NotFound, "isn't in its registry")]
    [InlineData(StandInDownload.Offline, "couldn't reach its registry")]
    [InlineData(StandInDownload.BadEngine, "too old for the package")]
    public async Task A_failed_download_says_why_in_plain_words_with_npms_own_lines(StandInDownload download, string why)
    {
        await _workbench.Settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, Ct);
        StandIns(download: download);
        var (result, _) = await TestAsync(new AcpConnectRequest(Template(AcpSignInCheck.SessionNew), Already));
        Assert.Equal((AcpTestOutcome.Failed, AcpTestStep.Download), (result.Outcome, result.FailedStep));
        Assert.Contains(why, result.Message, StringComparison.Ordinal);
        Assert.NotEmpty(result.Output);
        Assert.Empty(Received("initialize"));
    }

    [Fact]
    public async Task Cancel_stops_the_download_and_nothing_is_written()
    {
        StandIns(download: StandInDownload.Hang);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var steps = new List<AcpTestProgress>();
        var test = _connector.TestAsync(new AcpConnectRequest(Template(AcpSignInCheck.SessionNew), Already) { Consent = true }, _host, new SyncProgress<AcpTestProgress>(p =>
        {
            lock (steps)
            {
                steps.Add(p);
            }

            if (p.Step == AcpTestStep.Download && p.Detail?.StartsWith("npm http", StringComparison.Ordinal) == true)
            {
                cancel.Cancel();
            }
        }), cancel.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test);
        Assert.Null(Agents);
        Assert.Null(Enabled);
    }

    [Fact]
    public async Task A_claude_like_agent_is_judged_by_who_it_says_is_signed_in()
    {
        await _workbench.Settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, Ct);
        var template = Template(AcpSignInCheck.AuthStatusPush);

        // Signed out: needs sign-in, even with "I'm signed in already" (session/new alone would have passed).
        StandIns(modes: "ACP_FAKE_AUTH_ON_PROMPT=1;ACP_FAKE_AUTH_STATUS=none");
        var (signedOut, _) = await TestAsync(new AcpConnectRequest(template, Already));
        Outcome(AcpTestOutcome.NeedsSignIn, signedOut);
        Assert.Equal("Stand-in says you aren't signed in (Not logged in). Nothing was saved.", signedOut.Message);

        StandIns(modes: "ACP_FAKE_AUTH_ON_PROMPT=1;ACP_FAKE_AUTH_STATUS=account");
        var (account, _) = await TestAsync(new AcpConnectRequest(template, Already));
        Outcome(AcpTestOutcome.Connected, account);
        Assert.Equal("Claude Max · tester@example.com", account.SignedInAs);
        Assert.True(account.CanLogout);
        Assert.DoesNotContain("tester@example.com", AiLog, StringComparison.Ordinal); // the kind only in the log

        // Silence is "not confirmed", never "connected".
        StandIns(modes: "ACP_FAKE_AUTH_ON_PROMPT=1;ACP_FAKE_AUTH_STATUS=silent");
        var (silent, _) = await TestAsync(new AcpConnectRequest(template, Already));
        Outcome(AcpTestOutcome.NotConfirmed, silent); // Failed once on Linux (WSL, 2026-10-28) without saying why: the message says now
        Assert.StartsWith("Stand-in started, but it didn't say whether you're signed in within 3 seconds", silent.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_terminal_sign_in_runs_in_the_terminal_and_the_agent_starts_again_to_read_it()
    {
        await _workbench.Settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, Ct);
        var marker = Path.Combine(_root, "signed-in");
        StandIns(modes: $"ACP_FAKE_AUTH_ON_PROMPT=1;ACP_FAKE_AUTH_STATUS=none;ACP_FAKE_TERMINAL_AUTH=1;ACP_FAKE_LOGIN_MARKER={marker}");
        var subscription = new AcpSignInOption("subscription", AcpSignInKind.TerminalMethod, "Subscription", "") { MethodId = "fake-terminal-login", UnsetEnv = ["ANTHROPIC_API_KEY"] };
        var (result, steps) = await TestAsync(new AcpConnectRequest(Template(AcpSignInCheck.AuthStatusPush, 22, subscription, Already), subscription));

        Assert.Equal(AcpTestOutcome.Connected, result.Outcome);
        var run = Assert.Single(_host.Runs);
        Assert.Equal("Stand-in sign-in", run.Title);
        Assert.EndsWith("-y stand-in@1.0.0 --login", run.CommandLine, StringComparison.Ordinal);
        Assert.Equal(["ANTHROPIC_API_KEY"], run.Launch.UnsetEnvironment);
        Assert.Equal(_agents.ConnectTestDirectory, run.Launch.WorkingDirectory);
        Assert.Equal(2, Received("initialize").Count); // started again after the sign-in
        Assert.Empty(Received("authenticate"));
        Assert.Contains(steps, s => s is { Step: AcpTestStep.SignIn, State: AcpStepState.Done });

        // A method the agent doesn't offer here fails in plain words.
        var other = subscription with { MethodId = "not-offered", RemoteMethodId = null };
        var (missing, _) = await TestAsync(new AcpConnectRequest(Template(AcpSignInCheck.AuthStatusPush, 22, other, Already), other));
        Assert.Equal("Stand-in doesn't offer that sign-in here. It offers: Fake login, Fake terminal login.", missing.Message);
    }

    [Fact]
    public async Task A_codex_like_agent_signs_in_with_its_method_and_a_key_goes_once_in_meta_and_is_never_stored()
    {
        await _workbench.Settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, Ct);
        StandIns(modes: "ACP_FAKE_AUTH=1");
        var method = new AcpSignInOption("chatgpt", AcpSignInKind.AgentMethod, "Fake", "") { MethodId = "fake-login" };
        var key = new AcpSignInOption("apikey", AcpSignInKind.ApiKeyOnce, "API key", "") { MethodId = "fake-login", KeyName = "Key" };
        var template = Template(AcpSignInCheck.SessionNew, null, method, key, Already);

        var (signedOut, _) = await TestAsync(new AcpConnectRequest(template, Already));
        Assert.Equal((AcpTestOutcome.NeedsSignIn, "Stand-in says you aren't signed in. Nothing was saved."), (signedOut.Outcome, signedOut.Message));

        Assert.Equal(AcpTestOutcome.Connected, (await TestAsync(new AcpConnectRequest(template, method))).Result.Outcome);
        var (withKey, _) = await TestAsync(new AcpConnectRequest(template, key) { ApiKey = Key });
        Assert.Equal(AcpTestOutcome.Connected, withKey.Outcome);
        Assert.Equal(Key, Received("authenticate").Last().GetProperty("params").GetProperty("_meta").GetProperty("api-key").GetProperty("apiKey").GetString());

        await _connector.SaveAsync(new AcpConnectRequest(template, key) { ApiKey = Key }, Ct);
        Assert.Empty(_store.Stored); // Codex keeps it in its own store
        Assert.DoesNotContain(Key, File.ReadAllText(_workbench.Settings.UserFile), StringComparison.Ordinal);
        Assert.DoesNotContain(Key, AiLog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_api_key_reaches_the_agent_as_its_variable_and_is_stored_only_when_the_entry_is_written()
    {
        StandIns(modes: $"ACP_FAKE_REQUIRE_ENV=STAND_IN_KEY={Key}");
        var key = new AcpSignInOption("apikey", AcpSignInKind.ApiKeyEnv, "API key", "") { EnvVar = "STAND_IN_KEY", KeyName = "Key" };
        var template = Template(AcpSignInCheck.SessionNew, 22, key, Already);

        // The dialog's box checked, AI still off in the settings: the test runs and writes nothing.
        var (result, _) = await TestAsync(new AcpConnectRequest(template, key) { ApiKey = Key, Consent = true });
        Assert.Equal(AcpTestOutcome.Connected, result.Outcome);
        Assert.Empty(_store.Stored);
        Assert.Null(Agents);
        Assert.Null(Enabled);

        // Writing: the key first, then the entry (names only), then ai.enabled, all in the user's settings.
        await _connector.SaveAsync(new AcpConnectRequest(template, key) { ApiKey = Key, Consent = true }, Ct);
        Assert.Equal(Key, _store.Stored["codalus.shell.agents/stand-in/STAND_IN_KEY"]);
        var entry = Agents!.Value.GetProperty("stand-in");
        Assert.Equal(["STAND_IN_KEY"], entry.GetProperty("secretEnv").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("stand-in@1.0.0", entry.GetProperty("template").GetString());
        Assert.Equal(["-y", "stand-in@1.0.0"], entry.GetProperty("args").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(JsonValueKind.True, Enabled!.Value.ValueKind);
        Assert.DoesNotContain(Key, File.ReadAllText(_workbench.Settings.UserFile), StringComparison.Ordinal);
        Assert.Contains("wrote ai.agents.stand-in", AiLog, StringComparison.Ordinal);
        Assert.Null(_workbench.Settings.GetLayerValue(AcpAgents.AgentsSetting, SettingScope.Workspace));
    }

    [Fact]
    public async Task Without_consent_nothing_runs_and_a_failed_test_writes_nothing()
    {
        StandIns(modes: "ACP_FAKE_AUTH=1");
        var template = Template(AcpSignInCheck.SessionNew);
        var refused = await Assert.ThrowsAsync<AcpException>(() => _connector.TestAsync(new AcpConnectRequest(template, Already), _host, null, Ct));
        Assert.Equal("Check Turn on AI assistance first: external agents run only while it's on.", refused.Message);
        Assert.Empty(Received("initialize"));

        var (failed, _) = await TestAsync(new AcpConnectRequest(template, Already) { Consent = true });
        Assert.Equal(AcpTestOutcome.NeedsSignIn, failed.Outcome);
        Assert.Null(Agents);
        Assert.Null(Enabled);
    }

    [Fact]
    public async Task Turning_ai_off_during_a_test_on_the_settings_consent_stops_it()
    {
        await _workbench.Settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, Ct);
        StandIns(modes: "ACP_FAKE_START_DELAY=4000");
        var starting = new TaskCompletionSource();
        var test = _connector.TestAsync(new AcpConnectRequest(Template(AcpSignInCheck.SessionNew), Already), _host, new SyncProgress<AcpTestProgress>(p =>
        {
            if (p is { Step: AcpTestStep.Start, State: AcpStepState.Running })
            {
                starting.TrySetResult();
            }
        }), Ct);
        await starting.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);

        // The agent waits 4 s before it answers: AI goes off meanwhile.
        await _workbench.Settings.SetAsync(AiService.EnabledSetting, false, SettingScope.User, Ct);
        var result = await test;
        Assert.Equal("AI assistance was turned off, so the test stopped. Nothing was saved.", result.Message);
        Assert.Null(Agents);
    }

    [Fact]
    public async Task A_remote_window_refuses_connect_check_and_install()
    {
        _agents.IsRemoteWindow = () => true;
        StandIns();
        var template = Template(AcpSignInCheck.SessionNew);
        var expected = "Agents can't be connected from a remote window: they would run on this machine, while the folder and its terminal are on the remote one. Connect it from a local window.";
        Assert.Equal(expected, _connector.WhyNot());
        Assert.Equal(expected, (await Assert.ThrowsAsync<AcpException>(() => _connector.CheckAsync(template, false, Ct))).Message);
        Assert.Equal(expected, (await Assert.ThrowsAsync<AcpException>(() => _connector.TestAsync(new AcpConnectRequest(template, Already) { Consent = true }, _host, null, Ct))).Message);
        await Assert.ThrowsAsync<AcpException>(() => _connector.SaveAsync(new AcpConnectRequest(template, Already), Ct));
        await Assert.ThrowsAsync<AcpException>(() => _connector.InstallAsync("stand-in", AcpNodeInstall.Options(new AcpNodeFacts(PlatformKind.Linux, "ubuntu", false, false, true, null, null, false), 22).Options[0], _host, null, Ct));
        Assert.Empty(_host.Runs);
    }

    [Fact]
    public async Task Disconnect_stops_the_chat_signs_out_only_when_asked_removes_the_entry_and_key_and_leaves_ai_on()
    {
        await _workbench.Settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, Ct);
        File.WriteAllText(_workbench.Settings.UserFile, "{\n  \"ai.enabled\": true,\n  \"ai.agents\": {\n    // mine\n    \"mine\": { \"command\": \"my-agent\" }\n  }\n}\n");
        _workbench.Settings.Reload();
        StandIns(modes: "ACP_FAKE_AUTH_STATUS=account");
        var key = new AcpSignInOption("apikey", AcpSignInKind.ApiKeyEnv, "API key", "") { EnvVar = "STAND_IN_KEY", KeyName = "Key" };
        var template = Template(AcpSignInCheck.AuthStatusPush, 22, key, Already);
        await _connector.SaveAsync(new AcpConnectRequest(template, key) { ApiKey = Key }, Ct);
        Assert.Equal(["mine", "stand-in"], Agents!.Value.EnumerateObject().Select(p => p.Name));
        Assert.Contains("// mine", File.ReadAllText(_workbench.Settings.UserFile), StringComparison.Ordinal);

        // The saved entry runs "npx" from the PATH; for Disconnect's sign-out the stand-in npx is put first.
        await _workbench.Settings.SetMemberAsync(AcpAgents.AgentsSetting, "stand-in", AcpAgentCatalog.Entry(Path.Combine(_connector.SearchPath!, OperatingSystem.IsWindows() ? "npx.cmd" : "npx"), ["-y", "stand-in@1.0.0"], ["STAND_IN_KEY"]), SettingScope.User, Ct);
        Assert.Null(await _connector.DisconnectAsync("stand-in", signOut: true, Ct));
        Assert.Single(Received("logout"));
        Assert.Equal(["mine"], Agents!.Value.EnumerateObject().Select(p => p.Name));
        Assert.Contains("// mine", File.ReadAllText(_workbench.Settings.UserFile), StringComparison.Ordinal);
        Assert.Empty(_store.Stored);
        Assert.Equal(JsonValueKind.True, Enabled!.Value.ValueKind); // AI stays on

        // Without "Also sign out", nothing starts.
        await _connector.SaveAsync(new AcpConnectRequest(template, Already), Ct);
        Assert.Null(await _connector.DisconnectAsync("stand-in", signOut: false, Ct));
        Assert.Single(Received("logout"));
        Assert.Equal(["mine"], Agents!.Value.EnumerateObject().Select(p => p.Name));
    }

    /// <summary>Progress that reports at once, on the caller's thread (Progress&lt;T&gt; posts, so a test could miss the last step).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>The terminal, standing in: each run is recorded, and runs to its end for real unless <see cref="Exit"/> says otherwise.</summary>
    private sealed class FakeConnectHost : IAcpConnectHost
    {
        public List<(string Title, string CommandLine, ProcessLaunchInfo Launch, string? Hint)> Runs { get; } = [];

        public Func<ProcessLaunchInfo, int?>? Exit { get; set; }

        public async Task<int?> RunInTerminalAsync(string title, string commandLine, ProcessLaunchInfo launch, string? hint, CancellationToken cancellationToken)
        {
            lock (Runs)
            {
                Runs.Add((title, commandLine, launch, hint));
            }

            return Exit is { } exit ? exit(launch) : await AcpAgentTests.RunAsync(launch, cancellationToken);
        }
    }
}
