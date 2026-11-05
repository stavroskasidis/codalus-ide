using System.Text.Json;
using Codalus.Workbench.Ai.Ide;
using Codalus.Workbench.Ai.Mcp;
using Codalus.Workbench.Debugging;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The IDE's tools for agents (ADR 0092) without a shell: the catalog, <c>codalus mcp</c> listing the running IDE's tools
/// (plugins' too) with its instructions and relaying images, and the agent's browser over the DevTools Protocol (needs a
/// Chromium browser: skipped without one locally, failing in CI).
/// </summary>
public sealed class IdeToolsTests : IDisposable
{
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "codalus-agent-browser-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_profile, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Every_tool_has_a_unique_snake_case_name_an_object_schema_and_a_description()
    {
        Assert.Equal(IdeTools.BuiltIn.Count, IdeTools.BuiltIn.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (var tool in IdeTools.BuiltIn)
        {
            Assert.Matches("^[a-z][a-z_]*$", tool.Name);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), tool.Name);
            using var schema = JsonDocument.Parse(tool.Schema);
            Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
            if (schema.RootElement.TryGetProperty("required", out var required))
            {
                var properties = schema.RootElement.GetProperty("properties");
                Assert.All(required.EnumerateArray(), r => Assert.True(properties.TryGetProperty(r.GetString()!, out _), tool.Name + ": " + r));
            }
        }

