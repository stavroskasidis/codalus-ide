using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.Shell.Terminal;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Acp;
using Codalus.Workbench.Ai.Mcp;
using Codalus.Workbench.Settings;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Shell.Tests;

/// <summary>
/// AI Console (ADR 0095) in the headless shell, with a stand-in for an agent's CLI on a test PATH (a script that logs its
/// arguments, says it is ready and echoes each line it reads; nothing reaches a real agent): the console in the bottom
/// group with its label, the CLI's conversation id and the IDE's tools on a pipe of its own, the external agents' gate,
/// consoles that come back with their conversation, a CLI that isn't installed or sits inside the workspace, references
/// typed into the prompt, and the entry points.
/// </summary>
public sealed class AiConsoleTests
{
    private static string ShellDll => Path.Combine(ShellFixture.RepoRoot, "src", "Codalus.Shell", "bin", ShellFixture.Configuration, Codalus.Tests.RepoBuild.TargetFramework, "codalus.dll");

    private static string Dotnet =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host
        : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")).First(File.Exists);

    /// <summary>
    /// A stand-in CLI named <paramref name="name"/> in <paramref name="folder"/>: a line of its arguments to the log, then
    /// "fake-cli ready", then "got: line" for each line it reads until "exit". With the log's ".fail-resume" file beside
    /// it, a start that resumes a conversation says there is none and fails, as a CLI with nothing to resume does.
    /// </summary>
    private static void StandInCli(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        if (OperatingSystem.IsWindows())
        {
            Codalus.Tests.StandInScript.Write(Path.Combine(folder, name + ".cmd"), string.Join("\r\n",
                "@echo off",
                "echo args: %* >> \"%CONSOLE_FAKE_LOG%\"",
                "if not exist \"%CONSOLE_FAKE_LOG%.fail-resume\" goto ready",
                "echo %* | findstr /c:\"--resume\" /c:\"--continue\" >nul",
                "if errorlevel 1 goto ready",
                "echo No conversation found",
                "exit /b 1",
                ":ready",
                "echo fake-cli ready",
                ":loop",
                "set \"line=\"",
                "set /p line=",
                "if \"%line%\"==\"exit\" exit /b 0",
                "echo got: %line%",
                "goto loop",
                ""));
            return;
        }

        Codalus.Tests.StandInScript.Write(Path.Combine(folder, name), string.Join("\n",
            "#!/bin/sh",
            "echo \"args: $*\" >> \"$CONSOLE_FAKE_LOG\"",
            "if [ -f \"$CONSOLE_FAKE_LOG.fail-resume\" ]; then",
            "  case \" $* \" in *\" --resume \"*|*\" --continue \"*) echo 'No conversation found'; exit 1;; esac",
            "fi",
            "echo 'fake-cli ready'",
            "while IFS= read -r line; do",
            "  [ \"$line\" = \"exit\" ] && exit 0",
            "  echo \"got: $line\"",
            "done",
            ""));
    }

    private sealed record Setup(string Log, string Bin, string Folder);

    /// <summary>AI on, Claude connected (an entry as Connect writes it) with its stand-in CLI on the test PATH, and a trusted solution open.</summary>
    private static async Task<Setup> SetUpAsync(ShellFixture f, bool open = true, string extraAgents = "")
    {
        f.Window.Width = 1200;
        f.Window.Height = 800;
        var log = Path.Combine(f.UserData, "console-log.txt");
        var bin = Path.Combine(f.UserData, "cli-bin");
        StandInCli(bin, "claude");
        f.Shell.AgentSearchPath = bin;
        f.Shell.McpExecutable = (Dotnet, ShellDll);
        var settings = f.Shell.Workbench.Settings;
        await settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, CancellationToken.None);
        var env = JsonSerializer.Serialize(new Dictionary<string, string> { ["CONSOLE_FAKE_LOG"] = log });
        using var agents = JsonDocument.Parse($$"""{ "claude": { "command": "npx", "args": ["-y", "@agentclientprotocol/claude-agent-acp@0.94.0"], "template": "claude@0.94.0", "env": {{env}} }{{extraAgents.Replace("ENV", env, StringComparison.Ordinal)}} }""");
        await settings.SetAsync(AcpAgents.AgentsSetting, agents.RootElement.Clone(), SettingScope.User, CancellationToken.None);
        var folder = "";
        if (open)
        {
            folder = f.CopySample("BuildAndRun");
            f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
            await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BuildAndRun.slnx"));
        }

