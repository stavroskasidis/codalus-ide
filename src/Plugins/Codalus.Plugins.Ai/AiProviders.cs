using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codalus.Plugins.Proposed;

namespace Codalus.Plugins.Ai;

/// <summary>
/// The built-in AI providers (M28a, ADR 0062): thin clients of their APIs, with no privilege over third-party ones. The
/// IDE decides what a request carries; these only send it to the endpoint the user configured.
/// </summary>
public sealed class AiPlugin : ICodalusPlugin
{
    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var ai = context.Proposed().Ai;
        context.Subscriptions.Add(ai.RegisterModelProvider("openai", "OpenAI-compatible", new OpenAiCompatibleProvider(context, Http)));
        context.Subscriptions.Add(ai.RegisterModelProvider("anthropic", "Anthropic", new AnthropicProvider(context, Http)));
        return Task.CompletedTask;
    }
}

/// <summary>Reads server-sent events: each <c>data:</c> line's text (an event's other fields aren't needed here).</summary>
internal static class ServerSentEvents
{
    public static async IAsyncEnumerable<string> DataAsync(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                yield return line[5..].TrimStart();
            }
        }
    }

    /// <summary>Throws with the API's own message when a request failed.</summary>
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, string provider, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string? message = null;
        try
        {
            message = JsonNode.Parse(body)?["error"] is { } error ? error["message"]?.GetValue<string>() ?? error.ToString() : null;
        }
        catch (JsonException)
        {
        }

        throw new InvalidOperationException($"{provider} answered {(int)response.StatusCode}: {message ?? body}");
    }
}

/// <summary>
/// Any OpenAI-compatible endpoint (<c>/models</c>, <c>/chat/completions</c> with streaming): OpenAI itself, or a local
/// server (Ollama, LM Studio, llama.cpp, vLLM). The key is optional, since local servers need none.
/// </summary>
internal sealed class OpenAiCompatibleProvider(IPluginContext context, HttpClient http) : IAiModelProvider
{
    private const string KeyName = "openai.apiKey";

