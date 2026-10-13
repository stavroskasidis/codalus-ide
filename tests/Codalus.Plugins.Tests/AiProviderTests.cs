using System.Text.Json;
using Codalus.Platform;
using Codalus.Platform.Credentials;
using Codalus.Plugins.Protocol;
using Codalus.Rpc;
using Codalus.Tests;
using Codalus.Workbench;
using Codalus.Workbench.Plugins;
using Codalus.Workbench.Secrets;
using Codalus.Workbench.Settings;

namespace Codalus.Plugins.Tests;

/// <summary>
/// The built-in AI providers (M28a, ADR 0062) in a real plugin host, against mock OpenAI-compatible and Anthropic servers
/// that stream their answers as server-sent events. No test reaches a real provider.
/// </summary>
public sealed class AiProviderTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("codalus-ai-").FullName;
    private readonly InMemoryCredentialStore _store = new();
    private MockHttpServer _server = null!;
    private WorkbenchServices _workbench = null!;
    private PluginManager _manager = null!;
    private readonly List<(string Path, string Body, string? Key, string? Authorization)> _requests = [];

    private static string AiPluginDir => Path.Combine(PluginHostIntegrationTests.RepoRoot, "src", "Plugins", "Codalus.Plugins.Ai", "bin", PluginHostIntegrationTests.Configuration, "net10.0");

    private static CancellationToken Timeout()
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(60));
        return source.Token;
    }

    private static readonly string[] s_openAiEvents =
    [
        """data: {"choices":[{"delta":{"role":"assistant"}}]}""",
        """data: {"choices":[{"delta":{"content":"Hello"}}]}""",
        """data: {"choices":[{"delta":{"content":", world"}}]}""",
        "data: [DONE]",
    ];

    private static readonly string[] s_anthropicEvents =
    [
        "event: message_start\ndata: {\"type\":\"message_start\"}",
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Bonjour\"}}",
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\" le monde\"}}",
        "event: message_stop\ndata: {\"type\":\"message_stop\"}",
    ];

    // Answers with a tool call (M28d), its arguments in pieces as the APIs stream them.
    private static readonly string[] s_openAiToolEvents =
    [
        """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_9","type":"function","function":{"name":"read_file","arguments":""}}]}}]}""",
        """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\":"}}]}}]}""",
        """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"a.cs\"}"}}]}}]}""",
        """data: {"choices":[{"finish_reason":"tool_calls","delta":{}}]}""",
        "data: [DONE]",
    ];

    private static readonly string[] s_anthropicToolEvents =
    [
        "data: {\"type\":\"message_start\"}",
        "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
        "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Reading.\"}}",
        "data: {\"type\":\"content_block_stop\",\"index\":0}",
        "data: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"read_file\",\"input\":{}}}",
        "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"path\\\":\"}}",
        "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\\\"a.cs\\\"}\"}}",
        "data: {\"type\":\"content_block_stop\",\"index\":1}",
        "data: {\"type\":\"message_stop\"}",
    ];

    private MockHttpResponse Handle(MockHttpRequest request)
    {
        lock (_requests)
        {
            _requests.Add((request.Path, request.Body, request.Header("x-api-key"), request.Header("Authorization")));
        }

        var tools = request.Body.Contains("\"tools\"", StringComparison.Ordinal);
        return request.Path switch
        {
            "/v1/chat/completions" when tools => MockHttpResponse.Text(string.Join("\n\n", s_openAiToolEvents) + "\n\n", contentType: "text/event-stream"),
            "/anthropic/v1/messages" when tools => MockHttpResponse.Text(string.Join("\n\n", s_anthropicToolEvents) + "\n\n", contentType: "text/event-stream"),
            "/v1/models" => MockHttpResponse.Json("""{ "data": [ { "id": "local-coder" }, { "id": "local-chat" } ] }"""),
            "/v1/chat/completions" => MockHttpResponse.Text(string.Join("\n\n", s_openAiEvents) + "\n\n", contentType: "text/event-stream"),
            "/anthropic/v1/models" => MockHttpResponse.Json("""{ "data": [ { "id": "claude-opus-5-5", "display_name": "Claude Opus 5.5" } ] }"""),
            "/anthropic/v1/messages" => MockHttpResponse.Text(string.Join("\n\n", s_anthropicEvents) + "\n\n", contentType: "text/event-stream"),
            _ => MockHttpResponse.NotFound(),
        };
    }

    public async ValueTask InitializeAsync()
    {
        _server = new MockHttpServer(Handle);
        var paths = AppPaths.Resolve(PlatformInfo.Current, _ => null, Path.Combine(_root, "user-data"));
        paths.EnsureCreated();
        _workbench = new WorkbenchServices(paths, PlatformInfo.Current, watchFiles: false);
        _manager = new PluginManager(_workbench, _ => ProcessLaunchInfo.FromPath(PluginHostIntegrationTests.HostAssembly), "test")
        {
            Secrets = new SecretStorage(_store),
            Prompts = new KeyPrompt("sk-test-key"),
        };
        _manager.AddPlugins(PluginDiscovery.Scan([(AiPluginDir, true)], PlatformInfo.Current));
        var root = _server.BaseUrl.TrimEnd('/');
        await _workbench.Settings.SetAsync("ai.openai.baseUrl", root + "/v1", SettingScope.User, TestContext.Current.CancellationToken);
        await _workbench.Settings.SetAsync("ai.anthropic.baseUrl", root + "/anthropic", SettingScope.User, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void The_providers_are_known_before_their_plugin_runs()
    {
        Assert.Equal(["Anthropic", "OpenAI-compatible"], _manager.AiProviders.Select(p => p.Label));
        Assert.All(_manager.Plugins, p => Assert.NotEqual(PluginState.Active, p.State));
    }

    [Fact]
    public async Task An_openai_compatible_endpoint_lists_models_and_streams_an_answer_without_a_key()
    {
        var models = await _manager.GetAiModelsAsync("openai", Timeout());
        Assert.Equal(["local-chat", "local-coder"], models.Select(m => m.Id));

        var pieces = new List<string>();
        await _manager.ChatAsync("openai", "local-coder", [new AiMessageDto("system", "Be brief."), new AiMessageDto("user", "Hi")], pieces.Add, Timeout());
        Assert.Equal(["Hello", ", world"], pieces);

        var chat = _requests.Single(r => r.Path == "/v1/chat/completions");
        Assert.Null(chat.Authorization); // a local server needs no key
        using var body = JsonDocument.Parse(chat.Body);
        Assert.Equal("local-coder", body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(2, body.RootElement.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task Anthropic_needs_a_key_kept_in_the_credential_store_and_streams_an_answer()
    {
        Assert.Empty(await _manager.GetAiModelsAsync("anthropic", Timeout())); // no key yet: nothing is asked of the API
        Assert.DoesNotContain(_requests, r => r.Path.StartsWith("/anthropic", StringComparison.Ordinal));

        Assert.True(await _manager.ConfigureAiAsync("anthropic", Timeout()));
        var model = Assert.Single(await _manager.GetAiModelsAsync("anthropic", Timeout()));
        Assert.Equal(("claude-opus-5-5", "Claude Opus 5.5"), (model.Id, model.Label));

        var pieces = new List<string>();
        await _manager.ChatAsync("anthropic", "claude-opus-5-5", [new AiMessageDto("system", "Be brief."), new AiMessageDto("user", "Salut")], pieces.Add, Timeout());
        Assert.Equal("Bonjour le monde", string.Concat(pieces));

        var chat = _requests.Single(r => r.Path == "/anthropic/v1/messages");
        Assert.Equal("sk-test-key", chat.Key);
        using var body = JsonDocument.Parse(chat.Body);
        Assert.Equal("Be brief.", body.RootElement.GetProperty("system").GetString()); // the system prompt on its own
        Assert.Equal(1, body.RootElement.GetProperty("messages").GetArrayLength());
        Assert.True(body.RootElement.GetProperty("max_tokens").GetInt32() > 0);
    }

    /// <summary>A conversation in agent mode: a tool was called, and its result follows.</summary>
    private static readonly AiMessageDto[] s_agentMessages =
    [
        new("system", "Use tools."),
        new("user", "What's in a.cs?"),
        new("assistant", "Let me look.") { ToolCalls = [new AiToolCallDto("call_1", "list_files", "{}")] },
        new("tool", "a.cs") { ToolCallId = "call_1" },
    ];

    private static readonly AiToolDto[] s_tools = [new("read_file", "Reads a file.", """{"type":"object","properties":{"path":{"type":"string"}}}""")];

    [Fact]
    public async Task An_openai_compatible_endpoint_gets_tools_and_streams_tool_calls()
    {
        var events = new List<AiEventParams>();
        await _manager.ChatEventsAsync("openai", "local-coder", s_agentMessages, s_tools, events.Add, Timeout());

        var call = Assert.Single(events).ToolCall!;
        Assert.Equal(("call_9", "read_file", """{"path":"a.cs"}"""), (call.Id, call.Name, call.Arguments));
        using var body = JsonDocument.Parse(_requests.Single(r => r.Path == "/v1/chat/completions").Body);
        var tool = body.RootElement.GetProperty("tools")[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("read_file", tool.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("object", tool.GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal("call_1", messages[2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("list_files", messages[2].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("call_1", messages[3].GetProperty("tool_call_id").GetString());
    }

    [Fact]
    public async Task Anthropic_gets_tools_and_streams_text_and_tool_use()
    {
        Assert.True(await _manager.ConfigureAiAsync("anthropic", Timeout()));
        var events = new List<AiEventParams>();
        await _manager.ChatEventsAsync("anthropic", "claude-opus-5-5", s_agentMessages, s_tools, events.Add, Timeout());

        Assert.Equal("Reading.", events[0].Text);
        var call = events[1].ToolCall!;
        Assert.Equal(("toolu_1", "read_file", """{"path":"a.cs"}"""), (call.Id, call.Name, call.Arguments));
        using var body = JsonDocument.Parse(_requests.Single(r => r.Path == "/anthropic/v1/messages").Body);
        Assert.Equal("read_file", body.RootElement.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("object", body.RootElement.GetProperty("tools")[0].GetProperty("input_schema").GetProperty("type").GetString());
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength()); // user, assistant with tool_use, user with tool_result
        var use = messages[1].GetProperty("content")[1];
        Assert.Equal(("tool_use", "call_1", "list_files"), (use.GetProperty("type").GetString(), use.GetProperty("id").GetString(), use.GetProperty("name").GetString()));
        var result = messages[2].GetProperty("content")[0];
        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        Assert.Equal(("tool_result", "call_1", "a.cs"), (result.GetProperty("type").GetString(), result.GetProperty("tool_use_id").GetString(), result.GetProperty("content").GetString()));
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        _workbench.Dispose();
        await _server.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Answers the key prompt (a password box) with a key.</summary>
    private sealed class KeyPrompt(string key) : IPluginPrompts
    {
        public Task<int?> PickAsync(IReadOnlyList<QuickPickItemDto> items, string? placeholder, string source, CancellationToken cancellationToken) => Task.FromResult<int?>(null);

        public Task<string?> InputAsync(string title, string? prompt, string? value, string source, CancellationToken cancellationToken, bool password = false) =>
            Task.FromResult<string?>(password ? key : null);
    }
}