        await ShellFixture.PumpUntil(() => f.Shell.AiConsoleAgents.Count > 0);
        return new Setup(log, bin, folder);
    }

    private static AiConsoleView View(ShellFixture f) => f.Window.GetVisualDescendants().OfType<AiConsoleView>().First();

    private static string Screen(AiConsoleTabViewModel tab)
    {
        if (tab.Session is not { } session)
        {
            return string.Empty;
        }

        lock (session.Terminal.SyncRoot)
        {
            return string.Join('\n', Enumerable.Range(0, session.Terminal.TotalRows).Select(i => session.Terminal.GetRow(i).GetText().TrimEnd()));
        }
    }

    private static string[] Starts(string log) => File.Exists(log) ? [.. File.ReadAllLines(log).Where(l => l.StartsWith("args:", StringComparison.Ordinal)).Select(l => l["args:".Length..].Trim())] : [];

    private static async Task<AiConsoleTabViewModel> ShownAsync(ShellFixture f, int tabs = 1)
    {
        var console = f.Shell.AiConsole;
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<AiConsoleView>().Any() && console.Tabs.Count == tabs && console.Tabs.All(t => Screen(t).Contains("fake-cli ready", StringComparison.Ordinal)), 60,
            () => $"tabs {console.Tabs.Count}, notice '{console.Notice}', screens: {string.Join(" | ", console.Tabs.Select(Screen))}");
        return console.Selected!;
    }

    [AvaloniaFact]
    public async Task The_console_runs_the_agents_cli_at_the_bottom_with_its_label_and_the_ides_tools()
    {
        await using var f = ShellFixture.Create();
        var setup = await SetUpAsync(f);

        // A tool window of the bottom group, beside the Output and the Terminal.
        var window = f.Shell.Workbench.ToolWindows.All.Single(w => w.Id == AiConsoleViewModel.ToolWindowId);
        Assert.Equal(("AI Console", ToolWindowLocation.Bottom), (window.Title, window.Location));
        Assert.Equal(ToolWindowLocation.Bottom, f.Shell.Workbench.ToolWindows.All.Single(w => w.Id == TerminalViewModel.ToolWindowId).Location);

        await f.RunAsync("ai.console.show");
        var tab = await ShownAsync(f);
        var view = View(f);
        Assert.Equal(("claude", "Claude", "Claude"), (tab.Agent, tab.DisplayName, tab.Title));
        Assert.Same(tab.Session, view.Terminal.Session);

        // The CLI found on the PATH, in the workspace's folder, with a conversation id of Codalus's and the tools' file.
        var start = Assert.Single(Starts(setup.Log));
        var args = start.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(("--session-id", "--mcp-config"), (args[0], args[2]));
        Assert.True(Guid.TryParse(args[1], out _));
        Assert.Equal(args[1], tab.SessionId);
        Assert.Equal(Path.GetFullPath(setup.Folder), Path.GetFullPath(tab.Session!.InitialDirectory));

        // The label says what Codalus doesn't check, and that the IDE's tools are connected.
        var label = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ConsoleLabel");
        Assert.Equal("External agent's own CLI: it uses its own provider and data policy, and changes files and runs commands itself, asking you with its own prompts. Codalus doesn't preview its edits.", label.Text);
        Assert.True(label.IsEffectivelyVisible);
        Assert.True(tab.IdeTools);
        Assert.Equal("The IDE's tools are connected (codalus).", f.Shell.AiConsole.ToolsNote);

        // The file is codalus mcp for this console's own pipe, which reaches this IDE though ai.mcpServer.enabled is off.
        var config = start[(start.IndexOf("--mcp-config", StringComparison.Ordinal) + "--mcp-config".Length)..].Trim();
        var server = JsonElement.Parse(await File.ReadAllTextAsync(config, TestContext.Current.CancellationToken)).GetProperty("mcpServers").GetProperty("codalus");
        var command = server.GetProperty("command").GetString()!;
        var mcpArgs = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToList();
        Assert.Equal([ShellDll, "mcp", "--user-data-dir", Path.GetFullPath(f.UserData), "--agent-session"], mcpArgs.Take(5));
        Assert.Equal(1, f.Shell.OpenAgentMcpPipes);
        Assert.False(f.Shell.IsMcpServerListening);
        await using (var client = await McpClient.StartAsync("codalus", new McpServerConfig(command, mcpArgs, new Dictionary<string, string>()), CancellationToken.None))
        {
            using var empty = JsonDocument.Parse("{}");
            Assert.Contains("BuildAndRun.slnx", await client.CallAsync("get_solution", empty.RootElement, CancellationToken.None), StringComparison.Ordinal);
        }

        var output = f.Shell.Workbench.Output.GetOrCreate(AiService.OutputChannelId).GetText();
        Assert.Contains("IDE tool (Claude (AI Console)): get_solution", output, StringComparison.Ordinal);
        // The log names the program by its file: npm's script on Windows (claude.cmd), which cmd.exe runs.
        Assert.Contains($"agent claude: AI Console: {(OperatingSystem.IsWindows() ? "claude.cmd" : "claude")} --session-id", output, StringComparison.Ordinal);

        // Typing goes to the CLI.
        view.Terminal.Focus();
        f.Type("hello");
        f.Press(Key.Enter);
        await ShellFixture.PumpUntil(() => Screen(tab).Contains("got: hello", StringComparison.Ordinal), 20, () => Screen(tab));
        Assert.Equal(AiConsoleViewModel.ToolWindowId, f.Shell.Workbench.Context.GetValue("focusedView"));

        // A second console of the same agent is a tab of its own, with its own pipe.
        await f.RunAsync("ai.console.new");
        var second = await ShownAsync(f, tabs: 2);
        Assert.Equal(["Claude", "Claude 2"], f.Shell.AiConsole.Tabs.Select(t => t.Title));
        Assert.NotEqual(tab.SessionId, second.SessionId);
        Assert.Equal(2, f.Shell.OpenAgentMcpPipes);
        Assert.True(view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "TabList").IsEffectivelyVisible);

        // Close ends the CLI, its pipe and its file.
        var session = tab.Session;
        f.Shell.KillAiConsole(tab);
        f.Shell.KillAiConsole(second);
        await ShellFixture.PumpUntil(() => f.Shell.AiConsole.Tabs.Count == 0 && f.Shell.OpenAgentMcpPipes == 0 && !f.Shell.Terminals.Sessions.Contains(session), 20);
        Assert.False(File.Exists(config));
        Assert.Null(f.Shell.AiConsole.Selected);
    }

    [AvaloniaFact]
    public async Task The_console_runs_only_where_an_external_agent_may_and_comes_back_with_its_conversation()
    {
        await using var f = ShellFixture.Create();
        var setup = await SetUpAsync(f, open: false);
        var console = f.Shell.AiConsole;
        var settings = f.Shell.Workbench.Settings;

        // Without a folder nothing starts, and the window says why.
        await f.RunAsync("ai.console.show");
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<AiConsoleView>().Any() && console.HasNotice);
        Assert.Contains("open one first", console.Notice, StringComparison.Ordinal);
        Assert.Empty(console.Tabs);
        Assert.Empty(Starts(setup.Log));
        Assert.False(console.NewCommand.CanExecute(null));

        // A folder that isn't trusted: still nothing.
        var folder = f.CopySample("BuildAndRun");
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BuildAndRun.slnx"), trusted: false);
        await ShellFixture.PumpUntil(() => console.Notice?.Contains("trusted folders", StringComparison.Ordinal) == true, 60, () => console.Notice ?? "(no notice)");
        Assert.Empty(Starts(setup.Log));

        // Trusted: the console that is shown starts.
        await f.Shell.CloseSolutionAsync();
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BuildAndRun.slnx"));
        var tab = await ShownAsync(f);
        var id = tab.SessionId!;
        Assert.Null(console.Notice);
        Assert.Equal(1, f.Shell.OpenAgentMcpPipes);

        // AI turned off: the CLI and its pipe end, and the window says why.
        var session = tab.Session!;
        await settings.SetAsync(AiService.EnabledSetting, false, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => console.Tabs.Count == 0 && f.Shell.OpenAgentMcpPipes == 0 && !f.Shell.Terminals.Sessions.Contains(session), 20);
        Assert.Contains("Turn on AI assistance first", console.Notice, StringComparison.Ordinal);

        // On again: the kept console comes back, resuming its own conversation by its id.
        await settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, CancellationToken.None);
        tab = await ShownAsync(f);
        Assert.Equal(id, tab.SessionId);
        Assert.Equal($"--resume {id} --mcp-config", string.Join(' ', Starts(setup.Log)[^1].Split(' ').Take(3)));

        // A conversation the CLI can't resume (nothing was said in it): it fails at once, and a new one starts in the tab.
        await File.WriteAllTextAsync(setup.Log + ".fail-resume", "", TestContext.Current.CancellationToken);
        await settings.SetAsync(AiService.EnabledSetting, false, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => console.Tabs.Count == 0, 20);
        var before = Starts(setup.Log).Length;
        await settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => Starts(setup.Log).Length == before + 2 && console.Tabs.Count == 1 && Screen(console.Tabs[0]).Contains("fake-cli ready", StringComparison.Ordinal), 60,
            () => $"starts {string.Join(" | ", Starts(setup.Log))}; notice '{console.Notice}'; tabs {string.Join(", ", console.Tabs.Select(t => $"{t.Title} exited {t.Exited} code {t.Session?.ExitCode} screen '{Screen(t)}'"))}; log: {string.Join(" / ", f.Shell.Workbench.Output.GetOrCreate(AiService.OutputChannelId).GetText().Split('\n').Where(l => l.Contains("AI Console", StringComparison.Ordinal)))}");
        var starts = Starts(setup.Log);
        Assert.StartsWith($"--resume {id} ", starts[^2], StringComparison.Ordinal);
        Assert.StartsWith("--session-id ", starts[^1], StringComparison.Ordinal);
        Assert.NotEqual(id, console.Tabs[0].SessionId);
        Assert.False(console.Tabs[0].Exited);

        // The CLI ending by itself leaves its tab with what it printed; Restart starts a new conversation in it.
        File.Delete(setup.Log + ".fail-resume");
        tab = console.Tabs[0];
        tab.Session!.Write("exit\r");
        await ShellFixture.PumpUntil(() => tab.Exited, 20);
        Assert.Equal("Claude (exited: 0)", tab.Title);
        Assert.Equal(0, f.Shell.OpenAgentMcpPipes);
        await f.RunAsync("ai.console.restart");
        await ShownAsync(f);
        Assert.Equal(("Claude", false), (tab.Title, tab.Exited));

        // The agent disconnected: its console goes with it, and the AI key opens AI Chat again.
        using (var none = JsonDocument.Parse("{}"))
        {
            await settings.SetAsync(AcpAgents.AgentsSetting, none.RootElement.Clone(), SettingScope.User, CancellationToken.None);
        }

        await ShellFixture.PumpUntil(() => console.Tabs.Count == 0 && console.CanConnect, 20);
        Assert.Contains("No agent with a CLI of its own is connected", console.Notice, StringComparison.Ordinal);
        Assert.Equal(false, f.Shell.Workbench.Context.GetValue("aiConsoleAvailable"));
    }

    [AvaloniaFact]
    public async Task Resizing_the_window_resizes_the_console()
    {
        await using var f = ShellFixture.Create();
        await SetUpAsync(f);
        await f.RunAsync("ai.console.show");
        var tab = await ShownAsync(f);
        var control = View(f).Terminal;
        var session = tab.Session!;
        string Sizes() => $"control {control.Columns}x{control.Rows} in {control.Bounds.Size}, terminal {session.Terminal.Columns}x{session.Terminal.Rows}";
        bool Follows() => (session.Terminal.Columns, session.Terminal.Rows) == (control.Columns, control.Rows);

        // The CLI's terminal is as wide and high as the console shows it (what tells the CLI is PseudoTerminalTests').
        await ShellFixture.PumpUntil(() => control.Columns > 20 && Follows(), 20, Sizes);
        var (columns, rows) = (control.Columns, control.Rows);

        f.Window.Width = 1600;
        f.Window.Height = 1100;
        await ShellFixture.PumpUntil(() => control.Columns > columns && control.Rows > rows && Follows(), 20, Sizes);
        (columns, rows) = (control.Columns, control.Rows);

        f.Window.Width = 1000;
        f.Window.Height = 900;
        await ShellFixture.PumpUntil(() => control.Columns < columns && control.Rows < rows && Follows(), 20, Sizes);
    }

    [AvaloniaFact]
    public async Task A_cli_that_isnt_installed_or_sits_in_the_workspace_doesnt_start()
    {
        await using var f = ShellFixture.Create();
        var setup = await SetUpAsync(f, extraAgents: """, "mine": { "command": "mine-acp", "console": { "command": "mine", "args": ["chat"] }, "env": ENV }, "hand": { "command": "hand-acp" }""");
        var console = f.Shell.AiConsole;

        // Only agents with a CLI are offered: a template's, or an entry's console.
        Assert.Equal([("claude", "Claude"), ("mine", "mine")], f.Shell.AiConsoleAgents.Select(a => (a.Name, a.DisplayName)));

        // Not on the PATH: the window says so and offers the install (Claude's own page).
        f.Shell.AgentSearchPath = Path.Combine(f.UserData, "empty-path");
        await f.RunAsync("ai.console.show");
        await ShellFixture.PumpUntil(() => console.Notice?.Contains("claude isn't on your PATH", StringComparison.Ordinal) == true, 30, () => console.Notice ?? "(no notice)");
        Assert.True(console.CanInstall);
        Assert.False(console.CanConnect);
        Assert.Empty(console.Tabs);
        Assert.Equal(0, f.Shell.OpenAgentMcpPipes);
        var view = View(f);
        Assert.True(view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "InstallButton").IsEffectivelyVisible);
        Assert.False(view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConnectButton").IsEffectivelyVisible);

        // A program of the same name inside the workspace (a cloned repository's own folder on the PATH) isn't started.
        var planted = Path.Combine(setup.Folder, "tools");
        StandInCli(planted, "claude");
        f.Shell.AgentSearchPath = planted;
        await f.Shell.NewAiConsoleAsync("claude");
        await ShellFixture.PumpUntil(() => console.Notice?.Contains("is inside this folder", StringComparison.Ordinal) == true, 30, () => console.Notice ?? "(no notice)");
        Assert.Empty(console.Tabs);
        Assert.Empty(Starts(setup.Log));

        // An entry's own console command runs as written: no conversation id, no tools, and the note says so.
        StandInCli(setup.Bin, "mine");
        f.Shell.AgentSearchPath = setup.Bin;
        await f.Shell.NewAiConsoleAsync("mine");
        var tab = await ShownAsync(f);
        Assert.Equal("chat", Assert.Single(Starts(setup.Log)));
        Assert.Equal(("mine", null, false), (tab.Agent, tab.SessionId, tab.IdeTools));
        Assert.Equal(0, f.Shell.OpenAgentMcpPipes);
        Assert.Equal("The IDE's tools aren't connected: mine takes no MCP server for one run.", console.ToolsNote);
        Assert.Null(console.Notice);

        // The next console starts the agent chosen last.
        Assert.Equal("mine", f.Shell.Workbench.Settings.GetString(ShellHost.ConsoleAgentSetting));
        Assert.Null(f.Shell.Workbench.Settings.GetLayerValue(ShellHost.ConsoleAgentSetting, SettingScope.Workspace));
    }

    [AvaloniaFact]
    public async Task Add_selection_and_add_file_type_a_reference_into_the_prompt_never_the_text()
    {
        await using var f = ShellFixture.Create();
        var setup = await SetUpAsync(f);
        var file = Path.Combine(setup.Folder, "src", "Greetings", "Greeter.cs");
        await f.Shell.OpenFileAsync(file);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == file && f.Shell.ActiveEditor.Controller is not null, 30);
        var editor = f.Shell.ActiveEditor!.Controller!.Editor;
        var text = editor.Document.GetText(editor.Document.GetLineByNumber(3).Offset, editor.Document.GetLineByNumber(3).Length);

        // Lines 2 to 4, the selection ending at line 5's start: the console opens, its CLI starts, and the reference is
        // typed in Claude's form without being sent.
        editor.Select(editor.Document.GetLineByNumber(2).Offset, editor.Document.GetLineByNumber(5).Offset - editor.Document.GetLineByNumber(2).Offset);
        await f.RunAsync("ai.console.addSelection");
        var tab = await ShownAsync(f);
        await ShellFixture.PumpUntil(() => Screen(tab).Contains("@src/Greetings/Greeter.cs#L2-4", StringComparison.Ordinal), 20, () => Screen(tab));
        Assert.DoesNotContain("got:", Screen(tab), StringComparison.Ordinal);

        // The whole file, after it on the same prompt line; Enter sends what was typed, and none of it is the file's text.
        await f.RunAsync("ai.console.addFile");
        await ShellFixture.PumpUntil(() => Screen(tab).Contains("#L2-4 @src/Greetings/Greeter.cs", StringComparison.Ordinal), 20, () => Screen(tab));
        tab.Session!.Write("\r");
        await ShellFixture.PumpUntil(() => Screen(tab).Contains("got: @src/Greetings/Greeter.cs#L2-4 @src/Greetings/Greeter.cs", StringComparison.Ordinal), 20, () => Screen(tab));
        Assert.True(text.Trim().Length > 3);
        Assert.DoesNotContain(text.Trim(), Screen(tab), StringComparison.Ordinal);

        // A caret without a selection names its line.
        editor.Select(0, 0);
        editor.CaretOffset = editor.Document.GetLineByNumber(3).Offset;
        await f.RunAsync("ai.console.addSelection");
        await ShellFixture.PumpUntil(() => Screen(tab).Contains("@src/Greetings/Greeter.cs#L3 ", StringComparison.Ordinal) || Screen(tab).TrimEnd().EndsWith("Greeter.cs#L3", StringComparison.Ordinal), 20, () => Screen(tab));
        tab.Session.Write("\r");

        // A file the AI boundary excludes isn't named to the agent.
        var secret = Path.Combine(setup.Folder, ".env");
        await File.WriteAllTextAsync(secret, "TOKEN=1\n", TestContext.Current.CancellationToken);
        await f.Shell.AddFileToAiConsoleAsync(secret);
        await ShellFixture.PumpUntil(() => f.Shell.StatusBar.Message?.Contains(".env isn't added to AI Console", StringComparison.Ordinal) == true, 20, () => f.Shell.StatusBar.Message ?? "(no message)");
        Assert.DoesNotContain(".env", Screen(tab), StringComparison.Ordinal);

        // A file dragged from Solution Explorer carries its paths the same way.
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(SolutionExplorerView.s_paths, new[] { Path.Combine(setup.Folder, "README.md") }));
        Assert.Equal([Path.Combine(setup.Folder, "README.md")], AiConsoleView.DroppedPaths(transfer));
        await f.Shell.AiConsole.AddFileAsync(Path.Combine(setup.Folder, "README.md"));
        await ShellFixture.PumpUntil(() => Screen(tab).Contains("@README.md", StringComparison.Ordinal), 20, () => Screen(tab));
    }

    [AvaloniaFact]
    public async Task The_ai_key_opens_the_console_once_an_agent_with_a_cli_is_connected_and_ai_chat_until_then()
    {
        await using var f = ShellFixture.Create();

        // Ctrl/Cmd+Alt+I is the console's command; AI Chat keeps its own, without a key.
        var keys = f.Shell.Workbench.Keybindings.EffectiveRules.Where(r => r.Command is "ai.console.show" or "ai.chat.show").Select(r => r.Command).Distinct();
        Assert.Equal(["ai.console.show"], keys);

        // Nothing connected: the key's command opens AI Chat, and the console's commands are off.
        Assert.Equal(false, f.Shell.Workbench.Context.GetValue("aiConsoleAvailable"));
        await f.RunAsync("ai.console.show");
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<AiChatView>().Any());
        Assert.False(f.Shell.Layout.IsVisible(AiConsoleViewModel.ToolWindowId));
        f.Shell.Layout.CloseToolWindow(AiChatViewModel.ToolWindowId);

        // The window's own View command still shows the console, which offers Connect.
        await f.RunAsync("workbench.view." + AiConsoleViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<AiConsoleView>().Any() && f.Shell.AiConsole.HasNotice);
        f.Shell.Layout.CloseToolWindow(AiConsoleViewModel.ToolWindowId);

        // Connected: the same command opens the console and starts the CLI; AI Chat's command still opens AI Chat.
        var setup = await SetUpAsync(f);
        Assert.Equal(true, f.Shell.Workbench.Context.GetValue("aiConsoleAvailable"));
        await f.RunAsync("ai.console.show");
        await ShownAsync(f);
        Assert.True(f.Shell.Layout.IsVisible(AiConsoleViewModel.ToolWindowId));
        Assert.Single(Starts(setup.Log));
        await f.RunAsync("ai.chat.show");
        await ShellFixture.PumpUntil(() => f.Shell.Layout.IsVisible(AiChatViewModel.ToolWindowId));

        // Closing the window keeps its consoles; closing the last console doesn't start another by itself.
        f.Shell.Layout.CloseToolWindow(AiConsoleViewModel.ToolWindowId);
        await f.RunAsync("ai.console.show");
        await ShownAsync(f);
        Assert.Single(Starts(setup.Log));
        await f.RunAsync("ai.console.close");
        await ShellFixture.PumpUntil(() => f.Shell.AiConsole.Tabs.Count == 0);
        await f.RunAsync("ai.console.show");
        ShellFixture.Pump();
        Assert.Empty(f.Shell.AiConsole.Tabs);
        Assert.True(f.Shell.AiConsole.ShowEmptyState);
        Assert.Single(Starts(setup.Log));
    }
}
