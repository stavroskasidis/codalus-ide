using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.Theming;
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
}

/// <summary>A step in the setup's list: its number and title, and whether it's the current one.</summary>
public sealed class SetupStepViewModel(SetupStep step, string title) : ObservableObject
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

    /// <summary>"Step 1 of 4: Appearance, current step".</summary>
    public string AccessibleName => IsCurrent
        ? Localizer.Format("msg.firstRun.stepCurrent", "Step {0} of {1}: {2}, current step", Number, 4, Title)
        : Localizer.Format("msg.firstRun.step", "Step {0} of {1}: {2}", Number, 4, Title);
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
        _picture = new ThemePicture(ThemeService.VariantOf(id + ".light"));
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
/// The first-run setup (M46): Appearance (the four theme families as cards and a mode; the setup window and the IDE behind
/// it repaint as the user chooses, through <see cref="ThemeService.Preview"/>), Keyboard (the scheme), Your settings
/// (import from VS Code, a <c>.vssettings</c> file or an export, or settings sync from a folder or a Git repository) and
/// Tools (M37's check; nothing installs without a yes). Leaving a step with Next or Back saves its choice; Skip setup
/// keeps what was saved (for a new user data folder, the defaults: Harbor following the system, the default keys).
/// Done and Skip setup record <see cref="ShellHost.SetupCompletedSetting"/>. AI isn't asked about (ADR 0062).
/// </summary>
public sealed class SetupViewModel : ObservableObject
{
    private static readonly string[] s_families = [ThemeService.Harbor, ThemeService.Graphite, ThemeService.Daylight, ThemeService.Ember];

    private readonly ShellHost _shell;
    private SetupStep _current = SetupStep.Appearance;
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
        Steps =
        [
            new(SetupStep.Appearance, Localizer.Get("msg.firstRun.appearance", "Appearance")),
            new(SetupStep.Keyboard, Localizer.Get("msg.firstRun.keyboard", "Keyboard")),
            new(SetupStep.Settings, Localizer.Get("msg.firstRun.yourSettings", "Your settings")),
            new(SetupStep.Tools, Localizer.Get("msg.firstRun.tools", "Tools")),
        ];
        Families =
        [
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
        Steps[0].IsCurrent = true;
        Enter(SetupStep.Appearance);
    }

    /// <summary>Raised when the setup is done or skipped (the window closes).</summary>
    public event EventHandler? Finished;

    /// <summary>At the first start (closing goes on to the start window), or again from Help.</summary>
    public bool IsFirstStart { get; }

    public IReadOnlyList<SetupStepViewModel> Steps { get; }

    public SetupStep Current
    {
        get => _current;
        private set
        {
            if (SetField(ref _current, value))
            {
                foreach (var step in Steps)
                {
                    step.IsCurrent = step.Step == value;
                }

                OnPropertyChanged(nameof(IsAppearance));
                OnPropertyChanged(nameof(IsKeyboard));
                OnPropertyChanged(nameof(IsSettings));
                OnPropertyChanged(nameof(IsTools));
                OnPropertyChanged(nameof(CanGoBack));
                OnPropertyChanged(nameof(NextTitle));
                (BackCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsAppearance => _current == SetupStep.Appearance;

    public bool IsKeyboard => _current == SetupStep.Keyboard;

    public bool IsSettings => _current == SetupStep.Settings;

    public bool IsTools => _current == SetupStep.Tools;

    public bool CanGoBack => _current != SetupStep.Appearance;

    /// <summary>"Next: Keyboard", …, "Done" on the last step.</summary>
    public string NextTitle => _current switch
    {
        SetupStep.Appearance => Localizer.Get("msg.firstRun.nextKeyboard", "Next: Keyboard"),
        SetupStep.Keyboard => Localizer.Get("msg.firstRun.nextYourSettings", "Next: Your settings"),
        SetupStep.Settings => Localizer.Get("msg.firstRun.nextTools", "Next: Tools"),
        _ => Localizer.Get("msg.firstRun.done", "Done"),
    };

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

    /// <summary>The chosen family, or null when the theme is none of the four (Classic or a plugin's, from before).</summary>
    public string? SelectedFamily => _family;

    /// <summary>The chosen mode: <c>light</c>, <c>dark</c>, <c>highContrast</c>, or null to match the system.</summary>
    public string? SelectedVariant => _variant;

    public bool IsMatchSystem => _variant is null;

    /// <summary>The <c>workbench.colorTheme</c> value chosen (<c>harbor</c>, <c>ember.dark</c>), or null when no family is.</summary>
    public string? ThemeSetting => _family is null ? null : ThemeService.SettingFor(_family, _variant);

    private void SelectFamily(SetupFamilyOption family)
    {
        _family = family.Id;
        ShowAppearance(preview: true);
    }

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
            family.Picture = new ThemePicture(ThemeService.VariantOf(ThemeService.Resolve(ThemeService.SettingFor(family.Id, _variant), system)));
        }

        foreach (var mode in Modes)
        {
            mode.IsSelected = mode.Id == _variant;
        }

        OnPropertyChanged(nameof(SelectedFamily));
        OnPropertyChanged(nameof(SelectedVariant));
        OnPropertyChanged(nameof(IsMatchSystem));
        if (preview && ThemeSetting is { } setting)
        {
            _shell.Theme.Preview(setting);
        }
    }

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

    // ------------------------------------------------------------------ steps

    public async Task NextAsync()
    {
        if (IsFinished)
        {
            return;
        }

        await SaveAsync(_current).ConfigureAwait(true);
        if (_current == SetupStep.Tools)
        {
            await FinishAsync(done: true).ConfigureAwait(true);
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

        await SaveAsync(_current).ConfigureAwait(true);
        Enter(_current - 1);
    }

    /// <summary>
    /// Ends the setup: Done (<paramref name="done"/>; the steps were saved as they were left) or Skip setup (the current
    /// step's choice isn't saved, and a preview ends). Either records that the setup ran.
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
        Current = step;
        switch (step)
        {
            case SetupStep.Appearance:
                var parsed = ThemeService.Parse(_shell.Theme.Setting);
                _family = parsed is var (family, _) && s_families.Contains(family) ? family : null;
                _variant = parsed?.Variant is "light" or "dark" or "highContrast" ? parsed.Value.Variant : null;
                ShowAppearance(preview: false);
                break;
            case SetupStep.Keyboard:
                var scheme = _shell.Workbench.Settings.GetString(BuiltIns.KeyboardSchemeSetting, "default");
                _scheme = Schemes.Any(s => s.Id == scheme) ? scheme : "default";
                ShowScheme();
                break;
            case SetupStep.Tools:
                VisitedTools = true;
                FillTools();
                if (Tools.Count == 0)
                {
                    _ = CheckToolsAsync();
                }

                break;
        }
    }

    /// <summary>Saves a step's choice when it differs from what's saved; the appearance's preview ends.</summary>
    private async Task SaveAsync(SetupStep step)
    {
        var settings = _shell.Workbench.Settings;
        switch (step)
        {
            case SetupStep.Appearance:
                if (ThemeSetting is { } theme && ThemeService.Normalize(_shell.Theme.Setting) != theme)
                {
                    await settings.SetAsync(ThemeService.SettingKey, theme, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                }

                _shell.Theme.Preview(null);
                break;
            case SetupStep.Keyboard:
                if (settings.GetString(BuiltIns.KeyboardSchemeSetting, "default") != _scheme)
                {
                    await settings.SetAsync(BuiltIns.KeyboardSchemeSetting, _scheme, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                }

                break;
        }
    }
}
