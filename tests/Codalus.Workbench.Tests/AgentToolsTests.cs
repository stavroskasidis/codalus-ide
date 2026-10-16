using System.Text.Json;
using Codalus.Platform;
using Codalus.Plugins.Protocol;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Agent;
using Codalus.Workbench.Ai.Mcp;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Plugins;

namespace Codalus.Workbench.Tests;

/// <summary>Agent mode's tools (M28c, ADR 0063) against a stand-in host: confinement, exclusions, previews, approvals.</summary>
public sealed class AgentToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-agent-" + Guid.NewGuid().ToString("N"));
    private readonly WorkbenchServices _workbench;
    private readonly FakeHost _host = new();
    private readonly AgentTools _tools;

    public AgentToolsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "obj/\n");
        File.WriteAllText(Path.Combine(_root, "src", "A.cs"), "class A\n{\n    int X() => 1;\n}\n");
        File.WriteAllText(Path.Combine(_root, "src", "B.cs"), "class B { int Y() => 1; int Z() => 1; }\n");
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), "{ \"Password\": \"hunter2\" }\n");
        File.WriteAllText(Path.Combine(_root, ".env"), "TOKEN=abc\n");
        Directory.CreateDirectory(Path.Combine(_root, "obj"));
        File.WriteAllText(Path.Combine(_root, "obj", "gen.cs"), "generated");
        _workbench = new WorkbenchServices(AppPaths.Resolve(PlatformInfo.Current, _ => null, Path.Combine(_root, ".user")), PlatformInfo.Current, watchFiles: false);
        _workbench.Settings.Register(AiSettings.Descriptors(ContributionSource.Shell));
        var models = new LanguageModelService(new PluginManager(_workbench, _ => Codalus.Rpc.ProcessLaunchInfo.FromPath("none"), "test", new Codalus.Rpc.SupervisionPolicy()));
        var ai = new AiService(_workbench.Settings, models, _workbench.Output, () => _root, () => true);
        var external = new ExternalAgentTool(
            new ChatToolDto("weather__forecast", "The forecast.", JsonElement.Parse("""{"type":"object"}""")),
            "weather",
            (_, _) => Task.FromResult("Sunny, password=hunter2"));
        _tools = new AgentTools(ai, _host, _root, [external]);
    }

    public void Dispose()
    {
        _workbench.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private Task<AgentToolResult> Call(string tool, string arguments) =>
        _tools.RunAsync(new ChatToolCallDto("call_1", tool, arguments), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Reading_stays_inside_the_workspace_and_away_from_excluded_files()
    {
        var read = await Call("read_file", """{"path":"src/A.cs"}""");
        Assert.False(read.IsError);
        Assert.StartsWith("1: class A\n2: {\n", read.Text, StringComparison.Ordinal);

        Assert.Contains("outside the workspace", (await Call("read_file", """{"path":"../outside.txt"}""")).Text, StringComparison.Ordinal);
        Assert.Contains("outside the workspace", (await Call("read_file", JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = Path.GetTempPath() }))).Text, StringComparison.Ordinal);
        Assert.Contains("excluded from AI (holds secrets)", (await Call("read_file", """{"path":".env"}""")).Text, StringComparison.Ordinal);
        Assert.Contains("excluded from AI (.gitignore)", (await Call("read_file", """{"path":"obj/gen.cs"}""")).Text, StringComparison.Ordinal);
        Assert.Contains("doesn't exist", (await Call("read_file", """{"path":"src/Missing.cs"}""")).Text, StringComparison.Ordinal);
        Assert.True((await Call("read_file", "{not json")).IsError);
        Assert.True((await Call("no_such_tool", "{}")).IsError);

        // Redacted, and a link that leads out is refused.
        Assert.Contains("[redacted]", (await Call("read_file", """{"path":"appsettings.json"}""")).Text, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "escape"), Path.GetTempPath());
            Assert.Contains("leads outside the workspace", (await Call("read_file", """{"path":"escape/x.txt"}""")).Text, StringComparison.Ordinal);
        }

        var list = await Call("list_files", "{}");
        Assert.Contains("src/", list.Text, StringComparison.Ordinal);
        Assert.Contains("  A.cs", list.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("obj", list.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(".env", list.Text, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            // The link is listed, not followed: only src/'s two files are indented.
            Assert.Contains("escape/ (link)", list.Text, StringComparison.Ordinal);
            Assert.Equal(2, list.Text.Split('\n').Count(l => l.StartsWith("  ", StringComparison.Ordinal)));
        }

        var search = await Call("search_text", """{"pattern":"int \\w\\(\\)","glob":"*.cs"}""");
        Assert.Contains("src/A.cs:3: int X() => 1;", search.Text, StringComparison.Ordinal);
        Assert.Contains("src/B.cs:1:", search.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("gen.cs", search.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changes_go_through_the_preview_and_a_rejection_is_reported()
    {
        _host.Apply = true;
        var edit = await Call("edit_file", """{"path":"src/A.cs","old_text":"int X() => 1;","new_text":"int X() => 2;"}""");
        Assert.False(edit.IsError);
        Assert.Equal((Path.Combine(_root, "src", "A.cs"), "int X() => 1;", "int X() => 2;"), _host.Proposed[^1]);

        Assert.Contains("more than once", (await Call("edit_file", """{"path":"src/B.cs","old_text":"=> 1;","new_text":"=> 2;"}""")).Text, StringComparison.Ordinal);
        Assert.Contains("wasn't found", (await Call("edit_file", """{"path":"src/A.cs","old_text":"nope","new_text":"x"}""")).Text, StringComparison.Ordinal);
        Assert.Contains("excluded", (await Call("edit_file", """{"path":".env","old_text":"TOKEN","new_text":"X"}""")).Text, StringComparison.Ordinal);
        Assert.Single(_host.Proposed);

        _host.Apply = false;
        var rejected = await Call("create_file", """{"path":"src/C.cs","content":"class C {}"}""");
        Assert.True(rejected.IsError);
        Assert.Contains("rejected", rejected.Text, StringComparison.Ordinal);
        Assert.Equal((Path.Combine(_root, "src", "C.cs"), (string?)null, "class C {}"), _host.Proposed[^1]);
        Assert.Contains("exists: use edit_file", (await Call("create_file", """{"path":"src/A.cs","content":"x"}""")).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commands_and_external_tools_ask_first()
    {
        _host.Allow = false;
        var refused = await Call("run_command", """{"command":"echo agent-ran"}""");
        Assert.True(refused.IsError);
        Assert.Equal(["echo agent-ran"], _host.Approvals[^1]);

        _host.Allow = true;
        var ran = await Call("run_command", """{"command":"echo agent-ran","cwd":"src"}""");
        Assert.False(ran.IsError);
        Assert.Contains("Exit code 0.", ran.Text, StringComparison.Ordinal);
        Assert.Contains("agent-ran", ran.Text, StringComparison.Ordinal);
        Assert.Contains("outside the workspace", (await Call("run_command", """{"command":"echo x","cwd":"../.."}""")).Text, StringComparison.Ordinal);

        var forecast = await Call("weather__forecast", """{"city":"Athens"}""");
        Assert.Equal("Sunny, password=[redacted]", forecast.Text);
        Assert.Equal(["""{"city":"Athens"}"""], _host.Approvals[^1]);
        _host.Allow = false;
        Assert.True((await Call("weather__forecast", "{}")).IsError);

        Assert.Contains(_tools.Definitions, d => d.Name == "weather__forecast");
        Assert.Contains(_tools.Definitions, d => d.Name == "run_command");
    }

    [Fact]
    public void Mcp_servers_come_from_an_object_and_tool_names_are_safe()
    {
        using var document = JsonDocument.Parse("""{"files":{"command":"npx","args":["-y","server-files"],"env":{"ROOT":"/tmp"}},"broken":{"args":[]},"other":5}""");
        var servers = McpServers.Parse(document.RootElement);
        var files = Assert.Single(servers);
        Assert.Equal(("files", "npx", "-y server-files", "/tmp"), (files.Key, files.Value.Command, string.Join(' ', files.Value.Args), files.Value.Env["ROOT"]));
        Assert.Empty(McpServers.Parse(null));
        Assert.Equal("my_server__read_file", McpServers.ToolName("my server", "read.file"));
        Assert.Equal(64, McpServers.ToolName(new string('s', 50), new string('t', 50)).Length);
    }

    [Fact]
    public async Task The_mcp_bridge_answers_calls_and_says_when_nobody_listens()
    {
        var pipe = "codalus-test-" + Guid.NewGuid().ToString("N")[..12] + "-mcp";
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<McpException>(() => McpBridge.CallAsync(pipe, "get_solution", default, ct));
        using var bridge = McpBridge.Listen(pipe, (tool, args, _) => tool == "fail"
            ? throw new McpException("No.")
            : Task.FromResult($"{tool}:{(args.ValueKind == JsonValueKind.Object && args.TryGetProperty("path", out var p) ? p.GetString() : "")}"));
        using var arguments = JsonDocument.Parse("""{"path":"a.cs"}""");
        Assert.Equal("get_diagnostics:a.cs", await McpBridge.CallAsync(pipe, "get_diagnostics", arguments.RootElement, ct));
        Assert.Equal("No.", (await Assert.ThrowsAsync<McpException>(() => McpBridge.CallAsync(pipe, "fail", default, ct))).Message);
    }

    [Fact]
    public async Task The_stdio_server_lists_its_tools_and_relays_calls()
    {
        var input = new StringReader(string.Join('\n',
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_solution","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"build","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":5,"method":"resources/list","params":{}}"""));
        var output = new StringWriter();
        var server = new McpStdioServer(input, output, (tool, _, _) => tool == "build" ? throw new McpException("Codalus IDE isn't running.") : Task.FromResult("Solution: Demo.sln"));
        await server.RunAsync(TestContext.Current.CancellationToken);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonElement.Parse(l)).ToList();
        Assert.Equal(5, lines.Count);
        Assert.Equal("codalus", lines[0].GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal(McpStdioServer.Tools.Select(t => t.Name), lines[1].GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        Assert.Equal("Solution: Demo.sln", lines[2].GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.True(lines[3].GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal(-32601, lines[4].GetProperty("error").GetProperty("code").GetInt32());
    }

    private sealed class FakeHost : IAgentHost
    {
        public bool Apply { get; set; }

        public bool Allow { get; set; }

        public List<(string Path, string? Old, string New)> Proposed { get; } = [];

        public List<IReadOnlyList<string>> Approvals { get; } = [];

        public async Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken) =>
            File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;

        public Task<bool> ProposeChangeAsync(string path, string? oldText, string newText, CancellationToken cancellationToken)
        {
            Proposed.Add((path, oldText, newText));
            return Task.FromResult(Apply);
        }

        public Task<bool> ApproveAsync(string title, string message, IReadOnlyList<string> details, CancellationToken cancellationToken)
        {
            Approvals.Add(details);
            return Task.FromResult(Allow);
        }

        public Task<string> DiagnosticsAsync(string? path, CancellationToken cancellationToken) => Task.FromResult("No errors or warnings.");

        public Task<string> BuildOutputAsync(CancellationToken cancellationToken) => Task.FromResult("Build succeeded.");

        public Task<string> BuildAsync(CancellationToken cancellationToken) => Task.FromResult("The build succeeded.");
    }
}