        // What only reads never asks; what runs the workspace's code or drives the program is an action.
        Assert.All(["find_symbols", "find_references", "call_hierarchy", "get_test_results", "debug_get_variables", "browser_screenshot"], n => Assert.Equal(IdeToolKind.Read, IdeTools.Find(n)!.Kind));
        Assert.All(["build", "run_tests", "run_project", "debug_start", "debug_evaluate", "debug_step", "browser_evaluate"], n => Assert.Equal(IdeToolKind.Action, IdeTools.Find(n)!.Kind));
    }

    [Fact]
    public void The_list_round_trips_through_the_pipe_format()
    {
        var plugin = new IdeToolDefinition("lint_sql", "Lints SQL.", """{"type":"object","properties":{"sql":{"type":"string"}}}""", IdeToolKind.Plugin, "contoso.sql");
        var tools = IdeTools.FromJson(IdeTools.ToJson([.. IdeTools.BuiltIn, plugin]));
        Assert.Equal([.. IdeTools.BuiltIn.Select(t => t.Name), "lint_sql"], tools.Select(t => t.Name));
        Assert.Equal(plugin, tools[^1] with { Schema = plugin.Schema });
        Assert.Equal(IdeToolKind.Action, tools.First(t => t.Name == "build").Kind);
    }

    [Theory]
    [InlineData(null, IdeActionApproval.Allow)]
    [InlineData("allow", IdeActionApproval.Allow)]
    [InlineData("askOncePerSession", IdeActionApproval.AskOncePerSession)]
    [InlineData("ask", IdeActionApproval.Ask)]
    [InlineData("always", IdeActionApproval.Ask)]
    public void The_approval_setting_allows_by_default_and_an_unknown_value_asks(string? value, IdeActionApproval expected) =>
        Assert.Equal(expected, IdeTools.ParseApproval(value));

    [Fact]
    public void The_instructions_steer_agents_to_check_their_changes_and_read_the_state_tools_return()
    {
        Assert.Contains("Check your own changes before you say they are done", IdeTools.Instructions, StringComparison.Ordinal);
        Assert.Contains("debug_start returns once the program runs and a web app listens", IdeTools.Instructions, StringComparison.Ordinal);
        Assert.Contains("the very page being debugged", IdeTools.Instructions, StringComparison.Ordinal);
        Assert.Contains("use browser_* first", IdeTools.Find("debug_wait_for_stop")!.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("net::ERR_CONNECTION_REFUSED", true)]
    [InlineData("net::ERR_CONNECTION_RESET", true)]
    [InlineData("net::ERR_EMPTY_RESPONSE", true)]
    [InlineData("net::ERR_NAME_NOT_RESOLVED", false)]
    [InlineData("net::ERR_CERT_AUTHORITY_INVALID", false)]
    public void A_navigation_that_finds_nothing_listening_is_tried_again(string error, bool again) =>
        Assert.Equal(again, AgentBrowser.IsNotListeningYet(error));

    [Fact]
    public async Task Codalus_mcp_lists_the_ides_tools_says_how_to_use_them_and_relays_images()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipe = McpBridge.SessionPipeName("codalus-test-" + Guid.NewGuid().ToString("N")[..8], McpBridge.NewSessionToken());
        var plugin = new IdeToolDefinition("lint_sql", "Lints SQL.", """{"type":"object","properties":{}}""", IdeToolKind.Plugin, "contoso.sql");
        using var bridge = McpBridge.Listen(pipe, (tool, _, _) => Task.FromResult(tool switch
        {
            McpBridge.ListToolsCall => new IdeToolResult(IdeTools.ToJson([.. IdeTools.BuiltIn, plugin])),
            "browser_screenshot" => new IdeToolResult("A screenshot.") { Images = [new IdeToolImage("iVBORw0KGgo=", "image/png")] },
            "find_symbols" => IdeToolResult.Error("Nothing matches."),
            _ => throw new McpException($"There is no tool '{tool}'."),
        }));

        var input = new StringReader(string.Join('\n',
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"browser_screenshot","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"find_symbols","arguments":{"query":"x"}}}""",
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"nope","arguments":{}}}"""));
        var output = new StringWriter();
        var server = new McpStdioServer(input, output, c => McpBridge.ListToolsAsync(pipe, null, c), (tool, args, c) => McpBridge.CallToolAsync(pipe, tool, args, null, c));
        await server.RunAsync(ct);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonElement.Parse(l)).ToList();
        Assert.Contains("find_references", lines[0].GetProperty("result").GetProperty("instructions").GetString(), StringComparison.Ordinal);
        Assert.Contains("lint_sql", lines[1].GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        var screenshot = lines[2].GetProperty("result").GetProperty("content");
        Assert.Equal(("image", "image/png", "iVBORw0KGgo="), (screenshot[1].GetProperty("type").GetString(), screenshot[1].GetProperty("mimeType").GetString(), screenshot[1].GetProperty("data").GetString()));
        Assert.True(lines[3].GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("Nothing matches.", lines[3].GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.True(lines[4].GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task Without_a_running_ide_the_list_is_the_built_in_one()
    {
        var input = new StringReader("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""");
        var output = new StringWriter();
        var server = new McpStdioServer(input, output, _ => throw new McpException("Codalus IDE isn't running."), (_, _, _) => throw new McpException("Codalus IDE isn't running."));
        await server.RunAsync(TestContext.Current.CancellationToken);
        var tools = JsonElement.Parse(output.ToString()).GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString());
        Assert.Equal(IdeTools.BuiltIn.Select(t => t.Name), tools);
    }

    [Fact]
    public async Task The_agents_browser_reads_the_page_runs_scripts_keeps_the_console_and_takes_screenshots()
    {
        var browser = Browsers.Find();
        Assert.SkipWhen(browser is null && Environment.GetEnvironmentVariable("CI") != "true", "No Chrome, Edge or Chromium is installed.");
        Assert.NotNull(browser);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;

        const string page = "<title>Orders</title><h1>Orders</h1><button id=add onclick=\"document.querySelector('h1').textContent='Added'; console.log('added', 1)\">Add</button><script>console.warn('loaded'); setTimeout(() => { throw new Error('late failure'); }, 0)</script>";
        await using var agent = await AgentBrowser.StartAsync(browser.Path, _profile, "data:text/html," + Uri.EscapeDataString(page), headless: true, ct);

        var text = await agent.GetPageAsync(null, ct);
        Assert.Contains("Orders", text, StringComparison.Ordinal);
        Assert.Contains("<button id=\"add\"", await agent.GetPageAsync("#add", ct), StringComparison.Ordinal);
        Assert.Equal("Nothing matches the selector.", await agent.GetPageAsync(".missing", ct));

        Assert.Equal("\"Added\"", await agent.EvaluateAsync("(async () => { document.getElementById('add').click(); await new Promise(r => setTimeout(r, 10)); return document.querySelector('h1').textContent; })()", ct));
        Assert.Equal("3", await agent.EvaluateAsync("1 + 2", ct));
        Assert.Contains("boom", (await Assert.ThrowsAsync<IdeToolException>(() => agent.EvaluateAsync("(() => { throw new Error('boom'); })()", ct))).Message, StringComparison.Ordinal);

        await AssertEventuallyAsync(() => agent.Console(100) is var lines && lines.Contains("warning: loaded") && lines.Contains("log: added 1") && lines.Any(l => l.StartsWith("uncaught: Error: late failure", StringComparison.Ordinal)), ct);

        var png = Convert.FromBase64String(await agent.ScreenshotAsync(ct));
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
        Assert.True(agent.IsRunning);
    }

    [Fact]
    public async Task The_agent_attaches_to_a_browser_it_did_not_start_and_waits_for_a_server_that_starts_late()
    {
        var browser = Browsers.Find();
        Assert.SkipWhen(browser is null && Environment.GetEnvironmentVariable("CI") != "true", "No Chrome, Edge or Chromium is installed.");
        Assert.NotNull(browser);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;

        // Someone else's browser (the page the IDE debugs): a profile, a DevTools port, and a page.
        var start = new System.Diagnostics.ProcessStartInfo(browser.Path) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "--user-data-dir=" + _profile, "--remote-debugging-port=0", "--headless=new", "--no-first-run", "--no-default-browser-check", "data:text/html,<title>Debugged</title>" })
        {
            start.ArgumentList.Add(argument);
        }

        if (OperatingSystem.IsLinux() && Environment.UserName == "root")
        {
            start.ArgumentList.Add("--no-sandbox");
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await using (var attached = await AgentBrowser.AttachAsync(_profile, null, TimeSpan.FromSeconds(30), ct))
            {
                Assert.True(attached.IsAttached);
                await AssertEventuallyAsync(() => attached.GetPageAsync(null, ct).Result.Contains("Debugged", StringComparison.Ordinal), ct);

                // A server that isn't listening yet (a web app starting): the navigation is tried again until it answers.
                using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                probe.Start();
                var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                var url = $"http://127.0.0.1:{port}/";
                using var server = new System.Net.HttpListener();
                server.Prefixes.Add(url);
                var serving = Task.Run(async () =>
                {
                    await Task.Delay(1500, ct);
                    server.Start();
                    var context = await server.GetContextAsync();
                    var body = "<title>Late</title><p>Started late</p>"u8.ToArray();
                    context.Response.ContentType = "text/html";
                    await context.Response.OutputStream.WriteAsync(body, ct);
                    context.Response.Close();
                }, ct);
                await attached.NavigateAsync(url, ct, retryFor: TimeSpan.FromSeconds(30));
                Assert.Contains("Started late", await attached.GetPageAsync(null, ct), StringComparison.Ordinal);
                await serving;

                // Nothing ever listens: the error says what the browser said, after the wait.
                server.Stop();
                var refused = await Assert.ThrowsAsync<IdeToolException>(() => attached.NavigateAsync(url, ct, retryFor: TimeSpan.FromSeconds(1)));
                Assert.Contains("ERR_CONNECTION_REFUSED", refused.Message, StringComparison.Ordinal);
            }

            // Letting go leaves the browser running: it's the debugger's.
            await Task.Delay(500, ct);
            Assert.False(process.HasExited);
        }
        finally
        {
            Codalus.Platform.ProcessTree.Kill(process);
        }
    }

    private static async Task AssertEventuallyAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(50, cancellationToken);
        }
    }
}
