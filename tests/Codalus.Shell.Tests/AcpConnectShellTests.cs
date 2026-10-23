using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Tests;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Acp;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// Connecting an agent in the shell (D43, ADR 0083), headless, with stand-in node and npx on the shell's agent search path
/// and the stand-in ACP agent behind npx (answering Claude's recorded initialize): AI Chat's off state offers the
/// one-click agents, the Connect dialog shows what's there, how it signs in and exactly what runs, the consent comes from
/// the settings and gates Connect, a passing test writes the entry and turns AI on, a terminal sign-in runs in the
/// Terminal and the test goes on when it ends, a key goes to the credential store only, a remote window refuses, and
/// Disconnect removes the entry. Nothing reaches npm or a real agent.
/// </summary>
public sealed class AcpConnectShellTests
{
    private const string Label = "External agent: uses its own provider and data policy. Codalus checks what it asks the IDE for.";

    private static string StandInAgent => Path.Combine(ShellFixture.RepoRoot, "tests", "Codalus.AcpAgent.Fake", "bin", ShellFixture.Configuration, Codalus.Tests.RepoBuild.TargetFramework, "codalus-fake-acp-agent.dll");

    private static string ClaudeRecording => Path.Combine(ShellFixture.RepoRoot, "tests", "Shared", "AcpRecordings", "claude-0.94.0-initialize.json");

    private static string Dotnet =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host
        : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")).First(File.Exists);

    /// <summary>Stand-ins on the shell's search path: Node.js 22, npx, and behind it the stand-in agent answering as Claude does.</summary>
    private static string StandIns(ShellFixture f, string modes = "")
    {
        var log = Path.Combine(f.UserData, "agent-log-" + Guid.NewGuid().ToString("N")[..6] + ".jsonl");
        var env = new Dictionary<string, string> { ["ACP_FAKE_LOG"] = log, ["ACP_FAKE_INIT_FILE"] = ClaudeRecording };
        foreach (var pair in modes.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            env[pair.Split('=', 2)[0]] = pair.Split('=', 2)[1];
        }

        f.Shell.IsRemoteSession = false; // this machine's own SSH_* or CLAUDE_CODE_REMOTE don't count
        f.Shell.AgentSearchPath = StandInNode.Create(Path.Combine(f.UserData, "bin-" + Guid.NewGuid().ToString("N")[..6]), "v22.11.0", Dotnet, StandInAgent, agentEnv: env);
        return log;
    }

    private static T Named<T>(Control root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static ConnectAgentView Dialog(ShellFixture f) => f.Window.GetVisualDescendants().OfType<ConnectAgentView>().Single();

    /// <summary>Opens AI Chat with AI off and clicks the off state's button for <paramref name="agent"/>.</summary>
    private static async Task<ConnectAgentViewModel> ConnectFromOffStateAsync(ShellFixture f, string agent)
    {
        await f.RunAsync("ai.chat.show");
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<AiChatView>().Any(v => v.IsEffectivelyVisible));
        var view = f.Window.GetVisualDescendants().OfType<AiChatView>().First(v => v.IsEffectivelyVisible);
        await ShellFixture.PumpUntil(() => Named<StackPanel>(view, "ConnectRow").IsEffectivelyVisible);
        var button = Named<ItemsControl>(view, "ConnectButtons").GetVisualDescendants().OfType<Button>().Single(b => (b.Content as string) == agent);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var dialog = f.Shell.ConnectAgent;
        await ShellFixture.PumpUntil(() => dialog.IsOpen && dialog.Mode is ConnectAgentMode.Connect or ConnectAgentMode.Missing or ConnectAgentMode.Remote, 30, () => $"mode {dialog.Mode}");
        return dialog;
    }

    private static async Task<AcpTestOutcome?> ConnectAndWaitAsync(ShellFixture f, ConnectAgentViewModel dialog)
    {
        Assert.True(dialog.CanConnect, $"Connect is disabled: {dialog.Buttons.FirstOrDefault(b => b.IsPrimary)?.Tip}");
        dialog.Accept();
        await ShellFixture.PumpUntil(() => dialog.Mode == ConnectAgentMode.Result, 90, () => $"mode {dialog.Mode}, steps {string.Join("; ", dialog.Steps.Select(s => $"{s.Name} {s.State} {s.Detail}"))}");
        return dialog.ResultIsOk ? AcpTestOutcome.Connected : dialog.ResultIsWarning ? AcpTestOutcome.NotConfirmed : AcpTestOutcome.Failed;
    }

