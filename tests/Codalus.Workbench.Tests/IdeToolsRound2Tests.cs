using System.Text.Json;
using Codalus.Workbench.Ai.Ide;
using Codalus.Workbench.Ai.Mcp;
using Xunit;

namespace Codalus.Workbench.Tests;

/// <summary>M60: round 2's catalog, progress for long calls, and the setups to copy for agents outside the IDE.</summary>
public sealed class IdeToolsRound2Tests
{
    [Fact]
    public void Round_two_follows_round_one_and_every_name_is_unique()
    {
        var names = IdeTools.BuiltIn.Select(t => t.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("get_solution", names[0]);
        Assert.All(IdeToolsRound2.All, tool => Assert.Contains(tool.Name, names));
        Assert.All(IdeToolsRound2.All, tool => Assert.NotEqual(IdeToolKind.Plugin, tool.Kind));
        foreach (var tool in IdeTools.BuiltIn)
        {
            using var schema = JsonDocument.Parse(tool.Schema);
            Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
        }
    }

    [Fact]
    public void The_desktop_apps_windows_and_controls_are_read_and_its_input_is_an_action()
    {
        // M60b (D52): reading the app changes nothing; input is an action under ai.agent.ideActions, no approval of its own.
        Assert.Equal(
            [("desktop_app_windows", IdeToolKind.Read), ("desktop_app_controls", IdeToolKind.Read), ("desktop_app_input", IdeToolKind.Action)],
            IdeDesktopAppTools.Tools.Select(t => (t.Name, t.Kind)));
        Assert.All(IdeDesktopAppTools.Tools, tool => Assert.Contains(tool, IdeToolsRound2.All));
        using var input = JsonDocument.Parse(IdeTools.Find("desktop_app_input")!.Schema);
        Assert.Equal(["click", "text", "keys", "scroll"], input.RootElement.GetProperty("properties").GetProperty("kind").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));

        // An agent's own run can keep the app's windows out of the user's way, and the instructions say how.
        foreach (var name in new[] { "run_project", "debug_start" })
        {
            using var schema = JsonDocument.Parse(IdeTools.Find(name)!.Schema);
            Assert.Equal("boolean", schema.RootElement.GetProperty("properties").GetProperty("offScreen").GetProperty("type").GetString());
        }

        Assert.Contains("desktop_app_input", IdeTools.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_long_call_reports_progress_while_it_runs_when_the_client_sent_a_token()
    {
        var input = new StringReader(string.Join('\n',
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"build","arguments":{},"_meta":{"progressToken":"p1"}}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"build","arguments":{}}}"""));
        var output = new StringWriter();
        var server = new McpStdioServer(input, output, _ => Task.FromResult(IdeTools.BuiltIn), async (tool, _, ct) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
            return new IdeToolResult("Build succeeded.");
        })
        {
            ProgressInterval = TimeSpan.FromMilliseconds(50),
        };
        await server.RunAsync(TestContext.Current.CancellationToken);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonElement.Parse(l)).ToList();
        var progress = lines.TakeWhile(l => !l.TryGetProperty("id", out _)).ToList();
        Assert.NotEmpty(progress);
        Assert.All(progress, p =>
        {
            Assert.Equal("notifications/progress", p.GetProperty("method").GetString());
            Assert.Equal("p1", p.GetProperty("params").GetProperty("progressToken").GetString());
            Assert.StartsWith("build is still running", p.GetProperty("params").GetProperty("message").GetString(), StringComparison.Ordinal);
        });
        var answers = lines.Where(l => l.TryGetProperty("id", out _)).ToList();
        Assert.Equal([1, 2], answers.Select(a => a.GetProperty("id").GetInt32()));
        Assert.Equal("Build succeeded.", answers[0].GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());

        // Without a token, nothing but the answer.
        var afterFirst = lines.SkipWhile(l => !l.TryGetProperty("id", out var id) || id.GetInt32() != 1).Skip(1).ToList();
        Assert.Single(afterFirst);
    }

    [Fact]
    public void Each_known_agent_gets_a_setup_with_this_installs_path()
    {
        var program = Path.Combine(Path.GetTempPath(), "Codalus IDE", "codalus");
        var setups = McpSetups.For(program, null, null);
        Assert.Equal(["Claude Code", "Codex", "Gemini CLI", "GitHub Copilot CLI", ".mcp.json"], setups.Select(s => s.Agent));
        Assert.Equal($"claude mcp add codalus -- \"{Path.GetFullPath(program)}\" mcp", setups[0].Text);
        Assert.StartsWith("codex mcp add codalus -- ", setups[1].Text, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(setups[^1].Text);
        var server = json.RootElement.GetProperty("mcpServers").GetProperty("codalus");
        Assert.Equal(Path.GetFullPath(program), server.GetProperty("command").GetString());
        Assert.Equal(["mcp"], server.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        using var copilot = JsonDocument.Parse(setups[3].Text);
        Assert.Equal("local", copilot.RootElement.GetProperty("mcpServers").GetProperty("codalus").GetProperty("type").GetString());

        // A development build run through dotnet, with its own user data.
        var (command, arguments) = McpSetups.Command("/usr/bin/dotnet", "/src/codalus.dll", "/data");
        Assert.Equal("/usr/bin/dotnet", command.Replace('\\', '/').Split(':')[^1]);
        Assert.Equal(["mcp", "--user-data-dir"], arguments.Skip(1).Take(2));
        Assert.EndsWith("codalus.dll", arguments[0], StringComparison.Ordinal);
    }
}
