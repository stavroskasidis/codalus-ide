using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codalus.Workbench.Ai;

/// <summary>A tool <see cref="McpServer"/> offers.</summary>
public sealed record McpServerTool(string Name, string Description, string InputSchema, Func<JsonObject, CancellationToken, Task<string>> Invoke);

/// <summary>
/// A stdio MCP server (ADR 0063): JSON-RPC messages, one per line. It answers <c>initialize</c>, <c>ping</c>,
/// <c>tools/list</c> and <c>tools/call</c> (text content); <c>codalus mcp</c> runs it for external agents.
/// </summary>
public static class McpServer
{
    public static async Task RunAsync(TextReader input, TextWriter output, IReadOnlyList<McpServerTool> tools, string version, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(tools);
        var write = new SemaphoreSlim(1, 1);
        var running = new List<Task>();
        while (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            JsonObject? message;
            try
            {
                message = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                await WriteAsync(output, write, Error(null, -32700, "Parse error"), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (message?["method"]?.GetValue<string>() is not { } method || message["id"] is not { } id)
            {
                continue; // a notification (initialized, cancelled) or a response: nothing to answer
            }

            // Calls may take a while (a build): each answers on its own, so ping and others don't wait.
            running.RemoveAll(t => t.IsCompleted);
            running.Add(AnswerAsync(method, id.DeepClone(), message["params"] as JsonObject));
        }

        await Task.WhenAll(running).ConfigureAwait(false);
        write.Dispose();

        async Task AnswerAsync(string method, JsonNode id, JsonObject? parameters)
        {
            JsonObject reply;
            switch (method)
            {
                case "initialize":
                    reply = Result(id, new JsonObject
                    {
                        ["protocolVersion"] = parameters?["protocolVersion"]?.GetValue<string>() ?? McpClient.ProtocolVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "codalus", ["version"] = version },
                    });
                    break;
                case "ping":
                    reply = Result(id, new JsonObject());
                    break;
                case "tools/list":
                    reply = Result(id, new JsonObject
                    {
                        ["tools"] = new JsonArray([.. tools.Select(t => (JsonNode)new JsonObject
                        {
                            ["name"] = t.Name,
                            ["description"] = t.Description,
                            ["inputSchema"] = JsonNode.Parse(t.InputSchema),
                        })]),
                    });
                    break;
                case "tools/call":
                    var name = parameters?["name"]?.GetValue<string>();
                    if (tools.FirstOrDefault(t => t.Name == name) is not { } tool)
                    {
                        reply = Error(id, -32602, $"Unknown tool: {name}");
                        break;
                    }

                    string text;
                    var failed = false;
                    try
                    {
                        text = await tool.Invoke(parameters?["arguments"] as JsonObject ?? [], cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException)
                    {
                        text = ex.Message;
                        failed = true;
                    }

                    reply = Result(id, new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                        ["isError"] = failed,
                    });
                    break;
                default:
                    reply = Error(id, -32601, $"Method not found: {method}");
                    break;
            }

            await WriteAsync(output, write, reply, cancellationToken).ConfigureAwait(false);
        }
    }

    private static JsonObject Result(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static async Task WriteAsync(TextWriter output, SemaphoreSlim gate, JsonObject message, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await output.WriteAsync((message.ToJsonString() + "\n").AsMemory(), cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The tools of <c>codalus mcp</c>: each asks the running IDE (<paramref name="ask"/>: the tool and its arguments as
    /// JSON; null when no IDE answers).
    /// </summary>
    public static IReadOnlyList<McpServerTool> IdeTools(Func<string, string, CancellationToken, Task<string?>> ask)
    {
        ArgumentNullException.ThrowIfNull(ask);
        McpServerTool Tool(string name, string description, string schema) =>
            new(name, description, schema, async (args, ct) =>
                await ask(name, args.ToJsonString(), ct).ConfigureAwait(false)
                ?? "Codalus IDE isn't running for this user-data folder: start it and open the solution, then ask again.");
        return
        [
            Tool("get_solution", "The solution open in Codalus IDE: its path and projects with their target frameworks.", """{"type":"object","properties":{}}"""),
            Tool("get_problems", "The errors and warnings Codalus IDE shows: of one file (an absolute path), or of every file.", """{"type":"object","properties":{"path":{"type":"string"}}}"""),
            Tool("get_test_results", "The Test Explorer's last results: the failed tests with their messages, and the counts.", """{"type":"object","properties":{}}"""),
            Tool("build", "Builds the open solution in Codalus IDE and returns whether it succeeded, with its errors.", """{"type":"object","properties":{}}"""),
        ];
    }

    /// <summary>The name of the instance request that carries an MCP tool call.</summary>
    public const string RequestPrefix = "mcp/";
}
