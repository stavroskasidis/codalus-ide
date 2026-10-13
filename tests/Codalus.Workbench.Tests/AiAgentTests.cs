using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Codalus.Plugins.Protocol;
using Codalus.Workbench.Ai;

namespace Codalus.Workbench.Tests;

/// <summary>Agent mode's loop and tools, and MCP both ways (M28d, ADR 0063), without a model or a window.</summary>
public sealed class AiAgentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-agent-" + Guid.NewGuid().ToString("N"));

    public AiAgentTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        File.WriteAllText(Path.Combine(_root, "src", "Greeter.cs"), "class Greeter\n{\n    string Hi() => \"hi\";\n}\n");
        File.WriteAllText(Path.Combine(_root, "bin", "Greeter.cs"), "class Greeter { } // a copy in bin\n");
        File.WriteAllText(Path.Combine(_root, ".env"), "TOKEN=Greeter-secret\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeHost(string? root, bool trusted) : IAiAgentHost
    {
        public string? Root { get; } = root;

        public bool Trusted { get; } = trusted;

        public bool ApplyEdits { get; set; } = true;

        public bool AllowCommands { get; set; } = true;

        public List<(string Path, string? Old, string New)> Edits { get; } = [];

        public List<string> Commands { get; } = [];

        public List<AiApproval> Approvals { get; } = [];

        public int ToolApprovalsAsked { get; private set; }

        public string? OpenText(string path) => null;

        public string Problems(string? path) => path is null ? "Greeter.cs(3,5): error CS0001 everything" : string.Empty;

        public Task<bool> ProposeEditAsync(string path, string? oldText, string newText, CancellationToken cancellationToken)
        {
            Edits.Add((path, oldText, newText));
            if (ApplyEdits)
            {
                var text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
                File.WriteAllText(path, oldText is null ? newText : text.Replace(oldText, newText, StringComparison.Ordinal));
            }

            return Task.FromResult(ApplyEdits);
        }

        public Task<bool> ApproveCommandAsync(string command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(AllowCommands);
        }

        public Task<AiApproval> ApproveToolAsync(string server, string tool, string arguments, CancellationToken cancellationToken)
        {
            ToolApprovalsAsked++;
            var answer = Approvals.Count > 0 ? Approvals[0] : AiApproval.Deny;
            if (Approvals.Count > 0)
            {
                Approvals.RemoveAt(0);
            }

            return Task.FromResult(answer);
        }

        public Task<string> BuildAsync(CancellationToken cancellationToken) => Task.FromResult("The build succeeded.");
    }

    private AiAgentTools Tools(FakeHost host) => new(host, new AiContext(_root, (_, _) => Task.FromResult(false)));

    private static Task<string> Call(IReadOnlyList<AiAgentTool> tools, string name, string arguments) =>
        tools.Single(t => t.Definition.Name == name).Invoke((JsonObject)JsonNode.Parse(arguments)!, CancellationToken.None);

    [Fact]
    public async Task The_loop_runs_tool_calls_and_sends_their_results_back_until_an_answer_has_none()
    {
        var host = new FakeHost(_root, trusted: true);
        var requests = new List<IReadOnlyList<AiMessageDto>>();
        AiAgent.Chat chat = (messages, tools, onEvent, _) =>
        {
            requests.Add([.. messages]);
            Assert.Contains(tools, t => t.Name == "edit_file");
            if (requests.Count == 1)
            {
                onEvent(new AiEventParams("s", 0, "Let me look.", null));
                onEvent(new AiEventParams("s", 1, null, new AiToolCallDto("call_1", "read_file", """{"path":"src/Greeter.cs"}""")));
            }
            else if (requests.Count == 2)
            {
                onEvent(new AiEventParams("s", 0, null, new AiToolCallDto("call_2", "edit_file", """{"path":"src/Greeter.cs","old_text":"\"hi\"","new_text":"\"hello\""}""")));
            }
            else
            {
                onEvent(new AiEventParams("s", 0, "Changed the greeting.", null));
            }

            return Task.CompletedTask;
        };
        var progress = new List<AiAgentProgress>();
        var conversation = new List<AiMessageDto> { new("system", AiAgent.SystemPrompt), new("user", "Say hello instead.") };

        var answer = await new AiAgent(chat, Tools(host).Create()).RunAsync(conversation, progress.Add, CancellationToken.None);

        Assert.Equal("Changed the greeting.", answer);
        Assert.Equal(3, requests.Count);
        var read = Assert.Single(requests[1], m => m.Role == "tool");
        Assert.Equal("call_1", read.ToolCallId);
        Assert.Contains("3:     string Hi() => \"hi\";", read.Content, StringComparison.Ordinal);
        Assert.Equal("read_file", Assert.Single(requests[1], m => m.ToolCalls is not null).ToolCalls![0].Name);
        Assert.Contains("The user applied the edit", requests[2][^1].Content, StringComparison.Ordinal);
        Assert.Contains("\"hello\"", File.ReadAllText(Path.Combine(_root, "src", "Greeter.cs")), StringComparison.Ordinal);
        Assert.Contains(progress, p => p is AiAgentProgress.Calling { Tool: "edit_file" });
    }

    [Fact]
    public async Task The_loop_stops_after_its_steps()
    {
        var steps = 0;
        AiAgent.Chat chat = (_, _, onEvent, _) =>
        {
            onEvent(new AiEventParams("s", 0, null, new AiToolCallDto("c" + steps++, "list_files", "{}")));
            return Task.CompletedTask;
        };

        var answer = await new AiAgent(chat, Tools(new FakeHost(_root, trusted: true)).Create()).RunAsync([new("user", "loop")], _ => { }, CancellationToken.None);

        Assert.Equal(AiAgent.MaxSteps, steps);
        Assert.StartsWith("Stopped after", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reading_listing_and_searching_keep_to_the_workspace_and_leave_secrets_out()
    {
        var tools = Tools(new FakeHost(_root, trusted: false)).Create();

        Assert.DoesNotContain(tools, t => t.Definition.Name is "edit_file" or "run_command" or "build"); // untrusted: read-only
        var list = await Call(tools, "list_files", """{"recursive":true}""");
        Assert.Contains("src/Greeter.cs", list, StringComparison.Ordinal);
        Assert.DoesNotContain("bin", list, StringComparison.Ordinal);
        Assert.DoesNotContain(".env", list, StringComparison.Ordinal);

        Assert.Contains("secret", await Call(tools, "read_file", """{"path":".env"}"""), StringComparison.Ordinal);
        Assert.Contains("outside the workspace", await Call(tools, "read_file", """{"path":"../elsewhere.txt"}"""), StringComparison.Ordinal);
        Assert.Equal("2: {\n", await Call(tools, "read_file", """{"path":"src/Greeter.cs","start_line":2,"end_line":2}"""));

        var search = await Call(tools, "search_text", """{"query":"Greeter"}""");
        Assert.Equal("src/Greeter.cs:1: class Greeter", search);
        Assert.Contains("error CS0001", await Call(tools, "get_problems", "{}"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edits_quote_exact_text_and_the_users_choice_is_the_result()
    {
        var host = new FakeHost(_root, trusted: true) { ApplyEdits = false };
        var tools = Tools(host).Create();

        Assert.Contains("wasn't found", await Call(tools, "edit_file", """{"path":"src/Greeter.cs","old_text":"nope","new_text":"x"}"""), StringComparison.Ordinal);
        Assert.Contains("more than once", await Call(tools, "edit_file", """{"path":"src/Greeter.cs","old_text":"e","new_text":"x"}"""), StringComparison.Ordinal);
        Assert.Empty(host.Edits); // nothing was proposed

        Assert.Contains("rejected", await Call(tools, "edit_file", """{"path":"src/Greeter.cs","old_text":"Hi()","new_text":"Hello()"}"""), StringComparison.Ordinal);
        Assert.Equal("Hi()", Assert.Single(host.Edits).Old);
        Assert.Contains("secret", await Call(tools, "edit_file", """{"path":".env","new_text":"x"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commands_run_only_once_the_user_approves_them()
    {
        var host = new FakeHost(_root, trusted: true) { AllowCommands = false };
        var tools = Tools(host).Create();

        Assert.Contains("didn't allow", await Call(tools, "run_command", """{"command":"echo agent-ran"}"""), StringComparison.Ordinal);
        host.AllowCommands = true;
        var result = await Call(tools, "run_command", """{"command":"echo agent-ran"}""");
        Assert.Contains("agent-ran", result, StringComparison.Ordinal);
        Assert.Contains("Exit code 0.", result, StringComparison.Ordinal);
        Assert.Equal(["echo agent-ran", "echo agent-ran"], host.Commands);
    }

    [Fact]
    public void Long_output_keeps_its_start_and_end()
    {
        var text = new string('a', 3000) + new string('b', 10_000) + "THE END";
        var cut = AiAgentTools.Cut(text);
        Assert.True(cut.Length < 8200);
        Assert.StartsWith("aaa", cut, StringComparison.Ordinal);
        Assert.EndsWith("THE END", cut, StringComparison.Ordinal);
    }

    [Fact]
    public void Mcp_servers_are_read_from_the_setting_as_other_clients_write_them()
    {
        using var json = System.Text.Json.JsonDocument.Parse("""
            { "files": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "."], "env": { "A": "1" } },
              "broken": { "args": [] } }
            """);
        var server = Assert.Single(McpServerConfig.Parse(json.RootElement));
        Assert.Equal("files", server.Name);
        Assert.Equal("npx", server.Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "."], server.Arguments);
        Assert.Equal("1", server.Environment["A"]);
        Assert.Equal("my_server_x", McpServers.ToolName("my server.x"));
    }

    [Fact]
    public async Task The_mcp_client_lists_and_calls_the_tools_of_an_mcp_server()
    {
        // Client and server in this process, over two pipes.
        using var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var serverIn = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        using var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
        using var clientIn = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);
        var serverReader = new StreamReader(serverIn, Encoding.UTF8);
        var serverWriter = new StreamWriter(toClient, new UTF8Encoding(false));
        var calls = new List<string>();
        var server = McpServer.RunAsync(serverReader, serverWriter,
        [
            new McpServerTool("echo", "Says it back.", """{"type":"object","properties":{"text":{"type":"string"}}}""", (args, _) =>
            {
                calls.Add(args.ToJsonString());
                return Task.FromResult("You said " + args["text"]?.GetValue<string>());
            }),
            new McpServerTool("fail", "Fails.", """{"type":"object"}""", (_, _) => throw new InvalidOperationException("it broke")),
        ], "1.2.3", CancellationToken.None);

        var client = await McpClient.ConnectAsync(new McpServerConfig("test", "in-process", [], new Dictionary<string, string>()),
            new StreamReader(clientIn, Encoding.UTF8), new StreamWriter(toServer, new UTF8Encoding(false)), CancellationToken.None);
        await using (client)
        {
            var tools = await client.ListToolsAsync(CancellationToken.None);
            Assert.Equal(["echo", "fail"], tools.Select(t => t.Name));
            Assert.Contains("\"text\"", tools[0].InputSchema, StringComparison.Ordinal);
            Assert.Equal("You said hi", await client.CallToolAsync("echo", new JsonObject { ["text"] = "hi" }, CancellationToken.None));
            Assert.Equal("The tool reported an error: it broke", await client.CallToolAsync("fail", [], CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.CallToolAsync("missing", [], CancellationToken.None));
        }

        await server.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); // the client closed its side: the server ends
        Assert.Equal("""{"text":"hi"}""", Assert.Single(calls));
    }

    [Fact]
    public async Task The_ide_tools_of_codalus_mcp_say_when_no_ide_answers()
    {
        var asked = new List<(string, string)>();
        var tools = McpServer.IdeTools((tool, args, _) =>
        {
            asked.Add((tool, args));
            return Task.FromResult<string?>(tool == "build" ? "The build succeeded." : null);
        });

        Assert.Equal(["get_solution", "get_problems", "get_test_results", "build"], tools.Select(t => t.Name));
        Assert.Equal("The build succeeded.", await tools[3].Invoke([], CancellationToken.None));
        Assert.Contains("isn't running", await tools[1].Invoke(new JsonObject { ["path"] = "/a.cs" }, CancellationToken.None), StringComparison.Ordinal);
        Assert.Equal(("get_problems", """{"path":"/a.cs"}"""), asked[1]);
    }
}
