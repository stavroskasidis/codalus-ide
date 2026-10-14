using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codalus.Plugins.LanguageModels;
using Codalus.Plugins.Proposed;

namespace Codalus.Plugins.OpenAI;

/// <summary>
/// A provider on OpenAI's chat completions API (<c>POST /chat/completions</c> with <c>stream: true</c>, <c>GET /models</c>),
/// which OpenAI, Ollama and the common local servers serve.
/// </summary>
internal sealed class OpenAIProvider(string id, IPluginContext context, string label, string endpointSetting, string defaultEndpoint, KeyUse key)
    : HttpModelProvider(id, context, label, endpointSetting, defaultEndpoint, key)
{
    public const string OpenAIId = "openai";
    public const string OllamaId = "ollama";

    protected override void Authorize(HttpRequestMessage request, string key) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

    public override async Task<LanguageModelStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (Id == OpenAIId && IsOpenAI && await GetKeyAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return new LanguageModelStatus(false, "No OpenAI key is stored: Configure to enter one.");
        }

        return await base.GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>OpenAI's own API (not a compatible server): it needs a key, and takes <c>max_completion_tokens</c>.</summary>
    private bool IsOpenAI => Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) && uri.Host.EndsWith("openai.com", StringComparison.OrdinalIgnoreCase);

    public override async Task<IReadOnlyList<LanguageModelInfo>> GetModelsAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("/models", cancellationToken).ConfigureAwait(false);
        var models = new List<LanguageModelInfo>();
        if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in data.EnumerateArray())
            {
                if (model.TryGetProperty("id", out var idElement) && idElement.GetString() is { Length: > 0 } modelId && IsChatModel(modelId))
                {
                    models.Add(new LanguageModelInfo(modelId, modelId) { SupportsTools = true });
                }
            }
        }

        return [.. models.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>OpenAI lists embedding, audio, image and moderation models too; they can't chat.</summary>
    private static bool IsChatModel(string id) =>
        !(id.Contains("embedding", StringComparison.OrdinalIgnoreCase) || id.Contains("whisper", StringComparison.OrdinalIgnoreCase) ||
          id.Contains("tts", StringComparison.OrdinalIgnoreCase) || id.Contains("dall-e", StringComparison.OrdinalIgnoreCase) ||
          id.Contains("moderation", StringComparison.OrdinalIgnoreCase) || id.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase) ||
          id.Contains("transcribe", StringComparison.OrdinalIgnoreCase));

    public override async Task<ChatResult> ChatAsync(ChatRequest request, IProgress<ChatResponsePart> progress, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, "/chat/completions", BuildBody(request), cancellationToken, stream: true).ConfigureAwait(false);
        var calls = new SortedDictionary<int, ToolCallBuilder>();
        string? finish = null;
        int? input = null, output = null;
        await foreach (var sse in ReadEventsAsync(response, cancellationToken).ConfigureAwait(false))
        {
            if (sse.Data == "[DONE]")
            {
                break;
            }

            using var document = ParseChunk(sse.Data);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out _))
            {
                throw new LanguageModelException($"{Label}: {ErrorMessage(sse.Data) ?? "error"}");
            }

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                input = Int(usage, "prompt_tokens") ?? input;
                output = Int(usage, "completion_tokens") ?? output;
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } text)
                    {
                        progress.Report(new ChatResponsePart(text));
                    }

                    if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var call in toolCalls.EnumerateArray())
                        {
                            var index = Int(call, "index") ?? calls.Count;
                            if (!calls.TryGetValue(index, out var builder))
                            {
                                calls[index] = builder = new ToolCallBuilder();
                            }

                            builder.Add(call);
                        }
                    }
                }

                if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                {
                    finish = reason.GetString();
                }
            }
        }

        foreach (var call in calls.Values)
        {
            progress.Report(new ChatResponsePart(null) { ToolCall = call.Build() });
        }

        var finishReason = finish switch
        {
            "length" => ChatFinishReason.Length,
            "tool_calls" or "function_call" => ChatFinishReason.ToolCalls,
            "content_filter" => ChatFinishReason.Refused,
            _ when calls.Count > 0 => ChatFinishReason.ToolCalls,
            _ => ChatFinishReason.Stop,
        };
        return new ChatResult(finishReason) { InputTokens = input, OutputTokens = output };
    }

    private JsonDocument ParseChunk(string data)
    {
        try
        {
            return JsonDocument.Parse(data);
        }
        catch (JsonException ex)
        {
            throw new LanguageModelException($"{Label} sent a part that isn't JSON.", ex);
        }
    }

    internal string BuildBody(ChatRequest request)
    {
        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            var node = new JsonObject
            {
                ["role"] = message.Role switch { ChatRole.System => "system", ChatRole.Assistant => "assistant", ChatRole.Tool => "tool", _ => "user" },
                ["content"] = message.Content,
            };
            if (message.ToolCalls is { Count: > 0 } toolCalls)
            {
                node["tool_calls"] = new JsonArray([.. toolCalls.Select(c => (JsonNode)new JsonObject
                {
                    ["id"] = c.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
                })]);
            }

            if (message.ToolCallId is { } toolCallId)
            {
                node["tool_call_id"] = toolCallId;
            }

            messages.Add(node);
        }

        var body = new JsonObject
        {
            ["model"] = request.ModelId,
            ["messages"] = messages,
            ["stream"] = true,
        };
        if (Id == OpenAIId)
        {
            // Ollama and some local servers reject options they don't know.
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        if (request.MaxOutputTokens is { } max)
        {
            body[IsOpenAI ? "max_completion_tokens" : "max_tokens"] = max;
        }

        if (request.Stop is { Count: > 0 } stop)
        {
            body["stop"] = new JsonArray([.. stop.Select(s => (JsonNode?)s)]);
        }

        if (request.Tools is { Count: > 0 } tools)
        {
            body["tools"] = new JsonArray([.. tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(t.Parameters.GetRawText()),
                },
            })]);
        }

        if (request.Options?.TryGetValue("reasoning_effort", out var effort) == true)
        {
            body["reasoning_effort"] = effort;
        }

        return body.ToJsonString();
    }

    private static int? Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i) ? i : null;

    /// <summary>A tool call streamed in pieces: the id and name first, the arguments in fragments.</summary>
    private sealed class ToolCallBuilder
    {
        private readonly System.Text.StringBuilder _arguments = new();
        private string? _id;
        private string? _name;

        public void Add(JsonElement call)
        {
            if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                _id = id.GetString();
            }

            if (call.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
            {
                if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    _name = name.GetString();
                }

                if (function.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.String)
                {
                    _arguments.Append(arguments.GetString());
                }
            }
        }

        public ChatToolCall Build() =>
            new(_id ?? "call_" + Guid.NewGuid().ToString("N")[..12], _name ?? "", _arguments.Length == 0 ? "{}" : _arguments.ToString());
    }
}
