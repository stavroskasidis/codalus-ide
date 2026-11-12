using Codalus.Debugging;
using Codalus.Platform;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai.Acp;
using Codalus.Workbench.Ai.Cli;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Settings;
using Codalus.Workbench.Setup;
using Codalus.Workbench.Terminal;

namespace Codalus.Shell;

/// <summary>
/// AI Console (ADR 0095): a connected agent's own CLI in the IDE's terminal, in a tool window of the bottom group. It
/// starts only where an external agent may (ADR 0079's gate: AI on, the agent in the user's settings, a trusted folder,
/// not a remote window) and ends when that changes. The CLI works by itself, with its own approvals; what it gets from
/// the IDE is <c>codalus mcp</c> on a pipe of its own, with the checks every agent's tools have. Its consoles are kept per
/// workspace in the user's state and come back, with their conversations where the CLI can resume one.
/// </summary>
public sealed partial class ShellHost : IAiConsoleHost
{
    /// <summary>The agent the last console was started with (the user's settings), for the next one.</summary>
    public const string ConsoleAgentSetting = AgentClis.ConsoleAgentSetting;

    /// <summary>A resumed conversation that ends with an error sooner than this starts fresh, once (there was none to resume).</summary>
    internal static TimeSpan ConsoleResumeGrace { get; set; } = TimeSpan.FromSeconds(20);

    private AgentConsoleState _consoleState = null!;
    private string? _consoleRoot;
    private string? _consoleStartedRoot;
    private int _consoleStarting;

    public AiConsoleViewModel AiConsole { get; private set; } = null!;

    private void InitializeAiConsole()
    {
        _consoleState = new AgentConsoleState(Path.Combine(Paths.State, "ai-console.json"));
        AiConsole = new AiConsoleViewModel(this, TerminalLook, OpenTerminalLink);
        UpdateAiConsoleContext();

        // The gate, again, whenever what it reads changes: AI's state, the workspace and its trust, the agents.
        Ai.Changed += (_, _) => Ui.Post(EnforceAiConsole);
        Solutions.SolutionChanged += (_, _) => Ui.Post(EnforceAiConsole);
        Workbench.Settings.Changed += (_, e) =>
        {
            if (e.Keys.Contains(AcpAgents.AgentsSetting))
            {
                Ui.Post(EnforceAiConsole);
            }
        };
    }

    string? IAiConsoleHost.WhyAiConsoleCantRun => ExternalAgents.WhyNot();

