using Codalus.Platform;
using Codalus.Platform.Credentials;
using Codalus.Plugins.Protocol;
using Codalus.Rpc;
using Codalus.Tests;
using Codalus.Workbench;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Plugins;
using Codalus.Workbench.Secrets;
using Codalus.Workbench.Settings;

namespace Codalus.Plugins.Tests;

/// <summary>
/// The OpenAI-compatible plugin in a real plugin host (ADR 0062), driven through the workbench's
/// <see cref="LanguageModelService"/> and <see cref="AiService"/> as the shell wires them, against a mock server.
/// </summary>
public sealed class LanguageModelPluginHostTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(90);
    private readonly string _userData = Path.Combine(Path.GetTempPath(), "codalus-ai-" + Guid.NewGuid().ToString("N"));
    private readonly MockHttpServer _server = MockModelServers.OpenAI(Reply);
    private readonly InMemoryCredentialStore _store = new();
    private WorkbenchServices _workbench = null!;
    private PluginManager _manager = null!;
    private LanguageModelService _models = null!;
    private AiService _ai = null!;

    /// <summary>The mock's reply, by what the request asks (the system prompt of each feature).</summary>
    private static string Reply(MockHttpRequest request) =>
        request.Body.Contains("code completion engine", StringComparison.Ordinal) ? "```csharp\n    return a + b;\n```"
        : request.Body.Contains("git commit messages", StringComparison.Ordinal) ? "Add the greeter\n\nIt says hello."
        : request.Body.Contains("pull request descriptions", StringComparison.Ordinal) ? "Greet everyone\n\n- Adds the greeter."
        : request.Body.Contains("fix a compiler", StringComparison.Ordinal) ? "```csharp\nint x = 1;\nint y = x;\n```"
        : "A reply streamed from the plugin host.";

    internal static string OpenAIPluginDir => Path.Combine(PluginHostIntegrationTests.RepoRoot, "src", "Plugins", "Codalus.Plugins.OpenAI", "bin", PluginHostIntegrationTests.Configuration, Codalus.Tests.RepoBuild.TargetFramework);

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    private PluginDescription Plugin => _manager.Plugins.Single(p => p.Id == "codalus.openai");

    public async ValueTask InitializeAsync()
    {
        var paths = AppPaths.Resolve(PlatformInfo.Current, _ => null, _userData);
        paths.EnsureCreated();
        _workbench = new WorkbenchServices(paths, PlatformInfo.Current, watchFiles: false);
        _manager = new PluginManager(_workbench, _ => ProcessLaunchInfo.FromPath(PluginHostIntegrationTests.HostAssembly), "test",
            new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50), MaxRestarts = 2 })
        {
            Secrets = new SecretStorage(_store),
        };
        _workbench.Settings.Register(AiSettings.Descriptors(Codalus.Workbench.Commands.ContributionSource.Shell));
        _models = new LanguageModelService(_manager);
        _ai = new AiService(_workbench.Settings, _models, _workbench.Output, () => null, () => true);
        _manager.AddPlugins(PluginDiscovery.Scan([(OpenAIPluginDir, true)], PlatformInfo.Current));
        await _workbench.Settings.SetAsync("ai.openai.endpoint", _server.BaseUrl + "/v1", SettingScope.User, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        _workbench.Dispose();
        await _server.DisposeAsync();
        try
        {
            Directory.Delete(_userData, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Nothing_loads_or_is_sent_until_AI_is_on_and_a_provider_is_chosen()
    {
        Assert.Equal(["ollama", "openai"], _models.Providers.Select(p => p.Id).Order());
        Assert.True(_models.Provider("ollama")!.IsLocal);
        Assert.Contains("onLanguageModelProvider:openai", Plugin.ActivationEvents);
        Assert.Equal(AiState.Off, _ai.State);

        var messages = new[] { new ChatMessageDto("user", "Hi", null, null, false) };
        var off = await Assert.ThrowsAsync<AiUnavailableException>(() => _ai.ChatAsync("test", messages, AiPreparedContext.None, _ => { }, Timeout()));
        Assert.Contains("turned off", off.Message, StringComparison.Ordinal);

        await _workbench.Settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, Timeout());
        Assert.Equal(AiState.NoProvider, _ai.State);
        var none = await Assert.ThrowsAsync<AiUnavailableException>(() => _ai.ChatAsync("test", messages, AiPreparedContext.None, _ => { }, Timeout()));
        Assert.True(none.NeedsConfiguration);

        Assert.NotEqual(PluginState.Active, Plugin.State);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task A_chat_streams_through_the_host_and_is_logged_without_its_content()
    {
        await _workbench.Settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, Timeout());
        await _workbench.Settings.SetAsync(AiService.ProviderSetting, "openai", SettingScope.User, Timeout());
        await _store.SetAsync(SecretStorage.Service, "codalus.openai/apiKey:openai", MockModelServers.Key, Timeout());

        var models = await _models.GetModelsAsync("openai", Timeout());
        Assert.Equal(["gpt-mini", "gpt-test"], models.Select(m => m.Id));
        Assert.Equal(PluginState.Active, Plugin.State);

        var chat = new AiChat(_ai);
        var streamed = new List<string>();
        chat.TurnChanged += (_, turn) =>
        {
            lock (streamed)
            {
                streamed.Add(turn.Answer);
            }
        };
        var context = new AiContextItem(AiContextKind.Selection, "Program.cs", "var password = \"hunter2\";") { StartLine = 3, Language = "csharp" };
        var answered = await chat.SendAsync("What does this do?", [context], Timeout());

        Assert.Null(answered.Error);
        Assert.Equal("A reply streamed from the plugin host.", answered.Answer);
        Assert.Equal("stop", answered.FinishReason);
        Assert.Contains(streamed, s => s.Length > 0 && s.Length < answered.Answer.Length);

        // The model was the provider's first (ai.model is empty), and the secret in the selection was redacted.
        var sent = _server.Requests.Last(r => r.Path == "/v1/chat/completions").Body;
        Assert.Contains("\"model\":\"gpt-mini\"", sent, StringComparison.Ordinal);
        Assert.Contains("[redacted]", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", sent, StringComparison.Ordinal);

        var log = _workbench.Output.GetOrCreate(AiService.OutputChannelId).GetText();
        Assert.Contains("chat: openai/gpt-mini", log, StringComparison.Ordinal);
        Assert.Contains("sent: Selection in Program.cs (from line 3)", log, StringComparison.Ordinal);
        Assert.Contains("redacted: 1 secret(s)", log, StringComparison.Ordinal);
        Assert.Contains("42 tokens in, 7 out", log, StringComparison.Ordinal);
        Assert.DoesNotContain("var password", log, StringComparison.Ordinal);

        // A second question repeats the first exchange before it.
        await chat.SendAsync("And then?", [], Timeout());
        var second = _server.Requests.Last(r => r.Path == "/v1/chat/completions").Body;
        Assert.Contains("What does this do?", second, StringComparison.Ordinal);
        Assert.Contains("A reply streamed from the plugin host.", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_key_asks_for_configuration()
    {
        await _workbench.Settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, Timeout());
        await _workbench.Settings.SetAsync(AiService.ProviderSetting, "openai", SettingScope.User, Timeout());
        await _workbench.Settings.SetAsync(AiService.ModelSetting, "gpt-test", SettingScope.User, Timeout());

        var turn = await new AiChat(_ai).SendAsync("Hi", [], Timeout());

        Assert.True(turn.NeedsConfiguration);
        Assert.Contains("Incorrect API key", turn.Error, StringComparison.Ordinal);
        Assert.Contains("failed:", _workbench.Output.GetOrCreate(AiService.OutputChannelId).GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completions_commit_messages_descriptions_and_fixes_go_through_the_boundary()
    {
        await _workbench.Settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, Timeout());
        await _workbench.Settings.SetAsync(AiService.ProviderSetting, "openai", SettingScope.User, Timeout());
        await _workbench.Settings.SetAsync(AiService.ModelSetting, "gpt-test", SettingScope.User, Timeout());
        await _store.SetAsync(SecretStorage.Service, "codalus.openai/apiKey:openai", MockModelServers.Key, Timeout());
        var completions = new AiCompletions(_ai, _workbench.Settings);
        var folder = Path.Combine(_userData, "repo");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Calc.cs");

        // Completions have their own switch.
        var request = new AiCompletionRequest(file, "csharp", "int Add(int a, int b)\n{\n    ", "\n}");
        Assert.Null(await completions.CompleteAsync(request, Timeout()));
        await _workbench.Settings.SetAsync(AiCompletions.EnabledSetting, true, SettingScope.User, Timeout());
        Assert.Equal("return a + b;", await completions.CompleteAsync(request, Timeout()));
        var count = _server.Requests.Count;
        Assert.Null(await completions.CompleteAsync(request with { Path = Path.Combine(folder, ".env") }, Timeout()));
        Assert.Equal(count, _server.Requests.Count);

        // A commit message without the excluded file's diff.
        var diff = "diff --git a/Calc.cs b/Calc.cs\n+class Greeter {}\ndiff --git a/secrets.json b/secrets.json\n+{\"key\": \"value\"}\n";
        var message = await AiActions.CommitMessageAsync(_ai, diff, folder, ["Fix the build"], Timeout());
        Assert.Equal("Add the greeter\n\nIt says hello.", message);
        using (var body = System.Text.Json.JsonDocument.Parse(_server.Requests.Last().Body))
        {
            var sent = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            Assert.Contains("class Greeter", sent, StringComparison.Ordinal);
            Assert.DoesNotContain("\"key\"", sent, StringComparison.Ordinal);
            Assert.Contains("Also changed (not shown): secrets.json", sent, StringComparison.Ordinal);
            Assert.Contains("- Fix the build", sent, StringComparison.Ordinal);
        }

        var (title, description) = await AiActions.PullRequestAsync(_ai, ["Add the greeter"], diff, folder, Timeout());
        Assert.Equal(("Greet everyone", "- Adds the greeter."), (title, description));

        var fix = await AiActions.FixAsync(_ai, file, "csharp", "int x = 1\nint y = x;\n", 1, "CS1002", "; expected", Timeout());
        Assert.Equal(new AiFix(1, 3, "int x = 1;\nint y = x;"), fix);
        Assert.Contains("fix: openai/gpt-test", _workbench.Output.GetOrCreate(AiService.OutputChannelId).GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_mode_calls_tools_until_the_model_answers_and_logs_each_call()
    {
        await using var server = MockModelServers.Script(request =>
            request.Body.Contains("\"role\":\"tool\"", StringComparison.Ordinal)
                ? new MockTurn("Program.cs prints a greeting.")
                : new MockTurn("Let me look.", "read_file", """{"path":"Program.cs"}"""));
        await _workbench.Settings.SetAsync("ai.openai.endpoint", server.BaseUrl + "/v1", SettingScope.User, Timeout());
        await _workbench.Settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, Timeout());
        await _workbench.Settings.SetAsync(AiService.ProviderSetting, "openai", SettingScope.User, Timeout());
        await _workbench.Settings.SetAsync(AiService.ModelSetting, "gpt-test", SettingScope.User, Timeout());
        await _store.SetAsync(SecretStorage.Service, "codalus.openai/apiKey:openai", MockModelServers.Key, Timeout());
        var folder = Path.Combine(_userData, "agent");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "Program.cs"), "Console.WriteLine(\"Hello\");\n", Timeout());

        var chat = new AiChat(_ai);
        var tools = new Codalus.Workbench.Ai.Agent.AgentTools(_ai, new FileHost(), folder);
        var turn = await chat.SendAgentAsync("What does Program.cs do?", [], tools, maxSteps: 5, Timeout());

        Assert.Null(turn.Error);
        Assert.True(turn.IsAgent);
        Assert.Equal("Let me look.\n\nProgram.cs prints a greeting.", turn.Answer);
        var step = Assert.Single(turn.Steps);
        Assert.Equal(("read_file", "Program.cs", "Program.cs"), (step.Tool, step.Target, step.Outcome));

        // The second request carries the tool call and its result; the tools were offered.
        using (var body = System.Text.Json.JsonDocument.Parse(server.Requests.Last(r => r.Path == "/v1/chat/completions").Body))
        {
            var messages = body.RootElement.GetProperty("messages");
            var tool = messages.EnumerateArray().Last();
            Assert.Equal(("tool", "call_1"), (tool.GetProperty("role").GetString(), tool.GetProperty("tool_call_id").GetString()));
            Assert.Contains("Console.WriteLine", tool.GetProperty("content").GetString(), StringComparison.Ordinal);
            Assert.Contains(body.RootElement.GetProperty("tools").EnumerateArray(), t => t.GetProperty("function").GetProperty("name").GetString() == "edit_file");
        }

        var log = _workbench.Output.GetOrCreate(AiService.OutputChannelId).GetText();
        Assert.Contains("tool: read_file Program.cs → Program.cs", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.WriteLine", log, StringComparison.Ordinal);

        // The step limit: a model that keeps calling tools gets one answer without them.
        await using var looping = MockModelServers.Script(request =>
            request.Body.Contains("\"tools\"", StringComparison.Ordinal) ? new MockTurn("", "read_file", """{"path":"Program.cs"}""") : new MockTurn("Stopped at the limit."));
        await _workbench.Settings.SetAsync("ai.openai.endpoint", looping.BaseUrl + "/v1", SettingScope.User, Timeout());
        var limited = await chat.SendAgentAsync("Loop", [], tools, maxSteps: 2, Timeout());
        Assert.Equal(2, limited.Steps.Count);
        Assert.EndsWith("Stopped at the limit.", limited.Answer, StringComparison.Ordinal);
    }

    /// <summary>An agent host that reads from disk and allows nothing else.</summary>
    private sealed class FileHost : Codalus.Workbench.Ai.Agent.IAgentHost
    {
        public async Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken) => File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;

        public Task<bool> ProposeChangeAsync(string path, string? oldText, string newText, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> ApproveAsync(string title, string message, IReadOnlyList<string> details, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<string> DiagnosticsAsync(string? path, CancellationToken cancellationToken) => Task.FromResult("");

        public Task<string> BuildOutputAsync(CancellationToken cancellationToken) => Task.FromResult("");

        public Task<string> BuildAsync(CancellationToken cancellationToken) => Task.FromResult("");
    }
}