    [AvaloniaFact]
    public async Task With_ai_off_the_chat_offers_the_one_click_agents_and_the_dialog_shows_what_runs_before_anything_does()
    {
        await using var f = ShellFixture.Create();
        await f.Shell.Workbench.Settings.SetAsync(AiService.ProviderSetting, "ollama", SettingScope.User, CancellationToken.None);
        await f.Shell.Workbench.Settings.SetAsync(AiCompletions.EnabledSetting, true, SettingScope.User, CancellationToken.None);
        var log = StandIns(f);
        Assert.Equal(["Claude", "Codex", "Gemini CLI", "GitHub Copilot"], f.Shell.AiChat.ConnectAgents.Select(a => a.Name));
        var dialog = await ConnectFromOffStateAsync(f, "Claude");

        // The header, the requirements found on the search path, the ways Claude signs in.
        Assert.Equal(("Connect to Claude", "External agent · Anthropic · @agentclientprotocol/claude-agent-acp 0.94.0"), (dialog.Title, dialog.Context));
        Assert.Equal(["Node.js 22.11.0", "npx 10.9.0"], dialog.Requirements.Select(r => r.Name));
        Assert.EndsWith("Claude needs 22 or later", dialog.Requirements[0].Detail, StringComparison.Ordinal);
        Assert.Equal(["Claude subscription", "Anthropic Console account", "API key", "I'm signed in already"], dialog.SignInOptions.Select(o => o.Name));
        Assert.Equal("subscription", dialog.SignInOptions.Single(o => o.IsSelected).Id);

        // What runs: the exact commands, Claude's own login in the Terminal, and the variables it starts without.
        Assert.Equal(
            [("Starts", "npx -y @agentclientprotocol/claude-agent-acp@0.94.0"), ("Signs in (Terminal)", "npx -y @agentclientprotocol/claude-agent-acp@0.94.0 --cli auth login --claudeai")],
            dialog.Runs.Where(r => r.HasCommand).Select(r => (r.Label, r.Command!)));
        Assert.Equal("ANTHROPIC_API_KEY, ANTHROPIC_AUTH_TOKEN, ANTHROPIC_BASE_URL", dialog.Runs.Single(r => r.Label == "Starts without").Text);

        // The consent box, worded from the settings, and Connect disabled until it's checked.
        Assert.True(dialog.ShowConsent);
        Assert.Equal("External agents run only while it's on. This also turns the built-in assistant back on with ollama, and inline suggestions.", dialog.ConsentText);
        Assert.False(dialog.CanConnect);
        Assert.Equal(("Check Turn on AI assistance first", false), (dialog.Buttons.Single(b => b.IsPrimary).Tip, dialog.Buttons.Single(b => b.IsPrimary).IsEnabled));
        Assert.Equal("Writes ai.agents.claude and ai.enabled to your user settings after the test passes", dialog.FooterHint);
        await ShellFixture.PumpUntil(() => Named<TextBlock>(Dialog(f), "AgentLabel").IsEffectivelyVisible);
        Assert.Equal(Label, Named<TextBlock>(Dialog(f), "AgentLabel").Text);
        dialog.Consent = true;
        Assert.True(dialog.CanConnect);

        // Cancel: nothing ran, nothing was written.
        dialog.Cancel();
        await ShellFixture.PumpUntil(() => !dialog.IsOpen);
        Assert.False(File.Exists(log) && File.ReadAllText(log).Contains("initialize", StringComparison.Ordinal));
        Assert.Null(f.Shell.Workbench.Settings.GetLayerValue(AcpAgents.AgentsSetting, SettingScope.User));
        Assert.Null(f.Shell.Workbench.Settings.GetLayerValue(AiService.EnabledSetting, SettingScope.User));
    }