    private string BaseUrl => context.Settings.GetValue("ai.openai.baseUrl", "https://api.openai.com/v1").TrimEnd('/');

    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, new Uri(BaseUrl + path));
        if (await context.Secrets.GetAsync(KeyName, cancellationToken).ConfigureAwait(false) is { Length: > 0 } key)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return request;
    }

    public async Task<IReadOnlyList<AiModel>> GetModelsAsync(CancellationToken cancellationToken)
    {
        using var request = await RequestAsync(HttpMethod.Get, "/models", cancellationToken).ConfigureAwait(false);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ServerSentEvents.EnsureSuccessAsync(response, "The endpoint", cancellationToken).ConfigureAwait(false);
        var models = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))?["data"]?.AsArray() ?? [];
        return [.. models.Select(m => m?["id"]?.GetValue<string>()).OfType<string>().Order(StringComparer.Ordinal).Select(id => new AiModel(id, id))];
    }

    public async IAsyncEnumerable<string> ChatAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in ChatEventsAsync(request with { Tools = null }, cancellationToken).ConfigureAwait(false))
        {
            if (item.Text is { } text)
            {
                yield return text;
            }
        }
    }

    /// <summary>A message in the Chat Completions shape: an assistant's <c>tool_calls</c>, a tool result's <c>tool_call_id</c>.</summary>
    internal static JsonObject Message(AiMessage m)
    {
        var node = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
        if (m.ToolCalls is { Count: > 0 } calls)
        {
            node["tool_calls"] = new JsonArray([.. calls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
            })]);
        }

        if (m.ToolCallId is { } id)
        {
            node["tool_call_id"] = id;
        }

        return node;
    }

    public async IAsyncEnumerable<AiEvent> ChatEventsAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["stream"] = true,
            ["messages"] = new JsonArray([.. request.Messages.Select(m => (JsonNode)Message(m))]),
        };
        if (request.MaxTokens is { } max)
        {
            body["max_tokens"] = max;
        }

        if (request.Tools is { Count: > 0 } tools)
        {
            body["tools"] = new JsonArray([.. tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = JsonNode.Parse(t.InputSchema) },
            })]);
        }

        // A tool call streams in pieces under its index: the id and name first, then the arguments' text.
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Arguments)>();

        using var message = await RequestAsync(HttpMethod.Post, "/chat/completions", cancellationToken).ConfigureAwait(false);
        message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await ServerSentEvents.EnsureSuccessAsync(response, "The endpoint", cancellationToken).ConfigureAwait(false);
        await foreach (var data in ServerSentEvents.DataAsync(response, cancellationToken).ConfigureAwait(false))
        {
            if (data == "[DONE]")
            {
                break;
            }

            var delta = JsonNode.Parse(data)?["choices"]?[0]?["delta"];
            if (delta?["content"]?.GetValue<string>() is { Length: > 0 } text)
            {
                yield return new AiEvent(text, null);
            }

            foreach (var call in delta?["tool_calls"]?.AsArray() ?? [])
            {
                var index = call?["index"]?.GetValue<int>() ?? 0;
                var current = calls.TryGetValue(index, out var c) ? c : (Id: string.Empty, Name: string.Empty, Arguments: new StringBuilder());
                if (call?["id"]?.GetValue<string>() is { Length: > 0 } id)
                {
                    current.Id = id;
                }

                if (call?["function"]?["name"]?.GetValue<string>() is { Length: > 0 } name)
                {
                    current.Name = name;
                }

                current.Arguments.Append(call?["function"]?["arguments"]?.GetValue<string>());
                calls[index] = current;
            }
        }

        foreach (var (index, call) in calls)
        {
            yield return new AiEvent(null, new AiToolCall(call.Id.Length > 0 ? call.Id : "call_" + index, call.Name, call.Arguments.Length > 0 ? call.Arguments.ToString() : "{}"));
        }
    }

    public async Task<bool> ConfigureAsync(CancellationToken cancellationToken)
    {
        var key = await context.Window.ShowInputBoxAsync(new InputBoxOptions("OpenAI-compatible API key")
        {
            Prompt = $"The key for {BaseUrl} (stored in the system's credential store). Leave it empty for a local server that needs none.",
            Password = true,
        }, cancellationToken).ConfigureAwait(false);
        if (key is null)
        {
            return false;
        }

        if (key.Length == 0)
        {
            await context.Secrets.DeleteAsync(KeyName, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await context.Secrets.StoreAsync(KeyName, key.Trim(), cancellationToken).ConfigureAwait(false);
        }

        return true;
    }
}

/// <summary>Anthropic's Messages API (<c>/v1/models</c>, <c>/v1/messages</c> with streaming).</summary>
internal sealed class AnthropicProvider(IPluginContext context, HttpClient http) : IAiModelProvider
{
    private const string KeyName = "anthropic.apiKey";
    private const string ApiVersion = "2023-06-01";

    /// <summary>The Messages API requires a limit; this one leaves room for code.</summary>
    internal const int DefaultMaxTokens = 8192;

    private string BaseUrl => context.Settings.GetValue("ai.anthropic.baseUrl", "https://api.anthropic.com").TrimEnd('/');

    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var key = await context.Secrets.GetAsync(KeyName, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Anthropic needs an API key: choose Configure in the AI chat.");
        var request = new HttpRequestMessage(method, new Uri(BaseUrl + path));
        request.Headers.Add("x-api-key", key);
        request.Headers.Add("anthropic-version", ApiVersion);
        return request;
    }

