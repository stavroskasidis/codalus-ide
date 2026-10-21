using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.ViewModels;

/// <summary>What opened New Project: a new project, a new (blank) solution, or a project added to the open workspace.</summary>
public enum NewProjectMode
{
    Project,
    Solution,
    Add,
}

/// <summary>What is open when New Project opens.</summary>
public enum NewProjectWorkspace
{
    None,
    Solution,
    ProjectFile,
    Folder,
}

/// <summary>
/// The IDE as New Project sees it when it opens (ADR 0082): how it was opened, and a snapshot of the workspace, with the
/// solution folder that was selected for Add New Project.
/// </summary>
public sealed record NewProjectContext
{
    public NewProjectMode Mode { get; init; }

    /// <summary>Opened from the start window's Create a new project: Back on the first page returns there.</summary>
    public bool FromStartWindow { get; init; }

    /// <summary>A template to start on (its short name or row id), from the command's argument.</summary>
    public string? Template { get; init; }

    /// <summary>The language of <see cref="Template"/> (<c>C#</c>, <c>F#</c>, <c>VB</c>).</summary>
    public string? Language { get; init; }

    public NewProjectWorkspace Workspace { get; init; }

    /// <summary>The open solution, project file or folder.</summary>
    public string? WorkspacePath { get; init; }

    /// <summary>The open workspace's folder (where Add New Project's location starts).</summary>
    public string? WorkspaceDirectory { get; init; }

    /// <summary>The open workspace's name (the solution's or the folder's).</summary>
    public string? WorkspaceName { get; init; }

    public bool WorkspaceTrusted { get; init; }

    /// <summary>The open solution's project files (Enlist in Aspire orchestration looks for its AppHost among them).</summary>
    public IReadOnlyList<string> WorkspaceProjects { get; init; } = [];

    /// <summary>The solution folder selected when Add New Project opened (its id), where the project goes.</summary>
    public string? SolutionFolderId { get; init; }

    /// <summary>The name of <see cref="SolutionFolderId"/>.</summary>
    public string? SolutionFolderName { get; init; }

    /// <summary><c>templates.defaultLocation</c>.</summary>
    public string? DefaultLocationSetting { get; init; }

    public bool IsWindows { get; init; } = OperatingSystem.IsWindows();
}

/// <summary>The services New Project works with (the shell's; tests stand in for the CLI and the catalog).</summary>
public sealed class NewProjectServices
{
    public required ProjectTemplateCatalog Catalog { get; init; }

    public required NewProjectService Creator { get; init; }

    public required NewProjectHistory History { get; init; }

    public required ITemplateCli Cli { get; init; }

    /// <summary>Whether a folder is trusted (it reads the disk: called off the UI thread).</summary>
    public required Func<string, bool> IsTrusted { get; init; }

    /// <summary>Trusts a folder (Create, with Trust this location checked).</summary>
    public required Action<string> Trust { get; init; }

    public PathRules Rules { get; init; } = PathRules.Current;

    /// <summary>Install more templates…: the template manager (<c>tools.templates</c>).</summary>
    public Action? InstallMore { get; init; }

    /// <summary>Show Output: the CLI's log of a failed Create.</summary>
    public Action<string>? ShowOutput { get; init; }

    /// <summary>Back on the first page when opened from the start window.</summary>
    public Action? BackToStartWindow { get; init; }
}

/// <summary>A finished Create: what was asked and what was made. The shell opens it in the main window.</summary>
public sealed record NewProjectCompletion(NewProjectRequest Request, NewProjectPlan Plan, NewProjectResult Result, bool Trusted, string? SolutionFolderId);

/// <summary>
/// New Project (ADR 0082): the classic IDE's three-page wizard. <b>Create a new project</b> lists recent templates and
/// every template with search and the language, platform and project-type filters, and installs a missing template
/// pack in place. <b>Configure your new project</b> asks for the project name, the location, the solution (with a
/// solution or folder open), the solution name, Place solution and project in the same directory, and Trust this
/// location. <b>Additional information</b> shows the framework (with its support label) and the chosen member's options
/// and the IDE's host options. Create writes the files (phase 1, here) and raises <see cref="Finished"/>; the shell
/// closes the wizard and opens the result (phase 2). Every question is asked on the wizard's own pages.
/// </summary>
public sealed class NewProjectViewModel : ObservableObject, ILoadable, IDisposable
{
    private static readonly char[] s_configureKeys = ['J', 'L', 'O', 'M', 'D', 'T'];

    private readonly NewProjectServices _services;
    private readonly NewProjectContext _context;
    private readonly List<ProjectTemplateGroup> _groups = [];
    private readonly Dictionary<string, ProjectTemplateGroup> _completed = new(StringComparer.Ordinal);
    private NewProjectPage _page;
    private bool _isLoading;
    private string? _loadError;
    private string _search = string.Empty;
    private FilterOption? _language;
    private FilterOption? _platform;
    private FilterOption? _projectType;
    private NewProjectTemplateRow? _selectedTemplate;
    private bool _isLoadingTemplate;
    private CancellationTokenSource? _searchDelay;
    private bool _startedOnTemplate;

    private string _projectName = string.Empty;
    private bool _projectNameEdited;
    private string _solutionName = string.Empty;
    private bool _solutionNameEdited;
    private string _location = string.Empty;
    private bool _locationEdited;
    private SolutionModeChoice? _solutionMode;
    private bool _sameDirectory;
    private bool _trustLocation;
    private bool _locationTrusted;
    private string? _nameError;
    private string? _solutionNameError;
    private string? _locationError;
    private string? _targetError;
    private bool _isValidating;
    private CancellationTokenSource? _validation;

    private FrameworkChoice? _framework;
    private ProjectTemplateMember? _member;
    private bool _https = true;
    private AuthChoice? _auth;
    private bool _docker;
    private bool _aspire;
    private string? _globalJsonWarning;
    private CancellationTokenSource? _warning;

    private bool _isCreating;
    private string? _createStatus;
    private string? _createError;
    private string? _createLog;
    private CancellationTokenSource? _create;

