using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Acp;
using Codalus.Workbench.Ai.Mcp;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// External agents in AI Chat (M48, ADR 0079), with the stand-in ACP agent (tests/Codalus.AcpAgent.Fake): the picker and
/// the label, why an agent can't run, the turn's thought, plan and steps, a location that opens its file, the write
/// preview into the editor's buffer, permissions and commands with Reject the default, Stop closing a pending approval,
/// and <c>codalus mcp</c> reaching this IDE through the session's own pipe.
/// </summary>
public sealed class AcpShellTests
{
    private const string Label = "External agent: uses its own provider and data policy. Codalus checks what it asks the IDE for.";

    private static string StandInAgent => Path.Combine(ShellFixture.RepoRoot, "tests", "Codalus.AcpAgent.Fake", "bin", ShellFixture.Configuration, Codalus.Tests.RepoBuild.TargetFramework, "codalus-fake-acp-agent.dll");

    private static string ShellDll => Path.Combine(ShellFixture.RepoRoot, "src", "Codalus.Shell", "bin", ShellFixture.Configuration, Codalus.Tests.RepoBuild.TargetFramework, "codalus.dll");

    /// <summary>The dotnet that runs the tests' hosts, by its absolute path.</summary>
    private static string Dotnet =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host
        : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")).First(File.Exists);

