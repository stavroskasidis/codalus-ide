using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Rpc;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Acp;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Setup;

namespace Codalus.Shell.ViewModels;

/// <summary>What the Connect dialog shows now (D43, docs/design/controls/AgentConnect.dc.html).</summary>
public enum ConnectAgentMode
{
    /// <summary>Looking for what the agent needs.</summary>
    Checking,

    /// <summary>Everything is there: how to sign in, what runs, the consent.</summary>
    Connect,

    /// <summary>Something is missing or too old: how to install it.</summary>
    Missing,

    /// <summary>An install runs in the Terminal or here; the dialog checks again when it ends.</summary>
    Installing,

    /// <summary>The connection test's checklist.</summary>
    Testing,

    /// <summary>Connected, not confirmed, or failed.</summary>
    Result,

    /// <summary>A remote window: nothing is connected from here.</summary>
    Remote,
}

/// <summary>What the dialog needs from the shell: the connector, the terminal, the log, a page, AI Chat.</summary>
public interface IConnectAgentHost : IAcpConnectHost
{
    AcpAgentConnector Connector { get; }

    AiService Ai { get; }

    /// <summary>Agents' keys in the credential store, or null when the window has none.</summary>
    AcpAgentSecrets? Secrets { get; }

    /// <summary>The user settings file, as the result says where the entry went.</summary>
    string UserSettingsPath { get; }

    /// <summary>This IDE runs in an SSH session (or with NO_BROWSER): Claude offers its remote sign-in then.</summary>
    bool IsRemoteSession { get; }

    /// <summary>Inline suggestions are on in the user's settings (they come back with AI).</summary>
    bool InlineSuggestionsOn { get; }

    void OpenUrl(string url);

    void ShowTerminal();

    void ShowOutput();

    /// <summary>The agent was written to the settings: AI Chat's picker follows.</summary>
    void Connected(string agent);

    /// <summary>Start a Chat: AI Chat picks the agent and takes the focus.</summary>
    void StartChat(string agent);
}

/// <summary>A program row of Requirements.</summary>
public sealed class ConnectRequirementRow(AcpRequirement requirement, string agent)
{
    public AcpRequirement Requirement { get; } = requirement;

    public string Name => Requirement.Version is { } version ? $"{Requirement.Name} {version}" : Requirement.Name;

    public string Detail
    {
        get
        {
            var needs = Requirement.Needed is { } needed ? Localizer.Format("msg.acp.connect.needs", "{0} needs {1} or later", agent, needed) : null;
            var where = Requirement.State == AcpRequirementState.Missing
                ? Requirement.Id == "npx" ? Localizer.Get("msg.acp.connect.npxMissing", "Not found (it comes with Node.js)") : Localizer.Get("msg.acp.connect.notOnPath", "Not found on the PATH")
                : Requirement.Path ?? "";
            return string.Join(" · ", new[] { where, needs }.Where(s => s is { Length: > 0 }));
        }
    }

    public bool IsFound => Requirement.State == AcpRequirementState.Found;

    public bool IsMissing => Requirement.State == AcpRequirementState.Missing;

    public bool IsTooOld => Requirement.State == AcpRequirementState.TooOld;

    public string Icon => Requirement.State switch
    {
        AcpRequirementState.Found => "test-passed",
        AcpRequirementState.TooOld => "warning",
        _ => "test-failed",
    };

    public string? Chip => Requirement.State switch
    {
        AcpRequirementState.Missing => Localizer.Get("msg.acp.connect.missing", "Missing"),
        AcpRequirementState.TooOld => Localizer.Get("msg.acp.connect.tooOld", "Too old"),
        _ => null,
    };

    public bool HasChip => Chip is not null;

    public string AccessibleName => string.Join(", ", new[] { Name, Chip, Detail }.Where(s => s is { Length: > 0 }));
}

/// <summary>A radio row: a way to sign in, or a way to install.</summary>
public sealed class ConnectChoice(string id, string name, string description, object payload) : ObservableObject
{
    private bool _isSelected;

    public string Id { get; } = id;

    public string Name { get; } = name;

    public string Description { get; } = description;

    public object Payload { get; } = payload;

    /// <summary>The exact command, when it runs one.</summary>
    public string? Command { get; init; }

    public bool HasCommand => Command is { Length: > 0 };

    /// <summary>How it runs ("Runs in the Terminal: it asks for your password").</summary>
    public string? Meta { get; init; }

    public string? MetaIcon { get; init; }

    public bool HasMeta => Meta is { Length: > 0 };

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetField(ref _isSelected, value) && value)
            {
                Selected?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? Selected;

    public string AccessibleName => Name + ": " + Description;
}

/// <summary>A labeled line of What runs: a command, or a plain sentence.</summary>
public sealed record ConnectRunsLine(string Label, string? Command, string? Text)
{
    public bool HasCommand => Command is { Length: > 0 };

    public bool HasText => Text is { Length: > 0 };
}

/// <summary>A step of the test's checklist.</summary>
public sealed class ConnectStep(AcpTestStep step, string name, string? detail) : ObservableObject
{
    private string _name = name;
    private string? _detail = detail;
    private AcpStepState? _state;
    private string? _callCommand;
    private string? _callHint;

    public AcpTestStep Step { get; } = step;

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string? Detail
    {
        get => _detail;
        set
        {
            if (SetField(ref _detail, value))
            {
                OnPropertyChanged(nameof(HasDetail));
            }
        }
    }

    public bool HasDetail => Detail is { Length: > 0 };

    /// <summary>Null while it waits.</summary>
    public AcpStepState? State
    {
        get => _state;
        set
        {
            if (SetField(ref _state, value))
            {
                OnPropertyChanged(nameof(Icon));
                OnPropertyChanged(nameof(IsDone));
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(IsWaiting));
                OnPropertyChanged(nameof(ShowBar));
            }
        }
    }

    public string Icon => State switch
    {
        AcpStepState.Done => "test-passed",
        AcpStepState.Running => "test-running",
        AcpStepState.Failed => "test-failed",
        AcpStepState.Skipped => "test-skipped",
        _ => "test-none",
    };

    public bool IsDone => State is AcpStepState.Done;

    public bool IsRunning => State is AcpStepState.Running;

    public bool IsFailed => State is AcpStepState.Failed;

    public bool IsWaiting => State is null or AcpStepState.Skipped;

    /// <summary>The download's bar, while it runs.</summary>
    public bool ShowBar => State is AcpStepState.Running && Step == AcpTestStep.Download;

    /// <summary>The command a sign-in runs in the Terminal, while it does.</summary>
    public string? CallCommand
    {
        get => _callCommand;
        set
        {
            if (SetField(ref _callCommand, value))
            {
                OnPropertyChanged(nameof(HasCall));
            }
        }
    }

    public string? CallHint
    {
        get => _callHint;
        set
        {
            if (SetField(ref _callHint, value))
            {
                OnPropertyChanged(nameof(HasCallHint));
            }
        }
    }

    public bool HasCall => CallCommand is not null;

    public bool HasCallHint => CallHint is { Length: > 0 };

    public string AccessibleName => string.Join(", ", new[] { Name, Detail }.Where(s => s is { Length: > 0 }));
}