    public NewProjectViewModel(NewProjectServices services, NewProjectContext context)
    {
        _services = services;
        _context = context;
        BackCommand = new RelayCommand(GoBack, () => CanGoBack);
        NextCommand = new RelayCommand(() => _ = NextAsync(), () => CanGoNext);
        CreateCommand = new RelayCommand(() => _ = CreateAsync(), () => CanCreate);
        InstallCommand = new RelayCommand(() => _ = InstallAsync(), () => CanInstall);
        CancelCommand = new RelayCommand(Cancel);
        ClearAllCommand = new RelayCommand(ClearAll);
        ClearSearchCommand = new RelayCommand(() => Search = string.Empty);
        InstallMoreCommand = new RelayCommand(() => services.InstallMore?.Invoke());
        ShowOutputCommand = new RelayCommand(() => services.ShowOutput?.Invoke(_createLog ?? string.Empty), () => _createLog is not null);
        services.Catalog.Changed += OnCatalogChanged;

        var history = services.History;
        _sameDirectory = history.SameDirectory;
        _location = context.Mode == NewProjectMode.Add && context.WorkspaceDirectory is { } workspace ? workspace : history.DefaultLocation(context.DefaultLocationSetting);
        foreach (var choice in SolutionChoicesFor(context))
        {
            SolutionChoices.Add(choice);
        }

        _solutionMode = SolutionChoices.Count > 0 ? SolutionChoices[0] : null;
        RecentLocations = history.RecentLocations;
        Languages.Add(new FilterOption(null, Localizer.Get("msg.newProject.allLanguages", "All languages")));
        Platforms.Add(new FilterOption(null, Localizer.Get("msg.newProject.allPlatforms", "All platforms")));
        ProjectTypes.Add(new FilterOption(null, Localizer.Get("msg.newProject.allProjectTypes", "All project types")));
        foreach (var platform in TemplateTags.Platforms)
        {
            Platforms.Add(new FilterOption(platform, TemplateTags.PlatformLabel(platform)));
        }

        foreach (var type in TemplateTags.ProjectTypes)
        {
            ProjectTypes.Add(new FilterOption(type, TemplateTags.ProjectTypeLabel(type)));
        }

        _language = Languages[0];
        _platform = Platforms.FirstOrDefault(p => p.Value is not null && p.Value == history.PlatformFilter) ?? Platforms[0];
        _projectType = ProjectTypes.FirstOrDefault(p => p.Value is not null && p.Value == history.ProjectTypeFilter) ?? ProjectTypes[0];
    }

    /// <summary>Create finished (with what was made) or the wizard was cancelled (null). The shell closes the window.</summary>
    public event EventHandler<NewProjectCompletion?>? Finished;

    /// <summary>A page was entered: the view focuses its first field.</summary>
    public event EventHandler<NewProjectPage>? PageEntered;

    /// <summary>How long the search waits for more typing.</summary>
    internal TimeSpan SearchDelay { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>How long the checks that read the disk wait for more typing (a network path can be slow).</summary>
    internal TimeSpan ValidationDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The latest disk check (tests wait for it).</summary>
    internal Task Validation { get; private set; } = Task.CompletedTask;

    internal NewProjectContext Context => _context;

    internal bool IsWindows => _context.IsWindows;

    public bool IsAddMode => _context.Mode == NewProjectMode.Add;

    public string WindowTitle => IsAddMode ? Localizer.Get("msg.newProject.windowTitleAdd", "Add New Project") : Localizer.Get("msg.newProject.windowTitle", "New Project");

    public NewProjectPage Page
    {
        get => _page;
        private set
        {
            if (SetField(ref _page, value))
            {
                OnPropertyChanged(nameof(PageTitle));
                OnPropertyChanged(nameof(IsChoosePage));
                OnPropertyChanged(nameof(IsConfigurePage));
                OnPropertyChanged(nameof(IsAdditionalPage));
                RefreshCommands();
                PageEntered?.Invoke(this, value);
            }
        }
    }

    public bool IsChoosePage => Page == NewProjectPage.Choose;

    public bool IsConfigurePage => Page == NewProjectPage.Configure;

    public bool IsAdditionalPage => Page == NewProjectPage.Additional;

    public string PageTitle => Page switch
    {
        NewProjectPage.Configure => Localizer.Get("newProject.configureYourNewProject", "Configure your new project"),
        NewProjectPage.Additional => Localizer.Get("newProject.additionalInformation", "Additional information"),
        _ => IsAddMode ? Localizer.Get("msg.newProject.addTitle", "Add a new project") : Localizer.Get("msg.newProject.createTitle", "Create a new project"),
    };

    // ------------------------------------------------------------------ page 1: Create a new project

    public ObservableCollection<NewProjectTemplateRow> Templates { get; } = [];

    public ObservableCollection<NewProjectTemplateRow> RecentTemplates { get; } = [];

    public ObservableCollection<FilterOption> Languages { get; } = [];

    public ObservableCollection<FilterOption> Platforms { get; } = [];

    public ObservableCollection<FilterOption> ProjectTypes { get; } = [];

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetField(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    /// <summary>Why no <c>dotnet new</c> template was found (the CLI's last line), or null.</summary>
    public string? LoadError
    {
        get => _loadError;
        private set => SetField(ref _loadError, value);
    }

    /// <summary>Nothing matches the search and filters.</summary>
    public bool IsEmpty => !IsLoading && Templates.Count == 0;

    public bool HasRecentTemplates => RecentTemplates.Count > 0;

    public string Search
    {
        get => _search;
        set
        {
            if (SetField(ref _search, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasSearch));
                ScheduleFilter();
            }
        }
    }

    public bool HasSearch => _search.Length > 0;

    public FilterOption? SelectedLanguage
    {
        get => _language;
        set
        {
            if (SetField(ref _language, value))
            {
                ApplyFilter();
            }
        }
    }

    public FilterOption? SelectedPlatform
    {
        get => _platform;
        set
        {
            if (SetField(ref _platform, value))
            {
                ApplyFilter();
            }
        }
    }

    public FilterOption? SelectedProjectType
    {
        get => _projectType;
        set
        {
            if (SetField(ref _projectType, value))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>Clear all shows when a search or a filter narrows the list.</summary>
    public bool HasFilter => HasSearch || _language?.Value is not null || _platform?.Value is not null || _projectType?.Value is not null;

    public NewProjectTemplateRow? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (SetField(ref _selectedTemplate, value))
            {
                OnPropertyChanged(nameof(IsPackSelected));
                OnPropertyChanged(nameof(TemplateName));
                OnPropertyChanged(nameof(TemplateChips));
                OnTemplateChosen();
            }
        }
    }

    /// <summary>A template package is selected: the primary button is Install.</summary>
    public bool IsPackSelected => _selectedTemplate?.IsPack == true;

    /// <summary>The selected template's member is being read (from its <c>--help</c>, without the cache).</summary>
    public bool IsLoadingTemplate
    {
        get => _isLoadingTemplate;
        private set
        {
            if (SetField(ref _isLoadingTemplate, value))
            {
                RefreshCommands();
            }
        }
    }

    public string NotFindingText => Localizer.Get("newProject.notFindingWhatYouReLooking", "Not finding what you're looking for?");

    public string EmptyText => Localizer.Get("newProject.noTemplatesMatchYourSearch", "No templates match your search.");

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var groups = await _services.Catalog.LoadAsync(CancellationToken.None).ConfigureAwait(true);
            _groups.Clear();
            _groups.AddRange(groups);
            LoadError = groups.Any(g => g.Kind == ProjectTemplateKind.DotnetNew) ? null : _services.Catalog.LoadError;
            RebuildLanguages();
            RefreshRecent();
            ApplyFilter();
        }
        finally
        {
            IsLoading = false;
        }

