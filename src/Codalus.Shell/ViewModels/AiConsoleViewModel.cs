using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.Terminal;
using Codalus.Workbench.Ai.Cli;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Terminal;

namespace Codalus.Shell.ViewModels;

/// <summary>An agent AI Console can start: its entry's name in <c>ai.agents</c> and the name shown.</summary>
public sealed record AiConsoleAgentChoice(string Name, string DisplayName);

/// <summary>What AI Console needs from the shell (ADR 0095): the gate, the agents, and its consoles' processes.</summary>
public interface IAiConsoleHost
{
    /// <summary>Why no agent's CLI may run now (the external agents' gate, ADR 0079), or null.</summary>
    string? WhyAiConsoleCantRun { get; }

    /// <summary>The connected agents that have a CLI, in the order the picker shows them.</summary>
    IReadOnlyList<AiConsoleAgentChoice> AiConsoleAgents { get; }

    /// <summary>The window is shown: its kept consoles start again, or the first one starts.</summary>
    void ActivateAiConsole();

    /// <summary>A new console for <paramref name="agent"/> (null: the one chosen last).</summary>
    Task NewAiConsoleAsync(string? agent);

    /// <summary>Ends the console's CLI and starts it again with a new conversation.</summary>
    Task RestartAiConsoleAsync(AiConsoleTabViewModel tab);

    /// <summary>Ends the console's CLI and closes its tab.</summary>
    void KillAiConsole(AiConsoleTabViewModel tab);

    /// <summary>Connect to an Agent….</summary>
    Task ConnectAgentFromAiConsoleAsync();

    /// <summary>Installs the CLI of <paramref name="agent"/> the way its template knows, or opens its page.</summary>
    Task InstallAiConsoleCliAsync(string agent);

    /// <summary>Shows AI Chat.</summary>
    void ShowAiChatWindow();

    /// <summary>Types a reference to <paramref name="path"/> into the selected console's prompt (a file dropped on it).</summary>
    Task AddFileToAiConsoleAsync(string path);
}

/// <summary>One console: an agent's CLI in a terminal session, with the IDE's tools on a pipe of its own.</summary>
public sealed class AiConsoleTabViewModel(string agent, string displayName, AgentCli cli) : ObservableObject
{
    private TerminalSession? _session;
    private string _title = displayName;
    private bool _exited;
    private bool _ideTools;
    private int _number = 1;

    /// <summary>The agent's name in <c>ai.agents</c>.</summary>
    public string Agent { get; } = agent;

    public string DisplayName { get; } = displayName;

    public AgentCli Cli { get; } = cli;

    /// <summary>The conversation's id, when Codalus gave the CLI one (so this console resumes its own).</summary>
    public string? SessionId { get; internal set; }

    /// <summary>How the conversation started (a resumed one that fails at once starts fresh, once).</summary>
    internal AgentConsoleStart Start { get; set; }

    internal DateTime Started { get; set; }

    /// <summary>The console's pipe for the IDE's tools and its config file: disposed when its CLI ends.</summary>
    internal IDisposable? Resources { get; set; }

    /// <summary>Which of the agent's consoles this is (the second shows "Claude 2").</summary>
    internal int Number
    {
        get => _number;
        set
        {
            _number = value;
            UpdateTitle();
        }
    }

    public TerminalSession? Session
    {
        get => _session;
        internal set
        {
            if (SetField(ref _session, value))
            {
                Exited = false;
            }
        }
    }

    /// <summary>The IDE's tools are connected to this console's CLI.</summary>
    public bool IdeTools
    {
        get => _ideTools;
        internal set => SetField(ref _ideTools, value);
    }

    public string Title
    {
        get => _title;
        private set => SetField(ref _title, value);
    }

    public bool Exited
    {
        get => _exited;
        internal set
        {
            if (SetField(ref _exited, value))
            {
                UpdateTitle();
            }
        }
    }

    private void UpdateTitle()
    {
        var name = _number > 1 ? $"{DisplayName} {_number}" : DisplayName;
        Title = _exited ? Localizer.Format("msg.aiConsole.exited", "{0} (exited: {1})", name, _session?.ExitCode ?? 0) : name;
    }
}

/// <summary>
/// The AI Console tool window (ADR 0095): a connected agent's own CLI in the IDE's terminal, a tab per console, in the
/// bottom group beside the Output and the Terminal. It runs under the external agents' gate, says so in its label, and
/// gives each CLI the IDE's tools on a pipe of its own where the CLI can take them.
/// TODO(issue): find in a console, and AI Console in remote windows (ADR 0095).
/// </summary>
public sealed class AiConsoleViewModel : ObservableObject
{
    public const string ToolWindowId = "aiConsole";

    private readonly IAiConsoleHost _host;
    private AiConsoleTabViewModel? _selected;
    private string? _notice;
    private string? _installAgent;
    private bool _canConnect;

    public AiConsoleViewModel(IAiConsoleHost host, TerminalAppearance appearance, Action<TerminalLinkActivation> openLink)
    {
        _host = host;
        Appearance = appearance;
        OpenLink = openLink;
        NewCommand = new RelayCommand(p => _ = _host.NewAiConsoleAsync(p as string), _ => _host.WhyAiConsoleCantRun is null && Agents.Count > 0);
        RestartCommand = new RelayCommand(() => _ = _host.RestartAiConsoleAsync(_selected!), () => _selected is not null && _host.WhyAiConsoleCantRun is null);
        KillCommand = new RelayCommand(() => _host.KillAiConsole(_selected!), () => _selected is not null);
        ConnectCommand = new RelayCommand(() => _ = _host.ConnectAgentFromAiConsoleAsync());
        InstallCommand = new RelayCommand(() => _ = _host.InstallAiConsoleCliAsync(_installAgent!), () => _installAgent is not null);
        ShowChatCommand = new RelayCommand(_host.ShowAiChatWindow);
        Refresh();
    }

