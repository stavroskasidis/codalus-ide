using System.Text.Json;
using Codalus.Platform;
using Codalus.Platform.Credentials;
using Codalus.Tests;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Acp;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Plugins;
using Codalus.Workbench.Secrets;
using Codalus.Workbench.Settings;

namespace Codalus.Workbench.Tests;

/// <summary>
/// External agents' keys (<c>secretEnv</c>, ADR 0083): kept under the shell's own id in the credential store (a test store
/// here, never the OS's), given to the agent as variables, read once per session, and never in the settings file, the AI
/// Output channel, a message or a command's output. Plugins can't name the shell's id.
/// </summary>
public sealed class AcpAgentSecretsTests : IAsyncDisposable
{
    private const string Key = "s3cret-agent-key-1234";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-acp-secrets-" + Guid.NewGuid().ToString("N"));
    private readonly string _log;
    private readonly WorkbenchServices _workbench;
    private readonly AiService _ai;
    private readonly AcpAgents _agents;
    private readonly FakePersistentCredentialStore _store = new();
    private readonly AcpAgentSecrets _secrets;
    private readonly NullHost _host = new();
    private readonly AiChat _chat;

    public AcpAgentSecretsTests()
    {
        Directory.CreateDirectory(_root);
        var workspaceFile = Path.Combine(_root, ".codalus", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(workspaceFile)!);
        File.WriteAllText(workspaceFile, "{}");
        _log = Path.Combine(_root, ".agent-log.jsonl");
        _workbench = new WorkbenchServices(AppPaths.Resolve(PlatformInfo.Current, _ => null, Path.Combine(_root, ".user")), PlatformInfo.Current, workspaceFile, watchFiles: false);
        _workbench.Settings.Register(AiSettings.Descriptors(ContributionSource.Shell));
        var models = new LanguageModelService(new PluginManager(_workbench, _ => Codalus.Rpc.ProcessLaunchInfo.FromPath("none"), "test", new Codalus.Rpc.SupervisionPolicy()));
        _ai = new AiService(_workbench.Settings, models, _workbench.Output, () => _root, () => true);
        _secrets = new AcpAgentSecrets(new SecretStorage(_store, "Codalus (test)"));
        _agents = new AcpAgents(_workbench.Settings, _ai) { Secrets = _secrets, ProcessDirectory = Path.Combine(_root, ".ide-agents") };
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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Dotnet =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host
        : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")).First(File.Exists);

    private async Task ConfigureAsync(object entry)
    {
        await _workbench.Settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, Ct);
        await _workbench.Settings.SetAsync(AcpAgents.AgentsSetting, JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["stand-in"] = entry }), SettingScope.User, Ct);
    }

    private object Entry(string[]? secretEnv = null, Dictionary<string, string>? env = null, string[]? unsetEnv = null) => new
    {
        command = Dotnet,
        args = new[] { AcpAgentTests.StandInAgent },
        env = env ?? new Dictionary<string, string> { ["ACP_FAKE_LOG"] = _log, ["ACP_FAKE_REQUIRE_ENV"] = "MY_AGENT_KEY=sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Key))), ["ACP_FAKE_REPORT_ENV"] = "ACP_FAKE_UNSET_ME" },
        secretEnv = secretEnv ?? ["MY_AGENT_KEY"],
        unsetEnv = unsetEnv ?? [],
    };

    private Task<AiChatTurn> AskAsync(string question) => _chat.SendExternalAsync(question, [], _agents, "stand-in", _host, Ct);

    private string AiLog => _workbench.Output.GetOrCreate(AiService.OutputChannelId).GetText();

    [Fact]
    public async Task A_key_is_kept_under_the_shells_id_and_reaches_the_agent_as_its_variable()
    {
        await _secrets.StoreAsync("stand-in", "MY_AGENT_KEY", Key, Ct);
        Assert.Equal(Key, _store.Stored["codalus.shell.agents/stand-in/MY_AGENT_KEY"]);
        Assert.Equal(Key, await _secrets.GetAsync("stand-in", "MY_AGENT_KEY", Ct));
        await ConfigureAsync(Entry());

        // The stand-in refuses session/new unless the variable has the key: it got it.
        var turn = await AskAsync("hello");
        Assert.Null(turn.Error);

        // Names only in the settings, never the value: not in the file, the log or the answer.
        var settingsFile = File.ReadAllText(_workbench.Settings.UserFile);
        Assert.Contains("\"secretEnv\"", settingsFile, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, settingsFile, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, AiLog, StringComparison.Ordinal);
        Assert.False(_agents.Current!.Config.ToString().Contains(Key, StringComparison.Ordinal));

        Assert.True(await _secrets.DeleteAsync("stand-in", "MY_AGENT_KEY", Ct));
        Assert.Empty(_store.Stored);
    }

    [Fact]
    public async Task Keys_are_read_once_per_session_and_a_changed_key_starts_the_agent_again()
    {
        await _secrets.StoreAsync("stand-in", "MY_AGENT_KEY", Key, Ct);
        await ConfigureAsync(Entry());
        var reads = _store.Reads;
        Assert.Null((await AskAsync("hello")).Error);
        var session = _agents.Current;
        Assert.Null((await AskAsync("hello again")).Error);
        Assert.Null((await AskAsync("and again")).Error);
        Assert.Same(session, _agents.Current);
        Assert.Equal(reads + 1, _store.Reads); // once, not on every turn: the OS store can prompt or be slow

        // Another key: the cache is refreshed, and the session (started with the old one) starts again.
        await _secrets.StoreAsync("stand-in", "MY_AGENT_KEY", "another-key-5678", Ct);
        var turn = await AskAsync("hello");
        Assert.NotSame(session, _agents.Current);
        Assert.Contains("needs you to sign in", turn.Error, StringComparison.Ordinal); // the stand-in wants the first key
    }

    [Fact]
    public async Task A_missing_key_or_a_bad_name_says_what_to_do_and_nothing_starts()
    {
        await ConfigureAsync(Entry());
        var missing = await AskAsync("hello");
        Assert.Equal($"stand-in's MY_AGENT_KEY isn't in {_store.Name}: connect it again with its key (AI: Connect to an Agent…), or remove it from secretEnv in its entry.", missing.Error);
        Assert.Null(_agents.Current);

        foreach (var (names, why) in new[] { (new[] { "BAD NAME" }, "isn't a variable name"), (["PATH"], "can't set PATH"), (["ACP_FAKE_LOG"], "is in both env and secretEnv") })
        {
            await ConfigureAsync(Entry(names));
            Assert.Empty(_agents.Configured);
            Assert.Contains(why, Assert.Single(_agents.Problems), StringComparison.Ordinal);
            Assert.Contains(why, (await AskAsync("hello")).Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Unset_variables_leave_the_agents_environment()
    {
        Environment.SetEnvironmentVariable("ACP_FAKE_UNSET_ME", "from-the-ide");
        try
        {
            await _secrets.StoreAsync("stand-in", "MY_AGENT_KEY", Key, Ct);
            await ConfigureAsync(Entry(unsetEnv: ["ACP_FAKE_UNSET_ME"]));
            Assert.Null((await AskAsync("hello")).Error);
            var start = File.ReadAllLines(_log).Select(l => JsonElement.Parse(l)).First(m => m.TryGetProperty("env", out _)).GetProperty("env");
            Assert.Equal(JsonValueKind.Null, start.GetProperty("ACP_FAKE_UNSET_ME").ValueKind);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ACP_FAKE_UNSET_ME", null);
        }
    }

    [Fact]
    public void Exact_values_are_redacted_in_output_and_messages()
    {
        Assert.Equal("token: [redacted] done", SecretRedactor.Redact("token: " + Key + " done", [Key]).Text);
        var shown = AcpAgentSession.ShownOutput(new string('x', 20_000) + "\n" + Key + "\n", [Key]);
        Assert.DoesNotContain(Key, shown, StringComparison.Ordinal);
        Assert.Contains("[redacted]", shown, StringComparison.Ordinal);
        Assert.Equal("abc", SecretRedactor.Redact("abc", ["ab"]).Text); // too short to redact without breaking words
    }

    [Fact]
    public async Task The_store_probe_reports_a_store_that_falls_back_to_memory()
    {
        Assert.True(await _secrets.ProbeAsync(Ct));
        Assert.True(_secrets.IsPersistent);

        var failing = new FakePersistentCredentialStore { Fail = true };
        var secrets = new AcpAgentSecrets(new SecretStorage(failing, "Codalus (test)"));
        Assert.True(secrets.IsPersistent); // until a call fails
        Assert.False(await secrets.ProbeAsync(Ct));
        Assert.Equal("memory (this session only)", secrets.StoreName);

        var memory = new AcpAgentSecrets(new SecretStorage(new InMemoryCredentialStore(), "Codalus (test)"));
        Assert.False(await memory.ProbeAsync(Ct));
    }

    [Fact]
    public void A_plugin_host_may_use_only_its_own_plugins_ids_for_secrets_never_the_shells()
    {
        string[] mine = ["codalus.github", "acme.tools"];
        Assert.Null(PluginManager.SecretOwnerRefusal("codalus.github", mine));
        Assert.Null(PluginManager.SecretOwnerRefusal("acme.tools", mine));
        Assert.Contains("isn't a plugin of this host", PluginManager.SecretOwnerRefusal("codalus.openai", mine), StringComparison.Ordinal);
        Assert.Contains("the shell's own", PluginManager.SecretOwnerRefusal(AcpAgentSecrets.Id, [.. mine, AcpAgentSecrets.Id]), StringComparison.Ordinal);
        Assert.Contains("the shell's own", PluginManager.SecretOwnerRefusal("Codalus.Shell.Other", mine), StringComparison.Ordinal);
        Assert.NotNull(PluginManager.SecretOwnerRefusal("", mine));
    }

    /// <summary>A host that never gets asked anything (the turns here only say hello).</summary>
    private sealed class NullHost : IAcpHost
    {
        public Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<bool> ProposeWriteAsync(string path, string? oldText, string newText, string agent, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<string?> RequestPermissionAsync(AcpPermissionPrompt prompt, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<bool> ApproveCommandAsync(AcpCommandPrompt prompt, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<AcpAuthMethod?> ChooseSignInAsync(string agent, IReadOnlyList<AcpAuthMethod> methods, CancellationToken cancellationToken) => Task.FromResult<AcpAuthMethod?>(null);

        public Task ReloadFromDiskAsync(string path) => Task.CompletedTask;

        public Task<int?> RunTerminalSignInAsync(AcpTerminalSignIn signIn, CancellationToken cancellationToken) => Task.FromResult<int?>(null);

        public (AcpMcpServerStdio Server, IDisposable Lifetime)? StartMcpServer(string agent) => null;
    }
}
