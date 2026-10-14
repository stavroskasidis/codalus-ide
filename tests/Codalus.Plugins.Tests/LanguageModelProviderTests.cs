using System.Text.Json;
using Codalus.Plugins.Anthropic;
using Codalus.Plugins.LanguageModels;
using Codalus.Plugins.OpenAI;
using Codalus.Plugins.Proposed;
using Codalus.Plugins.Testing;

namespace Codalus.Plugins.Tests;

/// <summary>The built-in language model providers (ADR 0062) called directly, against <see cref="MockModelServers"/>.</summary>
public sealed class LanguageModelProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ChatRequest Hello(string model) =>
        new(model, [new ChatMessage(ChatRole.System, "Be brief."), new ChatMessage(ChatRole.User, "Hi")]);

    private static (TestPluginContext Context, OpenAIProvider Provider) OpenAI(string endpoint, string? key = MockModelServers.Key)
    {
        var context = new TestPluginContext("codalus.openai");
        context.Settings.Set("ai.openai.endpoint", endpoint + "/v1");
        if (key is not null)
        {
            context.Secrets.StoreAsync("apiKey:openai", key, CancellationToken.None).GetAwaiter().GetResult();
        }

        return (context, new OpenAIProvider(OpenAIProvider.OpenAIId, context, "OpenAI-compatible endpoint", "ai.openai.endpoint", "https://api.openai.com/v1", KeyUse.Optional));
    }

    private static (TestPluginContext Context, AnthropicProvider Provider) Anthropic(string endpoint, string? key = MockModelServers.Key)
    {
        var context = new TestPluginContext("codalus.anthropic");
        context.Settings.Set("ai.anthropic.endpoint", endpoint);
        if (key is not null)
        {
            context.Secrets.StoreAsync("apiKey:anthropic", key, CancellationToken.None).GetAwaiter().GetResult();
        }

        return (context, new AnthropicProvider(context));
    }

    private sealed class Parts : IProgress<ChatResponsePart>
    {
        public List<ChatResponsePart> Items { get; } = [];

        public string Text => string.Concat(Items.Select(p => p.Text));

        public void Report(ChatResponsePart value) => Items.Add(value);
    }

    [Fact]
    public async Task OpenAI_compatible_lists_chat_models_and_streams_a_reply_with_its_usage()
    {
        await using var server = MockModelServers.OpenAI();
        var (_, provider) = OpenAI(server.BaseUrl);

        var models = await provider.GetModelsAsync(Ct);
        Assert.Equal(["gpt-mini", "gpt-test"], models.Select(m => m.Id));

        var parts = new Parts();
        var result = await provider.ChatAsync(Hello("gpt-test"), parts, Ct);
        Assert.Equal("Hello from the model.", parts.Text);
        Assert.Equal(2, parts.Items.Count);
        Assert.Equal(ChatFinishReason.Stop, result.FinishReason);
        Assert.Equal(42, result.InputTokens);
        Assert.Equal(7, result.OutputTokens);

        var sent = server.Requests.Last();
        Assert.Equal("Bearer " + MockModelServers.Key, sent.Header("Authorization"));
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal("gpt-test", body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("system", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("Hi", body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task OpenAI_compatible_assembles_a_streamed_tool_call()
    {
        await using var server = MockModelServers.OpenAI(toolCall: "read_file");
        var (_, provider) = OpenAI(server.BaseUrl);
        var parts = new Parts();
        var request = Hello("gpt-test") with
        {
            Tools = [new ChatTool("read_file", "Reads a file.", JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}}}""").RootElement)],
        };

        var result = await provider.ChatAsync(request, parts, Ct);

        Assert.Equal(ChatFinishReason.ToolCalls, result.FinishReason);
        var call = Assert.Single(parts.Items, p => p.ToolCall is not null).ToolCall!;
        Assert.Equal(("call_1", "read_file", """{"path":"a.cs"}"""), (call.Id, call.Name, call.Arguments));
        using var body = JsonDocument.Parse(server.Requests.Last().Body);
        Assert.Equal("read_file", body.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task OpenAI_compatible_sends_tool_results_back_in_its_shape()
    {
        var (_, provider) = OpenAI("http://localhost:1234");
        var body = provider.BuildBody(new ChatRequest("m",
        [
            new ChatMessage(ChatRole.User, "Read a.cs"),
            new ChatMessage(ChatRole.Assistant, "") { ToolCalls = [new ChatToolCall("call_1", "read_file", """{"path":"a.cs"}""")] },
            new ChatMessage(ChatRole.Tool, "class A {}") { ToolCallId = "call_1" },
        ]) { MaxOutputTokens = 100 });

        using var document = JsonDocument.Parse(body);
        var messages = document.RootElement.GetProperty("messages");
        Assert.Equal("call_1", messages[1].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("tool", messages[2].GetProperty("role").GetString());
        Assert.Equal("call_1", messages[2].GetProperty("tool_call_id").GetString());

        // A compatible server takes max_tokens; OpenAI itself takes max_completion_tokens.
        Assert.Equal(100, document.RootElement.GetProperty("max_tokens").GetInt32());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task OpenAI_compatible_works_without_a_key_and_asks_for_one_when_refused()
    {
        await using var open = MockModelServers.OpenAI(requireKey: false);
        var (_, keyless) = OpenAI(open.BaseUrl, key: null);
        Assert.True((await keyless.GetStatusAsync(Ct)).IsReady);
        var parts = new Parts();
        await keyless.ChatAsync(Hello("gpt-test"), parts, Ct);
        Assert.Null(open.Requests.Last().Header("Authorization"));

        await using var locked = MockModelServers.OpenAI();
        var (_, wrong) = OpenAI(locked.BaseUrl, key: "wrong");
        var ex = await Assert.ThrowsAsync<LanguageModelException>(() => wrong.GetModelsAsync(Ct));
        Assert.True(ex.NeedsConfiguration);
        Assert.Contains("Incorrect API key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Configure_stores_the_key_in_secrets_and_an_empty_answer_removes_it()
    {
        var (context, provider) = OpenAI("http://localhost:1234", key: null);
        context.Window.InputBoxResponder = _ => "  sk-new-key  ";
        await provider.ConfigureAsync(Ct);
        Assert.Equal("sk-new-key", context.Secrets.Values["apiKey:openai"]);

        context.Window.InputBoxResponder = _ => "";
        await provider.ConfigureAsync(Ct);
        Assert.False(context.Secrets.Values.ContainsKey("apiKey:openai"));

        // OpenAI's own endpoint needs a key.
        var (_, openai) = OpenAI("https://api.openai.com", key: null);
        Assert.False((await openai.GetStatusAsync(Ct)).IsReady);
    }

    [Fact]
    public async Task Anthropic_lists_models_and_streams_text_with_its_usage()
    {
        await using var server = MockModelServers.Anthropic();
        var (_, provider) = Anthropic(server.BaseUrl);

        var model = Assert.Single(await provider.GetModelsAsync(Ct));
        Assert.Equal(("claude-test", "Claude Test", 1_000_000), (model.Id, model.Name, model.ContextWindow));

        var parts = new Parts();
        var result = await provider.ChatAsync(Hello("claude-test"), parts, Ct);
        Assert.Equal("Hello from Claude.", parts.Text);
        Assert.Equal((ChatFinishReason.Stop, 30, 9), (result.FinishReason, result.InputTokens, result.OutputTokens));

        using var body = JsonDocument.Parse(server.Requests.Last().Body);
        Assert.Equal("Be brief.", body.RootElement.GetProperty("system").GetString());
        Assert.Equal(16000, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        var message = Assert.Single(body.RootElement.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Anthropic_assembles_tool_use_from_json_fragments_and_reports_refusals()
    {
        await using var server = MockModelServers.Anthropic(toolUse: true);
        var (_, provider) = Anthropic(server.BaseUrl);
        var parts = new Parts();
        var result = await provider.ChatAsync(Hello("claude-test"), parts, Ct);
        Assert.Equal(ChatFinishReason.ToolCalls, result.FinishReason);
        var call = Assert.Single(parts.Items, p => p.ToolCall is not null).ToolCall!;
        Assert.Equal(("toolu_1", "read_file", """{"path":"a.cs"}"""), (call.Id, call.Name, call.Arguments));

        await using var refusing = MockModelServers.Anthropic(reply: "", stopReason: "refusal");
        var (_, refused) = Anthropic(refusing.BaseUrl);
        Assert.Equal(ChatFinishReason.Refused, (await refused.ChatAsync(Hello("claude-test"), new Parts(), Ct)).FinishReason);
    }

    [Fact]
    public async Task Anthropic_merges_tool_results_into_one_user_message()
    {
        var (_, provider) = Anthropic("http://localhost:1");
        var body = provider.BuildBody(new ChatRequest("claude-test",
        [
            new ChatMessage(ChatRole.User, "Read two files"),
            new ChatMessage(ChatRole.Assistant, "Reading.")
            {
                ToolCalls = [new ChatToolCall("t1", "read_file", """{"path":"a.cs"}"""), new ChatToolCall("t2", "read_file", """{"path":"b.cs"}""")],
            },
            new ChatMessage(ChatRole.Tool, "class A {}") { ToolCallId = "t1" },
            new ChatMessage(ChatRole.Tool, "not found") { ToolCallId = "t2", IsError = true },
        ]) { Options = new Dictionary<string, string> { ["effort"] = "low" } });

        using var document = JsonDocument.Parse(body);
        var messages = document.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("a.cs", messages[1].GetProperty("content")[1].GetProperty("input").GetProperty("path").GetString());
        var results = messages[2].GetProperty("content");
        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal("t2", results[1].GetProperty("tool_use_id").GetString());
        Assert.True(results[1].GetProperty("is_error").GetBoolean());
        Assert.Equal("low", document.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Anthropic_needs_a_key_and_says_so()
    {
        await using var server = MockModelServers.Anthropic();
        var (_, missing) = Anthropic(server.BaseUrl, key: null);
        Assert.False((await missing.GetStatusAsync(Ct)).IsReady);
        var ex = await Assert.ThrowsAsync<LanguageModelException>(() => missing.ChatAsync(Hello("claude-test"), new Parts(), Ct));
        Assert.True(ex.NeedsConfiguration);
        Assert.Empty(server.Requests);

        var (_, wrong) = Anthropic(server.BaseUrl, key: "wrong");
        ex = await Assert.ThrowsAsync<LanguageModelException>(() => wrong.GetModelsAsync(Ct));
        Assert.True(ex.NeedsConfiguration);
        Assert.Contains("invalid x-api-key", ex.Message, StringComparison.Ordinal);
    }
}
