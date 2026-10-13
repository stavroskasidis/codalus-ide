using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Codalus.Tests;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>Agent mode in the AI Chat and <c>codalus mcp</c> (M28d, ADR 0063), against a mock OpenAI-compatible server.</summary>
public sealed class AiAgentShellTests
{
    private static string AiPluginDir => Path.Combine(ShellFixture.RepoRoot, "src", "Plugins", "Codalus.Plugins.Ai", "bin", ShellFixture.Configuration, "net10.0");

    /// <summary>Answers each request with the next scripted reply: a tool call, or text.</summary>
    private sealed class ScriptedServer : IAsyncDisposable
    {
        private readonly List<string> _bodies = [];

        public ScriptedServer(params Func<string>[] replies)
        {
            Http = new MockHttpServer(request =>
            {
                if (request.Path == "/v1/models")
                {
                    return MockHttpResponse.Json("""{ "data": [ { "id": "local-coder" } ] }""");
                }

                if (request.Path != "/v1/chat/completions")
                {
                    return MockHttpResponse.NotFound();
                }

                int index;
                lock (_bodies)
                {
                    _bodies.Add(request.Body);
                    index = _bodies.Count - 1;
                }

                return MockHttpResponse.Text(replies[Math.Min(index, replies.Length - 1)]() + "data: [DONE]\n\n", contentType: "text/event-stream");
            });
        }

        public MockHttpServer Http { get; }

        public IReadOnlyList<string> Bodies
        {
            get
            {
                lock (_bodies)
                {
                    return [.. _bodies];
                }
            }
        }

        public ValueTask DisposeAsync() => Http.DisposeAsync();
    }

