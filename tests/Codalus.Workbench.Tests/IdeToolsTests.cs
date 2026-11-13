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
                // Its socket is bound from the start and only listens later, so connections are refused until then and
                // the port stays ours: a probed port freed for an HttpListener could be taken in between, and on Windows
                // the listener then never answered (Windows CI, run 42276868528).
                using var server = Bound();
                var url = $"http://127.0.0.1:{((System.Net.IPEndPoint)server.LocalEndPoint!).Port}/";
                var stopped = false;
                var accepted = 0;
                var serving = Task.Run(async () =>
                {
                    await Task.Delay(1500, ct);
                    server.Listen();
                    try
                    {
                        while (true)
                        {
                            // Every connection gets the page: the browser may open more than one (a preconnect, the favicon).
                            var connection = await server.AcceptAsync(ct);
                            Interlocked.Increment(ref accepted);
                            _ = Task.Run(() => ServeAsync(connection, "<title>Late</title><p>Started late</p>", ct), ct);
                        }
                    }
                    catch (Exception) when (Volatile.Read(ref stopped))
                    {
                        // The server closed below.
                    }
                }, ct);
                try
                {
                    await attached.NavigateAsync(url, ct, retryFor: TimeSpan.FromSeconds(30));
                }
                catch (IdeToolException) when (serving.IsFaulted)
                {
                    await serving; // why the server never answered
                    throw;
                }
                catch (IdeToolException ex)
                {
                    // Who answered the browser: none of this server's connections means another socket did (the diagnostic
                    // for Windows CI's ERR_INVALID_HTTP_RESPONSE, develop's run 43424616628).
                    throw new IdeToolException($"{ex.Message} The test's server accepted {Volatile.Read(ref accepted)} connection(s); it listened after 1.5 s.");
                }

                Assert.Contains("Started late", await attached.GetPageAsync(null, ct), StringComparison.Ordinal);
                Volatile.Write(ref stopped, true);
                server.Dispose();
                await serving;

                // Nothing ever listens: the error says what the browser said, after the wait. A port nothing has listened
                // on, not the served one: the browser's kept-alive connection to a stopped server can still get an empty
                // answer, which loads as an empty page (Ubuntu CI, run 42038288930; 2 in 30 locally). Probed and freed,
                // not held by a socket that never listens: macOS doesn't refuse a connection to one (macOS CI, run 42300832132).
                using var never = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                never.Start();
                var nothing = $"http://127.0.0.1:{((System.Net.IPEndPoint)never.LocalEndpoint).Port}/";
                never.Stop();
                var refused = await Assert.ThrowsAsync<IdeToolException>(() => attached.NavigateAsync(nothing, ct, retryFor: TimeSpan.FromSeconds(1)));
                Assert.True(refused.Message.Contains("ERR_CONNECTION_REFUSED", StringComparison.Ordinal), refused.Message);
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

    /// <summary>
    /// The page navigating on its own while the agent reads it (a debugger opening the app's page in the same tab, a
    /// redirect, a reload): the read waits for the new page instead of failing with "Inspected target navigated or closed"
    /// (Ubuntu CI, run 42288986008: browser_navigate while vscode-js-debug still opened the debugged page).
    /// </summary>
    [Fact]
    public async Task Reading_a_page_that_navigates_on_its_own_waits_for_the_new_page()
    {
        var browser = Browsers.Find();
        Assert.SkipWhen(browser is null && Environment.GetEnvironmentVariable("CI") != "true", "No Chrome, Edge or Chromium is installed.");
        Assert.NotNull(browser);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;

        // A page that keeps moving between two sites (127.0.0.1 and localhost), so the tab changes renderer process
        // on each hop, as a debugger's first navigation to the app does, and most reads overlap a navigation.
        using var server = Bound();
        server.Listen();
        var port = ((System.Net.IPEndPoint)server.LocalEndPoint!).Port;
        var stopped = false;
        var serving = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var connection = await server.AcceptAsync(ct);
                    _ = Task.Run(() => ServeAsync(connection, "<title>Hopping</title><p>Still here</p><script>setTimeout(() => location.href = location.hostname === 'localhost' ? 'http://127.0.0.1:" + port + "/' : 'http://localhost:" + port + "/', 5)</script>", ct), ct);
                }
            }
            catch (Exception) when (Volatile.Read(ref stopped))
            {
                // The server closed below.
            }
        }, ct);

        await using var agent = await AgentBrowser.StartAsync(browser.Path, _profile, "data:text/html,<title>First</title>", headless: true, ct);
        await agent.NavigateAsync($"http://127.0.0.1:{port}/", ct);
        var failures = new List<string>();
        for (var i = 0; i < 200; i++)
        {
            try
            {
                await agent.GetPageAsync(null, ct);
            }
            catch (IdeToolException e)
            {
                failures.Add(e.Message);
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of 200 reads failed: " + string.Join(" | ", failures.Distinct()));

        Volatile.Write(ref stopped, true);
        server.Dispose();
        await serving;
    }

    /// <summary>
    /// A loopback TCP socket bound to a free port and not listening: connections to it are refused. On Windows it holds the
    /// port on every address: another process's socket could otherwise bind the same port on the wildcard address, and while
    /// this one isn't listening yet Windows hands it the connections, so the browser got another server's answer
    /// (net::ERR_INVALID_HTTP_RESPONSE, Windows CI, develop's run 43424616628; inferred, not proven: see the ledger).
    /// </summary>
    private static System.Net.Sockets.Socket Bound()
    {
        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        if (OperatingSystem.IsWindows())
        {
            socket.ExclusiveAddressUse = true;
        }

        socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        return socket;
    }

    /// <summary>Answers one HTTP request on <paramref name="connection"/> with <paramref name="html"/>, then closes it.</summary>
    private static async Task ServeAsync(System.Net.Sockets.Socket connection, string html, CancellationToken cancellationToken)
    {
        using (connection)
        {
            var request = new System.Text.StringBuilder();
            var buffer = new byte[4096];
            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await connection.ReceiveAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return; // a preconnect closed unused
                }

                request.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, read));
            }

            var body = System.Text.Encoding.UTF8.GetBytes(html);
            var head = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await connection.SendAsync(head, cancellationToken);
            await connection.SendAsync(body, cancellationToken);
            connection.Shutdown(System.Net.Sockets.SocketShutdown.Both);
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
