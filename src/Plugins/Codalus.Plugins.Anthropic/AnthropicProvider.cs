using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codalus.Plugins.LanguageModels;
using Codalus.Plugins.Proposed;

namespace Codalus.Plugins.Anthropic;

/// <summary>
/// Anthropic's Messages API: <c>POST /v1/messages</c> streamed as server-sent events (text deltas, tool use with its
/// input streamed as JSON fragments), <c>GET /v1/models</c>. The system message goes in <c>system</c>; tool results go
/// back as <c>tool_result</c> blocks in one user message.
/// </summary>
internal sealed class AnthropicProvider(IPluginContext context)
    : HttpModelProvider(ProviderId, context, "Anthropic", "ai.anthropic.endpoint", "https://api.anthropic.com", KeyUse.Required)
{
    public const string ProviderId = "anthropic";
    public const string ApiVersion = "2023-06-01";

    /// <summary>Offered when the model list can't be read.</summary>
    internal static readonly IReadOnlyList<LanguageModelInfo> KnownModels =
    [
        new("claude-opus-5-5", "Claude Opus 5.5") { ContextWindow = 1_000_000, MaxOutputTokens = 128_000, SupportsTools = true },
        new("claude-sonnet-5-5", "Claude Sonnet 5.5") { ContextWindow = 1_000_000, MaxOutputTokens = 128_000, SupportsTools = true },
        new("claude-haiku-5-5", "Claude Haiku 5.5") { ContextWindow = 1_000_000, MaxOutputTokens = 128_000, SupportsTools = true },
    ];

    protected override void Authorize(HttpRequestMessage request, string key) => request.Headers.TryAddWithoutValidation("x-api-key", key);

    protected override void AddHeaders(HttpRequestMessage request) => request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);

    public override async Task<IReadOnlyList<LanguageModelInfo>> GetModelsAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("/v1/models?limit=100", cancellationToken).ConfigureAwait(false);
        var models = new List<LanguageModelInfo>();
        if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in data.EnumerateArray())
            {
                if (model.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } modelId)
                {
                    var name = model.TryGetProperty("display_name", out var display) && display.GetString() is { Length: > 0 } d ? d : modelId;
                    models.Add(new LanguageModelInfo(modelId, name)
                    {
                        ContextWindow = Int(model, "max_input_tokens"),
                        MaxOutputTokens = Int(model, "max_tokens"),
                        SupportsTools = true,
                    });
                }
            }
        }

        return models.Count > 0 ? models : KnownModels;
    }

    public override async Task<ChatResult> ChatAsync(ChatRequest request, IProgress<ChatResponsePart> progress, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, "/v1/messages", BuildBody(request), cancellationToken, stream: true).ConfigureAwait(false);
        var blocks = new Dictionary<int, ToolUse>();
        string? stop = null;
        int? input = null, output = null;
        var toolCalls = 0;
        await foreach (var sse in ReadEventsAsync(response, cancellationToken).ConfigureAwait(false))
        {
            using var document = Parse(sse.Data);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : sse.Event;
            switch (type)
            {
                case "message_start" when root.TryGetProperty("message", out var message) && message.TryGetProperty("usage", out var startUsage):
                    input = Int(startUsage, "input_tokens");
                    output = Int(startUsage, "output_tokens");
                    break;
                case "content_block_start" when root.TryGetProperty("content_block", out var block):
                    if (block.TryGetProperty("type", out var blockType) && blockType.GetString() == "tool_use")
                    {
                        blocks[Int(root, "index") ?? 0] = new ToolUse(
                            block.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                            block.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "");
                    }
                    else if (blockType.GetString() == "text" && block.TryGetProperty("text", out var initial) && initial.GetString() is { Length: > 0 } initialText)
                    {
                        progress.Report(new ChatResponsePart(initialText));
                    }

                    break;
                case "content_block_delta" when root.TryGetProperty("delta", out var delta):
                    switch (delta.TryGetProperty("type", out var deltaType) ? deltaType.GetString() : null)
                    {
                        case "text_delta" when delta.TryGetProperty("text", out var text) && text.GetString() is { Length: > 0 } chunk:
                            progress.Report(new ChatResponsePart(chunk));
                            break;
                        case "input_json_delta" when blocks.TryGetValue(Int(root, "index") ?? 0, out var use) && delta.TryGetProperty("partial_json", out var json):
                            use.Input.Append(json.GetString());
                            break;
                    }

                    break;
                case "content_block_stop":
                    if (blocks.Remove(Int(root, "index") ?? 0, out var finished))
                    {
                        toolCalls++;
                        progress.Report(new ChatResponsePart(null) { ToolCall = new ChatToolCall(finished.Id, finished.Name, finished.Input.Length == 0 ? "{}" : finished.Input.ToString()) });
                    }

                    break;
                case "message_delta":
                    if (root.TryGetProperty("delta", out var messageDelta) && messageDelta.TryGetProperty("stop_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                    {
                        stop = reason.GetString();
                    }

                    if (root.TryGetProperty("usage", out var usage))
                    {
                        output = Int(usage, "output_tokens") ?? output;
                        input = Int(usage, "input_tokens") ?? input;
                    }

                    break;
                case "error":
                    throw new LanguageModelException($"Anthropic: {ErrorMessage(sse.Data) ?? "error"}");
                case "message_stop":
                    goto done;
            }
        }

    done:
        var finishReason = stop switch
        {
            "max_tokens" or "model_context_window_exceeded" => ChatFinishReason.Length,
            "tool_use" => ChatFinishReason.ToolCalls,
            "refusal" => ChatFinishReason.Refused,
            _ when toolCalls > 0 => ChatFinishReason.ToolCalls,
            _ => ChatFinishReason.Stop,
        };
        return new ChatResult(finishReason) { InputTokens = input, OutputTokens = output };
    }

    private static JsonDocument Parse(string data)
    {
        try
        {
            return JsonDocument.Parse(data);
        }
        catch (JsonException ex)
        {
            throw new LanguageModelException("Anthropic sent a part that isn't JSON.", ex);
        }
    }

    internal string BuildBody(ChatRequest request)
    {
        var system = string.Join("\n\n", request.Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Content));
        var messages = new JsonArray();
        JsonObject? last = null;
        foreach (var message in request.Messages.Where(m => m.Role != ChatRole.System))
        {
            var role = message.Role == ChatRole.Assistant ? "assistant" : "user";
            var content = new JsonArray();
            if (message.Role == ChatRole.Tool)
            {
                var result = new JsonObject
                {
                    ["type"] = "tool_result",
                    ["tool_use_id"] = message.ToolCallId ?? "",
                    ["content"] = message.Content,
                };
                if (message.IsError)
                {
                    result["is_error"] = true;
                }

                content.Add(result);
            }
            else
            {
                if (message.Content.Length > 0)
                {
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = message.Content });
                }

                foreach (var call in message.ToolCalls ?? [])
                {
                    content.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = call.Id,
                        ["name"] = call.Name,
                        ["input"] = ParseArguments(call.Arguments),
                    });
                }
            }

            if (content.Count == 0)
            {
                continue;
            }

            // Messages alternate: tool results (and anything else of the same role) join the previous message.
            if (last is not null && (string?)last["role"] == role)
            {
                var existing = (JsonArray)last["content"]!;
                foreach (var block in content.ToList())
                {
                    content.Remove(block);
                    existing.Add(block);
                }

                continue;
            }

            last = new JsonObject { ["role"] = role, ["content"] = content };
            messages.Add(last);
        }

        var body = new JsonObject
        {
            ["model"] = request.ModelId,
            ["max_tokens"] = request.MaxOutputTokens ?? Context.Settings.GetValue("ai.anthropic.maxOutputTokens", 16000),
            ["messages"] = messages,
            ["stream"] = true,
        };
        if (system.Length > 0)
        {
            body["system"] = system;
        }

        if (request.Stop is { Count: > 0 } stop)
        {
            body["stop_sequences"] = new JsonArray([.. stop.Select(s => (JsonNode?)s)]);
        }

        if (request.Tools is { Count: > 0 } tools)
        {
            body["tools"] = new JsonArray([.. tools.Select(tool => (JsonNode)new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = JsonNode.Parse(tool.Parameters.GetRawText()),
            })]);
        }

        if (request.Options?.TryGetValue("effort", out var effort) == true)
        {
            body["output_config"] = new JsonObject { ["effort"] = effort };
        }

        return body.ToJsonString();
    }

    private static JsonNode ParseArguments(string arguments)
    {
        try
        {
            return JsonNode.Parse(arguments.Length == 0 ? "{}" : arguments) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static int? Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i) ? i : null;

    private sealed record ToolUse(string Id, string Name)
    {
        public StringBuilder Input { get; } = new();
    }
}