    private static async Task<(AiChatView View, string Log)> SetUpAsync(ShellFixture f)
    {
        var log = Path.Combine(f.UserData, "agent-log.jsonl");
        var settings = f.Shell.Workbench.Settings;
        await settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, CancellationToken.None);
        var agents = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["stand-in"] = new { command = "dotnet", args = new[] { StandInAgent }, env = new Dictionary<string, string> { ["ACP_FAKE_LOG"] = log } } });
        await settings.SetAsync(AcpAgents.AgentsSetting, agents, SettingScope.User, CancellationToken.None);
        f.Shell.McpExecutable = (Dotnet, ShellDll);
        f.Shell.Run("ai.chat.show");
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<AiChatView>().Any());
        return (f.Window.GetVisualDescendants().OfType<AiChatView>().First(), log);
    }

    private static async Task<AiChatTurn> AskAsync(ShellFixture f, string question)
    {
        var chat = f.Shell.AiChat;
        var count = chat.Chat.Turns.Count;
        chat.Input = question;
        _ = chat.SendAsync();
        await ShellFixture.PumpUntil(() => chat.Chat.Turns.Count > count, 30);
        var turn = chat.Chat.Turns[^1];
        await ShellFixture.PumpUntil(() => !turn.IsStreaming || f.Shell.Dialogs.IsOpen, 60);
        return turn;
    }

    private static T Visible<T>(Control root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    /// <summary>
    /// The chat view the window shows now. Opening the solution brings Solution Explorer to the front of the same group
    /// (later when the load is slow), which takes AI Chat out of the visual tree; this brings it back, as clicking its tab
    /// does, and the dock builds a new view for it.
    /// </summary>
    private static AiChatView ChatView(ShellFixture f)
    {
        if (!f.Window.GetVisualDescendants().OfType<AiChatView>().Any(v => v.IsEffectivelyVisible))
        {
            f.Shell.Layout.ShowToolWindow(AiChatViewModel.ToolWindowId, focus: false);
        }

        return f.Window.GetVisualDescendants().OfType<AiChatView>().LastOrDefault(v => v.IsEffectivelyVisible) ?? new AiChatView();
    }

    /// <summary>The step view whose step matches, in the chat the window shows (null: none yet).</summary>
    private static AiStepView? Step(ShellFixture f, Func<AiStepItem, bool> match) =>
        ChatView(f).GetVisualDescendants().OfType<AiStepView>().FirstOrDefault(v => v.DataContext is AiStepItem step && match(step));

    private static string Describe(ShellFixture f, AiChatTurn turn) =>
        $"thought '{turn.Thought}', answer '{turn.Answer}', streaming {turn.IsStreaming}, {f.Window.GetVisualDescendants().OfType<AiChatView>().Count()} chat view(s), tool windows [{string.Join(", ", f.Shell.Layout.OpenToolWindows.Select(t => t.Id + (t.Owner is Dock.Model.Core.IDock d && d.ActiveDockable == t ? "*" : "")))}]";

    [AvaloniaFact]
    public async Task The_picker_and_the_label_show_and_an_agents_turn_renders_with_its_steps()
    {
        await using var f = ShellFixture.Create();
        var (view, log) = await SetUpAsync(f);
        var chat = f.Shell.AiChat;

        // The picker, after the provider: Built-in, then the configured agent. Choosing it from its menu shows the label
        // in the composer and hides the provider.
        var picker = Visible<DropDownButton>(view, "AgentPicker");
        await ShellFixture.PumpUntil(() => picker.IsEffectivelyVisible);
        Assert.Equal(["Built-in", "stand-in"], chat.AgentChoices);
        Assert.Equal("Built-in", (picker.Content as TextBlock)?.Text);
        Assert.False(Visible<Border>(view, "ExternalAgentBanner").IsEffectivelyVisible);
        var menu = (MenuFlyout)picker.Flyout!;
        menu.ShowAt(picker);
        await ShellFixture.PumpUntil(() => menu.Items.Count == 2);
        Assert.Equal([true, false], menu.Items.OfType<MenuItem>().Select(i => i.IsChecked));
        menu.Items.OfType<MenuItem>().Single(i => (i.Header as string) == "stand-in").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Hide();
        await ShellFixture.PumpUntil(() => chat.IsExternal);
        Assert.Equal("stand-in", chat.SelectedAgent);
        await ShellFixture.PumpUntil(() => (picker.Content as TextBlock)?.Text == "stand-in");
        await ShellFixture.PumpUntil(() => Visible<Border>(view, "ExternalAgentBanner").IsEffectivelyVisible);
        Assert.Equal(Label, Visible<TextBlock>(view, "ExternalAgentLabel").Text);
        Assert.False(Visible<Button>(view, "Provider").IsEffectivelyVisible);

        // Without a workspace, the chat says why it can't run, and nothing starts.
        Assert.Contains("open one first", chat.AgentBlockedMessage, StringComparison.Ordinal);
        Assert.True(Visible<TextBlock>(view, "AgentBlocked").IsEffectivelyVisible);
        chat.Input = "hello";
        Assert.False(chat.CanSend);

        var folder = f.CopySample("BuildAndRun");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BuildAndRun.slnx"));
        await ShellFixture.PumpUntil(() => !chat.IsAgentBlocked && chat.CanSend, 60);

        // A turn: the thought collapsed, the plan, the answer, and the label still there.
        var turn = await AskAsync(f, "hello");
        await ShellFixture.PumpUntil(() => !turn.IsStreaming);
        Assert.Equal(("Hello from the stand-in.", "stand-in"), (turn.Answer, turn.ExternalAgent));
        await ShellFixture.PumpUntil(() => ChatView(f).GetVisualDescendants().OfType<Expander>().Any(e => (e.Header as string) == "Thinking"), describe: () => Describe(f, turn));
        view = ChatView(f);
        Assert.False(view.GetVisualDescendants().OfType<Expander>().Single(e => (e.Header as string) == "Thinking").IsExpanded);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "☑ Say hello");
        // The agent answers under its own name, with a chip that says it is an external agent.
        Assert.Equal("stand-in", f.Shell.AiChat.Turns[^1].Author);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "stand-in" && t.FontWeight == Avalonia.Media.FontWeight.SemiBold);
        Assert.Contains(view.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("chip") && b.Child is TextBlock { Text: "External agent" });

        // The session got the workspace and codalus mcp by its absolute path, with this session's pipe.
        var created = File.ReadAllLines(log).Select(l => JsonElement.Parse(l)).Single(m => m.TryGetProperty("method", out var method) && method.GetString() == "session/new").GetProperty("params");
        Assert.Equal(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar), created.GetProperty("cwd").GetString());
        var mcp = created.GetProperty("mcpServers")[0];
        var command = mcp.GetProperty("command").GetString()!;
        var args = mcp.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToList();
        Assert.True(Path.IsPathRooted(command));
        Assert.Equal([ShellDll, "mcp", "--user-data-dir", Path.GetFullPath(f.UserData), "--agent-session"], args.Take(5));
        Assert.Equal(1, f.Shell.OpenAgentMcpPipes);

        // codalus mcp reaches this IDE through the session's pipe, though ai.mcpServer.enabled is off.
        Assert.False(f.Shell.IsMcpServerListening);
        await using (var client = await McpClient.StartAsync("codalus", new McpServerConfig(command, args, new Dictionary<string, string>()), CancellationToken.None))
        {
            using var empty = JsonDocument.Parse("{}");
            var solution = await client.CallAsync("get_solution", empty.RootElement, CancellationToken.None);
            Assert.Contains("BuildAndRun.slnx", solution, StringComparison.Ordinal);
        }

        Assert.Contains("MCP server (external agent stand-in): get_solution", f.Shell.Workbench.Output.GetOrCreate(AiService.OutputChannelId).GetText(), StringComparison.Ordinal);

        // A read step with its location: clicking it opens the file at the line.
        turn = await AskAsync(f, "read src/Greetings/Greeter.cs");
        await ShellFixture.PumpUntil(() => !turn.IsStreaming);
        await ShellFixture.PumpUntil(() => Step(f, s => s is { Kind: "read", Title: "Read src/Greetings/Greeter.cs", Status: AiStepStatus.Completed }) is not null, describe: () => Describe(f, turn));
        var link = Step(f, s => s.Title == "Read src/Greetings/Greeter.cs")!.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Classes.Contains("link") && AutomationProperties.GetName(b) == "src/Greetings/Greeter.cs");
        Assert.Equal("Greeter.cs:2", (link.Content as TextBlock)?.Text);
        link.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var greeter = Path.Combine(folder, "src", "Greetings", "Greeter.cs");
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == greeter, 30);

        // A command asks first with its exact command line; Enter refuses it (Reject is the default).
        turn = await AskAsync(f, "run echo from-the-agent");
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen && f.Shell.Dialogs.Details.Any(d => d.Text == "echo from-the-agent"), 30);
        Assert.Contains("stand-in", f.Shell.Dialogs.Message, StringComparison.Ordinal);
        Assert.Equal(("terminal", "stand-in · external agent · the workspace"), (f.Shell.Dialogs.Icon, f.Shell.Dialogs.Context));
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => !turn.IsStreaming, 30);
        Assert.StartsWith("Run failed: The user didn't allow the command.", turn.Answer, StringComparison.Ordinal);

        // A permission: Allow Once, Reject and Always Reject, never always allow; Enter rejects. macOS puts them in
        // the reverse order (the primary last).
        turn = await AskAsync(f, "permission");
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen, 30);
        string[] options = ["Allow Once", "Reject", "Always Reject"];
        Assert.Equal(f.Shell.Dialogs.PrimaryLast ? options.AsEnumerable().Reverse() : options, f.Shell.Dialogs.Buttons.Select(b => b.Label));
        Assert.Equal("Reject", f.Shell.Dialogs.Buttons.Single(b => b.IsDefault).Label);
        Assert.Equal("shield", f.Shell.Dialogs.Icon);
        Assert.Contains(f.Shell.Dialogs.Details, d => d.Text == "+ new line" && d.IsAdded);
        Assert.Contains(f.Shell.Dialogs.Details, d => d.Text.Contains("\"path\": \"bin/\"", StringComparison.Ordinal));
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => !turn.IsStreaming, 30);
        Assert.Equal("Permission: selected reject", turn.Answer);

        // A write shows in the preview; Apply puts it in the editor's buffer, unsaved; the disk keeps the old text.
        var before = File.ReadAllText(greeter);
        turn = await AskAsync(f, "write src/Greetings/Greeter.cs");
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen && f.Shell.Dialogs.HasDetails, 30);
        Assert.Contains("stand-in", f.Shell.Dialogs.Title, StringComparison.Ordinal);
        Assert.Equal(("edit", "stand-in · external agent · " + Path.Combine("src", "Greetings", "Greeter.cs")), (f.Shell.Dialogs.Icon, f.Shell.Dialogs.Context));
        Assert.Contains(f.Shell.Dialogs.Details, d => d.Text.Contains("written by the agent", StringComparison.Ordinal));
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => !turn.IsStreaming, 30);
        Assert.Equal("Wrote it.", turn.Answer);
        var model = f.Shell.Editors.Select(e => e.Model).First(m => m.Path == greeter);
        Assert.Equal("written by the agent\n", model.Text.Text);
        Assert.True(model.IsModified);
        Assert.Equal(before, File.ReadAllText(greeter));
        await ShellFixture.PumpUntil(() => Step(f, s => s.Details?.Contains("(reviewed in the preview)", StringComparison.Ordinal) == true) is not null, describe: () => Describe(f, turn));

        // New Chat stops the agent and closes its pipe.
        var session = f.Shell.ExternalAgents.Current!;
        chat.NewChat();
        await ShellFixture.PumpUntil(() => f.Shell.ExternalAgents.Current is null && f.Shell.OpenAgentMcpPipes == 0, 30);
        Assert.False(session.IsAlive);
        Assert.Equal("Agent", AutomationProperties.GetName(Visible<DropDownButton>(ChatView(f), "AgentPicker")));
    }

    [AvaloniaFact]
    public async Task Stop_cancels_the_turn_and_closes_its_pending_approval()
    {
        await using var f = ShellFixture.Create();
        var (_, log) = await SetUpAsync(f);
        var folder = f.CopySample("BuildAndRun");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BuildAndRun.slnx"));
        var chat = f.Shell.AiChat;
        chat.SelectedAgentIndex = 1;
        await ShellFixture.PumpUntil(() => chat.CanSend || chat.Input.Length == 0, 30);

        var turn = await AskAsync(f, "slow");
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen, 30);
        Assert.True(chat.IsBusy);

        // An external agent's turn turns Send into Stop, as the built-in assistant's does; Stop stops it.
        Assert.True(chat.IsAnswering);
        Button? Shown(string name) => ChatView(f).GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == name);
        await ShellFixture.PumpUntil(() => Shown("StopButton") is { IsEffectivelyVisible: true } && Shown("Send") is { IsEffectivelyVisible: false });
        Shown("StopButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await ShellFixture.PumpUntil(() => !turn.IsStreaming, 30);
        Assert.False(f.Shell.Dialogs.IsOpen);
        Assert.False(chat.IsAnswering);
        Assert.Equal(("stopped", "Permission: cancelled"), (turn.FinishReason, turn.Answer));
        Assert.Equal("cancelled", turn.Steps.Single().Status);
        await ShellFixture.PumpUntil(() => Step(f, s => s.Status == AiStepStatus.Cancelled) is not null);
        Assert.Contains(File.ReadAllLines(log), l => l.Contains("\"session/cancel\"", StringComparison.Ordinal));
    }
}
