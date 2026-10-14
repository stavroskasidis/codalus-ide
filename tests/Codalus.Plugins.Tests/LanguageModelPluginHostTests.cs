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
    private readonly MockHttpServer _server = MockModelServers.OpenAI("A reply streamed from the plugin host.");
    private readonly InMemoryCredentialStore _store = new();
    private WorkbenchServices _workbench = null!;
    private PluginManager _manager = null!;
    private LanguageModelService _models = null!;
    private AiService _ai = null!;

    internal static string OpenAIPluginDir => Path.Combine(PluginHostIntegrationTests.RepoRoot, "src", "Plugins", "Codalus.Plugins.OpenAI", "bin", PluginHostIntegrationTests.Configuration, "net10.0");

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
}
