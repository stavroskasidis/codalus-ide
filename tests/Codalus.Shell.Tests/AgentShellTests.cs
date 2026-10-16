using System.Text.Json;
using Avalonia.Headless.XUnit;
using Codalus.Tests;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Mcp;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// M28c in the shell (ADR 0063): agent mode's edit applied through the preview and its command refused by default,
/// and <c>codalus mcp</c> reaching this IDE through the bridge, as an external agent would.
/// </summary>
public sealed class AgentShellTests
{
    private static async Task EnableAsync(ShellFixture f, MockHttpServer server)
    {
        var settings = f.Shell.Workbench.Settings;
        await settings.SetAsync("ai.openai.endpoint", server.BaseUrl + "/v1", SettingScope.User, CancellationToken.None);
        await settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, CancellationToken.None);
        await settings.SetAsync(AiService.ProviderSetting, "openai", SettingScope.User, CancellationToken.None);
        await settings.SetAsync(AiService.ModelSetting, "gpt-test", SettingScope.User, CancellationToken.None);
        await f.Shell.Secrets.StoreAsync("codalus.openai", "apiKey:openai", MockModelServers.Key, CancellationToken.None);
    }

    [AvaloniaFact]
    public async Task The_agent_edits_through_the_preview_and_a_command_is_refused_unless_allowed()
    {
        await using var server = MockModelServers.Script(request =>
        {
            using var body = JsonDocument.Parse(request.Body);
            var last = body.RootElement.GetProperty("messages").EnumerateArray().Last();
            var role = last.GetProperty("role").GetString();
            var content = last.GetProperty("content").GetString() ?? "";
            return role == "tool" ? new MockTurn("Done.")
                : content.Contains("Howdy", StringComparison.Ordinal) ? new MockTurn("Changing the greeting.", "edit_file", """{"path":"src/Greetings/Greeter.cs","old_text":"Hello from","new_text":"Howdy from"}""")
                : new MockTurn("Running the tests.", "run_command", """{"command":"dotnet test"}""");
        });
        await using var f = ShellFixture.Create(builtInPlugins: ShellFixture.PluginBin("Codalus.Plugins.OpenAI"));
        await EnableAsync(f, server);
        var folder = f.CopySample("BuildAndRun");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BuildAndRun.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2, seconds: 90);
        var greeter = Path.Combine(folder, "src", "Greetings", "Greeter.cs");

        // The edit shows in the preview first; Apply changes the file.
        var asking = f.Shell.RunAgentAsync("Make the greeting say Howdy.", [], null, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen && f.Shell.Dialogs.HasDetails, 60);
        Assert.Contains(f.Shell.Dialogs.Details, l => l.Text.Contains("Howdy from", StringComparison.Ordinal));
        f.Shell.Dialogs.Accept();
        await asking;
        var turn = f.Shell.AiChat.Chat.Turns[^1];
        Assert.Null(turn.Error);
        Assert.Equal(("edit_file", "applied"), (turn.Steps[0].Tool, turn.Steps[0].Outcome));
        Assert.Contains("Howdy from", File.ReadAllText(greeter), StringComparison.Ordinal);

        // A command asks first, and Enter refuses it (Reject is the default).
        asking = f.Shell.RunAgentAsync("Run the tests.", [], null, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen && f.Shell.Dialogs.Details.Any(l => l.Text == "dotnet test"), 60);
        f.Shell.Dialogs.Accept();
        await asking;
        turn = f.Shell.AiChat.Chat.Turns[^1];
        Assert.Equal(("run_command", "not allowed", true), (turn.Steps[0].Tool, turn.Steps[0].Outcome, turn.Steps[0].IsError));
        Assert.Contains("tool: run_command dotnet test → not allowed", f.Shell.Workbench.Output.GetOrCreate(AiService.OutputChannelId).GetText(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task An_external_agent_reaches_the_ide_through_codalus_mcp_only_while_it_is_on()
    {
        await using var f = ShellFixture.Create();
        var shellDll = Path.Combine(ShellFixture.RepoRoot, "src", "Codalus.Shell", "bin", ShellFixture.Configuration, Codalus.Tests.RepoBuild.TargetFramework, "codalus.dll");
        var config = new McpServerConfig("dotnet", [shellDll, "mcp", "--user-data-dir", f.UserData], new Dictionary<string, string>());
        Assert.False(f.Shell.IsMcpServerListening);

        await using var client = await McpClient.StartAsync("codalus", config, CancellationToken.None);
        Assert.Equal(McpStdioServer.Tools.Select(t => t.Name), client.Tools.Select(t => t.Name));
        using var empty = JsonDocument.Parse("{}");
        var off = await Assert.ThrowsAsync<McpException>(() => client.CallAsync("get_solution", empty.RootElement, CancellationToken.None));
        Assert.Contains("ai.mcpServer.enabled", off.Message, StringComparison.Ordinal);

        await f.Shell.Workbench.Settings.SetAsync(ShellHost.McpServerSetting, true, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.IsMcpServerListening);
        Assert.Equal("No solution is open.", await client.CallAsync("get_solution", empty.RootElement, CancellationToken.None));
        Assert.Equal("No errors or warnings.", await client.CallAsync("get_diagnostics", empty.RootElement, CancellationToken.None));
        Assert.Contains("MCP server: get_diagnostics", f.Shell.Workbench.Output.GetOrCreate(AiService.OutputChannelId).GetText(), StringComparison.Ordinal);
    }
}
