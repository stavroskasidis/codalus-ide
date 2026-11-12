using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Platform;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.Theming;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Acp;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Settings;
using Codalus.Workbench.Setup;

namespace Codalus.Shell.ViewModels;

/// <summary>The first-run setup's steps, in order.</summary>
public enum SetupStep
{
    Appearance,
    Keyboard,
    Settings,
    Tools,
    Ai,
}

/// <summary>What the setup window shows: the welcome page (at a first start only), one of the steps, or the summary.</summary>
public enum SetupPage
{
    /// <summary>"Welcome to Codalus IDE", with where the user comes from. Before the steps, at a first start.</summary>
    Welcome,

    /// <summary>The current <see cref="SetupStep"/>.</summary>
    Steps,

    /// <summary>"You're all set": what was chosen, a few keys, and what happens next.</summary>
    Finish,
}

/// <summary>The AI step's three paths (ADR 0096): none is the default, and the only one that needs no further choice.</summary>
public enum SetupAiPath
{
    /// <summary>AI assistance stays off (or is turned off).</summary>
    None,

    /// <summary>An external coding agent (ADR 0079), connected through Connect (ADR 0083).</summary>
    Agent,

    /// <summary>The built-in assistant with a provider the user chooses (ADR 0062).</summary>
    Assistant,
}

/// <summary>
/// What the AI step holds: the path; for an agent, its name in <c>ai.agents</c> or its template (<see cref="More"/>: chosen
/// from the list afterwards; null: not chosen yet); for the assistant, the provider (null: not chosen yet) and the two
/// as-you-type switches. Nothing of it is written before the setup is done (<c>ShellHost.ApplySetupAiAsync</c>).
/// </summary>
public sealed record SetupAiChoice(SetupAiPath Path, string? Agent = null, string? Provider = null, bool InlineSuggestions = false, bool NextEdit = false)
{
    /// <summary>The agent option that opens More agents… once the setup is done (not a name an <c>ai.agents</c> entry would have).</summary>
    public const string More = ":more";

    public static SetupAiChoice None { get; } = new(SetupAiPath.None);
}

/// <summary>
/// One of the AI step's three paths, as a card that can be compared with the others: what it is in a line, and the same
/// four facts for each (who answers, what it does, what leaves this machine, what it needs).
/// </summary>
public sealed class SetupAiPathOption : ObservableObject
{
    private bool _isSelected;

    public SetupAiPathOption(SetupAiPath path, string title, string description, IReadOnlyList<SetupSummaryRow> facts, Action<SetupAiPathOption> select)
    {
        Path = path;
        Title = title;
        Description = description;
        Facts = facts;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public SetupAiPath Path { get; }

    public string Title { get; }

    public string Description { get; }

    public IReadOnlyList<SetupSummaryRow> Facts { get; }

    /// <summary>
    /// The way Codalus IDE recommends (a coding agent, ADR 0096): its card says so beside its title. Recommended isn't
    /// preselected: No AI stays chosen until the user chooses.
    /// </summary>
    public bool IsRecommended { get; init; }

    /// <summary>"(recommended)", beside the title of the recommended way.</summary>
    public string RecommendedText { get; } = Localizer.Get("msg.firstRun.aiRecommended", "(recommended)");

    /// <summary>The title, with "(recommended)" on the recommended way.</summary>
    public string AccessibleName => IsRecommended ? $"{Title} {RecommendedText}" : Title;

    /// <summary>The line and the facts in one text, for a screen reader.</summary>
    public string HelpText => string.Join(" ", new[] { Description }.Concat(Facts.Select(f => $"{f.Label}: {f.Value}")));

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public ICommand SelectCommand { get; }
}

/// <summary>A row of the summary: what was set, and to what. Also a labeled fact of an AI path.</summary>
public sealed record SetupSummaryRow(string Label, string Value);

/// <summary>A key worth knowing on the summary, as the chosen keyboard scheme has it.</summary>
public sealed record SetupShortcutRow(string Keys, string Title);

/// <summary>A step in the setup's list: its number and title, and whether it's the current one.</summary>
public sealed class SetupStepViewModel(SetupStep step, string title, int count) : ObservableObject
{
    private bool _isCurrent;

    public SetupStep Step { get; } = step;

    public int Number => (int)Step + 1;

    public string Title { get; } = title;

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (SetField(ref _isCurrent, value))
            {
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    /// <summary>"Step 1 of 5: Appearance, current step".</summary>
    public string AccessibleName => IsCurrent
        ? Localizer.Format("msg.firstRun.stepCurrent", "Step {0} of {1}: {2}, current step", Number, count, Title)
        : Localizer.Format("msg.firstRun.step", "Step {0} of {1}: {2}", Number, count, Title);
}

/// <summary>A theme family's card in the setup: its picture in the chosen mode, its name and a line about it.</summary>
public sealed class SetupFamilyOption : ObservableObject
{
    private ThemePicture _picture;
    private bool _isSelected;

    public SetupFamilyOption(string id, string title, string description, Action<SetupFamilyOption> select)
    {
        Id = id;
        Title = title;
        Description = description;
        _picture = new ThemePicture(ThemeService.VariantOf(ThemeService.Resolve(id + ".light", null)));
        SelectCommand = new RelayCommand(() => select(this));
    }

    public string Id { get; }

    public string Title { get; }

    public string Description { get; }

