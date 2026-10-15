using System.Text;
using System.Text.Json;

namespace Codalus.Tests;

/// <summary>
/// Model services for the provider tests and the smoke test (ADR 0062): an OpenAI-compatible server and Anthropic's Messages API, answering
/// streamed requests with server-sent events. They check the key and keep every request; nothing reaches a real service.
/// </summary>
/// <summary>A scripted reply: text, and optionally one tool call with its arguments (JSON).</summary>
internal sealed record MockTurn(string Text, string? Tool = null, string? Arguments = null);

internal static class MockModelServers
{
    public const string Key = "test-key-0123456789";

    /// <summary>An OpenAI-compatible server: <c>/models</c> and <c>/chat/completions</c> streaming <paramref name="reply"/> in two parts.</summary>
    public static MockHttpServer OpenAI(string reply = "Hello from the model.", bool requireKey = true, string? toolCall = null) =>
        OpenAI(_ => reply, requireKey, toolCall);

    /// <summary>An OpenAI-compatible server whose reply depends on the request (its body: the messages sent).</summary>
    public static MockHttpServer OpenAI(Func<MockHttpRequest, string> replyFor, bool requireKey = true, string? toolCall = null) =>
        Script(request => new MockTurn(replyFor(request), toolCall, toolCall is null ? null : """{"path":"a.cs"}"""), requireKey);

    /// <summary>An OpenAI-compatible server whose every turn is scripted from the request: an agent's loop.</summary>
    public static MockHttpServer Script(Func<MockHttpRequest, MockTurn> script, bool requireKey = true) => new(request =>
    {
        if (requireKey && request.BearerToken != Key)
        {
            return MockHttpResponse.Json("""{"error":{"message":"Incorrect API key provided."}}""", 401);
        }

        switch (request.Method, request.Path)
        {
            case ("GET", "/v1/models"):
                return MockHttpResponse.Json("""{"object":"list","data":[{"id":"text-embedding-3-small"},{"id":"gpt-test"},{"id":"gpt-mini"}]}""");
            case ("POST", "/v1/chat/completions"):
                var turn = script(request);
                var reply = turn.Text;
                var sb = new StringBuilder();
                var half = reply.Length / 2;
                foreach (var part in new[] { reply[..half], reply[half..] })
                {
                    sb.Append("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"").Append(JsonEncodedText.Encode(part).ToString()).Append("\"}}]}\n\n");
                }

                if (turn.Tool is { } tool)
                {
                    var args = turn.Arguments ?? "{}";
                    var cut = args.Length / 2;
                    sb.Append("data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"").Append(tool).Append("\",\"arguments\":\"").Append(JsonEncodedText.Encode(args[..cut]).ToString()).Append("\"}}]}}]}\n\n");
                    sb.Append("data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"").Append(JsonEncodedText.Encode(args[cut..]).ToString()).Append("\"}}]}}]}\n\n");
                    sb.Append("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n");
                }
                else
                {
                    sb.Append("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
                }

                sb.Append("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":42,\"completion_tokens\":7}}\n\n");
                sb.Append("data: [DONE]\n\n");
                return MockHttpResponse.Text(sb.ToString(), 200, "text/event-stream");
            default:
                return MockHttpResponse.NotFound();
        }
    });

    /// <summary>Anthropic's Messages API: <c>/v1/models</c> and <c>/v1/messages</c> streaming <paramref name="reply"/>.</summary>
    public static MockHttpServer Anthropic(string reply = "Hello from Claude.", bool toolUse = false, string? stopReason = null) => new(request =>
    {
        if (request.Header("x-api-key") != Key)
        {
            return MockHttpResponse.Json("""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", 401);
        }

        if (request.Header("anthropic-version") != "2023-06-01")
        {
            return MockHttpResponse.Json("""{"type":"error","error":{"type":"invalid_request_error","message":"anthropic-version missing"}}""", 400);
        }

        switch (request.Method, request.Path)
        {
            case ("GET", "/v1/models"):
                return MockHttpResponse.Json("""{"data":[{"type":"model","id":"claude-test","display_name":"Claude Test","max_input_tokens":1000000,"max_tokens":128000}],"has_more":false}""");
            case ("POST", "/v1/messages"):
                var sb = new StringBuilder();
                void Event(string name, string data) => sb.Append("event: ").Append(name).Append("\ndata: ").Append(data).Append("\n\n");
                Event("message_start", """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","content":[],"usage":{"input_tokens":30,"output_tokens":1}}}""");
                Event("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""");
                var half = reply.Length / 2;
                foreach (var part in new[] { reply[..half], reply[half..] })
                {
                    Event("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"" + JsonEncodedText.Encode(part).ToString() + "\"}}");
                }

                Event("ping", """{"type":"ping"}""");
                Event("content_block_stop", """{"type":"content_block_stop","index":0}""");
                if (toolUse)
                {
                    Event("content_block_start", """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"read_file","input":{}}}""");
                    Event("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"path\":"}}""");
                    Event("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\"a.cs\"}"}}""");
                    Event("content_block_stop", """{"type":"content_block_stop","index":1}""");
                }

                Event("message_delta", "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"" + (stopReason ?? (toolUse ? "tool_use" : "end_turn")) + "\"},\"usage\":{\"output_tokens\":9}}");
                Event("message_stop", """{"type":"message_stop"}""");
                return MockHttpResponse.Text(sb.ToString(), 200, "text/event-stream");
            default:
                return MockHttpResponse.NotFound();
        }
    });
}