    [AvaloniaFact]
    public async Task In_an_ssh_session_claudes_own_remote_sign_in_shows_with_the_hint_to_run_claude_and_login()
    {
        await using var f = ShellFixture.Create();
        StandIns(f);
        f.Shell.IsRemoteSession = true;
        await f.RunAsync("ai.agents.connect.claude");
        var dialog = f.Shell.ConnectAgent;
        await ShellFixture.PumpUntil(() => dialog.Mode == ConnectAgentMode.Connect, 30);
        Assert.Equal("npx -y @agentclientprotocol/claude-agent-acp@0.94.0 --cli", dialog.Runs.Single(r => r.Label == "Signs in (Terminal)").Command);
        Assert.Contains("run claude in a terminal and type /login", dialog.RemoteSessionNote, StringComparison.Ordinal);
        dialog.Cancel();
    }

    [AvaloniaFact]
    public async Task A_passing_test_writes_the_entry_turns_ai_on_and_start_a_chat_picks_the_agent()
    {
        await using var f = ShellFixture.Create();
        StandIns(f, "ACP_FAKE_AUTH_STATUS=account");
        var dialog = await ConnectFromOffStateAsync(f, "Claude");
        dialog.SignInOptions.Single(o => o.Id == "already").IsSelected = true;
        dialog.Consent = true;

        Assert.Equal(AcpTestOutcome.Connected, await ConnectAndWaitAsync(f, dialog));
        Assert.Equal("Claude is connected", dialog.ResultTitle);
        Assert.Contains(dialog.ResultRows, r => r.Value == "Claude reports: Claude Max · tester@example.com");
        Assert.All(dialog.Steps, s => Assert.True(s.IsDone || s.State == AcpStepState.Skipped, s.Name));

        var entry = f.Shell.Workbench.Settings.GetLayerValue(AcpAgents.AgentsSetting, SettingScope.User)!.Value.GetProperty("claude");
        Assert.Equal("npx", entry.GetProperty("command").GetString());
        Assert.Equal("claude@0.94.0", entry.GetProperty("template").GetString());
        Assert.Equal(JsonValueKind.True, f.Shell.Workbench.Settings.GetLayerValue(AiService.EnabledSetting, SettingScope.User)!.Value.ValueKind);
        Assert.True(f.Shell.Workbench.Context.GetValue("externalAgentsConfigured") is true);

        // Start a Chat: AI Chat picks Claude, and its empty state is the external agent's.
        dialog.Accept();
        await ShellFixture.PumpUntil(() => f.Shell.AiChat.SelectedAgent == "claude");
        Assert.False(dialog.IsOpen);
        Assert.Equal("Ask Claude about your solution. It answers with its own provider; Codalus asks you before it changes a file or runs a command.", f.Shell.AiChat.ExternalReadyMessage);
        Assert.True(f.Shell.AiChat.ShowExternalReadyState);

        // The picker now offers Connect only to the agents not connected yet.
        Assert.DoesNotContain(f.Shell.AiChat.AgentMenuActions[0], a => a.Label == "Connect to Claude…");
    }

    [AvaloniaFact]
    public async Task A_terminal_sign_in_runs_in_the_terminal_and_the_test_goes_on_when_it_ends()
    {
        await using var f = ShellFixture.Create();
        await f.Shell.Workbench.Settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, CancellationToken.None);
        var marker = Path.Combine(f.UserData, "signed-in");
        StandIns(f, $"ACP_FAKE_AUTH_STATUS=none;ACP_FAKE_LOGIN_MARKER={marker}");
        await f.RunAsync("ai.agents.connect.claude");
        var dialog = f.Shell.ConnectAgent;
        await ShellFixture.PumpUntil(() => dialog.Mode == ConnectAgentMode.Connect, 30);
        Assert.False(dialog.ShowConsent); // AI is on already
        Assert.Equal("Writes ai.agents.claude to your user settings after the test passes", dialog.FooterHint);