    /// <summary>The picture's colors and shapes: the family's tokens in the chosen mode.</summary>
    public ThemePicture Picture
    {
        get => _picture;
        set => SetField(ref _picture, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public ICommand SelectCommand { get; }
}

/// <summary>One of a set of choices: a mode (its variant, null for Match system) or a keyboard scheme.</summary>
public sealed class SetupChoice : ObservableObject
{
    private bool _isSelected;

    public SetupChoice(string? id, string title, string description, Action<SetupChoice> select)
    {
        Id = id;
        Title = title;
        Description = description;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public string? Id { get; }

    public string Title { get; }

    public string Description { get; }

    public bool HasDescription => Description.Length > 0;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public ICommand SelectCommand { get; }
}

/// <summary>
/// A tool on the setup's Tools step (M37's check): how it stands, and a fix that first shows exactly what it runs and
/// installs only after the user's yes.
/// </summary>
public sealed class SetupToolRowViewModel : ObservableObject
{
    private readonly Func<ToolStatus, Task> _install;
    private bool _isConfirming;
    private bool _isInstalling;

    public SetupToolRowViewModel(ToolStatus tool, InstallPlan? plan, Func<ToolStatus, Task> install)
    {
        Tool = tool;
        Plan = plan;
        _install = install;
        InstallCommand = new RelayCommand(() => IsConfirming = true);
        CancelCommand = new RelayCommand(() => IsConfirming = false);
        ConfirmCommand = new RelayCommand(() => _ = ConfirmAsync());
    }

    public ToolStatus Tool { get; }

    public InstallPlan? Plan { get; }

    public string Title => Tool.Title;

    public string State => Tool.State switch
    {
        ToolState.Found => Tool.Version is { } v ? Localizer.Format("msg.setup.found", "Found, {0}", v) : Localizer.Get("msg.setup.foundNoVersion", "Found"),
        ToolState.Mismatch => Localizer.Get("msg.setup.mismatch", "Not the version needed"),
        _ => Tool.Required ? Localizer.Get("msg.setup.missingRequired", "Missing (needed)") : Localizer.Get("msg.setup.missingOptional", "Not installed (optional)"),
    };

    public string Icon => Tool.State == ToolState.Found ? "test-passed" : Tool.Required ? "error" : "warning";

    public string NeededFor => Tool.NeededFor;

    public string Details => string.Join(" · ", new[] { Tool.Path, Tool.Detail }.Where(s => !string.IsNullOrEmpty(s)));

    public bool HasDetails => Details.Length > 0;

    /// <summary>Whether it can be fixed from here: it needs it, and there's a plan.</summary>
    public bool CanInstall => Plan is not null && Tool.NeedsAttention && !_isConfirming && !_isInstalling;

    public string InstallTitle => Plan?.Kind switch
    {
        InstallKind.OpenPage => Localizer.Get("msg.setup.fixPage", "Open Download Page…"),
        InstallKind.Terminal => Localizer.Get("msg.setup.fixTerminal", "Install in Terminal…"),
        _ => Localizer.Get("msg.setup.fixInstall", "Install…"),
    };

    /// <summary>What saying yes does, before it does it: the plan and its exact command.</summary>
    public string Confirmation => Plan is null ? string.Empty : Localizer.Format("msg.firstRun.thisRuns", "{0}. This runs: {1}", Plan.Summary, Plan.Command);

    public string ConfirmTitle => Plan?.Kind switch
    {
        InstallKind.OpenPage => Localizer.Get("msg.setup.openPage", "Open Page"),
        InstallKind.Terminal => Localizer.Get("msg.setup.runInTerminal", "Run in Terminal"),
        _ => Localizer.Get("msg.setup.installNow", "Install"),
    };

    /// <summary>The plan is shown with Yes and Cancel.</summary>
    public bool IsConfirming
    {
        get => _isConfirming;
        private set
        {
            if (SetField(ref _isConfirming, value))
            {
                OnPropertyChanged(nameof(CanInstall));
            }
        }
    }

    public bool IsInstalling
    {
        get => _isInstalling;
        private set
        {
            if (SetField(ref _isInstalling, value))
            {
                OnPropertyChanged(nameof(CanInstall));
            }
        }
    }

    /// <summary>Install…: shows the plan; nothing runs yet.</summary>
    public ICommand InstallCommand { get; }

    /// <summary>The user's yes: runs the plan.</summary>
    public ICommand ConfirmCommand { get; }

    public ICommand CancelCommand { get; }

    public async Task ConfirmAsync()
    {
        IsConfirming = false;
        IsInstalling = true;
        try
        {
            await _install(Tool).ConfigureAwait(true);
        }
        finally
        {
            IsInstalling = false;
        }
    }
}

/// <summary>
/// One of System Integration's choices on the Tools step (M51, ADR 0084): off until chosen; what's registered already shows
/// checked when the setup runs again. Leaving the step applies a change the user made, and nothing else.
/// </summary>
public sealed class SetupIntegrationChoice : ObservableObject
{
    private bool _isChecked;
    private bool _initial;
    private OsIntegrationStatus _status = new(OsIntegrationState.NotRegistered);

    public SetupIntegrationChoice(OsIntegrationFeature feature, PlatformKind platform)
    {
        Feature = feature;
        Title = OsIntegrationText.Title(feature);
        Description = OsIntegrationText.Description(feature, platform);
    }

    public OsIntegrationFeature Feature { get; }

    public string Title { get; }

    public string Description { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set => SetField(ref _isChecked, value);
    }

    /// <summary>Whether it can be offered here (macOS file types need the app bundle).</summary>
    public bool IsAvailable => _status.State != OsIntegrationState.Unavailable;

    /// <summary>Why not, or how it stands when it's partly there or another copy's.</summary>
    public string? Note => _status.State switch
    {
        OsIntegrationState.Unavailable => _status.Detail,
        OsIntegrationState.Partial or OsIntegrationState.OtherCopy => Localizer.Get("msg.firstRun.integrationPartial", "Partly registered, or for another copy of Codalus IDE: check it to register it all for this copy. Tools → Options → Environment → System Integration removes it."),
        _ => null,
    };

    public bool HasNote => !string.IsNullOrEmpty(Note);

    /// <summary>The user changed it since it was shown.</summary>
    public bool IsChanged => _isChecked != _initial;

    /// <summary>Shows how it stands now: checked when it's registered.</summary>
    internal void Show(OsIntegrationStatus status)
    {
        _status = status;
        _initial = status.State == OsIntegrationState.Registered;
        IsChecked = _initial;
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(HasNote));
    }
}

/// <summary>
/// The first-run setup (M46): Appearance (the theme families as cards, Classic first (D36), and a mode; the setup window
/// and the IDE behind it repaint as the user chooses, through <see cref="ThemeService.Preview"/>; and the editor's font
/// size, on a sample), Keyboard (the scheme),
/// Your settings (import from VS Code, a <c>.vssettings</c> file or an export, or settings sync from a folder or a Git
/// repository), Tools (M37's check; nothing installs without a yes; and M51's System Integration, off unless checked)
/// and AI (ADR 0096: no AI, which is preselected, a coding agent, or the built-in assistant with a provider).
/// A first start opens on a welcome page (<see cref="SetupPage.Welcome"/>) that asks where the user comes from, which
/// preselects the keyboard scheme and marks the matching importer; the steps end on a summary
/// (<see cref="SetupPage.Finish"/>).
/// Leaving a step with Next or Back saves its choice, except AI's, which is applied only when the summary's button ends
/// the setup (the shell then opens Connect, or asks for the provider's key and model, in the main window);
/// Skip setup keeps what was saved (for a new user data folder, the defaults: Classic following the system, the default
/// keys, AI off).
/// Done and Skip setup record <see cref="ShellHost.SetupCompletedSetting"/>.
/// </summary>
public sealed class SetupViewModel : ObservableObject
{
    /// <summary>The editor font sizes the setup offers (the setting takes 6 to 72).</summary>
    public const double MinFontSize = 8;

    public const double MaxFontSize = 32;

    private const string FontSizeSetting = "editor.fontSize";

    private readonly ShellHost _shell;
    private SetupPage _page = SetupPage.Steps;
    private SetupStep _current = SetupStep.Appearance;
    private double _fontSize = 13;
    private IReadOnlyList<ThemePictureCodeLine> _codeSample = [];
    private string? _origin;
    private string? _originScheme;
    private string _summaryScheme = "default";
    private bool _aiLoaded;
    private SetupAiChoice _aiInitial = SetupAiChoice.None;
    private SetupAiPath _aiPath;
    private string? _aiAgent;
    private string? _aiProvider;
    private bool _aiInline;
    private bool _aiNextEdit;
    private string? _family;
    private string? _variant;
    private string _scheme = "default";
    private string _settingsStatus = string.Empty;
    private bool _isBusy;
    private bool _isChecking;
    private string _syncUrl = string.Empty;
    private string? _vsCodeFolder;

    public SetupViewModel(ShellHost shell, bool firstStart)
    {
        _shell = shell;
        IsFirstStart = firstStart;
        const int stepCount = 5;
        Steps =
        [
            new(SetupStep.Appearance, Localizer.Get("msg.firstRun.appearance", "Appearance"), stepCount),
            new(SetupStep.Keyboard, Localizer.Get("msg.firstRun.keyboard", "Keyboard"), stepCount),
            new(SetupStep.Settings, Localizer.Get("msg.firstRun.yourSettings", "Your settings"), stepCount),
            new(SetupStep.Tools, Localizer.Get("msg.firstRun.tools", "Tools"), stepCount),
            new(SetupStep.Ai, Localizer.Get("msg.firstRun.ai", "AI"), stepCount),
        ];
        Origins =
        [
            new("classic", Localizer.Get("msg.firstRun.originClassic", "The classic IDE"), Localizer.Get("msg.firstRun.originClassicDescription", "The Default keys, which are its own, and the importer for its exported settings (a .vssettings file) marked."), SelectOrigin),
            new("vscode", Localizer.Get("msg.firstRun.originVsCode", "VS Code"), Localizer.Get("msg.firstRun.originVsCodeDescription", "VS Code's keys, and the importer for its settings and key bindings marked."), SelectOrigin),
            new("rider", Localizer.Get("msg.firstRun.originRider", "Rider or ReSharper"), Localizer.Get("msg.firstRun.originRiderDescription", "The Default keys with ReSharper's on top."), SelectOrigin),
            new("other", Localizer.Get("msg.firstRun.originOther", "Somewhere else"), Localizer.Get("msg.firstRun.originOtherDescription", "Nothing is preselected: every step starts from the defaults."), SelectOrigin),
        ];
        // The same four facts for each path, so the cards read side by side (ADR 0096).
        var who = Localizer.Get("msg.firstRun.aiFactWho", "Who answers");
        var does = Localizer.Get("msg.firstRun.aiFactDoes", "What it does");
        var leaves = Localizer.Get("msg.firstRun.aiFactLeaves", "What leaves this machine");
        var needs = Localizer.Get("msg.firstRun.aiFactNeeds", "What you need");
        AiPaths =
        [
            new(SetupAiPath.None, Localizer.Get("msg.firstRun.aiNone", "No AI"), Localizer.Get("msg.firstRun.aiNoneDescription", "The IDE as it is. Completion and refactorings need no AI."),
            [
                new(who, Localizer.Get("msg.firstRun.aiNoneWho", "No one.")),
                new(does, Localizer.Get("msg.firstRun.aiNoneDoes", "Nothing.")),
                new(leaves, Localizer.Get("msg.firstRun.aiNoneLeaves", "Nothing.")),
                new(needs, Localizer.Get("msg.firstRun.aiNoneNeeds", "Nothing.")),
            ], SelectAiPath),
            new(SetupAiPath.Agent, Localizer.Get("msg.firstRun.aiAgent", "A coding agent"), Localizer.Get("msg.firstRun.aiAgentDescription", "Your own agent, with the IDE's tools."),
            [
                new(who, Localizer.Get("msg.firstRun.aiAgentWho", "Claude, Codex, Gemini CLI, GitHub Copilot or another, on your account.")),
                new(does, Localizer.Get("msg.firstRun.aiAgentDoes", "Works through a task by itself. No suggestions as you type.")),
                new(leaves, Localizer.Get("msg.firstRun.aiAgentLeaves", "What the agent reads, to its own provider.")),
                new(needs, Localizer.Get("msg.firstRun.aiAgentNeeds", "Its CLI or Node.js, and a sign-in.")),
            ], SelectAiPath) { IsRecommended = true },
            new(SetupAiPath.Assistant, Localizer.Get("msg.firstRun.aiAssistant", "The built-in assistant"), Localizer.Get("msg.firstRun.aiAssistantDescription", "Codalus IDE's own AI, with a model you choose."),
            [
                new(who, Localizer.Get("msg.firstRun.aiAssistantWho", "A model you choose, hosted or on this machine.")),
                new(does, Localizer.Get("msg.firstRun.aiAssistantDoes", "AI Chat, the AI actions and, if ticked, suggestions as you type.")),
                new(leaves, Localizer.Get("msg.firstRun.aiAssistantLeaves", "Only what a question needs. Secrets and ignored files are never sent.")),
                new(needs, Localizer.Get("msg.firstRun.aiAssistantNeeds", "An API key, or a model server on this machine.")),
            ], SelectAiPath),
        ];
        AgentBenefits =
        [
            Localizer.Get("msg.firstRun.aiBenefitTools", "It gets the IDE's tools: the compiler's symbols and references, the Error List, builds, tests and debugging."),
            Localizer.Get("msg.firstRun.aiBenefitChecks", "AI Chat shows its edits in a preview and asks before its commands."),
            Localizer.Get("msg.firstRun.aiBenefitContext", "Add Selection and Add File point it at the code you're looking at."),
        ];
        AgentWindows =
        [
            new(Localizer.Get("msg.firstRun.aiWindowConsole", "AI Console"), Localizer.Get("msg.firstRun.aiWindowConsoleDescription", "The agent's own CLI in a terminal tab. It edits and runs by itself.")),
            new(Localizer.Get("msg.firstRun.aiWindowChat", "AI Chat"), Localizer.Get("msg.firstRun.aiWindowChatDescription", "A conversation in the IDE. Its edits are previewed and its commands approved.")),
        ];
        DecreaseFontSizeCommand = new RelayCommand(() => FontSize -= 1, () => _fontSize > MinFontSize);
        IncreaseFontSizeCommand = new RelayCommand(() => FontSize += 1, () => _fontSize < MaxFontSize);
        Families =
        [
            new(ThemeService.Classic, "Classic", Localizer.Get("msg.firstRun.classic", "The classic IDE's familiar look: docked tool windows, plain tabs and a blue accent. Its Blue theme is in Options."), SelectFamily),
            new(ThemeService.Harbor, "Harbor", Localizer.Get("msg.firstRun.harbor", "The classic docked layout: a colored title bar, tool window captions and tab groups, closest to the IDE you know."), SelectFamily),
            new(ThemeService.Graphite, "Graphite", Localizer.Get("msg.firstRun.graphite", "Compact and dense: hairline dividers instead of gaps, flat tabs and more room for code and output."), SelectFamily),
            new(ThemeService.Daylight, "Daylight", Localizer.Get("msg.firstRun.daylight", "Flat and airy: soft rounded tabs, and the Start button filled with the accent."), SelectFamily),
            new(ThemeService.Ember, "Ember", Localizer.Get("msg.firstRun.ember", "Warm, with floating panels: rounded cards on the window's background."), SelectFamily),
        ];
        Modes =
        [
            new("light", Localizer.Get("msg.firstRun.light", "Light"), string.Empty, SelectMode),
            new("dark", Localizer.Get("msg.firstRun.dark", "Dark"), string.Empty, SelectMode),
            new("highContrast", Localizer.Get("msg.firstRun.highContrast", "High contrast"), string.Empty, SelectMode),
            new(null, Localizer.Get("msg.firstRun.matchSystem", "Match system"), string.Empty, SelectMode),
        ];
        Schemes =
        [
            new("default", Localizer.Get("msg.firstRun.schemeDefault", "Default"), Localizer.Get("msg.firstRun.schemeDefaultDescription", "The classic IDE's keys: F5 debugs, F6 builds, Ctrl+Q searches, Ctrl+, goes to anything."), SelectScheme),
            new("vscode", Localizer.Get("msg.firstRun.schemeVsCode", "VS Code"), Localizer.Get("msg.firstRun.schemeVsCodeDescription", "VS Code's keys: F1 is the command palette, Ctrl+, opens Settings, Ctrl+W closes."), SelectScheme),
            new("resharper", Localizer.Get("msg.firstRun.schemeResharper", "ReSharper"), Localizer.Get("msg.firstRun.schemeResharperDescription", "The default with ReSharper's: Ctrl+Shift+R refactors, Ctrl+W expands the selection, Alt+F7 finds usages."), SelectScheme),
            new("emacs", Localizer.Get("msg.firstRun.schemeEmacs", "Emacs"), Localizer.Get("msg.firstRun.schemeEmacsDescription", "The default with Emacs's editing keys: Ctrl+F/B/N/P move, Ctrl+K kills to the line's end, Ctrl+X chords."), SelectScheme),
        ];
        NextCommand = new RelayCommand(() => _ = NextAsync());
        BackCommand = new RelayCommand(() => _ = BackAsync(), () => CanGoBack);
        SkipCommand = new RelayCommand(() => _ = FinishAsync(done: false));
        ImportVsCodeCommand = new RelayCommand(() => _ = ImportVsCodeAsync(), () => HasVsCode && !_isBusy);
        ImportVsSettingsCommand = new RelayCommand(() => _ = ImportVsSettingsAsync(null), () => !_isBusy);
        ImportExportCommand = new RelayCommand(() => _ = ImportExportAsync(null), () => !_isBusy);
        SyncFolderCommand = new RelayCommand(() => _ = SyncWithFolderAsync(null), () => !_isBusy);
        SyncRepositoryCommand = new RelayCommand(() => _ = SyncAsync(_syncUrl), () => !_isBusy && SettingsSync.IsGitTarget(_syncUrl.Trim()));
        CheckToolsCommand = new RelayCommand(() => _ = CheckToolsAsync(), () => !_isChecking);
        OpenCheckEnvironmentCommand = new RelayCommand(shell.OpenCheckEnvironment);
        _vsCodeFolder = VsCodeImport.UserFolder(shell.Workbench.Platform, Environment.GetEnvironmentVariable);
        shell.ToolsChanged += OnToolsChanged;
        Integrations =
        [
            new(OsIntegrationFeature.Folders, shell.Workbench.Platform),
            new(OsIntegrationFeature.FileTypes, shell.Workbench.Platform),
        ];
        Enter(SetupStep.Appearance);
        if (firstStart)
        {
            Show(SetupPage.Welcome, SetupStep.Appearance);
        }
    }

    /// <summary>Raised when the setup is done or skipped (the window closes).</summary>
    public event EventHandler? Finished;

    /// <summary>At the first start (closing goes on to the start window), or again from Help.</summary>
    public bool IsFirstStart { get; }

    public IReadOnlyList<SetupStepViewModel> Steps { get; }

    /// <summary>The page shown: the welcome page, a step (<see cref="Current"/>) or the summary.</summary>
    public SetupPage Page => _page;

    /// <summary>The step shown, or the one the welcome page and the summary are next to (the first, the last).</summary>
    public SetupStep Current => _current;

    /// <summary>Shows a page (and, for <see cref="SetupPage.Steps"/>, a step), with everything that follows from it.</summary>
    private void Show(SetupPage page, SetupStep step)
    {
        if (_page == page && _current == step)
        {
            return;
        }

        _page = page;
        _current = step;
        foreach (var item in Steps)
        {
            item.IsCurrent = page == SetupPage.Steps && item.Step == step;
        }

        OnPropertyChanged(nameof(Page));
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(IsWelcome));
        OnPropertyChanged(nameof(IsFinish));
        OnPropertyChanged(nameof(IsAppearance));
        OnPropertyChanged(nameof(IsKeyboard));
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(IsTools));
        OnPropertyChanged(nameof(IsAi));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanSkip));
        OnPropertyChanged(nameof(NextTitle));
        OnPropertyChanged(nameof(SkipTitle));
        (BackCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public bool IsWelcome => _page == SetupPage.Welcome;

    public bool IsFinish => _page == SetupPage.Finish;

    public bool IsAppearance => _page == SetupPage.Steps && _current == SetupStep.Appearance;

    public bool IsKeyboard => _page == SetupPage.Steps && _current == SetupStep.Keyboard;

    public bool IsSettings => _page == SetupPage.Steps && _current == SetupStep.Settings;

    public bool IsTools => _page == SetupPage.Steps && _current == SetupStep.Tools;

    public bool IsAi => _page == SetupPage.Steps && _current == SetupStep.Ai;

    /// <summary>Back: from the summary to AI, from a step to the one before, from the first step to the welcome page (a first start has one).</summary>
    public bool CanGoBack => _page switch
    {
        SetupPage.Welcome => false,
        SetupPage.Finish => true,
        _ => _current != SetupStep.Appearance || IsFirstStart,
    };

    /// <summary>Skip setup shows until the summary, where everything but AI is saved and the button ends the setup.</summary>
    public bool CanSkip => _page != SetupPage.Finish;

    /// <summary>"Get Started" on the welcome page, "Next: Keyboard", …, and on the summary what its button does: "Done", or "Connect Claude…".</summary>
    public string NextTitle => _page switch
    {
        SetupPage.Welcome => Localizer.Get("msg.firstRun.getStarted", "Get Started"),
        SetupPage.Finish => FinishTitle,
        _ => _current switch
        {
            SetupStep.Appearance => Localizer.Get("msg.firstRun.nextKeyboard", "Next: Keyboard"),
            SetupStep.Keyboard => Localizer.Get("msg.firstRun.nextYourSettings", "Next: Your settings"),
            SetupStep.Settings => Localizer.Get("msg.firstRun.nextTools", "Next: Tools"),
            SetupStep.Tools => Localizer.Get("msg.firstRun.nextAi", "Next: AI"),
            _ => Localizer.Get("msg.firstRun.nextSummary", "Next: Summary"),
        },
    };

    /// <summary>"Use the defaults" on the welcome page (nothing was chosen yet), "Skip setup" on a step.</summary>
    public string SkipTitle => _page == SetupPage.Welcome
        ? Localizer.Get("msg.firstRun.useTheDefaults", "Use the defaults")
        : Localizer.Get("msg.firstRun.skipSetup", "Skip setup");

    public ICommand NextCommand { get; }

    public ICommand BackCommand { get; }

    public ICommand SkipCommand { get; }

    /// <summary>Done or Skip setup happened (or the window closed).</summary>
    public bool IsFinished { get; private set; }

    /// <summary>It ended with Done (not Skip setup or closing).</summary>
    public bool WasDone { get; private set; }

    /// <summary>The Tools step was shown (its check offers what M37 would offer at startup).</summary>
    public bool VisitedTools { get; private set; }

    // ------------------------------------------------------------------ appearance

    public IReadOnlyList<SetupFamilyOption> Families { get; }

    public IReadOnlyList<SetupChoice> Modes { get; }

    /// <summary>The chosen family, or null when the theme is none of them (a plugin's, from before).</summary>
    public string? SelectedFamily => _family;

    /// <summary>
    /// The chosen mode: <c>light</c>, <c>dark</c>, <c>highContrast</c>, or null to match the system; <c>blue</c> when the
    /// settings say Classic Blue, which only Options offers (no mode is chosen then, and it's kept unless one is).
    /// </summary>
    public string? SelectedVariant => _variant;

    public bool IsMatchSystem => _variant is null;

    /// <summary>Classic Blue, chosen in Options: the mode control shows none, and a note says where Blue is.</summary>
    public bool IsClassicBlue => _variant == "blue";

    /// <summary>The <c>workbench.colorTheme</c> value chosen (<c>harbor</c>, <c>ember.dark</c>), or null when no family is.</summary>
    public string? ThemeSetting => _family is null ? null : ThemeService.SettingFor(_family, _variant);

    private void SelectFamily(SetupFamilyOption family)
    {
        _family = family.Id;
        // Blue is Classic's only: another family matches the system, as a card in Options does.
        _variant = VariantFor(family.Id);
        ShowAppearance(preview: true);
    }

    /// <summary>The chosen mode where <paramref name="family"/> has it, or null (matching the system).</summary>
    private string? VariantFor(string family) =>
        _variant is { } variant && ThemeService.Families.First(f => f.Id == family).Variants.Contains(variant) ? variant : null;

    private void SelectMode(SetupChoice mode)
    {
        _variant = mode.Id;
        ShowAppearance(preview: true);
    }

    /// <summary>Shows the choice on the cards and modes; <paramref name="preview"/> repaints the IDE and this window in it.</summary>
    private void ShowAppearance(bool preview)
    {
        var system = _shell.Theme.SystemColors();
        foreach (var family in Families)
        {
            family.IsSelected = family.Id == _family;
            family.Picture = new ThemePicture(ThemeService.VariantOf(ThemeService.Resolve(ThemeService.SettingFor(family.Id, VariantFor(family.Id)), system)));
        }

        foreach (var mode in Modes)
        {
            mode.IsSelected = mode.Id == _variant;
        }

        // The font sample's colors until the window says which theme it is drawn in (ShowSampleIn): the chosen card's
        // (the theme being previewed), or the theme's own when no card is.
        CodeSample = (Families.FirstOrDefault(f => f.IsSelected)?.Picture
            ?? new ThemePicture(ThemeService.VariantOf(ThemeService.Resolve(_shell.Theme.Setting, system)))).Code;
        OnPropertyChanged(nameof(SelectedFamily));
        OnPropertyChanged(nameof(SelectedVariant));
        OnPropertyChanged(nameof(IsMatchSystem));
        OnPropertyChanged(nameof(IsClassicBlue));
        if (preview && ThemeSetting is { } setting)
        {
            _shell.Theme.Preview(setting);
        }
    }

    /// <summary>
    /// The editor's font size (<c>editor.fontSize</c>), shown on <see cref="CodeSample"/> as it's changed and saved with
    /// the step. Whole numbers from <see cref="MinFontSize"/> to <see cref="MaxFontSize"/>.
    /// </summary>
    public double FontSize
    {
        get => _fontSize;
        set
        {
            if (SetField(ref _fontSize, Math.Clamp(Math.Round(value), MinFontSize, MaxFontSize)))
            {
                OnPropertyChanged(nameof(FontSizeText));
                (DecreaseFontSizeCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (IncreaseFontSizeCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>"13 px".</summary>
    public string FontSizeText => Localizer.Format("msg.firstRun.fontSizeValue", "{0} px", _fontSize);

    public ICommand DecreaseFontSizeCommand { get; }

    public ICommand IncreaseFontSizeCommand { get; }

    /// <summary>The editor's font (<c>editor.fontFamily</c>), for the sample.</summary>
    public Avalonia.Media.FontFamily EditorFontFamily => _shell.EditorFont.Family;

    /// <summary>A few lines of C# in the previewed theme's syntax colors, drawn at <see cref="FontSize"/>.</summary>
    public IReadOnlyList<ThemePictureCodeLine> CodeSample
    {
        get => _codeSample;
        private set => SetField(ref _codeSample, value);
    }

    /// <summary>
    /// The sample in <paramref name="variant"/>'s syntax colors: the window calls it with the theme it is drawn in, at
    /// first and whenever that changes (a preview, or the system going dark while the theme follows it), so the sample's
    /// text never keeps another theme's colors on this theme's background.
    /// </summary>
    internal void ShowSampleIn(Avalonia.Styling.ThemeVariant variant) => CodeSample = new ThemePicture(variant).Code;

    // ------------------------------------------------------------------ welcome

    /// <summary>"I'm coming from": the classic IDE, VS Code, Rider or ReSharper, somewhere else. None is chosen at first.</summary>
    public IReadOnlyList<SetupChoice> Origins { get; }

    /// <summary>Where the user comes from (<c>classic</c>, <c>vscode</c>, <c>rider</c>, <c>other</c>), or null: not said.</summary>
    public string? SelectedOrigin => _origin;

    /// <summary>
    /// The importer the origin points at on Your settings (<c>vscode</c>, <c>vssettings</c>), or null. It is marked and
    /// takes the focus; nothing is imported until its button is pressed.
    /// </summary>
    public string? SuggestedImport => _origin switch
    {
        "vscode" => "vscode",
        "classic" => "vssettings",
        _ => null,
    };

    public bool SuggestsVsCode => SuggestedImport == "vscode";

    public bool SuggestsVsSettings => SuggestedImport == "vssettings";

    private void SelectOrigin(SetupChoice origin)
    {
        _origin = origin.Id;
        foreach (var choice in Origins)
        {
            choice.IsSelected = choice.Id == _origin;
        }

        OnPropertyChanged(nameof(SelectedOrigin));
        OnPropertyChanged(nameof(SuggestedImport));
        OnPropertyChanged(nameof(SuggestsVsCode));
        OnPropertyChanged(nameof(SuggestsVsSettings));
    }

    /// <summary>
    /// Leaving the welcome page: the origin's keyboard scheme is the one the Keyboard step shows chosen when it's next
    /// entered. Nothing is saved here; the step saves what it's left with, as always.
    /// </summary>
    private void ApplyOrigin() => _originScheme = _origin switch
    {
        "classic" => "default",
        "vscode" => "vscode",
        "rider" => "resharper",
        _ => null,
    };

    // ------------------------------------------------------------------ keyboard

    public IReadOnlyList<SetupChoice> Schemes { get; }

    public string SelectedScheme => _scheme;

    private void SelectScheme(SetupChoice scheme)
    {
        _scheme = scheme.Id ?? "default";
        ShowScheme();
    }

    private void ShowScheme()
    {
        foreach (var scheme in Schemes)
        {
            scheme.IsSelected = scheme.Id == _scheme;
        }

        OnPropertyChanged(nameof(SelectedScheme));
    }

    // ------------------------------------------------------------------ your settings

    /// <summary>VS Code's user folder on this machine (tests set their own), or null.</summary>
    public string? VsCodeFolder
    {
        get => _vsCodeFolder;
        internal set
        {
            if (SetField(ref _vsCodeFolder, value))
            {
                OnPropertyChanged(nameof(HasVsCode));
                OnPropertyChanged(nameof(VsCodeText));
                (ImportVsCodeCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasVsCode => _vsCodeFolder is not null && Directory.Exists(_vsCodeFolder);

    public string VsCodeText => HasVsCode
        ? Localizer.Format("msg.firstRun.vsCodeFound", "Its settings and key bindings, from {0}.", _vsCodeFolder)
        : Localizer.Get("msg.firstRun.vsCodeNotFound", "VS Code's settings weren't found on this machine (VS Code, Insiders or VSCodium).");

    /// <summary>The Git repository's URL to sync with.</summary>
    public string SyncUrl
    {
        get => _syncUrl;
        set
        {
            if (SetField(ref _syncUrl, value))
            {
                (SyncRepositoryCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>What the last import or sync did.</summary>
    public string SettingsStatus
    {
        get => _settingsStatus;
        private set
        {
            if (SetField(ref _settingsStatus, value))
            {
                OnPropertyChanged(nameof(HasSettingsStatus));
            }
        }
    }

    public bool HasSettingsStatus => _settingsStatus.Length > 0;

    /// <summary>An import or a sync is running.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                foreach (var command in new[] { ImportVsCodeCommand, ImportVsSettingsCommand, ImportExportCommand, SyncFolderCommand, SyncRepositoryCommand })
                {
                    (command as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }
    }

    public ICommand ImportVsCodeCommand { get; }

    public ICommand ImportVsSettingsCommand { get; }

    public ICommand ImportExportCommand { get; }

    public ICommand SyncFolderCommand { get; }

    public ICommand SyncRepositoryCommand { get; }

    /// <summary>Asks for a file (title, filter name, patterns) from the setup window; null: the main window's picker.</summary>
    public Func<string, string, string[], Task<string?>>? PickFile { get; set; }

    /// <summary>Asks for a folder from the setup window; null: the main window's picker.</summary>
    public Func<string, Task<string?>>? PickFolder { get; set; }

    public Task ImportVsCodeAsync() => RunSettingsAsync(() => _shell.ImportVsCodeSettingsAsync(_vsCodeFolder, confirm: false));

    /// <summary>Imports a <c>.vssettings</c> file (<paramref name="path"/>, or asked for).</summary>
    public async Task ImportVsSettingsAsync(string? path)
    {
        path ??= await PickFileAsync(Localizer.Get("msg.firstRun.pickVsSettings", "Import a .vssettings File"), Localizer.Get("msg.firstRun.vsSettingsFiles", "Exported IDE settings"), ["*.vssettings"]).ConfigureAwait(true);
        if (path is not null)
        {
            await RunSettingsAsync(() => _shell.ImportVsSettingsAsync(path, confirm: false)).ConfigureAwait(true);
        }
    }

    /// <summary>Imports a file exported with Tools → Export Settings (<paramref name="path"/>, or asked for): all of it.</summary>
    public async Task ImportExportAsync(string? path)
    {
        path ??= await PickFileAsync(Localizer.Get("msg.firstRun.pickExport", "Import Settings"), Localizer.Get("msg.firstRun.exportFiles", "Codalus IDE settings"), ["*.json"]).ConfigureAwait(true);
        if (path is not null)
        {
            await RunSettingsAsync(() => _shell.ImportSettingsAsync(path, ShellHost.SettingsParts.All)).ConfigureAwait(true);
        }
    }

    /// <summary>Turns settings sync on with a folder (<paramref name="folder"/>, or asked for).</summary>
    public async Task SyncWithFolderAsync(string? folder)
    {
        folder ??= PickFolder is { } pick ? await pick(Localizer.Get("msg.firstRun.pickSyncFolder", "Sync Settings with a Folder")).ConfigureAwait(true)
            : _shell.Window is { } window ? await window.PickFolderAsync(Localizer.Get("msg.firstRun.pickSyncFolder", "Sync Settings with a Folder")).ConfigureAwait(true)
            : null;
        if (folder is not null)
        {
            await SyncAsync(folder).ConfigureAwait(true);
        }
    }

    /// <summary>Turns settings sync on with a folder or a Git repository's URL; what's there replaces this machine's settings.</summary>
    public Task SyncAsync(string target) => string.IsNullOrWhiteSpace(target)
        ? Task.CompletedTask
        : RunSettingsAsync(async () => await _shell.TurnOnSettingsSyncFromSetupAsync(target).ConfigureAwait(true));

    private Task<string?> PickFileAsync(string title, string filterName, string[] patterns) =>
        PickFile is { } pick ? pick(title, filterName, patterns)
            : _shell.Window is { } window ? window.PickFileAsync(title, filterName, patterns)
            : Task.FromResult<string?>(null);

    private async Task RunSettingsAsync(Func<Task<string?>> work)
    {
        IsBusy = true;
        SettingsStatus = Localizer.Get("msg.firstRun.working", "Working…");
        try
        {
            SettingsStatus = await work().ConfigureAwait(true) ?? string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------------ tools

    /// <summary>The .NET SDK and git, as M37's check found them.</summary>
    public ObservableCollection<SetupToolRowViewModel> Tools { get; } = [];

    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (SetField(ref _isChecking, value))
            {
                (CheckToolsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand CheckToolsCommand { get; }

    /// <summary>Help → Check Environment: everything else the IDE uses.</summary>
    public ICommand OpenCheckEnvironmentCommand { get; }

    /// <summary>Runs installs instead of the real process (tests: nothing installs for real).</summary>
    internal ProcessRunner? InstallRunner { get; set; }

    public async Task CheckToolsAsync()
    {
        IsChecking = true;
        try
        {
            await _shell.CheckEnvironmentAsync(offer: false).ConfigureAwait(true);
        }
        finally
        {
            IsChecking = false;
        }

        FillTools();
    }

    private void OnToolsChanged(object? sender, EventArgs e) => Ui.Post(FillTools);

    private void FillTools() => ShowTools(_shell.Tools);

    /// <summary>
    /// Shows these tools' rows: the .NET SDK, the .NET runtime Codalus IDE needs when it's missing (M49; the SDK row is
    /// Found with SDKs alone), and git. Tests pass stand-ins.
    /// </summary>
    internal void ShowTools(IReadOnlyList<ToolStatus> tools)
    {
        Tools.Clear();
        foreach (var tool in tools.Where(t => t.Id is "dotnet" or "git" || t is { Id: "dotnet-runtime", NeedsAttention: true }))
        {
            // The row showed the plan and the user said yes: the install doesn't ask again.
            Tools.Add(new SetupToolRowViewModel(tool, _shell.PlanFor(tool), t => _shell.InstallToolAsync(t, _ => Task.FromResult(true), InstallRunner)));
        }
    }

    // ------------------------------------------------------------------ system integration

    /// <summary>"Open with Codalus IDE" on folders and the file types (M51): off unless chosen.</summary>
    public IReadOnlyList<SetupIntegrationChoice> Integrations { get; }

    private void ShowIntegrations()
    {
        foreach (var choice in Integrations)
        {
            choice.Show(_shell.OsIntegration.GetStatus(choice.Feature));
        }
    }

    /// <summary>Registers or removes what the user changed on the step; a failure is told and the setup goes on.</summary>
    private async Task SaveIntegrationsAsync()
    {
        foreach (var choice in Integrations.Where(c => c.IsChanged && c.IsAvailable))
        {
            var (status, error) = await _shell.SetOsIntegrationAsync(choice.Feature, choice.IsChecked).ConfigureAwait(true);
            if (error is not null)
            {
                _shell.Workbench.Notifications.Error(error);
            }

            choice.Show(status);
        }
    }

    // ------------------------------------------------------------------ AI

    /// <summary>No AI, a coding agent, or the built-in assistant, each with the same facts to compare.</summary>
    public IReadOnlyList<SetupAiPathOption> AiPaths { get; }

    /// <summary>Why to run an agent from Codalus IDE instead of a plain terminal (ADR 0092, 0095), shown with the agents: three short lines; the user guide has the rest.</summary>
    public IReadOnlyList<string> AgentBenefits { get; }

    /// <summary>The two windows an agent works in, and how they differ (ADR 0095).</summary>
    public IReadOnlyList<SetupSummaryRow> AgentWindows { get; }

    /// <summary>
    /// The agents to choose from: the one-click templates (ADR 0083), the user's other connected agents, and More agents…
    /// (<see cref="SetupAiChoice.More"/>). A connected one says so.
    /// </summary>
    public ObservableCollection<SetupChoice> AiAgents { get; } = [];

    /// <summary>The providers the installed plugins declare (ADR 0062), by name, a local one marked (their descriptions are in Options).</summary>
    public ObservableCollection<SetupChoice> AiProviders { get; } = [];

    public SetupAiPath AiPath => _aiPath;

    public bool IsAiAgent => _aiPath == SetupAiPath.Agent;

    public bool IsAiAssistant => _aiPath == SetupAiPath.Assistant;

    /// <summary>Why no agent can be connected from here (a remote window, ADR 0083), or null.</summary>
    public string? AgentsBlockedReason => _shell.AgentConnector.WhyNot();

    public bool AreAgentsBlocked => AgentsBlockedReason is not null;

    public bool HasNoProviders => AiProviders.Count == 0;

    /// <summary>Inline Suggestions (<c>ai.inlineSuggestions.enabled</c>): text around the caret goes to the provider as you type. Off unless ticked.</summary>
    public bool AiInlineSuggestions
    {
        get => _aiInline;
        set => SetField(ref _aiInline, value);
    }

    /// <summary>Next Edit Suggestions: recent edits and the lines around the caret go to the provider. Off unless ticked.</summary>
    public bool AiNextEditSuggestions
    {
        get => _aiNextEdit;
        set => SetField(ref _aiNextEdit, value);
    }

    /// <summary>What the step holds now, with only what its path uses.</summary>
    public SetupAiChoice AiChoice => _aiPath switch
    {
        SetupAiPath.Agent => new SetupAiChoice(SetupAiPath.Agent, Agent: _aiAgent),
        SetupAiPath.Assistant => new SetupAiChoice(SetupAiPath.Assistant, Provider: _aiProvider, InlineSuggestions: _aiInline, NextEdit: _aiNextEdit),
        _ => SetupAiChoice.None,
    };

    /// <summary>
    /// The step was shown and holds something other than what the settings have: ending the setup with the summary's
    /// button applies it (<c>ShellHost.ApplySetupAiAsync</c>). Skip setup, Esc and closing the window never do.
    /// </summary>
    public bool AiChanged => _aiLoaded && AiChoice != _aiInitial;

    private void SelectAiPath(SetupAiPathOption path)
    {
        _aiPath = path.Path;
        ShowAi();
    }

    private void SelectAiAgent(SetupChoice agent)
    {
        _aiPath = SetupAiPath.Agent;
        _aiAgent = agent.Id;
        ShowAi();
    }

    private void SelectAiProvider(SetupChoice provider)
    {
        _aiPath = SetupAiPath.Assistant;
        _aiProvider = provider.Id;
        ShowAi();
    }

    private void ShowAi()
    {
        foreach (var choice in AiPaths)
        {
            choice.IsSelected = choice.Path == _aiPath;
        }

        foreach (var agent in AiAgents)
        {
            agent.IsSelected = agent.Id == _aiAgent;
        }

        foreach (var provider in AiProviders)
        {
            provider.IsSelected = provider.Id == _aiProvider;
        }

        OnPropertyChanged(nameof(AiPath));
        OnPropertyChanged(nameof(IsAiAgent));
        OnPropertyChanged(nameof(IsAiAssistant));
        OnPropertyChanged(nameof(AiChoice));
        OnPropertyChanged(nameof(AiChanged));
    }

    /// <summary>
    /// What the user's settings say about AI, as a choice of the step: off (the default); on with an agent answering in AI
    /// Chat, or with a provider; on with only connected agents; on with nothing chosen (the assistant, no provider).
    /// Read from the user's layer, where all of it lives (ADR 0062, 0079).
    /// </summary>
    private SetupAiChoice ReadAi()
    {
        var settings = _shell.Workbench.Settings;
        if (settings.GetLayerValue(AiService.EnabledSetting, SettingScope.User) is not { ValueKind: System.Text.Json.JsonValueKind.True })
        {
            return SetupAiChoice.None;
        }

        var configured = _shell.ExternalAgents.Configured;
        var chatAgent = settings.GetString(AcpAgents.ChatAgentSetting, string.Empty);
        if (chatAgent.Length > 0 && configured.ContainsKey(chatAgent))
        {
            return new SetupAiChoice(SetupAiPath.Agent, Agent: chatAgent);
        }

        var provider = settings.GetString(AiService.ProviderSetting, string.Empty);
        if (provider.Length == 0 && configured.Count > 0)
        {
            return new SetupAiChoice(SetupAiPath.Agent, Agent: configured.Keys.Order(StringComparer.Ordinal).First());
        }

        return new SetupAiChoice(
            SetupAiPath.Assistant,
            Provider: provider.Length > 0 ? provider : null,
            InlineSuggestions: settings.GetLayerValue(AiCompletions.EnabledSetting, SettingScope.User) is { ValueKind: System.Text.Json.JsonValueKind.True },
            NextEdit: settings.GetLayerValue(AiNextEdit.EnabledSetting, SettingScope.User) is { ValueKind: System.Text.Json.JsonValueKind.True });
    }

    /// <summary>
    /// Entering the AI step: the agents and providers as they stand now, and the settings' state as the choice, unless the
    /// user already changed it here (an import or a sync on Your settings may have brought AI settings since).
    /// </summary>
    private void EnterAi()
    {
        var now = ReadAi();
        var untouched = !_aiLoaded || AiChoice == _aiInitial;
        _aiInitial = now;
        _aiLoaded = true;

        var configured = _shell.ExternalAgents.Configured;
        var connected = Localizer.Get("msg.firstRun.aiConnected", "Connected");
        AiAgents.Clear();
        foreach (var template in AcpAgentTemplates.All.Where(t => t.Tier == AcpTemplateTier.OneClick))
        {
            AiAgents.Add(new SetupChoice(template.Id, template.Name, configured.ContainsKey(template.Id) ? connected : template.Vendor, SelectAiAgent));
        }

        foreach (var name in configured.Keys.Where(k => AiAgents.All(a => a.Id != k)).Order(StringComparer.Ordinal))
        {
            AiAgents.Add(new SetupChoice(name, AcpAgentTemplates.Find(name)?.Name ?? name, connected, SelectAiAgent));
        }

        AiAgents.Add(new SetupChoice(SetupAiChoice.More, Localizer.Get("msg.firstRun.aiMoreAgents", "More agents…"), Localizer.Get("msg.firstRun.aiMoreAgentsDescription", "pi, OpenCode, goose"), SelectAiAgent));

        AiProviders.Clear();
        foreach (var provider in _shell.LanguageModels.Providers)
        {
            AiProviders.Add(new SetupChoice(provider.Id, provider.Label, provider.IsLocal ? Localizer.Get("msg.firstRun.aiOnThisMachine", "On this machine") : string.Empty, SelectAiProvider));
        }

        if (untouched)
        {
            _aiPath = now.Path;
            _aiAgent = now.Agent;
            _aiProvider = now.Provider;
            _aiInline = now.InlineSuggestions;
            _aiNextEdit = now.NextEdit;
            OnPropertyChanged(nameof(AiInlineSuggestions));
            OnPropertyChanged(nameof(AiNextEditSuggestions));
        }

        OnPropertyChanged(nameof(AgentsBlockedReason));
        OnPropertyChanged(nameof(AreAgentsBlocked));
        OnPropertyChanged(nameof(HasNoProviders));
        ShowAi();
    }

    /// <summary>The agent's or the provider's name as the step lists it.</summary>
    private string? AiAgentTitle => _aiAgent is null or SetupAiChoice.More ? null : AiAgents.FirstOrDefault(a => a.Id == _aiAgent)?.Title ?? _aiAgent;

    private string? AiProviderTitle => _aiProvider is null ? null : AiProviders.FirstOrDefault(p => p.Id == _aiProvider)?.Title ?? _aiProvider;

    private bool IsAiAgentConnected => _aiAgent is { } agent && _shell.ExternalAgents.Configured.ContainsKey(agent);

    // ------------------------------------------------------------------ summary

    /// <summary>What the setup left chosen: the theme, the keys, the font size, the tools and AI.</summary>
    public ObservableCollection<SetupSummaryRow> Summary { get; } = [];

    /// <summary>A few keys worth knowing, as the chosen scheme has them (a command without a key isn't listed).</summary>
    public ObservableCollection<SetupShortcutRow> Shortcuts { get; } = [];

    public bool HasShortcuts => Shortcuts.Count > 0;

    /// <summary>"Good to know (VS Code keys)".</summary>
    public string ShortcutsTitle => Localizer.Format("msg.firstRun.goodToKnow", "Good to know ({0} keys)", Schemes.FirstOrDefault(s => s.Id == _summaryScheme)?.Title ?? _summaryScheme);

    /// <summary>What the summary's button does next when AI was chosen: Connect, the list of agents, the provider's key and model; null when nothing follows.</summary>
    public string? AiNext => !AiChanged ? null : AiChoice switch
    {
        { Path: SetupAiPath.Agent } when IsAiAgentConnected => null,
        { Path: SetupAiPath.Agent } when AiAgentTitle is { } agent => Localizer.Format("msg.firstRun.aiNextConnect", "Next, Connect to {0} opens in the main window: it checks what {0} needs, signs you in and tests the connection. AI assistance turns on only when that test passes and you agree there.", agent),
        { Path: SetupAiPath.Agent } => Localizer.Get("msg.firstRun.aiNextChooseAgent", "Next, the list of agents opens in the main window. Choosing one opens Connect, which checks what the agent needs, signs you in and tests the connection."),
        { Path: SetupAiPath.Assistant } when AiProviderTitle is { } provider => Localizer.Format("msg.firstRun.aiNextProvider", "Next, AI assistance turns on with {0}, and the main window asks for what {0} needs (a key, kept in your system's credential store) and for the model.", provider),
        { Path: SetupAiPath.Assistant } => Localizer.Get("msg.firstRun.aiNextChooseProvider", "Next, AI assistance turns on and the main window asks which provider to use. Nothing is sent before you choose one."),
        _ => null,
    };

    public bool HasAiNext => AiNext is not null;

    /// <summary>The summary's button: "Done", or what it goes on to ("Connect Claude…", "Set Up Ollama…").</summary>
    private string FinishTitle => !AiChanged ? Localizer.Get("msg.firstRun.done", "Done") : AiChoice switch
    {
        { Path: SetupAiPath.Agent } when IsAiAgentConnected => Localizer.Get("msg.firstRun.done", "Done"),
        { Path: SetupAiPath.Agent } when AiAgentTitle is { } agent => Localizer.Format("msg.firstRun.finishConnect", "Connect {0}…", agent),
        { Path: SetupAiPath.Agent } => Localizer.Get("msg.firstRun.finishChooseAgent", "Choose an Agent…"),
        { Path: SetupAiPath.Assistant } when AiProviderTitle is { } provider => Localizer.Format("msg.firstRun.finishProvider", "Set Up {0}…", provider),
        { Path: SetupAiPath.Assistant } => Localizer.Get("msg.firstRun.finishChooseProvider", "Choose a Provider…"),
        _ => Localizer.Get("msg.firstRun.done", "Done"),
    };

    /// <summary>The AI row of the summary.</summary>
    private string AiSummary => AiChoice switch
    {
        { Path: SetupAiPath.Agent } when AiAgentTitle is { } agent => IsAiAgentConnected
            ? Localizer.Format("msg.firstRun.summaryAgentConnected", "{0}, connected", agent)
            : Localizer.Format("msg.firstRun.summaryAgent", "{0}, a coding agent (connects next)", agent),
        { Path: SetupAiPath.Agent } => Localizer.Get("msg.firstRun.summaryAgentToChoose", "A coding agent (you choose it next)"),
        { Path: SetupAiPath.Assistant } when AiProviderTitle is { } provider => string.Join(", ", new[]
        {
            Localizer.Format("msg.firstRun.summaryAssistant", "The built-in assistant with {0}", provider),
            _aiInline ? Localizer.Get("msg.firstRun.summaryInline", "inline suggestions") : null,
            _aiNextEdit ? Localizer.Get("msg.firstRun.summaryNextEdit", "next edit suggestions") : null,
        }.Where(part => part is not null)),
        { Path: SetupAiPath.Assistant } => Localizer.Get("msg.firstRun.summaryAssistantToChoose", "The built-in assistant (you choose the provider next)"),
        _ => Localizer.Get("msg.firstRun.summaryNoAi", "Off"),
    };

    /// <summary>The summary's rows and keys, from what is saved now (and AI's choice, which isn't yet).</summary>
    private void ShowSummary()
    {
        var settings = _shell.Workbench.Settings;
        Summary.Clear();

        var parsed = ThemeService.Parse(_shell.Theme.Setting);
        var family = Families.FirstOrDefault(f => f.Id == parsed?.Family)?.Title;
        var mode = parsed?.Variant switch
        {
            null => Localizer.Get("msg.firstRun.matchSystem", "Match system"),
            "blue" => Localizer.Get("msg.firstRun.blue", "Blue"),
            var variant => Modes.FirstOrDefault(m => m.Id == variant)?.Title ?? variant,
        };
        Summary.Add(new(Localizer.Get("msg.firstRun.summaryTheme", "Theme"), family is null ? _shell.Theme.Setting : $"{family}, {mode}"));

        var scheme = settings.GetString(BuiltIns.KeyboardSchemeSetting, "default");
        Summary.Add(new(Localizer.Get("msg.firstRun.summaryKeys", "Keys"), Schemes.FirstOrDefault(s => s.Id == scheme)?.Title ?? scheme));
        Summary.Add(new(Localizer.Get("msg.firstRun.summaryFontSize", "Editor font size"), Localizer.Format("msg.firstRun.fontSizeValue", "{0} px", settings.GetDouble(FontSizeSetting, 13))));

        var tools = _shell.Tools.Where(t => t.Id is "dotnet" or "git").ToList();
        Summary.Add(new(Localizer.Get("msg.firstRun.summaryTools", "Tools"), tools.Count == 0
            ? Localizer.Get("msg.firstRun.summaryToolsNotChecked", "Not checked yet")
            : string.Join(", ", tools.Select(t => t.NeedsAttention
                ? Localizer.Format("msg.firstRun.summaryToolMissing", "{0} is missing", t.Title)
                : t.Version is { } version ? $"{t.Title} {version}" : t.Title))));
        Summary.Add(new(Localizer.Get("msg.firstRun.summaryAi", "AI"), AiSummary));

        _summaryScheme = scheme;
        Shortcuts.Clear();
        foreach (var (command, title) in new[]
        {
            ("workbench.showCommandPalette", Localizer.Get("msg.firstRun.keyCommands", "Run any command")),
            ("workbench.action.gotoAll", Localizer.Get("msg.firstRun.keyGoToAll", "Go to any file, type or member")),
            ("build.buildSolution", Localizer.Get("msg.firstRun.keyBuild", "Build the solution")),
            ("debug.start", Localizer.Get("msg.firstRun.keyDebug", "Start debugging")),
        })
        {
            if (_shell.Workbench.Keybindings.GetBinding(command)?.ToLabel(_shell.Workbench.Platform) is { Length: > 0 } keys)
            {
                Shortcuts.Add(new(keys, title));
            }
        }

        OnPropertyChanged(nameof(HasShortcuts));
        OnPropertyChanged(nameof(ShortcutsTitle));
        OnPropertyChanged(nameof(AiNext));
        OnPropertyChanged(nameof(HasAiNext));
    }

    // ------------------------------------------------------------------ steps

    public async Task NextAsync()
    {
        if (IsFinished)
        {
            return;
        }

        switch (_page)
        {
            case SetupPage.Welcome:
                ApplyOrigin();
                Enter(SetupStep.Appearance);
                return;
            case SetupPage.Finish:
                await FinishAsync(done: true).ConfigureAwait(true);
                return;
        }

        await SaveAsync(_current).ConfigureAwait(true);
        if (_current == SetupStep.Ai)
        {
            ShowSummary();
            Show(SetupPage.Finish, SetupStep.Ai);
            return;
        }

        Enter(_current + 1);
    }

    public async Task BackAsync()
    {
        if (IsFinished || !CanGoBack)
        {
            return;
        }

        if (_page == SetupPage.Finish)
        {
            Enter(SetupStep.Ai);
            return;
        }

        await SaveAsync(_current).ConfigureAwait(true);
        if (_current == SetupStep.Appearance)
        {
            Show(SetupPage.Welcome, SetupStep.Appearance);
            return;
        }

        Enter(_current - 1);
    }

    /// <summary>
    /// Ends the setup: Done (<paramref name="done"/>; the steps were saved as they were left, and the shell applies AI's
    /// choice when <see cref="AiChanged"/>) or Skip setup (the current step's choice isn't saved, a preview ends, and
    /// nothing about AI changes). Either records that the setup ran.
    /// </summary>
    public async Task FinishAsync(bool done)
    {
        if (IsFinished)
        {
            return;
        }

        IsFinished = true;
        WasDone = done;
        _shell.ToolsChanged -= OnToolsChanged;
        _shell.Theme.Preview(null);
        await _shell.Workbench.Settings.SetAsync(ShellHost.SetupCompletedSetting, true, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        Finished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows a step with what's saved now (an import or a sync may have changed it).</summary>
    private void Enter(SetupStep step)
    {
        Show(SetupPage.Steps, step);
        switch (step)
        {
            case SetupStep.Appearance:
                // Every built-in family has a card (a value from before M45 shows as Classic); a plugin's theme none.
                var parsed = ThemeService.Parse(_shell.Theme.Setting);
                _family = parsed?.Family;
                _variant = parsed?.Variant;
                FontSize = _shell.Workbench.Settings.GetDouble(FontSizeSetting, 13);
                ShowAppearance(preview: false);
                break;
            case SetupStep.Keyboard:
                var scheme = _shell.Workbench.Settings.GetString(BuiltIns.KeyboardSchemeSetting, "default");
                _scheme = Schemes.Any(s => s.Id == scheme) ? scheme : "default";
                if (_originScheme is { } fromOrigin)
                {
                    // Where the user comes from, once: after that the step shows what it saved.
                    _scheme = fromOrigin;
                    _originScheme = null;
                }

                ShowScheme();
                break;
            case SetupStep.Ai:
                EnterAi();
                break;
            case SetupStep.Tools:
                VisitedTools = true;
                ShowIntegrations();
                FillTools();
                if (Tools.Count == 0 || !_shell.ToolsComplete)
                {
                    _ = CheckToolsAsync();
                }

                break;
        }
    }

    /// <summary>
    /// Saves a step's choice when it differs from what's saved; the appearance's preview ends. The theme is also saved when
    /// the settings don't have one, so a choice made here stays the user's when the default changes (D36: Harbor became
    /// Classic, and Harbor chosen here in 0.3.0 hadn't been written).
    /// </summary>
    private async Task SaveAsync(SetupStep step)
    {
        var settings = _shell.Workbench.Settings;
        switch (step)
        {
            case SetupStep.Appearance:
                if (ThemeSetting is { } theme && (ThemeService.Normalize(_shell.Theme.Setting) != theme || settings.GetLayerValue(ThemeService.SettingKey, SettingScope.User) is null))
                {
                    await settings.SetAsync(ThemeService.SettingKey, theme, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                }

                // The font size as the setting holds it: a whole number, written only when it changed.
                if (Math.Abs(Math.Clamp(settings.GetDouble(FontSizeSetting, 13), MinFontSize, MaxFontSize) - _fontSize) >= 0.5)
                {
                    await settings.SetAsync(FontSizeSetting, _fontSize, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                }

                _shell.Theme.Preview(null);
                break;
            case SetupStep.Keyboard:
                if (settings.GetString(BuiltIns.KeyboardSchemeSetting, "default") != _scheme)
                {
                    await settings.SetAsync(BuiltIns.KeyboardSchemeSetting, _scheme, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                }

                break;
            case SetupStep.Tools:
                await SaveIntegrationsAsync().ConfigureAwait(true);
                break;
        }
    }
}