    private static string ToolCall(string id, string name, object arguments) =>
        "data: " + JsonSerializer.Serialize(new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["delta"] = new JsonObject
                {
                    ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["index"] = 0, ["id"] = id, ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = name, ["arguments"] = JsonSerializer.Serialize(arguments) },
                    }),
                },
            }),
        }) + "\n\n";

    private static string Text(string text) =>
        "data: " + JsonSerializer.Serialize(new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = text } }) }) + "\n\n";

    [AvaloniaFact]
    public async Task The_agent_edits_through_the_preview_and_runs_an_approved_command()
    {
        await using var server = new ScriptedServer(
            () => ToolCall("call_1", "edit_file", new { path = "src/App/Program.cs", old_text = "\"fixture\"", new_text = "\"agent\"" }),
            () => ToolCall("call_2", "run_command", new { command = "echo agent-ran" }),
            () => Text("Changed the greeting."));
        await using var f = ShellFixture.Create(builtInPlugins: AiPluginDir);
        var solution = f.CopyFixture();
        f.Shell.Trust.Trust(Path.GetDirectoryName(Path.GetDirectoryName(solution))!);
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Solution is { Trusted: true }, seconds: 90);
        var settings = f.Shell.Workbench.Settings;
        await settings.SetAsync("ai.openai.baseUrl", server.Http.BaseUrl.TrimEnd('/') + "/v1", SettingScope.User, CancellationToken.None);
        await settings.SetAsync(ShellHost.AiEnabledSetting, true, SettingScope.User, CancellationToken.None);
        await settings.SetAsync(ShellHost.AiProviderSetting, "openai", SettingScope.User, CancellationToken.None);
        await settings.SetAsync(ShellHost.AiModelSetting, "local-coder", SettingScope.User, CancellationToken.None);

        await f.Shell.OnAiChatEventAsync("agent", "change", "true");
        await f.Shell.OnAiChatEventAsync("ctx:file", "change", "false");
        _ = f.Shell.OnAiChatEventAsync("question", "change", "Greet the agent instead.");

        // The edit waits in the refactoring preview until the user applies it.
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen, seconds: 60);
        Assert.Contains("change Program.cs", f.Shell.Dialogs.Request!.Title, StringComparison.Ordinal);
        var program = Path.Combine(Path.GetDirectoryName(solution)!, "src", "App", "Program.cs");
        Assert.Contains("\"fixture\"", File.ReadAllText(program), StringComparison.Ordinal);
        f.Shell.Dialogs.Accept();

        // The command waits for approval; its default is not to run.
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.Request?.Title.Contains("command", StringComparison.Ordinal) == true, seconds: 60);
        Assert.Contains(f.Shell.Dialogs.Buttons, b => b.Label == "Don't Run" && b.IsDefault);
        f.Shell.Dialogs.Buttons.Single(b => b.Label == "Run").Command.Execute(null);

        await ShellFixture.PumpUntil(() => JsonSerializer.Serialize(f.Shell.AiChatRoot()).Contains("Changed the greeting.", StringComparison.Ordinal), seconds: 60);
        Assert.Contains("\"agent\"", File.ReadAllText(program), StringComparison.Ordinal);
        Assert.Equal(3, server.Bodies.Count);
        using var first = JsonDocument.Parse(server.Bodies[0]);
        Assert.Contains(first.RootElement.GetProperty("tools").EnumerateArray(), t => t.GetProperty("function").GetProperty("name").GetString() == "run_command");
        Assert.Contains("The user applied the edit", server.Bodies[1], StringComparison.Ordinal);
        Assert.Contains("agent-ran", server.Bodies[2], StringComparison.Ordinal);
        var chat = JsonSerializer.Serialize(f.Shell.AiChatRoot());
        Assert.Contains("edit_file", chat, StringComparison.Ordinal); // the steps show in the conversation
    }

    private sealed class AllowAll : IAiAgentHost
    {
        public string? Root => null;

        public bool Trusted => false;

        public int Asked { get; private set; }

        public string? OpenText(string path) => null;

        public string Problems(string? path) => string.Empty;

        public Task<bool> ProposeEditAsync(string path, string? oldText, string newText, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> ApproveCommandAsync(string command, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<AiApproval> ApproveToolAsync(string server, string tool, string arguments, CancellationToken cancellationToken)
        {
            Asked++;
            return Task.FromResult(AiApproval.Session);
        }

        public Task<string> BuildAsync(CancellationToken cancellationToken) => Task.FromResult(string.Empty);
    }

    [AvaloniaFact]
    public async Task Codalus_mcp_answers_an_mcp_client_from_the_running_ide()
    {
        await using var f = ShellFixture.Create();
        var shell = Path.Combine(ShellFixture.RepoRoot, "src", "Codalus.Shell", "bin", ShellFixture.Configuration, "net10.0", "codalus.dll");
        var config = new McpServerConfig("codalus", "dotnet", [shell, "mcp", "--user-data-dir", f.UserData], new Dictionary<string, string>());
        await using var servers = new McpServers();
        var host = new AllowAll();

        // The MCP client (agent mode's) against the real `codalus mcp`, which asks this shell over its pipe.
        var tools = await RunOffUiAsync(() => servers.ToolsAsync([config], null, host, CancellationToken.None));
        Assert.Equal(["codalus__get_solution", "codalus__get_problems", "codalus__get_test_results", "codalus__build"], tools.Select(t => t.Definition.Name));
        Assert.Empty(servers.Failures);
        var solution = await RunOffUiAsync(() => tools[0].Invoke([], CancellationToken.None));
        Assert.Equal("No solution is open in Codalus IDE.", solution);
        Assert.Equal("No problems.", await RunOffUiAsync(() => tools[1].Invoke([], CancellationToken.None)));
        Assert.Equal(1, host.Asked); // allowed for the session: asked once
    }

    /// <summary>Runs work on the thread pool while this (the UI) thread keeps pumping, so the shell can answer.</summary>
    private static async Task<T> RunOffUiAsync<T>(Func<Task<T>> work)
    {
        var task = Task.Run(work);
        await ShellFixture.PumpUntil(() => task.IsCompleted, seconds: 120);
        return await task;
    }
}