    /// <summary>The connected agents that have a CLI of their own: a template's, or the entry's <c>console</c>.</summary>
    public IReadOnlyList<AiConsoleAgentChoice> AiConsoleAgents =>
        [.. ExternalAgents.Configured.Where(a => AgentClis.For(a.Key, a.Value) is not null).OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase).Select(a => new AiConsoleAgentChoice(a.Key, AgentDisplayName(a.Key, a.Value)))];

    private static string AgentDisplayName(string name, AcpAgentConfig config) => AcpAgentTemplates.FromTag(config.Template)?.Name ?? name;

    /// <summary>The <c>aiConsoleAvailable</c> context key: an agent with a CLI is connected.</summary>
    private void UpdateAiConsoleContext() => Workbench.Context.Set("aiConsoleAvailable", AiConsoleAgents.Count > 0);

    /// <summary>Shows AI Console and starts its consoles (the kept ones, or the first).</summary>
    public void ShowAiConsole()
    {
        Layout.ShowToolWindow(AiConsoleViewModel.ToolWindowId, focus: true);
        ActivateAiConsole();
    }

    /// <summary>
    /// The AI key and the entry points (ADR 0095): AI Console when an agent with a CLI is connected, else AI Chat, which
    /// the built-in assistant and agents without a CLI use.
    /// </summary>
    public void ShowAiConsoleOrChat()
    {
        if (AiConsoleAgents.Count > 0)
        {
            ShowAiConsole();
        }
        else
        {
            ShowAiChat();
        }
    }

    /// <summary>Stops the consoles that may no longer run (the gate, another workspace, an agent removed) and shows why.</summary>
    private void EnforceAiConsole()
    {
        UpdateAiConsoleContext();
        var root = Ai.WorkspaceRoot;
        if (AiConsole.Tabs.Count > 0 && (ExternalAgents.WhyNot() is not null || !string.Equals(root, _consoleRoot, StringComparison.Ordinal)))
        {
            // Kept for this workspace: they start again when it may run them.
            CloseAiConsoles();
        }

        var configured = ExternalAgents.Configured;
        foreach (var tab in AiConsole.Tabs.Where(t => !configured.ContainsKey(t.Agent)).ToList())
        {
            KillAiConsole(tab);
        }

        AiConsole.Refresh();
        if (Layout.IsVisible(AiConsoleViewModel.ToolWindowId))
        {
            ActivateAiConsole();
        }
    }

    /// <summary>Ends every console without forgetting them: the workspace's kept list stays as it is.</summary>
    private void CloseAiConsoles()
    {
        foreach (var tab in AiConsole.Tabs.ToList())
        {
            EndAiConsole(tab);
            AiConsole.Remove(tab);
        }

        _consoleRoot = null;
        _consoleStartedRoot = null;
    }

    private void EndAiConsole(AiConsoleTabViewModel tab)
    {
        tab.Resources?.Dispose();
        tab.Resources = null;
        if (tab.Session is { } session)
        {
            // Its end is this one's doing: not an exit the tab reports, and nothing to start fresh.
            tab.Session = null;
            Terminals.Close(session);
        }
    }

    public void ActivateAiConsole()
    {
        AiConsole.Refresh();
        if (ExternalAgents.WhyNot() is not null || AiConsole.Tabs.Count > 0 || Ai.WorkspaceRoot is not { } root || string.Equals(_consoleStartedRoot, root, StringComparison.Ordinal))
        {
            return; // closed by the user in this workspace: a console doesn't start again by itself
        }

        var agents = AiConsoleAgents;
        if (agents.Count == 0)
        {
            return;
        }

        _consoleStartedRoot = root;
        _ = RestoreAiConsolesAsync(root, agents);
    }

    /// <summary>The workspace's kept consoles, each with its conversation where its CLI can resume one; none kept: one for the agent chosen last.</summary>
    private async Task RestoreAiConsolesAsync(string root, IReadOnlyList<AiConsoleAgentChoice> agents)
    {
        var kept = _consoleState.Load(root).Where(t => agents.Any(a => a.Name == t.Agent)).ToList();
        if (kept.Count == 0)
        {
            await StartAiConsoleAsync(DefaultConsoleAgent(agents), AgentConsoleStart.Fresh, null, null).ConfigureAwait(true);
            return;
        }

        var continued = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tab in kept)
        {
            var cli = ExternalAgents.Configured.TryGetValue(tab.Agent, out var config) ? AgentClis.For(tab.Agent, config) : null;
            var start = cli switch
            {
                { ResumeSessionFlag: not null } when tab.Session is not null => AgentConsoleStart.ResumeOwn,

                // The CLI's last conversation in this folder: only one console can be it.
                { ContinueArgs.Count: > 0 } when continued.Add(tab.Agent) => AgentConsoleStart.ContinueLast,
                _ => AgentConsoleStart.Fresh,
            };
            await StartAiConsoleAsync(tab.Agent, start, start == AgentConsoleStart.ResumeOwn ? tab.Session : null, null).ConfigureAwait(true);
        }

        if (AiConsole.Tabs.Count > 0)
        {
            AiConsole.Selected = AiConsole.Tabs[0];
        }
    }

    private string DefaultConsoleAgent(IReadOnlyList<AiConsoleAgentChoice> agents)
    {
        var last = Workbench.Settings.GetLayerValue(ConsoleAgentSetting, SettingScope.User) is { ValueKind: System.Text.Json.JsonValueKind.String } value ? value.GetString() : null;
        return agents.FirstOrDefault(a => a.Name == last)?.Name ?? agents.FirstOrDefault(a => a.Name == ExternalAgents.ChatAgent)?.Name ?? agents[0].Name;
    }

    public async Task NewAiConsoleAsync(string? agent)
    {
        Layout.ShowToolWindow(AiConsoleViewModel.ToolWindowId, focus: true);
        AiConsole.Refresh();
        var agents = AiConsoleAgents;
        if (agents.Count == 0 || ExternalAgents.WhyNot() is not null)
        {
            return; // the window says why
        }

        _consoleStartedRoot = Ai.WorkspaceRoot;
        await StartAiConsoleAsync(agent is not null && agents.Any(a => a.Name == agent) ? agent : DefaultConsoleAgent(agents), AgentConsoleStart.Fresh, null, null).ConfigureAwait(true);
    }

    public async Task RestartAiConsoleAsync(AiConsoleTabViewModel tab)
    {
        if (!AiConsole.Tabs.Contains(tab))
        {
            return;
        }

        EndAiConsole(tab);
        await StartAiConsoleAsync(tab.Agent, AgentConsoleStart.Fresh, null, tab).ConfigureAwait(true);
    }

    public void KillAiConsole(AiConsoleTabViewModel tab)
    {
        EndAiConsole(tab);
        AiConsole.Remove(tab);
        SaveAiConsoles();
    }

    /// <summary>
    /// Starts the CLI of <paramref name="agent"/> in a console (<paramref name="into"/>: that tab's again): the gate, the
    /// entry and its keys, the program found on the PATH outside the workspace, a pipe for the IDE's tools where the CLI
    /// takes one, then the terminal session in the workspace's folder. What stops it is said in the window.
    /// </summary>
    private async Task<AiConsoleTabViewModel?> StartAiConsoleAsync(string agent, AgentConsoleStart start, string? sessionId, AiConsoleTabViewModel? into)
    {
        if (Interlocked.Exchange(ref _consoleStarting, 1) == 1)
        {
            // One at a time: the kept consoles start in their order, and a second click waits for the first.
            while (Interlocked.CompareExchange(ref _consoleStarting, 1, 0) == 1)
            {
                await Task.Delay(50).ConfigureAwait(true);
            }
        }

        IDisposable? resources = null;
        try
        {
            if (ExternalAgents.WhyNot() is { } why)
            {
                AiConsole.ShowNotice(why);
                return null;
            }

            var root = Ai.WorkspaceRoot!;
            if (!ExternalAgents.Configured.TryGetValue(agent, out var config) || AgentClis.For(agent, config) is not { } cli)
            {
                AiConsole.ShowNotice(Localizer.Format("msg.aiConsole.noCli", "{0} has no CLI AI Console can start. Use AI Chat with it.", agent));
                return null;
            }

            var name = AgentDisplayName(agent, config);
            try
            {
                config = await ExternalAgents.ResolveAsync(agent, config, _lifetime.Token).ConfigureAwait(true);
            }
            catch (AcpException ex)
            {
                AiConsole.ShowNotice(ex.Message);
                return null;
            }

            // The folder, AI or trust may have changed while the keys were read.
            if (ExternalAgents.WhyNot() is { } now || !string.Equals(Ai.WorkspaceRoot, root, StringComparison.Ordinal))
            {
                AiConsole.Refresh();
                return null;
            }

            var inherited = AgentSearchPath ?? Environment.GetEnvironmentVariable("PATH");
            var searchPath = config.NodeFolder is { } node ? node + Path.PathSeparator + inherited : inherited;
            if (AgentConsoleLauncher.FindProgram(cli, searchPath, root, out var problem) is not { } program)
            {
                var canInstall = AgentClis.Install(cli, PlatformInfo.Current).Count > 0;
                AiConsole.ShowNotice(Localizer.Format("msg.aiConsole.cliMissing", "{0}'s console needs its CLI: {1}", name, problem), installAgent: canInstall ? agent : null);
                Ai.LogAgent(agent, "AI Console: " + problem);
                return null;
            }

            if (start == AgentConsoleStart.Fresh && cli.NewSessionFlag is not null)
            {
                sessionId = Guid.NewGuid().ToString("D");
            }

            var caller = Localizer.Format("msg.aiConsole.caller", "{0} (AI Console)", name);
            var mcp = cli.Mcp == AgentCliMcp.None ? null : AcpHost.StartMcpServer(caller);
            var configFile = Path.Combine(Paths.State, "ai-console", Guid.NewGuid().ToString("N")[..12] + ".json");
            var launch = AgentConsoleLauncher.Build(cli, program, config, mcp?.Server, start, sessionId, configFile, inherited);
            var files = new ConsoleResources(mcp?.Lifetime, [.. launch.Files.Select(f => f.Path)]);
            resources = files;
            var (file, arguments) = (launch.Program, launch.Arguments);
            if (OperatingSystem.IsWindows())
            {
                // npm's CLIs are .cmd scripts, which only cmd.exe runs: without the IDE's tools when it can't be given
                // their arguments as they are.
                try
                {
                    (file, arguments) = WindowsStart(launch, searchPath);
                }
                catch (AcpException) when (launch.IdeTools)
                {
                    launch = AgentConsoleLauncher.WithoutTools(cli, program, config, start, sessionId, inherited);
                    (file, arguments) = WindowsStart(launch, searchPath);
                }
            }

            if (!launch.IdeTools)
            {
                files.Dispose();
                resources = null;
            }
            else
            {
                foreach (var (path, text) in launch.Files)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path, text, _lifetime.Token).ConfigureAwait(true);
                }
            }

            TerminalSession session;
            try
            {
                session = Terminals.Run(new TerminalLaunch(name, file, arguments, root, launch.Environment));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                AiConsole.ShowNotice(Localizer.Format("msg.aiConsole.couldNotStart", "Could not start {0}: {1}", Path.GetFileName(program), ex.Message));
                return null;
            }

            Ai.LogAgent(agent, "AI Console: " + launch.CommandLine + (launch.IdeTools ? "" : " (without the IDE's tools)"));
            var tab = into ?? new AiConsoleTabViewModel(agent, name, cli);
            tab.SessionId = cli.ResumeSessionFlag is null ? null : sessionId;
            tab.Start = start;
            tab.Started = DateTime.UtcNow;
            tab.Resources = resources;
            tab.IdeTools = launch.IdeTools;
            tab.Session = session;
            resources = null;
            _consoleRoot = root;
            if (into is null)
            {
                AiConsole.Add(tab);
            }
            else
            {
                AiConsole.OnTabChanged(tab);
            }

            AiConsole.ShowNotice(null);
            SaveAiConsoles();

            // Its end, watched only now that its tab is there: a CLI that fails at once (nothing to resume) has ended
            // already, and Ui.Post runs at once on this thread, so an exit handled before the tab was added found no
            // tab and was lost. Subscribed before its exit code is read, so an exit in between isn't missed either.
            session.Exited += (_, code) => Ui.Post(() => OnAiConsoleExited(tab, session, code));
            if (session.ExitCode is { } early)
            {
                OnAiConsoleExited(tab, session, early);
            }

            if (!string.Equals(Workbench.Settings.GetLayerValue(ConsoleAgentSetting, SettingScope.User) is { ValueKind: System.Text.Json.JsonValueKind.String } last ? last.GetString() : null, agent, StringComparison.Ordinal))
            {
                await Workbench.Settings.SetAsync(ConsoleAgentSetting, agent, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
            }

            return tab;
        }
        catch (Exception ex) when (ex is AcpException or IOException or UnauthorizedAccessException)
        {
            // A script cmd.exe can't be given, or the run's config file couldn't be written.
            AiConsole.ShowNotice(ex.Message);
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            resources?.Dispose();
            Volatile.Write(ref _consoleStarting, 0);
        }
    }

    private static (string File, IReadOnlyList<string> Arguments) WindowsStart(AgentConsoleLaunch launch, string? searchPath)
    {
        var start = WindowsCommand.Launch(launch.Program, launch.Arguments, searchPath);
        return (start.FileName, start.Arguments);
    }

    /// <summary>
    /// A console's CLI ended: its pipe closes and its tab stays with what it printed (Restart starts it again). A resumed
    /// conversation that ends with an error right away had nothing to resume: it starts fresh, once.
    /// </summary>
    private void OnAiConsoleExited(AiConsoleTabViewModel tab, TerminalSession session, int code)
    {
        if (tab.Session != session || _lifetime.IsCancellationRequested)
        {
            return; // started again meanwhile, or the IDE is closing (its consoles stay kept)
        }

        tab.Resources?.Dispose();
        tab.Resources = null;
        if (!AiConsole.Tabs.Contains(tab))
        {
            return;
        }

        if (tab.Start != AgentConsoleStart.Fresh && code != 0 && DateTime.UtcNow - tab.Started < ConsoleResumeGrace && ExternalAgents.WhyNot() is null)
        {
            Ai.LogAgent(tab.Agent, $"AI Console: nothing to resume (exit {code}); a new conversation starts");
            tab.Session = null;
            Terminals.Close(session);
            _ = StartAiConsoleAsync(tab.Agent, AgentConsoleStart.Fresh, null, tab);
            return;
        }

        tab.Exited = true;
        AiConsole.OnTabChanged(tab);
        SaveAiConsoles();
    }

    /// <summary>Keeps the running consoles of this workspace (an ended one isn't kept).</summary>
    private void SaveAiConsoles()
    {
        if (_consoleRoot is { } root && !_lifetime.IsCancellationRequested)
        {
            _consoleState.Save(root, [.. AiConsole.Tabs.Where(t => !t.Exited && t.Session is not null).Select(t => new AgentConsoleTab(t.Agent, t.SessionId))]);
        }
    }

    Task IAiConsoleHost.ConnectAgentFromAiConsoleAsync() => ConnectExternalAgentAsync();

    /// <summary>
    /// Installs an agent's CLI for its console: the exact command is shown first, as Connect shows its installs; a CLI
    /// Codalus has no pinned install for opens its own page.
    /// </summary>
    public async Task InstallAiConsoleCliAsync(string agent)
    {
        if (!ExternalAgents.Configured.TryGetValue(agent, out var config) || AgentClis.For(agent, config) is not { } cli
            || AgentClis.Install(cli, PlatformInfo.Current) is not [var option, ..])
        {
            return;
        }

        if (option.Kind == InstallKind.OpenPage)
        {
            OpenUrl(option.Command);
            return;
        }

        var install = Localizer.Get("msg.aiConsole.install", "Install");
        var choice = await Dialogs.ShowAsync(new DialogRequest(
            Localizer.Format("msg.aiConsole.installTitle", "Install {0}?", cli.Program),
            Localizer.Format("msg.aiConsole.installMessage", "AI Console starts {0}'s own CLI, which isn't on your PATH. This command installs it:", AgentDisplayName(agent, config)),
            [install, Localizer.Get("msg.acp.cancel", "Cancel")])
        {
            Details = [new DetailLine(option.Command)],
            DefaultButton = install,
            Icon = "terminal",
        }).ConfigureAwait(true);
        if (choice.Button != install)
        {
            return;
        }

        int? exit;
        try
        {
            AiConsole.ShowNotice(Localizer.Format("msg.aiConsole.installing", "Installing {0}…", cli.Program));
            exit = await Task.Run(() => AgentConnector.InstallAsync(agent, option, new ShellConnectHost(this), line => Ai.LogAgent(agent, line), _lifetime.Token)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is AcpException or IOException or InvalidOperationException or OperationCanceledException)
        {
            AiConsole.ShowNotice(Localizer.Format("msg.aiConsole.installFailed", "{0} wasn't installed: {1}", cli.Program, ex.Message), installAgent: agent);
            return;
        }

        if (exit != 0)
        {
            AiConsole.ShowNotice(Localizer.Format("msg.aiConsole.installExit", "{0} wasn't installed (exit code {1}). The AI Output has the command's output.", cli.Program, exit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"), installAgent: agent);
            return;
        }

        AiConsole.ShowNotice(null);
        await NewAiConsoleAsync(agent).ConfigureAwait(true);
    }

    /// <summary>Add Selection to AI Console: a reference to the editor's file and its selected lines, never the text.</summary>
    public Task AddSelectionToAiConsoleAsync()
    {
        if (ActiveEditor is not { Controller: { } controller } || Layout.ActiveDocument?.FilePath is not { } path)
        {
            return Task.CompletedTask;
        }

        var editor = controller.Editor;
        var document = editor.Document;
        var (start, length) = (editor.SelectionStart, editor.SelectionLength);
        var first = document.GetLineByOffset(length == 0 ? editor.CaretOffset : start);
        var last = length == 0 ? first : document.GetLineByOffset(start + length);

        // A selection that ends at a line's start doesn't include that line.
        if (last.LineNumber > first.LineNumber && last.Offset == start + length)
        {
            last = last.PreviousLine;
        }

        return AddToAiConsoleAsync(path, first.LineNumber, last.LineNumber);
    }

    /// <summary>Add File to AI Console: the given file, or Solution Explorer's selection while it has the focus, or the editor's.</summary>
    public Task AddFileToAiConsoleAsync(string? path)
    {
        path ??= Workbench.Context.GetValue("focusedView") is "solutionExplorer" ? Explorer.Selected?.Node.Path ?? Layout.ActiveDocument?.FilePath : Layout.ActiveDocument?.FilePath ?? Explorer.Selected?.Node.Path;
        return path is null ? Task.CompletedTask : AddToAiConsoleAsync(path, null, null);
    }

    Task IAiConsoleHost.AddFileToAiConsoleAsync(string path) => AddToAiConsoleAsync(path, null, null);

    /// <summary>
    /// Types a reference to a file (and lines) into the selected console's prompt, in the form its CLI reads, without
    /// sending it. The agent reads the file itself, so nothing of it passes through Codalus; a file the AI boundary
    /// excludes (ADR 0062) isn't named.
    /// </summary>
    private async Task AddToAiConsoleAsync(string path, int? firstLine, int? lastLine)
    {
        if (ExternalAgents.WhyNot() is { } why)
        {
            ShowAiConsole();
            ShowMessage(why);
            return;
        }

        var root = Ai.WorkspaceRoot;
        if (await Ai.Exclusions.ExclusionReasonAsync(path, root, _lifetime.Token).ConfigureAwait(true) is { } reason)
        {
            ShowMessage(Localizer.Format("msg.aiConsole.excluded", "{0} isn't added to AI Console: {1}", Path.GetFileName(path), reason));
            return;
        }

        Layout.ShowToolWindow(AiConsoleViewModel.ToolWindowId, focus: true);
        AiConsole.Refresh();
        var tab = AiConsole.Selected is { Exited: false, Session: not null } selected ? selected : AiConsole.Tabs.FirstOrDefault(t => t is { Exited: false, Session: not null });
        if (tab is null)
        {
            var agents = AiConsoleAgents;
            if (agents.Count == 0)
            {
                return; // the window says why
            }

            _consoleStartedRoot = root;
            tab = await StartAiConsoleAsync(DefaultConsoleAgent(agents), AgentConsoleStart.Fresh, null, null).ConfigureAwait(true);
            if (tab?.Session is not { } started)
            {
                return;
            }

            // Text sent before the CLI reads its terminal can be dropped.
            await started.WhenStartedAsync(TimeSpan.FromSeconds(10), _lifetime.Token).ConfigureAwait(true);
        }

        AiConsole.Selected = tab;
        if (_models.TryGetValue(path, out var model) && model.IsModified)
        {
            ShowMessage(Localizer.Format("msg.aiConsole.unsaved", "{0} has unsaved changes: the agent reads the file as it is on the disk.", Path.GetFileName(path)));
        }

        tab.Session!.Write(AgentConsoleReference.Format(tab.Cli.Reference, path, root, firstLine, lastLine));
    }

    /// <summary>A console's pipe for the IDE's tools and the config files of its run.</summary>
    private sealed class ConsoleResources(IDisposable? pipe, IReadOnlyList<string> files) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            pipe?.Dispose();
            foreach (var file in files)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left in the state folder: it names a pipe that is closed.
                }
            }
        }
    }
}
