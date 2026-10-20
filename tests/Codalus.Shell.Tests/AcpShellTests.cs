using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Codalus.Shell.Theming;
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
        await f.RunAsync("ai.chat.show");
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<AiChatView>().Any());
        return (f.Window.GetVisualDescendants().OfType<AiChatView>().First(), log);
    }

    private static async Task<AiChatTurn> AskAsync(ShellFixture f, string question)
    {
        var chat = f.Shell.AiChat;
        var count = chat.Chat.Turns.Count;
        chat.Input = question;
        string State() => $"turns {chat.Chat.Turns.Count} (were {count}), CanSend {chat.CanSend}, IsBusy {chat.IsBusy}, IsAnswering {chat.IsAnswering}, IsAgentBlocked {chat.IsAgentBlocked} ({chat.AgentBlockedMessage}), input '{chat.Input}'";

        // The previous turn stops streaming on the agent's thread; the chat stops answering a posted job later, and Send
        // until then leaves the question in the box (as Enter does): wait until it can send (Windows CI, PR #74).
        await ShellFixture.PumpUntil(() => chat.CanSend, 30, State);
        _ = chat.SendAsync();
        await ShellFixture.PumpUntil(() => chat.Chat.Turns.Count > count, 30, State);
        var turn = chat.Chat.Turns[^1];
        await ShellFixture.PumpUntil(() => !turn.IsStreaming || f.Shell.Dialogs.IsOpen, 60, () => $"the turn streams ({State()})");
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
        await ShellFixture.PumpUntil(() => menu.Items.Count == 4);
        Assert.Equal(["Built-in", "stand-in", "Add agent…"], menu.Items.OfType<MenuItem>().Select(i => i.Header as string));
        Assert.Equal([true, false, false], menu.Items.OfType<MenuItem>().Select(i => i.IsChecked));
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

        // Through its session pipe, an agent sees only what's under the workspace: not a file open from elsewhere, nor its selection.
        var outside = Path.Combine(f.UserData, "outside-notes.txt");
        await File.WriteAllTextAsync(outside, "private notes outside the workspace\n");
        await f.Shell.OpenFileAsync(outside);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == outside && f.Shell.ActiveEditor.Controller is not null, 30);
        f.Shell.ActiveEditor!.Controller!.Editor.SelectAll();
        await using (var client = await McpClient.StartAsync("codalus", new McpServerConfig(command, args, new Dictionary<string, string>()), CancellationToken.None))
        {
            using var empty = JsonDocument.Parse("{}");
            var open = await client.CallAsync("get_open_files", empty.RootElement, CancellationToken.None);
            Assert.DoesNotContain("outside-notes", open, StringComparison.Ordinal);
            Assert.DoesNotContain("private notes", open, StringComparison.Ordinal);
            using var elsewhere = JsonDocument.Parse(JsonSerializer.Serialize(new { path = outside }));
            Assert.Contains("isn't in the workspace", await client.CallAsync("get_diagnostics", elsewhere.RootElement, CancellationToken.None), StringComparison.Ordinal);
        }

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

        // The variables the agent sets for a command show in its approval, under the command line.
        turn = await AskAsync(f, "runenv GREETING=hello echo with-env");
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen && f.Shell.Dialogs.Details.Any(d => d.Text == "echo with-env"), 30);
        Assert.Contains(f.Shell.Dialogs.Details, d => d.Text == "With these environment variables:");
        Assert.Contains(f.Shell.Dialogs.Details, d => d.Text == "GREETING=hello");
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => !turn.IsStreaming, 30);

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

    /// <summary>Opens "Add agent…" from the agent picker's menu, as a click does, and waits for the list.</summary>
    private static async Task OpenAddAgentAsync(ShellFixture f)
    {
        var picker = Visible<DropDownButton>(ChatView(f), "AgentPicker");
        await ShellFixture.PumpUntil(() => picker.IsEffectivelyVisible);
        var menu = (MenuFlyout)picker.Flyout!;
        menu.ShowAt(picker);
        await ShellFixture.PumpUntil(() => menu.Items.OfType<MenuItem>().Any(i => (i.Header as string) == "Add agent…"));
        var add = menu.Items.OfType<MenuItem>().Last();
        Assert.Equal("Add agent…", add.Header as string);
        add.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Hide();
        await ShellFixture.PumpUntil(() => f.Shell.Palette.IsOpen && f.Shell.Palette.Rows.Count == AcpAgentCatalog.Agents.Count);
    }

    /// <summary>A folder with a stand-in for an agent's CLI (it fails if run; nothing runs it), for the list's PATH.</summary>
    private static string StandInCli(ShellFixture f, string name)
    {
        var bin = Path.Combine(f.UserData, "bin");
        Directory.CreateDirectory(bin);
        var file = Path.Combine(bin, OperatingSystem.IsWindows() ? name + ".cmd" : name);
        File.WriteAllText(file, OperatingSystem.IsWindows() ? "@exit /b 1\r\n" : "#!/bin/sh\nexit 1\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return bin;
    }

    [AvaloniaFact]
    public async Task Add_agent_lists_the_known_agents_asks_first_and_writes_the_user_settings_only()
    {
        await using var f = ShellFixture.Create();
        await SetUpAsync(f);
        var chat = f.Shell.AiChat;
        var settings = f.Shell.Workbench.Settings;
        f.Shell.AgentSearchPath = StandInCli(f, "gemini");

        // The list: each known agent with what it is, the exact command and its pinned version; Gemini's CLI is on the PATH.
        await OpenAddAgentAsync(f);
        var rows = f.Shell.Palette.Rows;
        Assert.Equal(["pi", "Claude", "Codex", "Gemini CLI", "GitHub Copilot", "OpenCode", "goose"], rows.Select(r => r.Label));
        Assert.Equal("0.64.0 · installed", rows.Single(r => r.Label == "Gemini CLI").KeyLabel);
        var claudeRow = rows.Single(r => r.Label == "Claude");
        Assert.Equal("0.93.0", claudeRow.KeyLabel);
        Assert.Equal("ACP wrapper for Anthropic's Claude · npx -y @agentclientprotocol/claude-agent-acp@0.93.0", claudeRow.Detail);
        Assert.Equal("goose acp", rows.Single(r => r.Label == "goose").Detail!.Split(" · ")[^1]);
        Assert.Equal("tested with 1.55.0", rows.Single(r => r.Label == "goose").KeyLabel);

        // Choosing one asks first, in the dialog frame: what runs, that npx downloads it, and the label. Cancel writes nothing.
        f.Shell.Palette.Accept(claudeRow);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Equal("Add Claude?", f.Shell.Dialogs.Title);
        Assert.Contains("When you start a chat with it, Codalus will run npx -y @agentclientprotocol/claude-agent-acp@0.93.0: npx downloads that package from npm the first time and runs it as you, with your account's access to your files, not only this folder's.", f.Shell.Dialogs.Message, StringComparison.Ordinal);
        Assert.Contains(Label, f.Shell.Dialogs.Message, StringComparison.Ordinal);
        Assert.Equal(("sparkle", "claude in your user settings (ai.agents)"), (f.Shell.Dialogs.Icon, f.Shell.Dialogs.Context));
        string[] buttons = ["Add", "Cancel"];
        Assert.Equal(f.Shell.Dialogs.PrimaryLast ? buttons.AsEnumerable().Reverse() : buttons, f.Shell.Dialogs.Buttons.Select(b => b.Label));
        f.Shell.Dialogs.Cancel();
        await ShellFixture.PumpUntil(() => !f.Shell.Dialogs.IsOpen);
        Assert.Equal(["stand-in"], settings.GetLayerValue(AcpAgents.AgentsSetting, SettingScope.User)!.Value.EnumerateObject().Select(p => p.Name));
        Assert.Null(chat.SelectedAgent);

        // Add writes exactly that entry to the user's settings (the workspace's untouched) and picks it; nothing starts.
        await OpenAddAgentAsync(f);
        f.Shell.Palette.Accept(f.Shell.Palette.Rows.Single(r => r.Label == "Claude"));
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => chat.SelectedAgent == "claude");
        var user = settings.GetLayerValue(AcpAgents.AgentsSetting, SettingScope.User)!.Value;
        Assert.Equal(["stand-in", "claude"], user.EnumerateObject().Select(p => p.Name));
        Assert.Equal("npx", AcpAgents.Parse(user)["claude"].Command);
        Assert.Equal(["-y", "@agentclientprotocol/claude-agent-acp@0.93.0"], AcpAgents.Parse(user)["claude"].Args);
        Assert.Null(settings.GetLayerValue(AcpAgents.AgentsSetting, SettingScope.Workspace));
        Assert.Null(f.Shell.ExternalAgents.Current);
        Assert.Equal(["Built-in", "claude", "stand-in"], chat.AgentChoices);
        await ShellFixture.PumpUntil(() => (Visible<DropDownButton>(ChatView(f), "AgentPicker").Content as TextBlock)?.Text == "claude");

        // Added already: marked so, and choosing it picks it without asking or adding it twice.
        chat.SelectedAgentIndex = 0;
        await OpenAddAgentAsync(f);
        var added = f.Shell.Palette.Rows.Single(r => r.Label == "Claude");
        Assert.Equal("0.93.0 · added", added.KeyLabel);
        f.Shell.Palette.Accept(added);
        await ShellFixture.PumpUntil(() => chat.SelectedAgent == "claude");
        Assert.False(f.Shell.Dialogs.IsOpen);
        Assert.Equal(2, settings.GetLayerValue(AcpAgents.AgentsSetting, SettingScope.User)!.Value.EnumerateObject().Count());
        Assert.Null(f.Shell.ExternalAgents.Current);
    }

    [AvaloniaFact]
    public async Task Add_agent_offers_to_replace_an_entry_it_cant_use_and_a_mistyped_entry_doesnt_break_the_chat()
    {
        await using var f = ShellFixture.Create();
        await SetUpAsync(f);
        var chat = f.Shell.AiChat;
        var settings = f.Shell.Workbench.Settings;
        f.Shell.AgentSearchPath = Path.Combine(f.UserData, "empty-path"); // not this machine's PATH
        using (var broken = JsonDocument.Parse("""{ "claude": { "args": [] }, "typo": { "command": 5 }, "list": [1] }"""))
        {
            await settings.SetAsync(AcpAgents.AgentsSetting, broken.RootElement.Clone(), SettingScope.User, CancellationToken.None);
        }

        // Mistyped entries are skipped: the chat and its picker go on.
        chat.Refresh();
        await ShellFixture.PumpUntil(() => chat.AgentChoices.SequenceEqual(["Built-in"]));

        // claude is in the settings but unusable: not "added"; choosing it asks to replace it.
        await OpenAddAgentAsync(f);
        var row = f.Shell.Palette.Rows.Single(r => r.Label == "Claude");
        Assert.Equal("0.93.0", row.KeyLabel);
        f.Shell.Palette.Accept(row);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Equal("Replace Claude?", f.Shell.Dialogs.Title);
        Assert.StartsWith("Your settings have an entry named claude that Codalus can't use. Replace it?", f.Shell.Dialogs.Message, StringComparison.Ordinal);
        Assert.Equal("Replace", f.Shell.Dialogs.Buttons.Single(b => b.IsDefault).Label);
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => chat.SelectedAgent == "claude");
        var user = settings.GetLayerValue(AcpAgents.AgentsSetting, SettingScope.User)!.Value;
        Assert.Equal("npx", AcpAgents.Parse(user)["claude"].Command);
        Assert.Equal(["typo", "list", "claude"], user.EnumerateObject().Select(p => p.Name));
        Assert.Null(f.Shell.ExternalAgents.Current);
    }

    [AvaloniaTheory]
    [InlineData("harbor.light")]
    [InlineData("ember.dark")]
    public async Task The_add_agent_list_names_its_rows_and_cuts_no_text(string theme)
    {
        await using var f = ShellFixture.Create();
        await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, theme, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == ThemeService.Resolve(theme, null));
        await SetUpAsync(f);
        f.Shell.AgentSearchPath = StandInCli(f, "goose");
        await OpenAddAgentAsync(f);
        var list = f.Window.GetVisualDescendants().OfType<QuickPickView>().Single(v => v.IsEffectivelyVisible);
        await ShellFixture.PumpUntil(() => list.GetVisualDescendants().OfType<ListBoxItem>().Count() == AcpAgentCatalog.Agents.Count);
        foreach (var row in f.Shell.Palette.Rows)
        {
            Assert.Contains(row.Label, row.AccessibleName, StringComparison.Ordinal);
            Assert.Contains(row.KeyLabel!, row.AccessibleName, StringComparison.Ordinal);
        }

        Assert.Contains(f.Shell.Palette.Rows, r => r.KeyLabel == "tested with 1.55.0 · installed");
        Assert.Empty(PolishTests.Clipped(list));
        f.Shell.Palette.Cancel();
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