/// <summary>A row of the result: what was found.</summary>
public sealed record ConnectResultRow(string Key, string Value);

/// <summary>A fix the result offers, as a button with its icon.</summary>
public sealed record ConnectFix(string Label, string Icon, ICommand Command);

/// <summary>A footer button: primary (the accent) or not, enabled or not, with why not as its tip.</summary>
public sealed record ConnectButton(string Label, bool IsPrimary, ICommand Command, bool IsEnabled = true, string? Tip = null);

/// <summary>
/// Connect to <em>agent</em> (D43, ADR 0083): the in-window dialog that checks what an external agent needs, installs what's
/// missing with the user's own package manager, signs it in, tests the connection on the dialog's unsaved consent, and
/// only then writes its <c>ai.agents</c> entry (and <c>ai.enabled</c>, when its box turned AI on) through
/// <see cref="AcpAgentConnector"/>. One instance, opened per agent; Cancel writes nothing.
/// </summary>
public sealed class ConnectAgentViewModel : ObservableObject, IAcpConnectHost, IDisposable
{
    private readonly IConnectAgentHost _host;
    private AcpAgentTemplate? _template;
    private ConnectAgentMode _mode;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<string?>? _closed;
    private string _apiKey = string.Empty;
    private bool _consent;
    private bool _consentShown;
    private bool _persistent = true;
    private bool _useCodalusNode;
    private AcpRequirements? _requirements;
    private AcpTestResult? _result;
    private bool _saved;
    private string _title = string.Empty;
    private string _context = string.Empty;
    private string? _resultTitle;
    private string? _resultText;
    private string _resultIcon = "test-passed";
    private string? _resultCommand;
    private string? _footerHint;
    private string? _footerLink;
    private Action? _footerLinkRun;
    private string? _installStatus;
    private string? _remoteText;

    public ConnectAgentViewModel(IConnectAgentHost host)
    {
        _host = host;
        CheckAgainCommand = new RelayCommand(() => _ = CheckAsync());
        FooterLinkCommand = new RelayCommand(() => _footerLinkRun?.Invoke());
        CancelCommand = new RelayCommand(Cancel);
        ShowTerminalCommand = new RelayCommand(host.ShowTerminal);
    }

    public bool IsOpen => _closed is not null;

    public AcpAgentTemplate? Template => _template;

    public ConnectAgentMode Mode
    {
        get => _mode;
        private set
        {
            if (SetField(ref _mode, value))
            {
                OnPropertyChanged(nameof(ShowRequirements));
                OnPropertyChanged(nameof(ShowInstall));
                OnPropertyChanged(nameof(ShowForm));
                OnPropertyChanged(nameof(ShowConsent));
                OnPropertyChanged(nameof(ShowSteps));
                OnPropertyChanged(nameof(ShowResult));
                OnPropertyChanged(nameof(ShowRemote));
                OnPropertyChanged(nameof(ShowLabel));
                OnPropertyChanged(nameof(IsChecking));
                OnPropertyChanged(nameof(IsBusy));
            }
        }
    }

    public bool IsChecking => Mode == ConnectAgentMode.Checking;

    /// <summary>A step runs (the check, an install, the test): Connect waits.</summary>
    public bool IsBusy => Mode is ConnectAgentMode.Checking or ConnectAgentMode.Installing or ConnectAgentMode.Testing;

    public bool ShowRequirements => Mode is ConnectAgentMode.Checking or ConnectAgentMode.Connect or ConnectAgentMode.Missing or ConnectAgentMode.Installing;

    public bool ShowInstall => Mode == ConnectAgentMode.Missing;

    /// <summary>Sign in and What runs, once the agent can run.</summary>
    public bool ShowForm => Mode == ConnectAgentMode.Connect;

    public bool ShowConsent => Mode == ConnectAgentMode.Connect && _consentShown;

    public bool ShowSteps => Mode is ConnectAgentMode.Testing or ConnectAgentMode.Installing;

    public bool ShowResult => Mode == ConnectAgentMode.Result;

    public bool ShowRemote => Mode == ConnectAgentMode.Remote;

    public bool ShowLabel => Mode == ConnectAgentMode.Connect;

    public string Title
    {
        get => _title;
        private set => SetField(ref _title, value);
    }

    public string Context
    {
        get => _context;
        private set => SetField(ref _context, value);
    }

    public ObservableCollection<ConnectRequirementRow> Requirements { get; } = [];

    public ICommand CheckAgainCommand { get; }

    public string? InstallTitle => _requirements?.InstallTitle;

    public string? InstallNote => _requirements?.InstallNote;

    public bool HasInstallNote => InstallNote is { Length: > 0 };

    public ObservableCollection<ConnectChoice> InstallOptions { get; } = [];

    /// <summary>A line under the install options while one runs here (winget's output, the download).</summary>
    public string? InstallStatus
    {
        get => _installStatus;
        private set
        {
            if (SetField(ref _installStatus, value))
            {
                OnPropertyChanged(nameof(HasInstallStatus));
            }
        }
    }

    public bool HasInstallStatus => InstallStatus is { Length: > 0 };

    public string SignInAside => _template is null ? string.Empty
        : _template.SignIn.Any(o => o.Kind == AcpSignInKind.ApiKeyEnv)
            ? Localizer.Format("msg.acp.connect.signInAside", "How {0} signs in. Only an API key passes through Codalus.", _template.Name)
            : Localizer.Format("msg.acp.connect.signInAsideOwn", "How {0} signs in. Its sign-in stays with {0}.", _template.Name);

    public ObservableCollection<ConnectChoice> SignInOptions { get; } = [];

    public AcpSignInOption? SelectedSignIn => SignInOptions.FirstOrDefault(c => c.IsSelected)?.Payload as AcpSignInOption;

    public bool ShowKey => SelectedSignIn?.TakesKey == true;

    public string KeyName => SelectedSignIn?.KeyName ?? string.Empty;