    public ObservableCollection<AiConsoleTabViewModel> Tabs { get; } = [];

    /// <summary>The agents a new console can start.</summary>
    public ObservableCollection<AiConsoleAgentChoice> Agents { get; } = [];

    public TerminalAppearance Appearance { get; }

    /// <summary>Opens a Ctrl+clicked link (a URL in the browser, a file in the editor).</summary>
    public Action<TerminalLinkActivation> OpenLink { get; }

    public ICommand NewCommand { get; }

    public ICommand RestartCommand { get; }

    public ICommand KillCommand { get; }

    public ICommand ConnectCommand { get; }

    public ICommand InstallCommand { get; }

    public ICommand ShowChatCommand { get; }

    /// <summary>The label every console shows (ADR 0095, amending ADR 0079's).</summary>
    public static string Label => Localizer.Get("msg.aiConsole.label", "External agent's own CLI: it uses its own provider and data policy, and changes files and runs commands itself, asking you with its own prompts. Codalus doesn't preview its edits.");

    public AiConsoleTabViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasConsole));
                OnPropertyChanged(nameof(ToolsNote));
                OnPropertyChanged(nameof(ShowEmptyState));
                RaiseCommands();
            }
        }
    }

    public bool HasConsole => _selected is not null;

    /// <summary>Whether the selected console's CLI has the IDE's tools, in words (after the label).</summary>
    public string ToolsNote => _selected switch
    {
        null => string.Empty,
        { IdeTools: true } => Localizer.Get("msg.aiConsole.toolsConnected", "The IDE's tools are connected (codalus)."),
        var tab => Localizer.Format("msg.aiConsole.toolsNotConnected", "The IDE's tools aren't connected: {0} takes no MCP server for one run.", tab.Cli.Program),
    };

    /// <summary>Why nothing runs, or what went wrong starting a console; null when there is nothing to say.</summary>
    public string? Notice
    {
        get => _notice;
        private set
        {
            if (SetField(ref _notice, value))
            {
                OnPropertyChanged(nameof(HasNotice));
                OnPropertyChanged(nameof(ShowEmptyState));
            }
        }
    }

    public bool HasNotice => _notice is not null;

    /// <summary>The notice offers Connect to an Agent… (no agent with a CLI is connected).</summary>
    public bool CanConnect
    {
        get => _canConnect;
        private set => SetField(ref _canConnect, value);
    }

    /// <summary>The notice offers to install the CLI of an agent whose program isn't on the PATH.</summary>
    public bool CanInstall => _installAgent is not null;

    /// <summary>No console and nothing to say: the hint to start one.</summary>
    public bool ShowEmptyState => _selected is null && _notice is null;

    /// <summary>The agents and the gate, again (settings, the workspace, its trust or AI's state changed).</summary>
    public void Refresh()
    {
        var agents = _host.AiConsoleAgents;
        if (!Agents.SequenceEqual(agents))
        {
            Agents.Clear();
            foreach (var agent in agents)
            {
                Agents.Add(agent);
            }
        }

        if (_host.WhyAiConsoleCantRun is { } why)
        {
            ShowNotice(why);
        }
        else if (agents.Count == 0)
        {
            ShowNotice(Localizer.Get("msg.aiConsole.noAgent", "No agent with a CLI of its own is connected. Connect one, or use AI Chat."), connect: true);
        }
        else if (_installAgent is null || agents.All(a => a.Name != _installAgent))
        {
            ShowNotice(null);
        }

        RaiseCommands();
    }

    /// <summary>Says why a console didn't start; <paramref name="installAgent"/> offers its CLI's install.</summary>
    public void ShowNotice(string? notice, bool connect = false, string? installAgent = null)
    {
        _installAgent = notice is null ? null : installAgent;
        CanConnect = notice is not null && connect;
        Notice = notice;
        OnPropertyChanged(nameof(CanInstall));
        ((RelayCommand)InstallCommand).RaiseCanExecuteChanged();
    }

    /// <summary>The window is shown (the view attached, or its command ran).</summary>
    public void Activate() => _host.ActivateAiConsole();

    /// <summary>A file dropped on the console: a reference to it goes into the prompt.</summary>
    public Task AddFileAsync(string path) => _host.AddFileToAiConsoleAsync(path);

    internal void Add(AiConsoleTabViewModel tab)
    {
        Tabs.Add(tab);
        Renumber();
        Selected = tab;
    }

    internal void Remove(AiConsoleTabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        Tabs.RemoveAt(index);
        Renumber();
        if (_selected == tab)
        {
            Selected = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }
    }

    /// <summary>The selected console changed what it shows (its CLI started again, or its tools).</summary>
    internal void OnTabChanged(AiConsoleTabViewModel tab)
    {
        if (tab == _selected)
        {
            OnPropertyChanged(nameof(ToolsNote));
        }

        RaiseCommands();
    }

    private void Renumber()
    {
        foreach (var group in Tabs.GroupBy(t => t.Agent, StringComparer.Ordinal))
        {
            var number = 0;
            foreach (var tab in group)
            {
                tab.Number = ++number;
            }
        }
    }

    private void RaiseCommands()
    {
        ((RelayCommand)NewCommand).RaiseCanExecuteChanged();
        ((RelayCommand)RestartCommand).RaiseCanExecuteChanged();
        ((RelayCommand)KillCommand).RaiseCanExecuteChanged();
    }
}
