using Codalus.Debugging;
using Codalus.Rpc;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Acp;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Settings;

namespace Codalus.Shell;

/// <summary>
/// Connecting external agents from templates (D43, ADR 0083): the Connect dialog, where it's offered (AI Chat, its agent
/// menu, the palette, Tools > AI), More agents… and Manage Agents…, Check Connection and Disconnect. The work is
/// <see cref="AcpAgentConnector"/>'s; this is the shell's side: the terminal for sign-ins and installs, the palettes, the
/// confirmations. Nothing is connected from a remote window.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>The connector behind the Connect dialog.</summary>
    public AcpAgentConnector AgentConnector { get; private set; } = null!;

    /// <summary>The Connect dialog (one, opened per agent).</summary>
    public ConnectAgentViewModel ConnectAgent { get; private set; } = null!;

    /// <summary>Whether this IDE runs in an SSH session, or without a browser: Claude offers its remote sign-in then (tests set it).</summary>
    public bool IsRemoteSession { get; set; } = s_remoteSessionVariables.Any(v => Environment.GetEnvironmentVariable(v) is { Length: > 0 });

    /// <summary>The variables Claude's adapter reads as "no browser here" (its isRemote check).</summary>
    private static readonly string[] s_remoteSessionVariables = ["SSH_CONNECTION", "SSH_CLIENT", "SSH_TTY", "NO_BROWSER", "CLAUDE_CODE_REMOTE"];

    private void InitializeAgentConnect()
    {
        ExternalAgents.Secrets = new AcpAgentSecrets(Secrets);
        ExternalAgents.BuiltInNodeFolder = () => Node.IsInstalled ? Path.GetDirectoryName(Node.NodePath) : null;
        AgentConnector = new AcpAgentConnector(Workbench.Settings, Ai, ExternalAgents) { CodalusNode = Node };
        ConnectAgent = new ConnectAgentViewModel(new ShellConnectHost(this));
        UpdateAgentsContext();
        Workbench.Settings.Changed += (_, e) =>
        {
            if (e.Keys.Contains(AcpAgents.AgentsSetting))
            {
                Ui.Post(UpdateAgentsContext);
            }
        };
    }

    Task<string?> IAiChatHost.ConnectExternalAgentAsync(string? templateId) => ConnectExternalAgentAsync(templateId);

    string? IAiChatHost.WhyAgentsCantConnect => AgentConnector.WhyNot();

    /// <summary>The <c>externalAgentsConfigured</c> context key (Check and Disconnect are enabled by it).</summary>
    private void UpdateAgentsContext() => Workbench.Context.Set("externalAgentsConfigured", ExternalAgents.Configured.Count > 0);

    /// <summary>
    /// Connect to <paramref name="templateId"/>… (AI Chat's buttons, the palette, Tools > AI): the dialog, its sign-in
    /// <paramref name="signIn"/> chosen when given. Without a template, More agents…. The agent written, or null.
    /// </summary>
    public async Task<string?> ConnectExternalAgentAsync(string? templateId = null, string? signIn = null)
    {
        if (templateId is null)
        {
            return await MoreAgentsAsync().ConfigureAwait(true);
        }

        if (AcpAgentTemplates.Find(templateId) is not { } template)
        {
            return null;
        }

        return await ConnectAgent.OpenAsync(template, signIn).ConfigureAwait(true);
    }

    /// <summary>
    /// More agents… (replaces Add agent…): every template in the palette, with its vendor and the exact command, marked
    /// connected when <c>ai.agents</c> has it and "not installed" when it runs a CLI that isn't on the PATH; choosing one
    /// opens the Connect dialog.
    /// </summary>
    public async Task<string?> MoreAgentsAsync()
    {
        var templates = AcpAgentTemplates.All;
        var installed = await AcpAgentCatalog.FindInstalledAsync(templates.Where(t => t.NeedsCli).Select(t => t.Agent), AgentSearchPath, TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(true);
        var configured = ExternalAgents.Configured;
        var items = templates.Select(template => new QuickPickItem(template.Name)
        {
            Detail = template.NeedsCli && template.CliMinimum is { } minimum
                ? Localizer.Format("msg.acp.more.detailCliMinimum", "{0} · needs the {1} CLI {2} or later", template.Agent.CommandLine, template.Agent.Cli, minimum)
                : template.Agent.UsesNpx ? template.Vendor + " · " + template.Agent.CommandLine : Localizer.Format("msg.acp.more.detailCli", "Its own CLI: {0} (tested with {1})", template.Agent.CommandLine, template.Version),
            KeyLabel = configured.ContainsKey(template.Id) ? Localizer.Get("msg.acp.more.connected", "Connected")
                : template.NeedsCli && !installed.Contains(template.Id) ? Localizer.Format("msg.acp.more.notInstalled", "{0} not installed", template.Agent.Cli) : null,
            Icon = "bot",
            Tag = template,
        }).ToList();
        if (await Palette.ShowAsync(items, Localizer.Get("msg.acp.more.placeholder", "Choose an agent to connect")).ConfigureAwait(true) is not { Tag: AcpAgentTemplate chosen })
        {
            return null;
        }

        return await ConnectAgent.OpenAsync(chosen).ConfigureAwait(true);
    }

    /// <summary>
    /// Manage Agents…: the configured agents (the template, where the key is, which one is chatting; a hand-written entry
    /// says so) and Connect to an Agent…; choosing one offers Check Connection, Change API Key…, Sign In Again…, Edit in
    /// settings.json and Disconnect….
    /// </summary>
    public async Task ManageExternalAgentsAsync()
    {
        var configured = ExternalAgents.Configured;
        var store = ExternalAgents.Secrets?.StoreName ?? "";
        var items = configured.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).Select(entry =>
        {
            var template = AcpAgentTemplates.FromTag(entry.Value.Template);
            var detail = template is null
                ? Localizer.Format("msg.acp.manage.byHand", "Added by hand: {0}", entry.Value.ToString())
                : entry.Value.SecretEnv.Count > 0
                    ? Localizer.Format("msg.acp.manage.keyIn", "{0} · API key in {1}", entry.Value.Template, store)
                    : Localizer.Format("msg.acp.manage.ownSignIn", "{0} · {1} keeps its own sign-in", entry.Value.Template, template.Name);
            return new QuickPickItem(template?.Name ?? entry.Key)
            {
                Detail = detail,
                KeyLabel = ExternalAgents.Current?.Name == entry.Key ? Localizer.Get("msg.acp.manage.chatting", "Chatting now") : null,
                Icon = "bot",
                Tag = entry.Key,
            };
        }).ToList();
        items.Add(new QuickPickItem(Localizer.Get("msg.acp.manage.connect", "Connect to an Agent…")) { Icon = "plug", Tag = true });
        var chosen = await Palette.ShowAsync(items, Localizer.Get("msg.acp.manage.placeholder", "Manage agents")).ConfigureAwait(true);
        if (chosen?.Tag is true)
        {
            await MoreAgentsAsync().ConfigureAwait(true);
            return;
        }

        if (chosen?.Tag is not string name || !configured.TryGetValue(name, out var config))
        {
            return;
        }

        var agentTemplate = AcpAgentTemplates.FromTag(config.Template);
        var label = agentTemplate?.Name ?? name;
        var actions = new List<QuickPickItem>
        {
            new(Localizer.Get("msg.acp.manage.check", "Check Connection")) { Detail = Localizer.Get("msg.acp.manage.checkDetail", "Starts it in an empty folder and checks its sign-in; sends no prompt"), Icon = "check-circle", Tag = "check" },
        };
        if (agentTemplate?.SignIn.FirstOrDefault(o => o.TakesKey) is { } key)
        {
            actions.Add(new(Localizer.Get("msg.acp.manage.changeKey", "Change API Key…")) { Detail = key.EnvVar is { } variable ? Localizer.Format("msg.acp.manage.keyDetail", "{0} in {1}", variable, store) : key.Name, Icon = "key", Tag = "key:" + key.Id });
        }

        if (agentTemplate?.SignIn.FirstOrDefault(o => o.Kind is AcpSignInKind.TerminalMethod or AcpSignInKind.AgentMethod or AcpSignInKind.TerminalCommand) is { } again)
        {
            actions.Add(new(Localizer.Get("msg.acp.manage.signInAgain", "Sign In Again…")) { Detail = again.Name, Icon = "terminal", Tag = "signin:" + again.Id });
        }

        actions.Add(new(Localizer.Get("msg.acp.manage.edit", "Edit in settings.json")) { Detail = AcpAgents.AgentsSetting + "." + name, Icon = "braces", Tag = "edit" });
        actions.Add(new(Localizer.Get("msg.acp.manage.disconnect", "Disconnect…")) { Detail = Localizer.Get("msg.acp.manage.disconnectDetail", "Removes its entry and its key; can also sign it out"), Icon = "plug-disconnected", Tag = "disconnect" });
        var action = await Palette.ShowAsync(actions, Localizer.Format("msg.acp.manage.what", "What to do with {0}", label)).ConfigureAwait(true);
        switch (action?.Tag)
        {
            case "check":
                await CheckExternalAgentAsync(name).ConfigureAwait(true);
                break;
            case "edit":
                await OpenSettingsJsonAsync().ConfigureAwait(true);
                break;
            case "disconnect":
                await DisconnectExternalAgentAsync(name).ConfigureAwait(true);
                break;
            case string tag when tag.StartsWith("key:", StringComparison.Ordinal) || tag.StartsWith("signin:", StringComparison.Ordinal):
                await ConnectAgent.OpenAsync(agentTemplate!, tag[(tag.IndexOf(':', StringComparison.Ordinal) + 1)..]).ConfigureAwait(true);
                break;
        }
    }

    /// <summary>
    /// Check Connection of a configured agent: started in the test folder as written, its sign-in checked; the result in
    /// a dialog. Without a name, the configured agents in the palette.
    /// </summary>
    public async Task CheckExternalAgentAsync(string? name = null)
    {
        if ((name ?? await PickConfiguredAgentAsync(Localizer.Get("msg.acp.check.placeholder", "Which agent to check?")).ConfigureAwait(true)) is not { } agent)
        {
            return;
        }

        AcpTestResult result;
        var task = Workbench.Progress.Begin(Localizer.Format("msg.acp.check.progress", "Checking {0}…", agent), "AI");
        try
        {
            result = await Task.Run(() => AgentConnector.CheckConnectionAsync(agent, new ShellConnectHost(this), null, CancellationToken.None)).ConfigureAwait(true);
        }
        catch (AcpException ex)
        {
            result = new AcpTestResult(AcpTestOutcome.Failed, ex.Message);
        }
        finally
        {
            task.End();
        }

        var title = result.Outcome switch
        {
            AcpTestOutcome.Connected => Localizer.Format("msg.acp.check.connected", "{0} is connected", agent),
            AcpTestOutcome.NotConfirmed => Localizer.Format("msg.acp.check.notConfirmed", "{0} started, but didn't say whether you're signed in", agent),
            AcpTestOutcome.NeedsSignIn => Localizer.Format("msg.acp.check.needsSignIn", "{0} says you aren't signed in", agent),
            _ => Localizer.Format("msg.acp.check.failed", "{0} didn't connect", agent),
        };
        var close = Localizer.Get("msg.acp.connect.close", "Close");
        var output = Localizer.Get("msg.acp.connect.showOutput", "Show Output");
        var details = new List<DetailLine>();
        if (result.Agent is { } running)
        {
            details.Add(new DetailLine($"{running} · ACP v{result.Protocol}"));
        }

        if (result.SignedInAs is { } who)
        {
            details.Add(new DetailLine(Localizer.Format("msg.acp.connect.reports", "{0} reports: {1}", agent, who)));
        }

        var choice = await Dialogs.ShowAsync(new DialogRequest(title, result.Message, [close, output])
        {
            DefaultButton = close,
            Icon = result.Outcome == AcpTestOutcome.Connected ? "check-circle" : "warning",
            Context = Localizer.Format("msg.acp.check.context", "External agent · ai.agents.{0}", agent),
            Details = details.Count > 0 ? details : null,
        }).ConfigureAwait(true);
        if (choice.Button == output)
        {
            ShowAiLog();
        }
    }

    /// <summary>
    /// Disconnect… (D43): what it removes and what stays, and an unchecked "Also sign out of <em>agent</em> on this
    /// computer" when the agent offers sign-out; then <see cref="AcpAgentConnector.DisconnectAsync"/>. Never turns AI off.
    /// </summary>
    public async Task DisconnectExternalAgentAsync(string? name = null)
    {
        if ((name ?? await PickConfiguredAgentAsync(Localizer.Get("msg.acp.disconnect.placeholder", "Which agent to disconnect?")).ConfigureAwait(true)) is not { } agent)
        {
            return;
        }

        var config = ExternalAgents.Configured.GetValueOrDefault(agent);
        var template = AcpAgentTemplates.FromTag(config?.Template) ?? AcpAgentTemplates.Find(agent);
        var label = template?.Name ?? agent;
        var disconnect = Localizer.Get("msg.acp.disconnect.disconnect", "Disconnect");
        var removes = new List<DetailLine>
        {
            new(Localizer.Format("msg.acp.disconnect.entry", "Its entry, ai.agents.{0}, from your user settings. The other agents and your comments stay.", agent)),
        };
        if (config?.SecretEnv is { Count: > 0 } secrets)
        {
            removes.Add(new(Localizer.Format("msg.acp.disconnect.key", "Its API key, {0}, from {1}.", string.Join(", ", secrets), ExternalAgents.Secrets?.StoreName ?? "")));
        }

        if (ExternalAgents.Current?.Name == agent)
        {
            removes.Add(new(Localizer.Get("msg.acp.disconnect.chat", "Its chat, which stops now.")));
        }

        var answer = await Dialogs.ShowAsync(new DialogRequest(
            Localizer.Format("msg.acp.disconnect.title", "Disconnect {0}?", label),
            Localizer.Get("msg.acp.disconnect.removes", "Disconnecting removes:") + "\n" + string.Join("\n", removes.Select(r => "• " + r.Text)) + "\n\n" + Localizer.Get("msg.acp.disconnect.keeps", "AI assistance stays on, and npm keeps the package in its cache."),
            [disconnect, Localizer.Get("msg.acp.connect.cancel", "Cancel")])
        {
            DefaultButton = disconnect,
            Icon = "plug-disconnected",
            Context = Localizer.Format("msg.acp.disconnect.context", "External agent · ai.agents.{0} in your user settings", agent),
            Option = template?.CanSignOut == true
                ? new DialogOption("signout", Localizer.Format("msg.acp.disconnect.signOut", "Also sign out of {0} on this computer", label), false)
                {
                    Note = Localizer.Format("msg.acp.disconnect.signOutNote", "Runs {0}'s own sign-out (logout). {0} in a terminal is signed out too.", label),
                }
                : null,
        }).ConfigureAwait(true);
        if (answer.Button != disconnect)
        {
            return;
        }

        string? problem;
        try
        {
            problem = await Task.Run(() => AgentConnector.DisconnectAsync(agent, answer.Checked.Contains("signout"), CancellationToken.None)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is AcpException or IOException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.acp.disconnect.failed", "Couldn't disconnect {0}: {1}", label, ex.Message));
            return;
        }

        AiChat.Refresh();
        if (problem is not null)
        {
            Workbench.Notifications.Warning(problem);
        }

        ShowMessage(Localizer.Format("msg.acp.disconnect.done", "{0} is disconnected.", label));
    }

    private async Task<string?> PickConfiguredAgentAsync(string placeholder)
    {
        var configured = ExternalAgents.Configured;
        if (configured.Count == 0)
        {
            ShowMessage(Localizer.Get("msg.acp.noneConfigured", "No external agents are connected. AI: Connect to an Agent… sets one up."));
            return null;
        }

        var items = configured.Keys.Order(StringComparer.OrdinalIgnoreCase).Select(name => new QuickPickItem(AcpAgentTemplates.FromTag(configured[name].Template)?.Name ?? name) { Detail = configured[name].ToString(), Icon = "bot", Tag = name }).ToList();
        return (await Palette.ShowAsync(items, placeholder).ConfigureAwait(true))?.Tag as string;
    }

    /// <summary>
    /// A command in the IDE's terminal for an agent (a sign-in, an install), waited for to its end: subscribed to its exit
    /// before reading its exit code, so an early exit isn't missed; Cancel kills it. Null in a remote window, whose
    /// terminal runs on the remote host while the agent runs here (D43).
    /// </summary>
    internal async Task<int?> RunAgentTerminalAsync(string title, ProcessLaunchInfo launch, CancellationToken cancellationToken)
    {
        if (Remote is not null)
        {
            return null;
        }

        return await Ui.RunAsync(async () =>
        {
            var folder = launch.WorkingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Directory.CreateDirectory(folder); // the terminal starts in the home folder when its folder doesn't exist
            var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (key, value) in launch.Environment ?? new Dictionary<string, string>())
            {
                environment[key] = value;
            }

            foreach (var name in launch.UnsetEnvironment ?? [])
            {
                environment[name] = null;
            }

            var session = Terminals.Run(new TerminalLaunch(title, launch.FileName, launch.Arguments, folder, environment));
            var exited = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Exited += (_, code) => exited.TrySetResult(code);
            if (session.ExitCode is { } early)
            {
                exited.TrySetResult(early);
            }

            Layout.ShowToolWindow(TerminalViewModel.ToolWindowId);
            TerminalWindow.Show(session);
            await using var registration = cancellationToken.Register(() => Ui.Post(session.Kill)).ConfigureAwait(true);
            return await exited.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
        }).Unwrap().ConfigureAwait(false);
    }

    /// <summary>The Connect dialog's and Check Connection's side of the shell.</summary>
    private sealed class ShellConnectHost(ShellHost shell) : IConnectAgentHost
    {
        public AcpAgentConnector Connector => shell.AgentConnector;

        public AiService Ai => shell.Ai;

        public AcpAgentSecrets? Secrets => shell.ExternalAgents.Secrets;

        public string UserSettingsPath => shell.Workbench.Settings.UserFile;

        public bool IsRemoteSession => shell.IsRemoteSession;

        public bool InlineSuggestionsOn => shell.Workbench.Settings.GetLayerValue(AiCompletions.EnabledSetting, SettingScope.User) is { ValueKind: System.Text.Json.JsonValueKind.True };

        public void OpenUrl(string url) => shell.OpenUrl(url);

        public void ShowTerminal() => shell.Layout.ShowToolWindow(TerminalViewModel.ToolWindowId);

        public void ShowOutput() => shell.ShowAiLog();

        public void Connected(string agent) => Ui.Post(() =>
        {
            shell.UpdateAgentsContext();
            shell.AiChat.Refresh();
        });

        public void StartChat(string agent)
        {
            shell.ShowAiChat();
            shell.AiChat.SelectAgent(agent);
        }

        public Task<int?> RunInTerminalAsync(string title, string commandLine, ProcessLaunchInfo launch, string? hint, CancellationToken cancellationToken) =>
            shell.RunAgentTerminalAsync(title, launch, cancellationToken);
    }
}