    /// <summary>The key typed (held in memory until the entry is written; never logged).</summary>
    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (SetField(ref _apiKey, value))
            {
                UpdateButtons();
            }
        }
    }

    public string KeyNote
    {
        get
        {
            if (_template is null || SelectedSignIn is not { TakesKey: true } option)
            {
                return string.Empty;
            }

            if (option.Kind == AcpSignInKind.ApiKeyOnce)
            {
                return Localizer.Format("msg.acp.connect.keyOnce", "Sent to {0} once, when you connect. {0} keeps it in its own sign-in store ({1}); Disconnect can sign it out. Codalus doesn't keep it.", _template.Name, option.KeyStore ?? "");
            }

            return _persistent
                ? Localizer.Format("msg.acp.connect.keyKept", "Kept in {0}. {1} gets it as {2} when it starts; it's never written to a file.", StoreName, _template.Name, option.EnvVar)
                : Localizer.Format("msg.acp.connect.keyMemory", "Lasts until Codalus closes: the OS credential store isn't available, so the key is kept in memory only. {0} gets it as {1} when it starts; it's never written to a file.", _template.Name, option.EnvVar);
        }
    }

    public bool KeyNoteIsWarning => SelectedSignIn?.Kind == AcpSignInKind.ApiKeyEnv && !_persistent;

    private string StoreName => _host.Secrets?.StoreName ?? Localizer.Get("msg.acp.connect.noStore", "the credential store");

    public string? SignInWarning => _template?.SignInWarning;

    public bool HasSignInWarning => SignInWarning is { Length: > 0 };

    /// <summary>In an SSH session, Claude's sign-in is its own TUI: the hint to sign in with <c>claude</c> and <c>/login</c>.</summary>
    public string? RemoteSessionNote => _template is not null && _host.IsRemoteSession && _template.SignIn.Any(o => o.RemoteMethodId is not null)
        ? Localizer.Format("msg.acp.connect.sshNote", "This is an SSH session, so {0} signs in with its own Log in with Claude in the Terminal. If that doesn't work, run claude in a terminal and type /login, then choose I'm signed in already.", _template.Name)
        : null;

    public bool HasRemoteSessionNote => RemoteSessionNote is not null;

    public ObservableCollection<ConnectRunsLine> Runs { get; } = [];

    public string RunsNote => _template?.Agent.UsesNpx == true
        ? Localizer.Get("msg.acp.connect.runsNpx", "npx downloads the package from npm the first time. It runs as you, with your account's access to your files.")
        : Localizer.Format("msg.acp.connect.runsCli", "{0} must be installed: Codalus doesn't download it. It runs as you, with your account's access to your files.", _template?.Agent.Cli ?? "");

    public bool Consent
    {
        get => _consent;
        set
        {
            if (SetField(ref _consent, value))
            {
                UpdateButtons();
            }
        }
    }

    /// <summary>The consent's line, from the settings: what else comes back on with AI (D43).</summary>
    public string ConsentText
    {
        get
        {
            var ai = _host.Ai;
            var provider = ai.ProviderId is { } id ? ai.Provider?.Label ?? id : null;
            var completions = _host.InlineSuggestionsOn;
            var text = Localizer.Get("msg.acp.connect.consentRuns", "External agents run only while it's on.");
            return (provider, completions) switch
            {
                ({ } p, true) => text + " " + Localizer.Format("msg.acp.connect.consentProviderSuggestions", "This also turns the built-in assistant back on with {0}, and inline suggestions.", p),
                ({ } p, false) => text + " " + Localizer.Format("msg.acp.connect.consentProvider", "This also turns the built-in assistant back on with {0}.", p),
                (null, true) => text + " " + Localizer.Get("msg.acp.connect.consentSuggestions", "This also turns inline suggestions back on, once a provider is chosen."),
                _ => text + " " + Localizer.Get("msg.acp.connect.consentNothingElse", "The built-in assistant still needs a provider before it sends anything."),
            };
        }
    }

    /// <summary>AI is off for this workspace by its settings: Connect writes the entry, but the agent won't run here.</summary>
    public string? WorkspaceNote => _host.Ai.IsOffForWorkspace ? Localizer.Get("msg.acp.connect.offForWorkspace", "AI assistance is off for this workspace by its settings, so the agent won't run here; it will in other folders.") : null;

    public bool HasWorkspaceNote => Mode == ConnectAgentMode.Connect && WorkspaceNote is not null;

    public ObservableCollection<ConnectStep> Steps { get; } = [];

    public string StepsTitle => Mode == ConnectAgentMode.Installing ? Localizer.Get("msg.acp.connect.waitingForTerminal", "Waiting for the install") : Localizer.Get("msg.acp.connect.testing", "Testing the connection");

    public string StepsScope => Mode == ConnectAgentMode.Installing
        ? Localizer.Get("msg.acp.connect.installScope", "Cancel stops waiting; an install in the Terminal goes on there.")
        : Localizer.Format("msg.acp.connect.scope", "In an empty folder ({0}), with no workspace, no MCP servers and no prompt; anything {1} asks the IDE for is refused.", _host.Connector.TestFolder, _template?.Name ?? "");

    public string? ResultTitle
    {
        get => _resultTitle;
        private set => SetField(ref _resultTitle, value);
    }

    public string? ResultText
    {
        get => _resultText;
        private set => SetField(ref _resultText, value);
    }

    public string ResultIcon
    {
        get => _resultIcon;
        private set
        {
            if (SetField(ref _resultIcon, value))
            {
                OnPropertyChanged(nameof(ResultIsOk));
                OnPropertyChanged(nameof(ResultIsWarning));
                OnPropertyChanged(nameof(ResultIsError));
            }
        }
    }

    public bool ResultIsOk => ResultIcon == "test-passed";

    public bool ResultIsWarning => ResultIcon == "warning";

    public bool ResultIsError => ResultIcon == "test-failed";

    public ObservableCollection<ConnectResultRow> ResultRows { get; } = [];

    /// <summary>The tool's own lines for a failure (npm's errors).</summary>
    public string? ResultCommand
    {
        get => _resultCommand;
        private set
        {
            if (SetField(ref _resultCommand, value))
            {
                OnPropertyChanged(nameof(HasResultCommand));
            }
        }
    }

    public bool HasResultCommand => ResultCommand is { Length: > 0 };

    public ObservableCollection<ConnectFix> Fixes { get; } = [];

    public bool HasFixes => Fixes.Count > 0;

    public string RemoteTitle => Localizer.Get("msg.acp.connect.remoteTitle", "Agents can't be connected from a remote window");

    public string? RemoteText
    {
        get => _remoteText;
        private set => SetField(ref _remoteText, value);
    }

    public string? FooterHint
    {
        get => _footerHint;
        private set
        {
            if (SetField(ref _footerHint, value))
            {
                OnPropertyChanged(nameof(HasFooterHint));
            }
        }
    }

    public bool HasFooterHint => FooterHint is { Length: > 0 };

    public string? FooterLink
    {
        get => _footerLink;
        private set
        {
            if (SetField(ref _footerLink, value))
            {
                OnPropertyChanged(nameof(HasFooterLink));
            }
        }
    }

    public bool HasFooterLink => FooterLink is { Length: > 0 };

    public ICommand FooterLinkCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand ShowTerminalCommand { get; }

    /// <summary>The buttons' order: the primary last (macOS) or first (Windows, Linux). Tests set it.</summary>
    public bool PrimaryLast { get; set; } = DialogViewModel.PrimaryLastOnThisOs;

    /// <summary>The footer's buttons in the OS's order (macOS: the primary last).</summary>
    public ObservableCollection<ConnectButton> Buttons { get; } = [];

    /// <summary>The dialog opened or its mode changed: the view moves the focus (any thread is never used: UI thread).</summary>
    public event EventHandler? Opened;

    // ------------------------------------------------------------------ opening and closing

    /// <summary>
    /// Opens the dialog for <paramref name="template"/>, its sign-in <paramref name="signIn"/> chosen when given (Manage
    /// Agents' Change API Key… and Sign In Again…); the agent written, or null, when it closes.
    /// </summary>
    public Task<string?> OpenAsync(AcpAgentTemplate template, string? signIn = null)
    {
        _closed?.TrySetResult(null);
        _cts?.Cancel(); // a step of the previous agent stops; its token stays valid for what still holds it
        _cts = new CancellationTokenSource();
        var closed = _closed = new TaskCompletionSource<string?>();
        _template = template;
        _requirements = null;
        _result = null;
        _saved = false;
        _apiKey = string.Empty;
        _consent = false;
        _useCodalusNode = false;
        _consentShown = !_host.Ai.IsEnabled && !_host.Ai.IsOffForWorkspace;
        Title = Localizer.Format("msg.acp.connect.title", "Connect to {0}", template.Name);
        Context = template.Agent.UsesNpx
            ? Localizer.Format("msg.acp.connect.context", "External agent · {0} · {1} {2}", template.Vendor, template.Package![..template.Package!.LastIndexOf('@')], template.Version)
            : Localizer.Format("msg.acp.connect.contextCli", "External agent · {0} · {1} (tested with {2})", template.Vendor, template.Agent.CommandLine, template.Version);
        Requirements.Clear();
        InstallOptions.Clear();
        Steps.Clear();
        ResultRows.Clear();
        Fixes.Clear();
        BuildSignIn(signIn);
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(ApiKey));
        OnPropertyChanged(nameof(Consent));
        OnPropertyChanged(nameof(SignInAside));
        OnPropertyChanged(nameof(SignInWarning));
        OnPropertyChanged(nameof(HasSignInWarning));
        OnPropertyChanged(nameof(RemoteSessionNote));
        OnPropertyChanged(nameof(HasRemoteSessionNote));
        OnPropertyChanged(nameof(ConsentText));
        OnPropertyChanged(nameof(RunsNote));
        if (_host.Connector.WhyNot() is { } remote)
        {
            ShowRemoteState(remote);
        }
        else
        {
            _ = StartAsync();
        }

        Opened?.Invoke(this, EventArgs.Empty);
        return closed.Task;
    }

    private void ShowRemoteState(string reason)
    {
        RemoteText = reason;
        Context = Localizer.Format("msg.acp.connect.contextVendor", "External agent · {0}", _template!.Vendor);
        Mode = ConnectAgentMode.Remote;
        FooterHint = null;
        FooterLink = null;
        SetButtons(new ConnectButton(Localizer.Get("msg.acp.connect.close", "Close"), true, new RelayCommand(Close)));
    }

    private async Task StartAsync()
    {
        // The store is asked once before the key note says where a key goes (it is "persistent" until a call fails).
        if (_host.Secrets is { } secrets)
        {
            try
            {
                _persistent = await secrets.ProbeAsync(Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        else
        {
            _persistent = false;
        }

        OnPropertyChanged(nameof(KeyNote));
        OnPropertyChanged(nameof(KeyNoteIsWarning));
        await CheckAsync().ConfigureAwait(true);
    }

    private CancellationToken Token => _cts?.Token ?? CancellationToken.None;

    /// <summary>Cancel (Esc, the close button): a running step stops, nothing is written, and the dialog closes.</summary>
    public void Cancel() => Close();

    public void Close()
    {
        _cts?.Cancel();
        var closed = _closed;
        _closed = null;
        OnPropertyChanged(nameof(IsOpen));
        closed?.TrySetResult(_saved ? _template?.Id : null);
    }

    // ------------------------------------------------------------------ requirements and installs

    /// <summary>Checks Again: what the agent needs, off the UI thread.</summary>
    public async Task CheckAsync()
    {
        if (_template is not { } template || !IsOpen)
        {
            return;
        }

        Mode = ConnectAgentMode.Checking;
        UpdateButtons();
        try
        {
            var requirements = await Task.Run(() => _host.Connector.CheckAsync(template, _useCodalusNode, Token)).ConfigureAwait(true);
            if (!ReferenceEquals(_template, template))
            {
                return;
            }

            ApplyRequirements(requirements);
        }
        catch (OperationCanceledException)
        {
        }
        catch (AcpException ex)
        {
            ShowRemoteState(ex.Message);
        }
    }

    private void ApplyRequirements(AcpRequirements requirements)
    {
        _requirements = requirements;
        Requirements.Clear();
        foreach (var row in requirements.Rows)
        {
            Requirements.Add(new ConnectRequirementRow(row, _template!.Name));
        }

        InstallOptions.Clear();
        foreach (var option in requirements.Install)
        {
            var choice = new ConnectChoice(option.Id, option.Name, option.Description, option)
            {
                Command = option.Kind == InstallKind.OpenPage || option.IsCodalusNode ? null : option.Command,
                Meta = option switch
                {
                    { IsCodalusNode: true } => Localizer.Format("msg.acp.connect.metaCodalusNode", "Downloads Node.js {0} from nodejs.org once (about 50 MB), checked against its SHA-256.", Workbench.Node.NodeRuntime.Version),
                    { Kind: InstallKind.OpenPage } => Localizer.Format("msg.acp.connect.metaPage", "Opens {0} in your browser.", new Uri(option.Command).Host),
                    { Kind: InstallKind.Terminal } => Localizer.Get("msg.acp.connect.metaTerminal", "Runs in the Terminal: it asks for your password."),
                    { ReadsWindowsPath: true } => Localizer.Get("msg.acp.connect.metaWinget", "Runs here; Windows may ask for permission. Codalus reads the new PATH when it finishes."),
                    _ => Localizer.Get("msg.acp.connect.metaRun", "Runs here, with its output below."),
                },
                MetaIcon = option switch
                {
                    { IsCodalusNode: true } => "cloud-download",
                    { Kind: InstallKind.OpenPage } => "link-external",
                    { Kind: InstallKind.Terminal } => "terminal",
                    _ => "play",
                },
            };
            choice.Selected += OnInstallSelected;
            InstallOptions.Add(choice);
        }

        if (InstallOptions.Count > 0)
        {
            InstallOptions[0].IsSelected = true;
        }

        OnPropertyChanged(nameof(InstallTitle));
        OnPropertyChanged(nameof(InstallNote));
        OnPropertyChanged(nameof(HasInstallNote));
        InstallStatus = null;
        Mode = requirements.Met ? ConnectAgentMode.Connect : ConnectAgentMode.Missing;
        OnPropertyChanged(nameof(HasWorkspaceNote));
        BuildRuns();
        UpdateButtons();
        Opened?.Invoke(this, EventArgs.Empty);
    }

    private void OnInstallSelected(object? sender, EventArgs e)
    {
        foreach (var other in InstallOptions.Where(o => o != sender))
        {
            other.IsSelected = false;
        }

        UpdateButtons();
    }

    private AcpInstallOption? SelectedInstall => InstallOptions.FirstOrDefault(o => o.IsSelected)?.Payload as AcpInstallOption;

    /// <summary>The Missing state's primary: runs the chosen install (or opens its page), then checks again.</summary>
    public async Task InstallAsync()
    {
        if (_template is not { } template || SelectedInstall is not { } option)
        {
            return;
        }

        if (option.Kind == InstallKind.OpenPage)
        {
            _host.OpenUrl(option.Command);
            FooterHint = Localizer.Get("msg.acp.connect.afterPage", "Once it's installed, Check Again finds it.");
            return;
        }

        Mode = ConnectAgentMode.Installing;
        Steps.Clear();
        Steps.Add(new ConnectStep(AcpTestStep.Requirements, option.Kind == InstallKind.Terminal
            ? Localizer.Get("msg.acp.connect.finishInTerminal", "Finish in the Terminal: type your password if it asks for it.")
            : Localizer.Format("msg.acp.connect.installing", "Installing with {0}", option.Name), Localizer.Get("msg.acp.connect.checksAgainWhenDone", "Codalus checks again when the command ends, then continues."))
        { State = AcpStepState.Running, CallCommand = option.Kind == InstallKind.Terminal ? option.Command : null });
        OnPropertyChanged(nameof(StepsTitle));
        OnPropertyChanged(nameof(StepsScope));
        UpdateButtons();
        try
        {
            if (option.IsCodalusNode)
            {
                _useCodalusNode = true;
            }

            await Task.Run(() => _host.Connector.InstallAsync(template.Id, option, this, line => Ui.Post(() => InstallStatus = line), Token)).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is AcpException or IOException or HttpRequestException or InvalidOperationException or PlatformNotSupportedException)
        {
            InstallStatus = ex.Message;
        }

        await CheckAsync().ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ sign-in and what runs

    private void BuildSignIn(string? preselect)
    {
        SignInOptions.Clear();
        foreach (var option in _template!.SignIn)
        {
            var choice = new ConnectChoice(option.Id, option.Name, option.Description, option);
            choice.Selected += OnSignInSelected;
            SignInOptions.Add(choice);
        }

        if ((SignInOptions.FirstOrDefault(c => c.Id == preselect) ?? SignInOptions.FirstOrDefault()) is { } first)
        {
            first.IsSelected = true;
        }
    }

    private void OnSignInSelected(object? sender, EventArgs e)
    {
        foreach (var other in SignInOptions.Where(o => o != sender))
        {
            other.IsSelected = false;
        }

        OnPropertyChanged(nameof(SelectedSignIn));
        OnPropertyChanged(nameof(ShowKey));
        OnPropertyChanged(nameof(KeyName));
        OnPropertyChanged(nameof(KeyNote));
        OnPropertyChanged(nameof(KeyNoteIsWarning));
        BuildRuns();
        UpdateButtons();
    }

    private void BuildRuns()
    {
        Runs.Clear();
        if (_template is not { } template || SelectedSignIn is not { } option)
        {
            return;
        }

        var start = template.Agent.CommandLine;
        Runs.Add(new ConnectRunsLine(Localizer.Get("msg.acp.connect.starts", "Starts"), start, null));
        switch (option.Kind)
        {
            case AcpSignInKind.TerminalMethod:
                var args = _host.IsRemoteSession && option.RemoteTerminalArgs.Count > 0 ? option.RemoteTerminalArgs : option.TerminalArgs;
                Runs.Add(new ConnectRunsLine(Localizer.Get("msg.acp.connect.signsInTerminal", "Signs in (Terminal)"), start + (args.Count > 0 ? " " + string.Join(' ', args) : ""), null));
                break;
            case AcpSignInKind.TerminalCommand:
                Runs.Add(new ConnectRunsLine(Localizer.Get("msg.acp.connect.signsInTerminal", "Signs in (Terminal)"), string.Join(' ', new[] { template.Agent.Cli }.Concat(option.CommandArgs)), null));
                break;
            case AcpSignInKind.ApiKeyEnv:
                Runs.Add(new ConnectRunsLine(
                    Localizer.Get("msg.acp.connect.withVariable", "With the variable"),
                    $"{option.EnvVar}=<{(_persistent ? Localizer.Format("msg.acp.connect.fromStore", "from {0}", StoreName) : Localizer.Get("msg.acp.connect.fromMemory", "from memory, this session only"))}>",
                    null));
                if (option.MethodId is { } keyMethod)
                {
                    Runs.Add(new ConnectRunsLine(Localizer.Get("msg.acp.connect.signsIn", "Signs in"), null, Localizer.Format("msg.acp.connect.signsInWithKey", "With the key it was given (authenticate, {0})", keyMethod)));
                }

                break;
            case AcpSignInKind.ApiKeyOnce:
                Runs.Add(new ConnectRunsLine(Localizer.Get("msg.acp.connect.signsIn", "Signs in"), null, Localizer.Format("msg.acp.connect.signsInOnce", "With the key in its sign-in request (authenticate, {0}), never in its environment", option.MethodId)));
                break;
            case AcpSignInKind.AgentMethod:
                Runs.Add(new ConnectRunsLine(Localizer.Get("msg.acp.connect.signsIn", "Signs in"), null, Localizer.Format("msg.acp.connect.signsInBrowser", "{0} opens your browser (authenticate, {1})", template.Name, option.MethodId)));
                break;
        }

        if (option.UnsetEnv.Count > 0)
        {
            Runs.Add(new ConnectRunsLine(Localizer.Get("msg.acp.connect.startsWithout", "Starts without"), null, string.Join(", ", option.UnsetEnv)));
        }
    }

    // ------------------------------------------------------------------ the test

    /// <summary>Connect: the test, on the dialog's unsaved consent; written only when it passes.</summary>
    public async Task TestAsync()
    {
        if (_template is not { } template || SelectedSignIn is not { } option || !CanConnect)
        {
            return;
        }

        var request = new AcpConnectRequest(template, option) { ApiKey = option.TakesKey ? ApiKey : null, Consent = _consentShown && Consent, UseCodalusNode = _useCodalusNode };
        Mode = ConnectAgentMode.Testing;
        Steps.Clear();
        Steps.Add(new ConnectStep(AcpTestStep.Requirements, Localizer.Get("msg.acp.connect.stepRequirements", "Requirements"), null));
        Steps.Add(new ConnectStep(AcpTestStep.Download, template.Package is { } package ? Localizer.Format("msg.acp.connect.stepDownloading", "Downloading {0}", package) : Localizer.Get("msg.acp.connect.stepNoDownload", "Nothing to download"), template.Package is null ? Localizer.Format("msg.acp.connect.stepOwnCli", "{0} runs its own CLI", template.Name) : null));
        Steps.Add(new ConnectStep(AcpTestStep.Start, Localizer.Get("msg.acp.connect.stepStarting", "Starting"), Localizer.Get("msg.acp.connect.stepStartingDetail", "The agent answers initialize")));
        Steps.Add(new ConnectStep(AcpTestStep.SignIn, Localizer.Get("msg.acp.connect.stepSigningIn", "Signing in"), option.Name));
        Steps.Add(new ConnectStep(AcpTestStep.Check, Localizer.Get("msg.acp.connect.stepChecking", "Checking sign-in"), template.SignInCheck == AcpSignInCheck.AuthStatusPush
            ? Localizer.Format("msg.acp.connect.stepCheckingStatus", "{0} reports who is signed in", template.Name)
            : Localizer.Format("msg.acp.connect.stepCheckingSession", "{0} creates a session", template.Name)));
        Steps.Add(new ConnectStep(AcpTestStep.Done, Localizer.Get("msg.acp.connect.stepDone", "Done"), null));
        OnPropertyChanged(nameof(StepsTitle));
        OnPropertyChanged(nameof(StepsScope));
        UpdateButtons();
        AcpTestResult result;
        try
        {
            result = await Task.Run(() => _host.Connector.TestAsync(request, this, new UiProgress(OnProgress), Token)).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (AcpException ex)
        {
            result = new AcpTestResult(AcpTestOutcome.Failed, ex.Message);
        }

        if (!IsOpen || !ReferenceEquals(_template, template))
        {
            return;
        }

        _result = result;
        if (result.Outcome == AcpTestOutcome.Connected)
        {
            await SaveAsync(request, confirmed: true).ConfigureAwait(true);
            return;
        }

        ShowResultState(request);
    }

    private void OnProgress(AcpTestProgress progress)
    {
        if (Steps.FirstOrDefault(s => s.Step == progress.Step) is not { } step)
        {
            return;
        }

        step.State = progress.State;
        switch (progress.Step, progress.State)
        {
            case (AcpTestStep.Download, AcpStepState.Done):
                step.Name = Localizer.Format("msg.acp.connect.stepDownloaded", "Downloaded {0}", _template!.Package);
                step.Detail = Localizer.Get("msg.acp.connect.stepCached", "Kept in npm's cache, so the agent starts without downloading it again");
                break;
            case (AcpTestStep.Start, AcpStepState.Done):
                step.Name = Localizer.Get("msg.acp.connect.stepStarted", "Started");
                step.Detail = progress.Detail;
                break;
            case (AcpTestStep.SignIn, AcpStepState.Done):
                step.CallCommand = null;
                step.CallHint = null;
                break;
            default:
                if (progress.Detail is { Length: > 0 } detail)
                {
                    step.Detail = detail;
                }

                break;
        }
    }

    /// <summary>
    /// Writes the agent (a passing test, or Save Anyway): the key, the entry, and <c>ai.enabled</c> when the box was
    /// checked; then the result says what went where.
    /// </summary>
    private async Task SaveAsync(AcpConnectRequest request, bool confirmed)
    {
        try
        {
            await Task.Run(() => _host.Connector.SaveAsync(request, Token)).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is AcpException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _result = new AcpTestResult(AcpTestOutcome.Failed, Localizer.Format("msg.acp.connect.saveFailed", "The test passed, but the settings couldn't be written: {0}", ex.Message));
            ShowResultState(request);
            return;
        }

        _saved = true;
        _host.Connected(request.Template.Id);
        ShowConnectedState(request, confirmed);
    }

    private void ShowConnectedState(AcpConnectRequest request, bool confirmed)
    {
        var template = request.Template;
        var result = _result!;
        var turnedOn = request.Consent;
        Mode = ConnectAgentMode.Result;
        Context = Localizer.Format("msg.acp.connect.contextSignIn", "External agent · {0} · {1}", template.Vendor, request.SignIn.Name);
        ResultIcon = "test-passed";
        ResultTitle = Localizer.Format("msg.acp.connect.isConnected", "{0} is connected", template.Name);
        ResultText = turnedOn
            ? Localizer.Format("msg.acp.connect.connectedOn", "Its entry is in your user settings, and AI assistance is on. Pick {0} in AI Chat's agent menu, or start a chat now.", template.Name)
            : Localizer.Format("msg.acp.connect.connectedText", "Its entry is in your user settings. Pick {0} in AI Chat's agent menu, or start a chat now.", template.Name);
        ResultCommand = null;
        ResultRows.Clear();
        ResultRows.Add(new ConnectResultRow(Localizer.Get("msg.acp.connect.rowAgent", "Agent"), $"{result.Agent} · ACP v{result.Protocol}"));
        ResultRows.Add(new ConnectResultRow(Localizer.Get("msg.acp.connect.rowSignedIn", "Signed in"), !confirmed
            ? Localizer.Get("msg.acp.connect.notReported", "Not reported")
            : result.SignedInAs is { } who ? Localizer.Format("msg.acp.connect.reports", "{0} reports: {1}", template.Name, who) : Localizer.Get("msg.acp.connect.sessionCreated", "Session created")));
        if (result.Modes.Count > 0)
        {
            ResultRows.Add(new ConnectResultRow(Localizer.Get("msg.acp.connect.rowMode", "Mode"), result.Modes.Count > 1 ? Localizer.Format("msg.acp.connect.modeOthers", "{0} (AI Chat lists the others)", result.Modes[0]) : result.Modes[0]));
        }

        ResultRows.Add(new ConnectResultRow(Localizer.Get("msg.acp.connect.rowSaved", "Saved"), turnedOn
            ? Localizer.Format("msg.acp.connect.savedBoth", "ai.agents.{0} and ai.enabled in {1}", template.Id, _host.UserSettingsPath)
            : Localizer.Format("msg.acp.connect.savedEntry", "ai.agents.{0} in {1}", template.Id, _host.UserSettingsPath)));
        ResultRows.Add(new ConnectResultRow(Localizer.Get("msg.acp.connect.rowKeptBy", "Sign-in kept by"), request.SignIn.Kind switch
        {
            AcpSignInKind.ApiKeyEnv => _persistent
                ? Localizer.Format("msg.acp.connect.keptInStore", "Codalus: {0} in {1}", request.SignIn.EnvVar, StoreName)
                : Localizer.Format("msg.acp.connect.keptInMemory", "Codalus: {0} in memory, until Codalus closes", request.SignIn.EnvVar),
            AcpSignInKind.ApiKeyOnce => Localizer.Format("msg.acp.connect.keptByAgentStore", "{0}, in its own sign-in store ({1})", template.Name, request.SignIn.KeyStore ?? ""),
            _ => Localizer.Format("msg.acp.connect.keptByAgent", "{0}, on this computer (no key in Codalus)", template.Name),
        }));
        Fixes.Clear();
        OnPropertyChanged(nameof(HasFixes));
        FooterHint = Localizer.Get("msg.acp.connect.manageHint", "Manage Agents… in the agent menu checks or disconnects it.");
        FooterLink = null;
        SetButtons(
            new ConnectButton(Localizer.Get("msg.acp.connect.startChat", "Start a Chat"), true, new RelayCommand(() =>
            {
                var agent = template.Id;
                Close();
                _host.StartChat(agent);
            })),
            new ConnectButton(Localizer.Get("msg.acp.connect.close", "Close"), false, new RelayCommand(Close)));
        Opened?.Invoke(this, EventArgs.Empty);
    }

    private void ShowResultState(AcpConnectRequest request)
    {
        var template = request.Template;
        var result = _result!;
        Mode = ConnectAgentMode.Result;
        Context = Localizer.Format("msg.acp.connect.contextSignIn", "External agent · {0} · {1}", template.Vendor, request.SignIn.Name);
        ResultRows.Clear();
        Fixes.Clear();
        ResultCommand = result.Output.Count > 0 ? string.Join('\n', result.Output) : null;
        if (result.Agent is { } agent)
        {
            ResultRows.Add(new ConnectResultRow(Localizer.Get("msg.acp.connect.rowAgent", "Agent"), $"{agent} · ACP v{result.Protocol}"));
        }

        if (result.Outcome == AcpTestOutcome.NotConfirmed)
        {
            ResultIcon = "warning";
            ResultTitle = Localizer.Format("msg.acp.connect.unconfirmedTitle", "{0} started, but it didn't say whether you're signed in", template.Name);
            ResultText = result.Message;
            ResultRows.Add(new ConnectResultRow(Localizer.Get("msg.acp.connect.rowChecked", "Checked"), Localizer.Get("msg.acp.connect.checkedSession", "Started, answered initialize and created a session")));
            ResultRows.Add(new ConnectResultRow(Localizer.Get("msg.acp.connect.rowSignedIn", "Signed in"), Localizer.Get("msg.acp.connect.notReported", "Not reported")));
            FooterHint = Localizer.Get("msg.acp.connect.saveAnywayHint", "Save Anyway writes the entry now.");
            FooterLink = Localizer.Get("msg.acp.connect.showOutput", "Show Output");
            _footerLinkRun = _host.ShowOutput;
            SetButtons(
                new ConnectButton(Localizer.Get("msg.acp.connect.saveAnyway", "Save Anyway"), true, new RelayCommand(() => _ = SaveAsync(request, confirmed: false))),
                new ConnectButton(Localizer.Get("msg.acp.connect.signIn", "Sign In…"), false, new RelayCommand(() => BackToForm(template.SignIn.FirstOrDefault(o => o.Kind != AcpSignInKind.AlreadySignedIn)?.Id))),
                new ConnectButton(Localizer.Get("msg.acp.connect.cancel", "Cancel"), false, new RelayCommand(Cancel)));
        }
        else
        {
            ResultIcon = "test-failed";
            ResultTitle = result.Outcome == AcpTestOutcome.NeedsSignIn
                ? Localizer.Format("msg.acp.connect.notSignedInTitle", "{0} says you aren't signed in", template.Name)
                : result.FailedStep switch
                {
                    AcpTestStep.Download => Localizer.Format("msg.acp.connect.downloadFailedTitle", "Couldn't download {0}", template.Package),
                    AcpTestStep.Requirements => Localizer.Format("msg.acp.connect.requirementsFailedTitle", "{0} can't run yet", template.Name),
                    AcpTestStep.SignIn => Localizer.Format("msg.acp.connect.signInFailedTitle", "{0} didn't sign in", template.Name),
                    _ => Localizer.Format("msg.acp.connect.failedTitle", "{0} didn't connect", template.Name),
                };
            ResultText = result.Message;
            if (result.SignedInAs is { } who)
            {
                ResultRows.Add(new ConnectResultRow(Localizer.Get("msg.acp.connect.rowSignedIn", "Signed in"), Localizer.Format("msg.acp.connect.reports", "{0} reports: {1}", template.Name, who)));
            }

            if (result.Outcome == AcpTestOutcome.NeedsSignIn)
            {
                foreach (var option in template.SignIn.Where(o => o.Kind != AcpSignInKind.AlreadySignedIn && o != request.SignIn))
                {
                    var label = option.TakesKey
                        ? Localizer.Get("msg.acp.connect.useKey", "Use an API Key…")
                        : Localizer.Format("msg.acp.connect.signInWith", "Sign In with {0}…", option.Name);
                    Fixes.Add(new ConnectFix(label, option.TakesKey ? "key" : "terminal", new RelayCommand(() => BackToForm(option.Id))));
                }
            }

            if (result.FailedStep == AcpTestStep.Download)
            {
                Fixes.Add(new ConnectFix(Localizer.Get("msg.acp.connect.npmProxySettings", "npm's Proxy Settings"), "link-external", new RelayCommand(() => _host.OpenUrl("https://docs.npmjs.com/cli/using-npm/config#proxy"))));
            }

            FooterHint = Localizer.Get("msg.acp.connect.tryAgainHint", "Try Again tests it again, for a sign-in made elsewhere.");
            FooterLink = Localizer.Get("msg.acp.connect.showOutput", "Show Output");
            _footerLinkRun = _host.ShowOutput;
            SetButtons(
                new ConnectButton(Localizer.Get("msg.acp.connect.tryAgain", "Try Again"), true, new RelayCommand(() => _ = TestAsync())),
                new ConnectButton(Localizer.Get("msg.acp.connect.cancel", "Cancel"), false, new RelayCommand(Cancel)));
        }

        OnPropertyChanged(nameof(HasFixes));
        Opened?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A fix: back to the form with that sign-in chosen.</summary>
    private void BackToForm(string? signIn)
    {
        if (SignInOptions.FirstOrDefault(c => c.Id == signIn) is { } choice)
        {
            choice.IsSelected = true;
        }

        Mode = ConnectAgentMode.Connect;
        BuildRuns();
        UpdateButtons();
        Opened?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ the footer

    /// <summary>Connect can go: everything is there, a sign-in chosen (with its key), the consent checked where it shows.</summary>
    public bool CanConnect =>
        Mode == ConnectAgentMode.Connect && SelectedSignIn is { } option && (!option.TakesKey || ApiKey.Trim().Length > 0) && (!_consentShown || Consent);

    private void UpdateButtons()
    {
        if (_template is null || Mode is ConnectAgentMode.Result or ConnectAgentMode.Remote)
        {
            return;
        }

        var cancel = new ConnectButton(Localizer.Get("msg.acp.connect.cancel", "Cancel"), false, new RelayCommand(Cancel));
        var connect = Localizer.Get("msg.acp.connect.connect", "Connect");
        _footerLinkRun = null;
        FooterLink = null;
        switch (Mode)
        {
            case ConnectAgentMode.Missing:
                var option = SelectedInstall;
                var label = option switch
                {
                    { IsCodalusNode: true } => Localizer.Get("msg.acp.connect.downloadNode", "Download Node.js"),
                    { Kind: InstallKind.OpenPage } => Localizer.Get("msg.acp.connect.openPage", "Open Page"),
                    { Kind: InstallKind.Terminal } => Localizer.Get("msg.acp.connect.installInTerminal", "Install in Terminal"),
                    _ => Localizer.Get("msg.acp.connect.install", "Install"),
                };
                FooterHint = option?.Kind == InstallKind.OpenPage
                    ? Localizer.Get("msg.acp.connect.afterPage", "Once it's installed, Check Again finds it.")
                    : Localizer.Get("msg.acp.connect.checksAgain", "Codalus checks again when it finishes.");
                SetButtons(new ConnectButton(label, true, new RelayCommand(() => _ = InstallAsync()), option is not null), cancel);
                break;
            case ConnectAgentMode.Installing:
                FooterHint = null;
                FooterLink = Localizer.Get("msg.acp.connect.showTerminal", "Show Terminal");
                _footerLinkRun = _host.ShowTerminal;
                SetButtons(new ConnectButton(connect, true, new RelayCommand(() => { }), false, Localizer.Get("msg.acp.connect.waitingInstall", "Waiting for the install")), cancel);
                break;
            case ConnectAgentMode.Testing:
                FooterHint = Localizer.Get("msg.acp.connect.nothingSaved", "Nothing is saved until the test passes.");
                FooterLink = Localizer.Get("msg.acp.connect.showOutput", "Show Output");
                _footerLinkRun = _host.ShowOutput;
                SetButtons(new ConnectButton(connect, true, new RelayCommand(() => { }), false, Localizer.Get("msg.acp.connect.testingTip", "Testing")), cancel);
                break;
            case ConnectAgentMode.Checking:
                FooterHint = null;
                SetButtons(new ConnectButton(connect, true, new RelayCommand(() => { }), false), cancel);
                break;
            default:
                FooterHint = _consentShown
                    ? Localizer.Format("msg.acp.connect.writesBoth", "Writes ai.agents.{0} and ai.enabled to your user settings after the test passes", _template.Id)
                    : Localizer.Format("msg.acp.connect.writesEntry", "Writes ai.agents.{0} to your user settings after the test passes", _template.Id);
                var tip = _consentShown && !Consent
                    ? Localizer.Get("msg.acp.connect.checkConsentFirst", "Check Turn on AI assistance first")
                    : SelectedSignIn?.TakesKey == true && ApiKey.Trim().Length == 0 ? Localizer.Get("msg.acp.connect.typeKeyFirst", "Type the key first") : null;
                SetButtons(new ConnectButton(connect, true, new RelayCommand(() => _ = TestAsync()), CanConnect, tip), cancel);
                break;
        }
    }

    private void SetButtons(params ConnectButton[] buttons)
    {
        Buttons.Clear();
        // DialogViewModel.Order's rule (D38): on macOS the primary goes last, the others before it in reverse.
        foreach (var button in PrimaryLast ? [.. buttons.Where(b => !b.IsPrimary).Reverse(), .. buttons.Where(b => b.IsPrimary)] : buttons)
        {
            Buttons.Add(button);
        }

        OnPropertyChanged(nameof(CanConnect));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    /// <summary>Enter: the primary button, when it's enabled.</summary>
    public void Accept()
    {
        if (Buttons.FirstOrDefault(b => b.IsPrimary) is { IsEnabled: true } primary && primary.Command.CanExecute(null))
        {
            primary.Command.Execute(null);
        }
    }

    // ------------------------------------------------------------------ the terminal

    /// <summary>A sign-in or an install in the Terminal: its step shows the command and Show Terminal while it runs.</summary>
    async Task<int?> IAcpConnectHost.RunInTerminalAsync(string title, string commandLine, ProcessLaunchInfo launch, string? hint, CancellationToken cancellationToken)
    {
        await Ui.RunAsync(() =>
        {
            if (Steps.FirstOrDefault(s => s.Step == AcpTestStep.SignIn) is { } step && Mode == ConnectAgentMode.Testing)
            {
                step.CallCommand = commandLine;
                step.CallHint = hint;
            }
        }).ConfigureAwait(false);
        return await _host.RunInTerminalAsync(title, commandLine, launch, hint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reports on the UI thread, in order.</summary>
    private sealed class UiProgress(Action<AcpTestProgress> report) : IProgress<AcpTestProgress>
    {
        public void Report(AcpTestProgress value) => Ui.Post(() => report(value));
    }
}