    public async Task<IReadOnlyList<AiModel>> GetModelsAsync(CancellationToken cancellationToken)
    {
        if (await context.Secrets.GetAsync(KeyName, cancellationToken).ConfigureAwait(false) is null)
        {
            return [];
        }

        using var request = await RequestAsync(HttpMethod.Get, "/v1/models?limit=100", cancellationToken).ConfigureAwait(false);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ServerSentEvents.EnsureSuccessAsync(response, "Anthropic", cancellationToken).ConfigureAwait(false);
        var models = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))?["data"]?.AsArray() ?? [];
        return [.. models.Select(m => (Id: m?["id"]?.GetValue<string>(), Label: m?["display_name"]?.GetValue<string>()))
            .Where(m => m.Id is not null).Select(m => new AiModel(m.Id!, m.Label ?? m.Id!))];
    }

    public async IAsyncEnumerable<string> ChatAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in ChatEventsAsync(request with { Tools = null }, cancellationToken).ConfigureAwait(false))
        {
            if (item.Text is { } text)
            {
                yield return text;
            }
        }
    }

    /// <summary>
    /// The conversation in the Messages API's shape: an assistant's tool calls are <c>tool_use</c> blocks, and tool
    /// results are <c>tool_result</c> blocks of one user turn.
    /// </summary>
    internal static JsonArray Messages(IEnumerable<AiMessage> messages)
    {
        var result = new JsonArray();
        JsonArray? results = null;
        foreach (var m in messages.Where(m => m.Role != "system"))
        {
            if (m.Role == "tool")
            {
                if (results is null)
                {
                    results = [];
                    result.Add(new JsonObject { ["role"] = "user", ["content"] = results });
                }

                results.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = m.ToolCallId, ["content"] = m.Content });
                continue;
            }

            results = null;
            if (m.ToolCalls is { Count: > 0 } calls)
            {
                var blocks = new JsonArray();
                if (m.Content.Length > 0)
                {
                    blocks.Add(new JsonObject { ["type"] = "text", ["text"] = m.Content });
                }

                foreach (var call in calls)
                {
                    blocks.Add(new JsonObject { ["type"] = "tool_use", ["id"] = call.Id, ["name"] = call.Name, ["input"] = ParseObject(call.Arguments) });
                }

                result.Add(new JsonObject { ["role"] = m.Role, ["content"] = blocks });
            }
            else
            {
                result.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
            }
        }

        return result;
    }

    private static JsonNode ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    public async IAsyncEnumerable<AiEvent> ChatEventsAsync(AiChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The Messages API takes the system prompt on its own, and alternating user and assistant turns.
        var system = string.Join("\n\n", request.Messages.Where(m => m.Role == "system").Select(m => m.Content));
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxTokens ?? DefaultMaxTokens,
            ["stream"] = true,
            ["messages"] = Messages(request.Messages),
        };
        if (system.Length > 0)
        {
            body["system"] = system;
        }

        if (request.Tools is { Count: > 0 } tools)
        {
            body["tools"] = new JsonArray([.. tools.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = JsonNode.Parse(t.InputSchema),
            })]);
        }

        (string Id, string Name, StringBuilder Input)? toolUse = null;

        using var message = await RequestAsync(HttpMethod.Post, "/v1/messages", cancellationToken).ConfigureAwait(false);
        message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await ServerSentEvents.EnsureSuccessAsync(response, "Anthropic", cancellationToken).ConfigureAwait(false);
        await foreach (var data in ServerSentEvents.DataAsync(response, cancellationToken).ConfigureAwait(false))
        {
            var node = JsonNode.Parse(data);
            var type = node?["type"]?.GetValue<string>();
            if (type == "content_block_start" && node!["content_block"] is { } block && block["type"]?.GetValue<string>() == "tool_use")
            {
                toolUse = (block["id"]?.GetValue<string>() ?? string.Empty, block["name"]?.GetValue<string>() ?? string.Empty, new StringBuilder());
            }
            else if (type == "content_block_delta")
            {
                var delta = node!["delta"];
                if (delta?["partial_json"]?.GetValue<string>() is { } partial)
                {
                    toolUse?.Input.Append(partial);
                }
                else if (delta?["text"]?.GetValue<string>() is { Length: > 0 } text)
                {
                    yield return new AiEvent(text, null);
                }
            }
            else if (type == "content_block_stop" && toolUse is { } use)
            {
                toolUse = null;
                yield return new AiEvent(null, new AiToolCall(use.Id, use.Name, use.Input.Length > 0 ? use.Input.ToString() : "{}"));
            }
            else if (type == "error")
            {
                throw new InvalidOperationException("Anthropic: " + (node!["error"]?["message"]?.GetValue<string>() ?? data));
            }
            else if (type == "message_stop")
            {
                yield break;
            }
        }
    }

    public async Task<bool> ConfigureAsync(CancellationToken cancellationToken)
    {
        var key = await context.Window.ShowInputBoxAsync(new InputBoxOptions("Anthropic API key")
        {
            Prompt = "Your key from console.anthropic.com (stored in the system's credential store).",
            Password = true,
        }, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        await context.Secrets.StoreAsync(KeyName, key.Trim(), cancellationToken).ConfigureAwait(false);
        return true;
    }
}