        // Claude subscription: the adapter's own login in the Terminal (a tab of its own), then the test goes on.
        var terminals = f.Shell.Terminals.Sessions.Count;
        Assert.Equal(AcpTestOutcome.Connected, await ConnectAndWaitAsync(f, dialog));
        Assert.True(File.Exists(marker));
        Assert.Equal(terminals + 1, f.Shell.Terminals.Sessions.Count);
        Assert.Equal("Claude sign-in", f.Shell.Terminals.Sessions[^1].Name);
        var entry = f.Shell.Workbench.Settings.GetLayerValue(AcpAgents.AgentsSetting, SettingScope.User)!.Value.GetProperty("claude");
        Assert.Equal(["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL"], entry.GetProperty("unsetEnv").EnumerateArray().Select(e => e.GetString()));
        dialog.Close();
    }

    [AvaloniaFact]
    public async Task An_api_key_goes_to_the_credential_store_only_and_the_dialog_says_it_lasts_until_codalus_closes()
    {
        await using var f = ShellFixture.Create();
        const string Key = "sk-ant-test-key-0123456789abcdef";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Key)));
        StandIns(f, $"ACP_FAKE_AUTH_STATUS=account;ACP_FAKE_REQUIRE_ENV=ANTHROPIC_API_KEY=sha256:{hash}");
        var dialog = await ConnectFromOffStateAsync(f, "Claude");
        dialog.SignInOptions.Single(o => o.Id == "apikey").IsSelected = true;
        Assert.True(dialog.ShowKey);
        Assert.Equal("Anthropic API key", dialog.KeyName);

        // The tests' store keeps keys in memory: the note is the warning.
        await ShellFixture.PumpUntil(() => dialog.KeyNoteIsWarning);
        Assert.Equal("Lasts until Codalus closes: the OS credential store isn't available, so the key is kept in memory only. Claude gets it as ANTHROPIC_API_KEY when it starts; it's never written to a file.", dialog.KeyNote);
        Assert.Equal("ANTHROPIC_API_KEY=<from memory, this session only>", dialog.Runs.Single(r => r.Label == "With the variable").Command);
        dialog.Consent = true;
        Assert.False(dialog.CanConnect); // no key yet
        dialog.ApiKey = Key;
        Assert.Equal(AcpTestOutcome.Connected, await ConnectAndWaitAsync(f, dialog));

        Assert.Equal(Key, await f.Shell.Secrets.GetAsync(AcpAgentSecrets.Id, "claude/ANTHROPIC_API_KEY", CancellationToken.None));
        Assert.DoesNotContain(Key, File.ReadAllText(f.Shell.Workbench.Settings.UserFile), StringComparison.Ordinal);
        Assert.DoesNotContain(Key, f.Shell.Workbench.Output.GetOrCreate(AiService.OutputChannelId).GetText(), StringComparison.Ordinal);
        dialog.Close();

        // Disconnect: the entry and the key go; AI stays on. Claude offers sign-out, unchecked.
        var disconnecting = f.Shell.DisconnectExternalAgentAsync("claude");
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Equal("Disconnect Claude?", f.Shell.Dialogs.Title);
        Assert.Contains("Its API key, ANTHROPIC_API_KEY,", f.Shell.Dialogs.Message, StringComparison.Ordinal);
        Assert.Equal(("Also sign out of Claude on this computer", false), (f.Shell.Dialogs.OptionLabel, f.Shell.Dialogs.OptionChecked));
        f.Shell.Dialogs.Accept();
        await disconnecting;
        Assert.False(f.Shell.Workbench.Settings.GetLayerValue(AcpAgents.AgentsSetting, SettingScope.User)!.Value.TryGetProperty("claude", out _));
        Assert.Null(await f.Shell.Secrets.GetAsync(AcpAgentSecrets.Id, "claude/ANTHROPIC_API_KEY", CancellationToken.None));
        Assert.Equal(JsonValueKind.True, f.Shell.Workbench.Settings.GetLayerValue(AiService.EnabledSetting, SettingScope.User)!.Value.ValueKind);
    }

    [AvaloniaFact]
    public async Task A_signed_out_claude_fails_with_fixes_and_nothing_is_written()
    {
        await using var f = ShellFixture.Create();
        StandIns(f, "ACP_FAKE_AUTH_STATUS=none");
        var dialog = await ConnectFromOffStateAsync(f, "Claude");
        dialog.SignInOptions.Single(o => o.Id == "already").IsSelected = true;
        dialog.Consent = true;
        Assert.Equal(AcpTestOutcome.Failed, await ConnectAndWaitAsync(f, dialog));
        Assert.Equal("Claude says you aren't signed in", dialog.ResultTitle);
        Assert.Equal(["Sign In with Claude subscription…", "Sign In with Anthropic Console account…", "Use an API Key…"], dialog.Fixes.Select(x => x.Label));
        Assert.Equal("Try Again", dialog.Buttons.Single(b => b.IsPrimary).Label);
        Assert.Null(f.Shell.Workbench.Settings.GetLayerValue(AcpAgents.AgentsSetting, SettingScope.User));
        Assert.Null(f.Shell.Workbench.Settings.GetLayerValue(AiService.EnabledSetting, SettingScope.User));

        // A fix goes back to the form with that sign-in chosen.
        dialog.Fixes.Last().Command.Execute(null);
        Assert.Equal((ConnectAgentMode.Connect, "apikey"), (dialog.Mode, dialog.SignInOptions.Single(o => o.IsSelected).Id));
        dialog.Cancel();
    }

    [AvaloniaFact]
    public async Task A_remote_window_disables_the_buttons_with_the_reason_and_the_dialog_refuses()
    {
        await using var f = ShellFixture.Create();
        StandIns(f);
        f.Shell.ExternalAgents.IsRemoteWindow = () => true;
        await f.RunAsync("ai.chat.show");
        f.Shell.AiChat.Refresh();
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<AiChatView>().Any(v => v.IsEffectivelyVisible));
        var view = f.Window.GetVisualDescendants().OfType<AiChatView>().First(v => v.IsEffectivelyVisible);
        await ShellFixture.PumpUntil(() => Named<StackPanel>(view, "ConnectRow").IsEffectivelyVisible);
        Assert.All(Named<ItemsControl>(view, "ConnectButtons").GetVisualDescendants().OfType<Button>(), b => Assert.False(b.IsEnabled));
        Assert.StartsWith("Agents can't be connected from a remote window", Named<TextBlock>(view, "ConnectBlocked").Text, StringComparison.Ordinal);
        Assert.False(Named<Button>(view, "MoreAgents").IsVisible);

        await f.RunAsync("ai.agents.connect.codex");
        await ShellFixture.PumpUntil(() => f.Shell.ConnectAgent.Mode == ConnectAgentMode.Remote);
        Assert.Equal("Agents can't be connected from a remote window", f.Shell.ConnectAgent.RemoteTitle);
        Assert.Equal(["Close"], f.Shell.ConnectAgent.Buttons.Select(b => b.Label));
        f.Shell.ConnectAgent.Accept();
        Assert.False(f.Shell.ConnectAgent.IsOpen);
    }

    [AvaloniaFact]
    public async Task The_commands_are_offered_while_ai_is_off_and_in_tools_ai()
    {
        await using var f = ShellFixture.Create();
        var commands = f.Shell.Workbench.Commands;
        foreach (var template in AcpAgentTemplates.All)
        {
            var descriptor = commands.Descriptors.Single(c => c.Id == "ai.agents.connect." + template.Id);
            Assert.Equal(("AI", $"Connect to {template.Name}…"), (descriptor.Category, descriptor.Title));
            Assert.True(descriptor.Enablement is null || descriptor.Enablement.Evaluate(f.Shell.Workbench.Context));
        }

        Assert.Equal("Connect to an Agent…", commands.Descriptors.Single(c => c.Id == "ai.agents.connect").Title);
        Assert.False(commands.Descriptors.Single(c => c.Id == "ai.agents.disconnect").Enablement!.Evaluate(f.Shell.Workbench.Context)); // no agent yet
        var toolsAi = f.Shell.Workbench.Menus.Rules.Where(r => r.Location == BuiltIns.ToolsAiMenu).Select(r => r.Command).ToList();
        Assert.Contains("ai.agents.connect.claude", toolsAi);
        Assert.Contains("ai.agents.connect", toolsAi);
    }
}