        if (!_startedOnTemplate)
        {
            _startedOnTemplate = true;
            await StartOnTemplateAsync().ConfigureAwait(true);
        }
    }

    /// <summary>New Solution starts on Blank Solution's Configure page; a template argument starts on that template.</summary>
    private async Task StartOnTemplateAsync()
    {
        var wanted = _context.Mode == NewProjectMode.Solution ? ProjectTemplateCatalog.BlankSolutionId : _context.Template;
        if (wanted is null)
        {
            return;
        }

        var group = _groups.FirstOrDefault(g => g.Id == wanted)
            ?? _groups.Where(g => string.Equals(g.ShortName, wanted, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(g => string.Equals(g.Language, _context.Language ?? "C#", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
        if (group is null)
        {
            return;
        }

        SelectedTemplate = Templates.FirstOrDefault(r => r.Id == group.Id) ?? new NewProjectTemplateRow(this, group, isRecent: false, isPinned: false);
        await WaitForTemplateAsync().ConfigureAwait(true);
        if (CanGoNext)
        {
            await NextAsync().ConfigureAwait(true);
        }
    }

    private void OnCatalogChanged(object? sender, EventArgs e) => Ui.Post(() => _ = ReloadAsync());

    /// <summary>The templates changed (a pack installed): read them again, keeping the selection by id.</summary>
    private async Task ReloadAsync()
    {
        var keep = _selectedTemplate?.Id;
        await LoadAsync().ConfigureAwait(true);
        if (keep is not null && Templates.FirstOrDefault(r => r.Id == keep) is { } row)
        {
            SelectedTemplate = row;
        }
    }

    private void RebuildLanguages()
    {
        var keep = _language?.Value ?? _services.History.LanguageFilter;
        Languages.Clear();
        Languages.Add(new FilterOption(null, Localizer.Get("msg.newProject.allLanguages", "All languages")));
        foreach (var language in _groups.Select(g => g.Language).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal)
                     .OrderBy(l => l switch { "C#" => 0, "F#" => 1, "VB" => 2, _ => 3 }).ThenBy(l => l, StringComparer.Ordinal))
        {
            Languages.Add(new FilterOption(language, TemplateTags.LanguageLabel(language)));
        }

        _language = Languages.FirstOrDefault(l => l.Value == keep) ?? Languages[0];
        OnPropertyChanged(nameof(SelectedLanguage));
    }

    private void ScheduleFilter()
    {
        _searchDelay?.Cancel();
        if (SearchDelay <= TimeSpan.Zero)
        {
            ApplyFilter();
            return;
        }

        var cts = _searchDelay = new CancellationTokenSource();
        _ = Task.Delay(SearchDelay, cts.Token).ContinueWith(
            t =>
            {
                if (!t.IsCanceled)
                {
                    Ui.Post(ApplyFilter);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    /// <summary>
    /// The rows the search and filters leave, best match first: a match in the name, then in the short name, then in
    /// the description or tags; without a search, the catalog's order.
    /// </summary>
    internal void ApplyFilter()
    {
        var search = _search.Trim();
        var keep = _selectedTemplate?.Id;
        var matches = new List<(ProjectTemplateGroup Group, int Rank)>();
        foreach (var group in _groups)
        {
            if (!PassesFilters(group))
            {
                continue;
            }

            var rank = search.Length == 0 ? 0
                : group.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ? 0
                : group.ShortName.Contains(search, StringComparison.OrdinalIgnoreCase) ? 1
                : group.Description.Contains(search, StringComparison.CurrentCultureIgnoreCase) || group.Chips.Any(c => c.Contains(search, StringComparison.CurrentCultureIgnoreCase)) ? 2
                : -1;
            if (rank >= 0)
            {
                matches.Add((group, rank));
            }
        }

        Templates.Clear();
        foreach (var (group, _) in matches.OrderBy(m => m.Rank))
        {
            var row = new NewProjectTemplateRow(this, group, isRecent: false, isPinned: false) { NameRuns = Highlight(group.Name, search) };
            Templates.Add(row);
        }

        foreach (var recent in RecentTemplates)
        {
            recent.NameRuns = Highlight(recent.Name, search);
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasFilter));
        if (keep is not null)
        {
            var again = Templates.FirstOrDefault(r => r.Id == keep);
            if (again is not null)
            {
                _selectedTemplate = again;
                OnPropertyChanged(nameof(SelectedTemplate));
            }
        }

        _services.History.SetFilters(_language?.Value, _platform?.Value, _projectType?.Value);
    }

    private bool PassesFilters(ProjectTemplateGroup group) =>
        (_language?.Value is not { } language || group.Language.Length == 0 || group.Language == language)
        && (_platform?.Value is not { } platform || group.Platforms.Contains(platform, StringComparer.Ordinal))
        && (_projectType?.Value is not { } type || group.ProjectTypes.Contains(type, StringComparer.Ordinal));

    /// <summary>A name in pieces, each occurrence of <paramref name="search"/> marked.</summary>
    internal static IReadOnlyList<HighlightRun> Highlight(string name, string search)
    {
        if (search.Length == 0)
        {
            return [new HighlightRun(name, false)];
        }

        var runs = new List<HighlightRun>();
        var start = 0;
        while (start < name.Length)
        {
            var at = name.IndexOf(search, start, StringComparison.CurrentCultureIgnoreCase);
            if (at < 0)
            {
                break;
            }

            if (at > start)
            {
                runs.Add(new HighlightRun(name[start..at], false));
            }

            runs.Add(new HighlightRun(name.Substring(at, search.Length), true));
            start = at + search.Length;
        }

        if (start < name.Length)
        {
            runs.Add(new HighlightRun(name[start..], false));
        }

        return runs;
    }

    private void RefreshRecent()
    {
        RecentTemplates.Clear();
        foreach (var recent in _services.History.RecentTemplates)
        {
            if (_groups.FirstOrDefault(g => g.Id == recent.Id) is { Kind: not ProjectTemplateKind.Pack } group)
            {
                RecentTemplates.Add(new NewProjectTemplateRow(this, group, isRecent: true, isPinned: recent.Pinned) { NameRuns = Highlight(group.Name, _search.Trim()) });
            }
        }

        OnPropertyChanged(nameof(HasRecentTemplates));
    }

    internal void SetPinned(NewProjectTemplateRow row, bool pinned)
    {
        _services.History.SetPinned(row.Id, pinned);
        RefreshRecent();
    }

    internal void RemoveRecent(NewProjectTemplateRow row)
    {
        _services.History.RemoveRecentTemplate(row.Id);
        RefreshRecent();
    }

    private void ClearAll()
    {
        _search = string.Empty;
        _language = Languages[0];
        _platform = Platforms[0];
        _projectType = ProjectTypes[0];
        OnPropertyChanged(nameof(Search));
        OnPropertyChanged(nameof(HasSearch));
        OnPropertyChanged(nameof(SelectedLanguage));
        OnPropertyChanged(nameof(SelectedPlatform));
        OnPropertyChanged(nameof(SelectedProjectType));
        ApplyFilter();
    }

    /// <summary>The selected row's group, with its member read from its help when the cache had none.</summary>
    private ProjectTemplateGroup? Group => _selectedTemplate is { } row ? _completed.GetValueOrDefault(row.Id) ?? row.Group : null;

    /// <summary>A template was chosen: its framework and member, read from its help first when the cache had none.</summary>
    private void OnTemplateChosen()
    {
        _framework = null;
        foreach (var option in Options)
        {
            option.ValueChanged -= OnOptionChanged;
        }

        Options.Clear();
        _member = null;
        if (Group is { Kind: not ProjectTemplateKind.Pack } group)
        {
            _member = group.MemberFor(group.DefaultFramework(_services.History.Framework(group.Id)));
            if (_member is { NeedsHelp: true } partial)
            {
                _ = CompleteMemberAsync(group, partial);
            }
        }

        RefreshCommands();
    }

    private Task? _completing;

    private Task CompleteMemberAsync(ProjectTemplateGroup group, ProjectTemplateMember member)
    {
        IsLoadingTemplate = true;
        return _completing = Run();

        async Task Run()
        {
            try
            {
                var completed = await _services.Catalog.CompleteAsync(group, member, CancellationToken.None).ConfigureAwait(true);
                _completed[group.Id] = group with { Members = [completed] };
                if (_selectedTemplate?.Id == group.Id)
                {
                    _member = completed;
                }
            }
            finally
            {
                IsLoadingTemplate = false;
            }
        }
    }

    /// <summary>Waits for the selected template's member to be read (tests, and a template given by argument).</summary>
    internal Task WaitForTemplateAsync() => _completing ?? Task.CompletedTask;

    // ------------------------------------------------------------------ installing a template pack

    private bool CanInstall => IsChoosePage && _selectedTemplate is { IsPack: true, IsInstalling: false };

    private async Task InstallAsync()
    {
        if (_selectedTemplate is not { IsPack: true, Group.Package: { } package } row)
        {
            return;
        }

        row.InstallError = null;
        row.IsInstalling = true;
        RefreshCommands();
        try
        {
            var (succeeded, output) = await _services.Cli.InstallAsync(package, CancellationToken.None).ConfigureAwait(true);
            if (!succeeded)
            {
                row.InstallError = Localizer.Format("msg.newProject.installFailed", "Couldn't install {0}: {1}", package, new CliResult(1, output).LastMeaningfulLine);
                return;
            }

            // The catalog reloads on the CLI's change; select the pack's first template.
            await LoadAsync().ConfigureAwait(true);
            var prefix = TemplatePacks.Known.FirstOrDefault(p => p.Package == package)?.Prefix;
            SelectedTemplate = Templates.FirstOrDefault(r => prefix is not null && r.Group.Kind == ProjectTemplateKind.DotnetNew && r.Group.ShortName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            row.IsInstalling = false;
            RefreshCommands();
        }
    }

    // ------------------------------------------------------------------ page 2: Configure your new project

    /// <summary>The chosen template's name, on the next pages' header.</summary>
    public string? TemplateName => _selectedTemplate?.Name;

    public IReadOnlyList<string> TemplateChips => _selectedTemplate?.Chips ?? [];

    public bool IsBlankSolution => _selectedTemplate?.Group.Kind == ProjectTemplateKind.BlankSolution;

    public string ProjectName
    {
        get => _projectName;
        set
        {
            value ??= string.Empty;
            if (SetField(ref _projectName, value))
            {
                _projectNameEdited = true;
                if (!_solutionNameEdited)
                {
                    _solutionName = value;
                    OnPropertyChanged(nameof(SolutionName));
                }

                OnConfigureChanged();
            }
        }
    }

    public string Location
    {
        get => _location;
        set
        {
            if (SetField(ref _location, value ?? string.Empty))
            {
                _locationEdited = true;
                OnConfigureChanged();
            }
        }
    }

    /// <summary>The location box's drop-down: recent locations, newest first.</summary>
    public IReadOnlyList<string> RecentLocations { get; }

    /// <summary>Create new solution, or add to the open solution or folder (shown with one open, in new mode).</summary>
    public ObservableCollection<SolutionModeChoice> SolutionChoices { get; } = [];

    public bool ShowSolutionChoice => !IsAddMode && !IsBlankSolution && SolutionChoices.Count > 1;

    public SolutionModeChoice? SelectedSolutionChoice
    {
        get => _solutionMode;
        set
        {
            if (SetField(ref _solutionMode, value))
            {
                if (!_locationEdited)
                {
                    _location = CreatesSolution ? _services.History.DefaultLocation(_context.DefaultLocationSetting) : _context.WorkspaceDirectory ?? _location;
                    OnPropertyChanged(nameof(Location));
                }

                OnPropertyChanged(nameof(CreatesSolution));
                OnPropertyChanged(nameof(IsSolutionNameEnabled));
                OnPropertyChanged(nameof(ShowSolutionName));
                OnPropertyChanged(nameof(ShowSameDirectory));
                OnPropertyChanged(nameof(ShowTrust));
                OnConfigureChanged();
            }
        }
    }

    public SolutionMode SolutionMode => IsBlankSolution ? SolutionMode.CreateNew : _solutionMode?.Mode ?? (IsAddMode ? AddModeFor(_context) : SolutionMode.CreateNew);

    /// <summary>Create makes a new solution (a new project in new mode, or Blank Solution).</summary>
    public bool CreatesSolution => IsBlankSolution || SolutionMode == SolutionMode.CreateNew;

    public bool ShowProjectName => !IsBlankSolution;

    public bool ShowSolutionName => CreatesSolution;

    public string SolutionName
    {
        get => _solutionName;
        set
        {
            if (SetField(ref _solutionName, value ?? string.Empty))
            {
                _solutionNameEdited = _solutionName != _projectName;
                OnConfigureChanged();
            }
        }
    }

    /// <summary>The solution name follows the project's with the same directory, and there's none to name when adding.</summary>
    public bool IsSolutionNameEnabled => IsBlankSolution || (CreatesSolution && !_sameDirectory);

    public bool ShowSameDirectory => CreatesSolution && !IsBlankSolution;

    public bool SameDirectory
    {
        get => _sameDirectory;
        set
        {
            if (SetField(ref _sameDirectory, value))
            {
                OnPropertyChanged(nameof(IsSolutionNameEnabled));
                OnConfigureChanged();
            }
        }
    }

    /// <summary>Trust this location: shown when Create makes a new solution (adding uses the open workspace's trust).</summary>
    public bool ShowTrust => CreatesSolution;

    public bool TrustLocation
    {
        get => _trustLocation || _locationTrusted;
        set
        {
            if (SetField(ref _trustLocation, value))
            {
                OnPropertyChanged(nameof(IsTrusted));
                RefreshHostOptions();
            }
        }
    }

    /// <summary>The location (or a parent) is trusted already: the box is checked and disabled.</summary>
    public bool LocationTrusted
    {
        get => _locationTrusted;
        private set
        {
            if (SetField(ref _locationTrusted, value))
            {
                OnPropertyChanged(nameof(TrustLocation));
                OnPropertyChanged(nameof(IsTrustEnabled));
                OnPropertyChanged(nameof(TrustNote));
                OnPropertyChanged(nameof(IsTrusted));
                RefreshHostOptions();
            }
        }
    }

    public bool IsTrustEnabled => !_locationTrusted;

    public string TrustNote => _locationTrusted
        ? Localizer.Get("msg.newProject.locationTrusted", "This location is trusted.")
        : Localizer.Get("msg.newProject.trustNote", "Otherwise the solution opens showing only its structure until you trust it.");

    /// <summary>Whether what's created will be trusted: the location (checked or already) or the open workspace.</summary>
    public bool IsTrusted => CreatesSolution ? TrustLocation : _context.WorkspaceTrusted;

    public string? NameError
    {
        get => _nameError;
        private set => SetField(ref _nameError, value);
    }

    public string? SolutionNameError
    {
        get => _solutionNameError;
        private set => SetField(ref _solutionNameError, value);
    }

    public string? LocationError
    {
        get => _locationError;
        private set => SetField(ref _locationError, value);
    }

    /// <summary>An existing folder, or a path Windows can't take.</summary>
    public string? TargetError
    {
        get => _targetError;
        private set => SetField(ref _targetError, value);
    }

    public bool IsValidating
    {
        get => _isValidating;
        private set
        {
            if (SetField(ref _isValidating, value))
            {
                RefreshCommands();
            }
        }
    }

    /// <summary>"Project will be created in "…/"" (or the solution, for Blank Solution).</summary>
    public string CreatedIn
    {
        get
        {
            var plan = Plan();
            return plan.Location is null ? string.Empty
                : IsBlankSolution ? Localizer.Format("msg.newProject.solutionCreatedIn", "Solution will be created in \"{0}\"", plan.CreatedIn)
                : Localizer.Format("msg.newProject.projectCreatedIn", "Project will be created in \"{0}\"", plan.CreatedIn);
        }
    }

    /// <summary>Where an added project goes in the open workspace, or null when Create makes a new solution.</summary>
    public string? AddsTo => CreatesSolution ? null
        : SolutionMode == SolutionMode.AddToFolder ? Localizer.Get("msg.newProject.addsToFolder", "Adds the project to the folder's projects.")
        : _context.SolutionFolderName is { Length: > 0 } folder ? Localizer.Format("msg.newProject.addsToSolutionFolder", "Adds the project to the solution \"{0}\", in its folder \"{1}\".", _context.WorkspaceName, folder)
        : Localizer.Format("msg.newProject.addsToSolution", "Adds the project to the solution \"{0}\".", _context.WorkspaceName);

    private bool IsConfigureValid =>
        (IsBlankSolution || _nameError is null) && (!CreatesSolution || !IsSolutionNameEnabled || _solutionNameError is null)
        && _locationError is null && _targetError is null && !_isValidating;

    private static IEnumerable<SolutionModeChoice> SolutionChoicesFor(NewProjectContext context)
    {
        var create = new SolutionModeChoice(SolutionMode.CreateNew, Localizer.Get("msg.newProject.createNewSolution", "Create new solution"));
        var toSolution = new SolutionModeChoice(SolutionMode.AddToSolution, Localizer.Get("msg.newProject.addToSolution", "Add to solution"));
        var toFolder = new SolutionModeChoice(SolutionMode.AddToFolder, Localizer.Get("msg.newProject.addToFolder", "Add to folder"));
        return (context.Mode, context.Workspace) switch
        {
            (NewProjectMode.Add, NewProjectWorkspace.Folder) => [toFolder],
            (NewProjectMode.Add, _) => [toSolution],
            (_, NewProjectWorkspace.Solution) => [create, toSolution],
            (_, NewProjectWorkspace.Folder) => [create, toFolder],
            _ => [create],
        };
    }

    private static SolutionMode AddModeFor(NewProjectContext context) => context.Workspace == NewProjectWorkspace.Folder ? SolutionMode.AddToFolder : SolutionMode.AddToSolution;

    /// <summary>Entering Configure: the template's suggested name (made unique in the location), unless the user typed one.</summary>
    private void EnterConfigure()
    {
        if (!_projectNameEdited || string.IsNullOrEmpty(_projectName))
        {
            var suggestion = _member?.DefaultName ?? (IsBlankSolution ? "Solution1" : "Project1");
            _projectName = suggestion;
            if (!_solutionNameEdited)
            {
                _solutionName = suggestion;
            }

            OnPropertyChanged(nameof(ProjectName));
            OnPropertyChanged(nameof(SolutionName));
            _ = SuggestUniqueNameAsync(suggestion);
        }

        foreach (var name in new[] { nameof(IsBlankSolution), nameof(ShowProjectName), nameof(ShowSolutionChoice), nameof(ShowSolutionName), nameof(IsSolutionNameEnabled), nameof(ShowSameDirectory), nameof(ShowTrust), nameof(CreatesSolution) })
        {
            OnPropertyChanged(name);
        }

        Page = NewProjectPage.Configure;
        OnConfigureChanged();
    }

    /// <summary><c>ConsoleApp1</c> → <c>ConsoleApp2</c> while a folder of that name exists in the location (off the UI thread).</summary>
    private async Task SuggestUniqueNameAsync(string suggestion)
    {
        var location = NewProjectPlan.FullLocation(_location);
        if (location is null)
        {
            return;
        }

        var blank = IsBlankSolution;
        var unique = await Task.Run(() => Unique(location, suggestion)).ConfigureAwait(true);
        if (unique != suggestion && _projectName == suggestion)
        {
            _projectName = unique;
            if (!_solutionNameEdited || blank)
            {
                _solutionName = unique;
            }

            OnPropertyChanged(nameof(ProjectName));
            OnPropertyChanged(nameof(SolutionName));
            OnConfigureChanged();
        }
    }

    internal static string Unique(string location, string suggestion)
    {
        var stem = suggestion.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        var name = suggestion;
        for (var i = 2; i < 1000 && Directory.Exists(Path.Combine(location, name)); i++)
        {
            name = stem + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return name;
    }

    /// <summary>The names and the location changed: the quick checks now, the disk's after a pause, off the UI thread.</summary>
    private void OnConfigureChanged()
    {
        NameError = IsBlankSolution ? null : NewProjectPlan.ValidateName(_projectName);
        SolutionNameError = CreatesSolution && IsSolutionNameEnabled ? NewProjectPlan.ValidateName(_solutionName) : null;
        var plan = Plan();
        LocationError = plan.ValidateLocation();
        OnPropertyChanged(nameof(CreatedIn));
        OnPropertyChanged(nameof(AddsTo));
        _validation?.Cancel();
        if (Page == NewProjectPage.Choose)
        {
            return;
        }

        var cts = _validation = new CancellationTokenSource();
        IsValidating = true;
        Validation = ValidateOnDiskAsync(plan, cts);
        RefreshCommands();
    }

    private async Task ValidateOnDiskAsync(NewProjectPlan plan, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            if (ValidationDelay > TimeSpan.Zero)
            {
                await Task.Delay(ValidationDelay, token).ConfigureAwait(true);
            }

            var rules = _services.Rules;
            var namesOk = _nameError is null || IsBlankSolution;
            var (target, trusted) = await Task.Run(
                () => (plan.Location is null || !namesOk ? null : plan.ValidateTarget(rules), plan.Location is not null && _services.IsTrusted(plan.Location)),
                token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            TargetError = target;
            LocationTrusted = trusted;
            IsValidating = false;
        }
        catch (OperationCanceledException)
        {
            // A newer check runs.
        }
    }

    // ------------------------------------------------------------------ page 3: Additional information

    public IReadOnlyList<FrameworkChoice> Frameworks => Group?.Frameworks ?? [];

    public bool ShowFramework => Frameworks.Count > 0;

    public FrameworkChoice? SelectedFramework
    {
        get => _framework;
        set
        {
            if (SetField(ref _framework, value))
            {
                RebuildOptions();
                ScheduleGlobalJsonCheck();
            }
        }
    }

    public ObservableCollection<TemplateOptionViewModel> Options { get; } = [];

    /// <summary>The member the framework picked: its options are the page's.</summary>
    internal ProjectTemplateMember? Member => _member;

    /// <summary>"Class Library has no other options.": a template with a framework and nothing else.</summary>
    public string? NoOptionsNote => ShowFramework && Options.Count == 0 && !ShowHttps && !ShowAuth && !ShowDocker && !ShowAspire
        ? Localizer.Format("msg.newProject.noOtherOptions", "{0} has no other options.", TemplateName)
        : null;

    public bool ShowHttps => _member?.HostOptions.DisableHttpsOption is not null;

    public string HttpsLabel { get; private set; } = Localizer.Get("templates.option.https", "Configure for HTTPS");

    public bool Https
    {
        get => _https || ForcesHttps;
        set
        {
            if (SetField(ref _https, value))
            {
                OnPropertyChanged(nameof(Https));
            }
        }
    }

    /// <summary>Individual Accounts and Microsoft identity platform need HTTPS: the box is checked and locked.</summary>
    public bool ForcesHttps => TemplateHostOptions.ForcesHttps(_auth?.Value);

    public bool IsHttpsEnabled => !ForcesHttps;

    public string? HttpsToolTip => ForcesHttps && _auth is { } auth ? Localizer.Format("templates.option.httpsRequired", "{0} needs HTTPS.", auth.Label) : null;

    public bool ShowAuth => AuthChoices.Count > 0;

    public string AuthLabel { get; private set; } = Localizer.Get("templates.option.authentication", "Authentication type");

    public ObservableCollection<AuthChoice> AuthChoices { get; } = [];

    public AuthChoice? SelectedAuth
    {
        get => _auth;
        set
        {
            if (SetField(ref _auth, value))
            {
                OnPropertyChanged(nameof(ForcesHttps));
                OnPropertyChanged(nameof(IsHttpsEnabled));
                OnPropertyChanged(nameof(Https));
                OnPropertyChanged(nameof(HttpsToolTip));
                OnPropertyChanged(nameof(AuthNote));
                EvaluateConditions();
            }
        }
    }

    public string? AuthNote => TemplateHostOptions.AuthNote(_auth?.Value, IsWindows);

    public bool ShowDocker => _member?.HostOptions.Docker == true;

    public string DockerLabel { get; private set; } = Localizer.Get("templates.option.docker", "Enable container support");

    public bool Docker
    {
        get => _docker && IsTrusted;
        set => SetField(ref _docker, value);
    }

    public bool ShowAspire => _member?.HostOptions.Aspire == true;

    public string AspireLabel { get; private set; } = Localizer.Get("templates.option.aspire", "Enlist in Aspire orchestration");

    public bool Aspire
    {
        get => _aspire && IsTrusted;
        set => SetField(ref _aspire, value);
    }

    public string AspireDescription => !CreatesSolution && _existingAspire?.AppHost is { } appHost
        ? Localizer.Format("templates.option.aspireExisting", "Adds this project to {0}, the solution's AppHost.", Path.GetFileNameWithoutExtension(appHost))
        : Localizer.Format("templates.option.aspireDescription", "Adds {0}.AppHost and {0}.ServiceDefaults projects. Installs the Aspire templates from nuget.org first if they're missing.", AspireName);

    /// <summary>The name new Aspire projects start with: the new solution's, else the open one's.</summary>
    private string AspireName => CreatesSolution ? SolutionNameForAspire : _context.WorkspaceName ?? _projectName;

    private AspireProjects? _existingAspire;

    /// <summary>The open solution's AppHost and ServiceDefaults, read off the UI thread when Additional information opens.</summary>
    private async Task DetectAspireAsync()
    {
        if (CreatesSolution || !ShowAspire || _context.WorkspaceProjects.Count == 0)
        {
            return;
        }

        var projects = _context.WorkspaceProjects;
        _existingAspire = await Task.Run(() => AspireEnlistment.Detect(projects)).ConfigureAwait(true);
        OnPropertyChanged(nameof(AspireDescription));
    }

    private string SolutionNameForAspire => _sameDirectory ? _projectName : _solutionName;

    /// <summary>Container support and Aspire evaluate projects and restore: they need a trusted location.</summary>
    public bool AreTrustedOptionsEnabled => IsTrusted;

    public string? TrustedOptionsNote => IsTrusted ? null
        : CreatesSolution ? Localizer.Get("templates.option.needsTrust", "Needs a trusted location (previous page).")
        : Localizer.Get("templates.option.needsTrustedSolution", "Needs a trusted solution.");

    /// <summary>The location's global.json pins an SDK too old for the framework.</summary>
    public string? GlobalJsonWarning
    {
        get => _globalJsonWarning;
        private set => SetField(ref _globalJsonWarning, value);
    }

    /// <summary>Whether Create comes after Configure (a template with no framework and no options has no page 3).</summary>
    public bool HasAdditionalPage => !IsBlankSolution && _member is { } member
        && (member.Frameworks.Count > 0 || member.Parameters.Count > 0 || !member.HostOptions.IsEmpty || member.NeedsHelp);

    private void EnterAdditional()
    {
        var group = Group!;
        var moniker = group.DefaultFramework(_services.History.Framework(group.Id));
        _framework = group.Frameworks.FirstOrDefault(f => f.Moniker == moniker);
        OnPropertyChanged(nameof(Frameworks));
        OnPropertyChanged(nameof(ShowFramework));
        OnPropertyChanged(nameof(SelectedFramework));
        RebuildOptions();
        Page = NewProjectPage.Additional;
        ScheduleGlobalJsonCheck();
        _ = DetectAspireAsync();
    }

    /// <summary>
    /// The options of the member the framework picks, values carried over by name where the new member has them,
    /// else remembered (by their persistence keys), else the template's.
    /// </summary>
    private void RebuildOptions()
    {
        if (Group is not { } group)
        {
            return;
        }

        var previous = Options.ToDictionary(o => o.Name, o => o.SymbolValue, StringComparer.Ordinal);
        foreach (var option in Options)
        {
            option.ValueChanged -= OnOptionChanged;
        }

        Options.Clear();
        var member = group.MemberFor(_framework?.Moniker);
        _member = member;
        if (member is null)
        {
            return;
        }

        var history = _services.History;
        var keys = new AccessKeyAllocator(['B', 'C', 'F']);
        foreach (var parameter in member.Parameters)
        {
            var value = previous.TryGetValue(parameter.Name, out var carried) ? carried
                : parameter.PersistenceKey is { } key && history.Value(key, parameter.Name) is { } remembered ? remembered
                : SymbolDefault(parameter);
            TemplateOptionViewModel option = parameter.DataType switch
            {
                TemplateDataType.Bool => new BoolOptionViewModel(parameter, value),
                TemplateDataType.Choice => new ChoiceOptionViewModel(parameter, value),
                _ => new TextOptionViewModel(parameter, value),
            };
            option.Label = keys.Allocate(parameter.Label, assign: false);
            option.ValueChanged += OnOptionChanged;
            Options.Add(option);
        }

        // The IDE's own options take the first free letters of their labels.
        HttpsLabel = keys.Allocate(Localizer.Get("templates.option.https", "Configure for HTTPS"), assign: true);
        AuthLabel = keys.Allocate(Localizer.Get("templates.option.authentication", "Authentication type"), assign: true);
        DockerLabel = keys.Allocate(Localizer.Get("templates.option.docker", "Enable container support"), assign: true);
        AspireLabel = keys.Allocate(Localizer.Get("templates.option.aspire", "Enlist in Aspire orchestration"), assign: true);

        var authKeep = _auth?.Value ?? history.Value("group:" + group.Id, "$auth");
        AuthChoices.Clear();
        foreach (var auth in member.HostOptions.AuthChoices ?? [])
        {
            AuthChoices.Add(new AuthChoice(auth, TemplateHostOptions.AuthLabel(auth)));
        }

        _auth = AuthChoices.FirstOrDefault(a => a.Value == authKeep) ?? AuthChoices.FirstOrDefault(a => a.Value == "None") ?? (AuthChoices.Count > 0 ? AuthChoices[0] : null);
        foreach (var name in new[]
                 {
                     nameof(SelectedAuth), nameof(ShowAuth), nameof(ShowHttps), nameof(Https), nameof(ForcesHttps), nameof(IsHttpsEnabled), nameof(HttpsToolTip), nameof(AuthNote),
                     nameof(ShowDocker), nameof(ShowAspire), nameof(HttpsLabel), nameof(AuthLabel), nameof(DockerLabel), nameof(AspireLabel), nameof(AspireDescription),
                 })
        {
            OnPropertyChanged(name);
        }

        EvaluateConditions();
        RefreshHostOptions();
        OnPropertyChanged(nameof(NoOptionsNote));
    }

    /// <summary>The template's value a parameter starts with: the IDE host file's default, turned back for an inverted box.</summary>
    private static string? SymbolDefault(TemplateParameter parameter)
    {
        var shown = parameter.DisplayDefault ?? parameter.DefaultValue;
        return parameter.Invert && bool.TryParse(shown, out var flag) ? (!flag ? "true" : "false") : shown;
    }

    private void OnOptionChanged(object? sender, EventArgs e) => EvaluateConditions();

    /// <summary>Each option's condition (<c>(InteractivityPlatform != "None")</c>) over the page's values, as an IDE host.</summary>
    private void EvaluateConditions()
    {
        if (_member is null)
        {
            return;
        }

        var values = SymbolValues();
        foreach (var option in Options)
        {
            option.IsEnabled = !_isCreating && TemplateCondition.Evaluate(option.Parameter.EnabledCondition, values, _member.ComputedSymbols);
        }
    }

    private Dictionary<string, string?> SymbolValues()
    {
        var values = Options.ToDictionary(o => o.Name, o => o.SymbolValue, StringComparer.Ordinal);
        if (_framework is { } framework)
        {
            values["Framework"] = framework.Moniker;
        }

        if (_auth is { } auth)
        {
            values["auth"] = auth.Value;
        }

        return values;
    }

    private void RefreshHostOptions()
    {
        OnPropertyChanged(nameof(AreTrustedOptionsEnabled));
        OnPropertyChanged(nameof(TrustedOptionsNote));
        OnPropertyChanged(nameof(Docker));
        OnPropertyChanged(nameof(Aspire));
        OnPropertyChanged(nameof(NoOptionsNote));
    }

    private void ScheduleGlobalJsonCheck()
    {
        _warning?.Cancel();
        var cts = _warning = new CancellationTokenSource();
        var token = cts.Token;
        var location = Plan().SolutionDirectory ?? _location;
        var framework = _framework?.Moniker;
        _ = Task.Run(() => NewProjectPlan.GlobalJsonWarning(location, framework), token).ContinueWith(
            t =>
            {
                if (t.IsCompletedSuccessfully && !token.IsCancellationRequested)
                {
                    Ui.Post(() => GlobalJsonWarning = t.Result);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    // ------------------------------------------------------------------ navigation

    public ICommand BackCommand { get; }

    public ICommand NextCommand { get; }

    public ICommand CreateCommand { get; }

    public ICommand InstallCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand ClearAllCommand { get; }

    public ICommand ClearSearchCommand { get; }

    public ICommand InstallMoreCommand { get; }

    public ICommand ShowOutputCommand { get; }

    /// <summary>The primary button: Install on a pack, Create on the last page, else Next.</summary>
    public bool IsInstallVisible => IsChoosePage && IsPackSelected;

    public bool IsCreateVisible => !IsInstallVisible && (IsAdditionalPage || (IsConfigurePage && !HasAdditionalPage));

    public bool IsNextVisible => !IsInstallVisible && !IsCreateVisible;

    /// <summary>Back on the first page returns to the start window when the wizard came from there.</summary>
    public bool CanGoBack => !_isCreating && (Page != NewProjectPage.Choose || (_context.FromStartWindow && !IsAddMode));

    public string BackToolTip => Page == NewProjectPage.Choose && _context.FromStartWindow
        ? Localizer.Get("msg.newProject.backToStartWindow", "Back to the start window (Alt+Left)")
        : Localizer.Get("newProject.backTip", "Back (Alt+Left)");

    public bool CanGoNext => !_isCreating && Page switch
    {
        NewProjectPage.Choose => _selectedTemplate is { IsPack: false } && !_isLoadingTemplate,
        NewProjectPage.Configure => HasAdditionalPage && IsConfigureValid,
        _ => false,
    };

    public bool CanCreate => !_isCreating && Page switch
    {
        NewProjectPage.Configure => !HasAdditionalPage && IsConfigureValid,
        NewProjectPage.Additional => IsConfigureValid,
        _ => false,
    };

    /// <summary>Alt+Left.</summary>
    public void GoBack()
    {
        if (!CanGoBack)
        {
            return;
        }

        if (Page == NewProjectPage.Choose)
        {
            Finish(null);
            _services.BackToStartWindow?.Invoke();
            return;
        }

        Page = Page == NewProjectPage.Additional ? NewProjectPage.Configure : NewProjectPage.Choose;
    }

    /// <summary>Alt+Right and Enter: Next, or Create on the last page.</summary>
    public Task GoForwardAsync() =>
        CanGoNext ? NextAsync()
        : CanCreate ? CreateAsync()
        : CanInstall ? InstallAsync()
        : Task.CompletedTask;

    private async Task NextAsync()
    {
        if (!CanGoNext)
        {
            return;
        }

        if (Page == NewProjectPage.Choose)
        {
            await WaitForTemplateAsync().ConfigureAwait(true);
            EnterConfigure();
        }
        else if (Page == NewProjectPage.Configure)
        {
            EnterAdditional();
        }
    }

    private void RefreshCommands()
    {
        foreach (var command in new[] { BackCommand, NextCommand, CreateCommand, InstallCommand, ShowOutputCommand })
        {
            ((RelayCommand)command).RaiseCanExecuteChanged();
        }

        foreach (var name in new[] { nameof(CanGoBack), nameof(CanGoNext), nameof(CanCreate), nameof(IsInstallVisible), nameof(IsCreateVisible), nameof(IsNextVisible), nameof(BackToolTip), nameof(HasAdditionalPage) })
        {
            OnPropertyChanged(name);
        }
    }

    // ------------------------------------------------------------------ Create

    public bool IsCreating
    {
        get => _isCreating;
        private set
        {
            if (SetField(ref _isCreating, value))
            {
                RefreshCommands();
                EvaluateConditions();
            }
        }
    }

    /// <summary>"Creating Hello…" while Create runs.</summary>
    public string? CreateStatus
    {
        get => _createStatus;
        private set => SetField(ref _createStatus, value);
    }

    /// <summary>Why Create failed (the CLI's last line); Show Output opens its log.</summary>
    public string? CreateError
    {
        get => _createError;
        private set => SetField(ref _createError, value);
    }

    /// <summary>The request the pages describe now.</summary>
    internal NewProjectRequest Request()
    {
        var group = Group ?? new ProjectTemplateGroup { Id = "none", Kind = ProjectTemplateKind.BlankSolution, Name = string.Empty };
        var framework = _framework?.Moniker ?? (Frameworks.Count > 0 ? group.DefaultFramework(_services.History.Framework(group.Id)) : null);
        var member = group.MemberFor(framework);
        return new NewProjectRequest
        {
            Group = group,
            Member = member,
            ProjectName = _projectName.Trim(),
            Location = _location,
            SolutionMode = SolutionMode,
            SolutionName = (IsBlankSolution ? _solutionName : _sameDirectory ? _projectName : _solutionName).Trim(),
            SameDirectory = _sameDirectory && !IsBlankSolution,
            TrustLocation = CreatesSolution && _trustLocation && !_locationTrusted,
            Framework = framework,
            Values = Options.Count > 0 ? SymbolValues() : new Dictionary<string, string?>(),
            HostOptions = new HostOptionValues { Https = Https, Auth = _auth?.Value, Docker = Docker, Aspire = Aspire },
            ExistingAspire = CreatesSolution ? null : _existingAspire,
            AspireName = AspireName,
        };
    }

    private NewProjectPlan Plan() => NewProjectPlan.Create(Request());

    private async Task CreateAsync()
    {
        if (!CanCreate || _selectedTemplate is null)
        {
            return;
        }

        var request = Request();
        var plan = NewProjectPlan.Create(request);
        CreateError = null;
        _createLog = null;
        IsCreating = true;
        var cts = _create = new CancellationTokenSource();
        try
        {
            // The disk checks once more, now: the location may have changed since they ran.
            var error = await Task.Run(() => plan.ValidateTarget(_services.Rules), cts.Token).ConfigureAwait(true);
            if (error is not null)
            {
                TargetError = error;
                return;
            }

            var location = plan.Location!;
            var trusted = CreatesSolution ? request.TrustLocation || _locationTrusted : _context.WorkspaceTrusted;
            if (request.TrustLocation)
            {
                Directory.CreateDirectory(location);
                _services.Trust(location);
            }

            var progress = new Progress<string>(status => CreateStatus = status);
            var result = await _services.Creator.CreateAsync(plan, progress, cts.Token).ConfigureAwait(true);
            Remember(request, plan);
            Finish(new NewProjectCompletion(request, plan, result, trusted, IsAddMode || SolutionMode == SolutionMode.AddToSolution ? _context.SolutionFolderId : null));
        }
        catch (NewProjectException ex)
        {
            CreateError = Localizer.Format("msg.newProject.createFailed", "Couldn't create the project: {0}", ex.Message);
            _createLog = ex.Log;
        }
        catch (OperationCanceledException)
        {
            // Cancel while creating: back to the page, the folders it made removed.
        }
        finally
        {
            CreateStatus = null;
            _create = null;
            cts.Dispose();
            IsCreating = false;
            RefreshCommands();
        }
    }

    /// <summary>What the next New Project starts with: the template, the location, the framework and the options.</summary>
    private void Remember(NewProjectRequest request, NewProjectPlan plan)
    {
        var history = _services.History;
        var group = request.Group;
        history.AddRecentTemplate(group.Id);
        if (plan.Location is { } location && CreatesSolution)
        {
            history.AddRecentLocation(location);
        }

        history.SetSameDirectory(request.SameDirectory);
        if (request.Framework is { } framework && group.Kind != ProjectTemplateKind.BlankSolution)
        {
            history.SetFramework(group.Id, framework);
        }

        var values = new List<(string, string, string?)>();
        foreach (var option in Options.Where(o => o.Parameter.PersistenceKey is not null))
        {
            values.Add((option.Parameter.PersistenceKey!, option.Name, option.SymbolValue));
        }

        if (_auth is { } auth)
        {
            values.Add(("group:" + group.Id, "$auth", auth.Value));
        }

        if (values.Count > 0)
        {
            history.SetValues(values);
        }
    }

    /// <summary>Escape, or Cancel: stops a running Create, else closes the wizard.</summary>
    public void Cancel()
    {
        if (_create is { } running)
        {
            running.Cancel();
            return;
        }

        Finish(null);
    }

    private bool _finished;

    private void Finish(NewProjectCompletion? completion)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _services.Catalog.Changed -= OnCatalogChanged;
        Dispose();
        Finished?.Invoke(this, completion);
    }

    /// <summary>Stops the pending checks (the wizard finished).</summary>
    public void Dispose()
    {
        foreach (var cts in new[] { _validation, _warning, _searchDelay })
        {
            cts?.Cancel();
            cts?.Dispose();
        }

        (_validation, _warning, _searchDelay) = (null, null, null);
    }

    /// <summary>The window was closed from outside (its close button, the solution closing in add mode).</summary>
    internal void OnClosed()
    {
        _create?.Cancel();
        Finish(null);
    }

    /// <summary>The command's optional argument: <c>{ "template": …, "language": …, "fromStartWindow": true }</c>.</summary>
    public static NewProjectContext ParseArguments(IReadOnlyList<JsonElement> args, NewProjectMode mode)
    {
        if (args.Count == 0 || args[0].ValueKind != JsonValueKind.Object)
        {
            return new NewProjectContext { Mode = mode };
        }

        var argument = args[0];
        string? Text(string name) => argument.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return new NewProjectContext
        {
            Mode = mode,
            Template = Text("template"),
            Language = Text("language"),
            FromStartWindow = argument.TryGetProperty("fromStartWindow", out var start) && start.ValueKind == JsonValueKind.True,
        };
    }
}
