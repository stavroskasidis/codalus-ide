using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Notifications;
using Codalus.Workbench.Packages;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>The NuGet Package Manager tab of a project, or of the solution (<see cref="PackageManagerViewModel.Project"/> null).</summary>
public sealed class PackageManagerDocument : ShellDocument
{
    public PackageManagerDocument(PackageManagerViewModel viewModel)
    {
        Id = DocumentIdFor(viewModel.Project);
        Title = viewModel.Title;
        ViewModel = viewModel;
    }

    public override string Kind => "packageManager";

    public PackageManagerViewModel ViewModel { get; }

    public static string DocumentIdFor(string? project) => "nuget:" + (project ?? "solution");

    public override bool OnClose()
    {
        var closing = base.OnClose();
        if (closing)
        {
            ViewModel.Dispose();
        }

        return closing;
    }
}

/// <summary>What the package manager needs from the shell besides the workbench services.</summary>
public sealed record PackageManagerContext(
    DialogViewModel Dialogs,
    NotificationService Notifications,
    Action<string> OpenUrl,
    Action OpenSources,
    Action ShowRestoreOutput)
{
    /// <summary>Signs in to a package source by name (deferred from M9a); true when it did.</summary>
    public Func<string, Task<bool>>? SignIn { get; init; }
}

public enum PackageTab
{
    Browse,
    Installed,
    Updates,
    Consolidate,
}

/// <summary>
/// Visual Studio's NuGet Package Manager (ADR 0012): Browse, Installed, Updates and (for the solution) Consolidate, with
/// a details pane to install, update or uninstall a version. Feeds are queried through the build host; installed data
/// comes from the project evaluations and assets files.
/// </summary>
public sealed class PackageManagerViewModel : ObservableObject, IDisposable
{
    public const int PageSize = 25;
    private const string AllSources = "All sources";

    private readonly PackageService _packages;
    private readonly SolutionService _solutions;
    private readonly PackageManagerContext _context;
    private readonly EventHandler _onPackagesChanged;
    private readonly EventHandler _onSourcesChanged;
    private readonly EventHandler<RestoreStateChangedEventArgs> _onRestoreState;
    private CancellationTokenSource _listLoad = new();
    private CancellationTokenSource _detailsLoad = new();
    private IReadOnlyList<ProjectPackages> _installed = [];
    private IReadOnlyList<ProjectPackages> _solutionInstalled = [];
    private Dictionary<string, PackageStatus> _status = new(StringComparer.OrdinalIgnoreCase);
    private PackageTab _tab = PackageTab.Browse;
    private string _search = string.Empty;
    private bool _includePrerelease;
    private string _selectedSource = AllSources;
    private PackageRowViewModel? _selected;
    private PackageDetailsViewModel? _details;
    private bool _isLoading;
    private bool _isBusy;
    private string? _error;
    private string? _sourceErrors;
    private string? _restoreStatus;
    private bool _hasMore;
    private int _browseSkip;
    private int _updatesCount;
    private int _consolidateCount;
    private int _installedGeneration;
    private bool _disposed;

    public PackageManagerViewModel(PackageService packages, SolutionService solutions, string? project, PackageManagerContext context)
    {
        _packages = packages;
        _solutions = solutions;
        _context = context;
        Project = project;
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(refresh: true));
        LoadMoreCommand = new RelayCommand(() => _ = LoadMoreAsync());
        OpenSourcesCommand = new RelayCommand(context.OpenSources);
        UpdateSelectedCommand = new RelayCommand(() => _ = UpdateSelectedAsync());
        ShowRestoreOutputCommand = new RelayCommand(context.ShowRestoreOutput);
        SignInCommand = new RelayCommand(() => _ = SignInAsync());
        Sources.Add(AllSources);

        _onPackagesChanged = (_, _) => Ui.Post(() => _ = ReloadInstalledAsync());
        _onSourcesChanged = (_, _) => Ui.Post(() => _ = LoadSourcesAsync());
        _onRestoreState = (_, e) => Ui.Post(() => RestoreStatus = e.State switch
        {
            "running" => "Restoring packages…",
            "failed" => "Restore failed" + (e.Message is { } message ? ": " + message : "."),
            _ => null,
        });
        packages.PackagesChanged += _onPackagesChanged;
        packages.SourcesChanged += _onSourcesChanged;
        solutions.RestoreStateChanged += _onRestoreState;
    }

    /// <summary>The project path, or null for the solution.</summary>
    public string? Project { get; }

    public bool IsSolution => Project is null;

    /// <summary>
    /// The project's target frameworks (M33): the version picker marks the versions that don't support them and starts
    /// at the newest one that does. Empty for the solution, whose projects can differ.
    /// </summary>
    public IReadOnlyList<string> TargetFrameworks =>
        Project is { } project && _solutions.Projects.TryGetValue(project, out var info) ? info.TargetFrameworks : [];

    public string ProjectName => Project is null ? "Solution" : Path.GetFileNameWithoutExtension(Project);

    public string Title => "NuGet: " + ProjectName;

    public ObservableCollection<PackageRowViewModel> Rows { get; } = [];

    public ObservableCollection<string> Sources { get; } = [];

    public ICommand RefreshCommand { get; }

    public ICommand LoadMoreCommand { get; }

    public ICommand OpenSourcesCommand { get; }

    public ICommand UpdateSelectedCommand { get; }

    public ICommand ShowRestoreOutputCommand { get; }

    /// <summary>Signs in to <see cref="SignInSource"/>, then searches again.</summary>
    public ICommand SignInCommand { get; }

    private string? _signInSource;

    /// <summary>The first source of the last request that refused for want of a sign-in, or null.</summary>
    public string? SignInSource
    {
        get => _signInSource;
        private set
        {
            if (SetField(ref _signInSource, value))
            {
                OnPropertyChanged(nameof(CanSignIn));
            }
        }
    }

    public bool CanSignIn => _signInSource is not null && _context.SignIn is not null;

    private async Task SignInAsync()
    {
        if (_signInSource is { } source && _context.SignIn is { } signIn && await signIn(source).ConfigureAwait(true))
        {
            await RefreshAsync(refresh: true).ConfigureAwait(true);
        }
    }

    private void ShowSourceErrors(PackageSourceError[] errors)
    {
        SourceErrors = Describe(errors);
        SignInSource = errors.FirstOrDefault(e => PackageService.NeedsSignIn(e.Message))?.Source;
    }

    /// <summary>A Browse page arrived from the host and its rows were added (benchmarks measure from here to the frame).</summary>
    public event EventHandler? PageLoaded;

    /// <summary>Searches now, without the typing debounce (Enter in the search box).</summary>
    public Task SearchNowAsync() => LoadListAsync(refresh: false);

    public PackageTab Tab
    {
        get => _tab;
        set
        {
            if (value == PackageTab.Consolidate && !IsSolution)
            {
                value = PackageTab.Installed;
            }

            if (SetField(ref _tab, value))
            {
                OnPropertyChanged(nameof(IsBrowseTab));
                OnPropertyChanged(nameof(IsInstalledTab));
                OnPropertyChanged(nameof(IsUpdatesTab));
                OnPropertyChanged(nameof(IsConsolidateTab));
                OnPropertyChanged(nameof(SearchPlaceholder));
                Selected = null;
                _ = LoadListAsync(refresh: false);
            }
        }
    }

    public bool IsBrowseTab
    {
        get => _tab == PackageTab.Browse;
        set
        {
            if (value)
            {
                Tab = PackageTab.Browse;
            }
        }
    }

    public bool IsInstalledTab
    {
        get => _tab == PackageTab.Installed;
        set
        {
            if (value)
            {
                Tab = PackageTab.Installed;
            }
        }
    }

    public bool IsUpdatesTab
    {
        get => _tab == PackageTab.Updates;
        set
        {
            if (value)
            {
                Tab = PackageTab.Updates;
            }
        }
    }

    public bool IsConsolidateTab
    {
        get => _tab == PackageTab.Consolidate;
        set
        {
            if (value)
            {
                Tab = PackageTab.Consolidate;
            }
        }
    }

    public string UpdatesLabel => _updatesCount > 0 ? $"Updates {_updatesCount}" : "Updates";

    public string ConsolidateLabel => _consolidateCount > 0 ? $"Consolidate {_consolidateCount}" : "Consolidate";

    public string SearchPlaceholder => _tab == PackageTab.Browse ? "Search packages (Enter)" : "Filter packages";

    public string Search
    {
        get => _search;
        set
        {
            if (SetField(ref _search, value ?? string.Empty))
            {
                if (_tab == PackageTab.Browse)
                {
                    _ = DebouncedBrowseAsync();
                }
                else
                {
                    BuildLocalRows();
                }
            }
        }
    }

    public bool IncludePrerelease
    {
        get => _includePrerelease;
        set
        {
            if (SetField(ref _includePrerelease, value))
            {
                _ = RefreshAsync(refresh: false);
            }
        }
    }

    public string SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (SetField(ref _selectedSource, value ?? AllSources) && _tab == PackageTab.Browse)
            {
                _ = LoadListAsync(refresh: false);
            }
        }
    }

    private string? SourceFilter => _selectedSource == AllSources ? null : _selectedSource;

    /// <summary>True while the list is rebuilt: the list control clears its selection, which must not clear the details.</summary>
    private bool _rebuildingRows;

    public PackageRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (value is { IsHeader: true } || (value is null && _rebuildingRows))
            {
                return;
            }

            if (SetField(ref _selected, value))
            {
                _ = LoadDetailsAsync(value, version: null);
            }
        }
    }

    public PackageDetailsViewModel? Details
    {
        get => _details;
        private set
        {
            if (SetField(ref _details, value))
            {
                OnPropertyChanged(nameof(HasDetails));
            }
        }
    }

    public bool HasDetails => _details is not null;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    /// <summary>An install, update or uninstall is running.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _error is not null;

    /// <summary>Sources that failed in the last request (the others still answered).</summary>
    public string? SourceErrors
    {
        get => _sourceErrors;
        private set
        {
            if (SetField(ref _sourceErrors, value))
            {
                OnPropertyChanged(nameof(HasSourceErrors));
            }
        }
    }

    public bool HasSourceErrors => _sourceErrors is not null;

    public string? RestoreStatus
    {
        get => _restoreStatus;
        private set
        {
            if (SetField(ref _restoreStatus, value))
            {
                OnPropertyChanged(nameof(HasRestoreStatus));
            }
        }
    }

    public bool HasRestoreStatus => _restoreStatus is not null;

    public bool HasMore
    {
        get => _hasMore;
        private set => SetField(ref _hasMore, value);
    }

    public bool IsEmpty => !_isLoading && Rows.Count == 0;

    public string EmptyText => _tab switch
    {
        PackageTab.Browse => _search.Length == 0 ? "Search for packages, or browse the most popular ones." : "No packages match your search.",
        PackageTab.Installed => "No packages are installed.",
        PackageTab.Updates => "All packages are up to date.",
        _ => "Every package uses the same version in all projects.",
    };

    public bool SelectAllUpdates
    {
        get => Rows.Count > 0 && Rows.Where(r => !r.IsHeader).All(r => r.IsChecked);
        set
        {
            foreach (var row in Rows.Where(r => !r.IsHeader))
            {
                row.IsChecked = value;
            }

            OnPropertyChanged();
        }
    }

    /// <summary>Loads sources and the first page (called when the document first shows).</summary>
    public async Task ActivateAsync()
    {
        await LoadSourcesAsync().ConfigureAwait(true);
        await RefreshAsync(refresh: false).ConfigureAwait(true);
    }

    private async Task LoadSourcesAsync()
    {
        try
        {
            var sources = await _packages.SourcesAsync(CancellationToken.None).ConfigureAwait(true);
            var selected = _selectedSource;
            Sources.Clear();
            Sources.Add(AllSources);
            foreach (var source in sources.Sources.Where(s => s.Enabled))
            {
                Sources.Add(source.Name);
            }

            _selectedSource = Sources.Contains(selected) ? selected : AllSources;
            OnPropertyChanged(nameof(SelectedSource));
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Error = ex.Message;
        }
    }

    public async Task RefreshAsync(bool refresh)
    {
        await ReloadInstalledAsync(listAfterwards: false).ConfigureAwait(true);
        await LoadListAsync(refresh).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ installed packages and status

    private async Task ReloadInstalledAsync(bool listAfterwards = true)
    {
        if (_disposed)
        {
            return;
        }

        // Reloads overlap (an edit, then its restore): only the latest one's result counts.
        var generation = ++_installedGeneration;
        try
        {
            IReadOnlyList<ProjectPackages> installed;
            try
            {
                installed = await _packages.InstalledAsync(null, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex) when (IsExpected(ex) && generation == _installedGeneration)
            {
                // Once more after a moment: a project file read while a restore or an edit writes it can fail.
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(true);
                installed = await _packages.InstalledAsync(null, CancellationToken.None).ConfigureAwait(true);
            }

            if (generation != _installedGeneration)
            {
                return;
            }

            _solutionInstalled = installed;
            _installed = Project is null ? _solutionInstalled : [.. _solutionInstalled.Where(p => SolutionService.PathComparer.Equals(p.Project, Project))];
            await LoadStatusAsync(refresh: false).ConfigureAwait(true);
            if (generation != _installedGeneration)
            {
                return;
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Error = ex.Message;
        }

        if (listAfterwards && _tab != PackageTab.Browse)
        {
            BuildLocalRows();
        }
        else
        {
            MarkInstalled();
        }

        if (_details is { } details)
        {
            UpdateDetailsInstallState(details);
        }
    }

    /// <summary>The latest version of every installed package and the badges of the installed versions.</summary>
    private async Task LoadStatusAsync(bool refresh)
    {
        var queries = _installed
            .SelectMany(p => p.Packages.Where(i => i.Kind != InstalledPackageKinds.Implicit))
            .GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PackageStatusQuery(g.Key, g.Select(i => i.Resolved ?? i.Requested).FirstOrDefault(v => v is not null)))
            .ToList();
        if (queries.Count == 0)
        {
            _status = new(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            var result = await _packages.StatusAsync(queries, _includePrerelease, CancellationToken.None, refresh).ConfigureAwait(true);
            _status = result.Packages.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
            ShowSourceErrors(result.Errors);
        }

        _updatesCount = InstalledGroups().Count(g => HasUpdate(g.Key, g.Value));
        _consolidateCount = IsSolution ? InstalledGroups().Count(g => Versions(g.Value).Count > 1) : 0;
        OnPropertyChanged(nameof(UpdatesLabel));
        OnPropertyChanged(nameof(ConsolidateLabel));
    }

    /// <summary>Direct packages by id, with the projects that reference them.</summary>
    private Dictionary<string, List<(ProjectPackages Project, InstalledPackage Package)>> InstalledGroups()
    {
        var groups = new Dictionary<string, List<(ProjectPackages, InstalledPackage)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in _installed)
        {
            foreach (var package in project.Packages.Where(p => p.Kind != InstalledPackageKinds.Implicit))
            {
                if (!groups.TryGetValue(package.Id, out var list))
                {
                    groups[package.Id] = list = [];
                }

                list.Add((project, package));
            }
        }

        return groups;
    }

    private static List<string> Versions(List<(ProjectPackages Project, InstalledPackage Package)> uses) =>
        [.. uses.Select(u => u.Package.Resolved ?? u.Package.Requested ?? "?").Distinct(StringComparer.OrdinalIgnoreCase)];

    private bool HasUpdate(string id, List<(ProjectPackages Project, InstalledPackage Package)> uses) =>
        _status.TryGetValue(id, out var status) && status.Latest is { } latest
        && uses.Any(u => u.Package.Kind == InstalledPackageKinds.Direct && Compare(u.Package.Resolved ?? u.Package.Requested, latest) < 0);

    // ------------------------------------------------------------------ the list

    private async Task DebouncedBrowseAsync()
    {
        var token = RestartListLoad();
        try
        {
            await Task.Delay(300, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await LoadListAsync(refresh: false).ConfigureAwait(true);
    }

    private CancellationToken RestartListLoad()
    {
        _listLoad.Cancel();
        _listLoad.Dispose();
        _listLoad = new CancellationTokenSource();
        return _listLoad.Token;
    }

    private async Task LoadListAsync(bool refresh)
    {
        Error = null;
        if (_tab != PackageTab.Browse)
        {
            RestartListLoad();
            if (refresh)
            {
                await LoadStatusAsync(refresh: true).ConfigureAwait(true);
            }

            BuildLocalRows();
            return;
        }

        var token = RestartListLoad();
        IsLoading = true;
        try
        {
            var result = await _packages.SearchAsync(_search, _includePrerelease, 0, PageSize, SourceFilter, token, refresh).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            var selectedId = _selected?.Id;
            _rebuildingRows = true;
            try
            {
                Rows.Clear();
                _browseSkip = PageSize;
                AddBrowseRows(result);
            }
            finally
            {
                _rebuildingRows = false;
            }

            Reselect(selectedId);
            PageLoaded?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Rows.Clear();
            Error = ex.Message;
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(EmptyText));
            }
        }
    }

    private async Task LoadMoreAsync()
    {
        if (_tab != PackageTab.Browse || !_hasMore || _isLoading)
        {
            return;
        }

        var token = _listLoad.Token;
        IsLoading = true;
        try
        {
            var result = await _packages.SearchAsync(_search, _includePrerelease, _browseSkip, PageSize, SourceFilter, token).ConfigureAwait(true);
            _browseSkip += PageSize;
            AddBrowseRows(result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void AddBrowseRows(PackageSearchResult result)
    {
        var existing = Rows.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var package in result.Packages.Where(p => !existing.Contains(p.Id)))
        {
            Rows.Add(new PackageRowViewModel(package.Id)
            {
                Version = package.Version,
                Description = package.Description,
                Authors = package.Authors,
                Downloads = package.Downloads,
                IsDeprecated = package.Deprecated,
                VulnerabilitySeverity = package.VulnerabilitySeverity,
                Source = package.Source,
            });
        }

        HasMore = result.HasMore;
        ShowSourceErrors(result.Errors);
        MarkInstalled();
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Shows the installed version next to browse results.</summary>
    private void MarkInstalled()
    {
        var groups = InstalledGroups();
        foreach (var row in Rows)
        {
            row.InstalledVersion = groups.TryGetValue(row.Id, out var uses) ? VersionText(uses) : null;
        }
    }

    /// <summary>Installed, Updates and Consolidate are built from the installed data, filtered by the search text.</summary>
    private void BuildLocalRows()
    {
        var selectedId = _selected?.Id;
        _rebuildingRows = true;
        try
        {
            BuildLocalRowsCore();
        }
        finally
        {
            _rebuildingRows = false;
        }

        Reselect(selectedId);
    }

    /// <summary>After a rebuild: the row of the package that was selected (its details stay), or none.</summary>
    private void Reselect(string? selectedId)
    {
        if (selectedId is not null && Rows.FirstOrDefault(r => !r.IsHeader && r.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase)) is { } again)
        {
            _selected = again;
            OnPropertyChanged(nameof(Selected));
        }
        else if (selectedId is not null)
        {
            Selected = null;
        }
    }

    private void BuildLocalRowsCore()
    {
        Rows.Clear();
        var groups = InstalledGroups();
        bool Matches(string id) => _search.Length == 0 || id.Contains(_search.Trim(), StringComparison.OrdinalIgnoreCase);
        IEnumerable<KeyValuePair<string, List<(ProjectPackages Project, InstalledPackage Package)>>> chosen = _tab switch
        {
            PackageTab.Updates => groups.Where(g => HasUpdate(g.Key, g.Value)),
            PackageTab.Consolidate => groups.Where(g => Versions(g.Value).Count > 1),
            _ => groups,
        };

        var top = chosen.Where(g => Matches(g.Key)).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
        if (_tab == PackageTab.Installed && top.Count > 0)
        {
            Rows.Add(PackageRowViewModel.Header("Top-level packages"));
        }

        foreach (var (id, uses) in top)
        {
            _status.TryGetValue(id, out var status);
            var first = uses[0].Package;
            Rows.Add(new PackageRowViewModel(id)
            {
                Version = _tab == PackageTab.Updates ? status?.Latest : VersionText(uses),
                InstalledVersion = VersionText(uses),
                LatestVersion = status?.Latest,
                IsDeprecated = status?.Deprecated == true,
                VulnerabilitySeverity = status?.VulnerabilitySeverity ?? -1,
                Note = first.Kind switch
                {
                    InstalledPackageKinds.Global => "global",
                    _ when uses.Any(u => u.Package.VersionOverride is not null) => "override",
                    _ when uses.Any(u => u.Package.CentralVersion is not null) => "central",
                    _ => null,
                },
                IsChecked = _tab == PackageTab.Updates,
                HasCheckBox = _tab == PackageTab.Updates,
            });
        }

        if (_tab == PackageTab.Installed && !IsSolution)
        {
            var transitive = _installed.SelectMany(p => p.Transitive).Where(t => Matches(t.Id)).OrderBy(t => t.Id, StringComparer.OrdinalIgnoreCase).ToList();
            if (transitive.Count > 0)
            {
                Rows.Add(PackageRowViewModel.Header("Transitive packages"));
                foreach (var package in transitive)
                {
                    Rows.Add(new PackageRowViewModel(package.Id) { Version = package.Resolved, InstalledVersion = package.Resolved, IsTransitive = true });
                }
            }
        }

        HasMore = false;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(SelectAllUpdates));
    }

    private static string VersionText(List<(ProjectPackages Project, InstalledPackage Package)> uses)
    {
        var versions = Versions(uses);
        return versions.Count == 1 ? versions[0] : "multiple";
    }

    // ------------------------------------------------------------------ details

    private async Task LoadDetailsAsync(PackageRowViewModel? row, string? version)
    {
        _detailsLoad.Cancel();
        _detailsLoad.Dispose();
        _detailsLoad = new CancellationTokenSource();
        var token = _detailsLoad.Token;
        if (row is null)
        {
            Details = null;
            return;
        }

        var details = _details?.Id.Equals(row.Id, StringComparison.OrdinalIgnoreCase) == true && version is not null
            ? _details
            : new PackageDetailsViewModel(row.Id, this);
        details.IsLoading = true;
        details.Error = null;
        Details = details;
        UpdateDetailsInstallState(details);
        if (row.IsTransitive && Project is not null)
        {
            _ = LoadWhyAsync(details, Project, row.Id);
        }

        try
        {
            // The installed version first (Installed/Updates show what you have), else the latest.
            var wanted = version ?? _tab switch
            {
                PackageTab.Browse => null,
                PackageTab.Updates => row.LatestVersion,
                _ => row.InstalledVersion is { } installed && installed != "multiple" ? installed : null,
            };
            var result = await _packages.DetailsAsync(row.Id, _includePrerelease, wanted, _tab == PackageTab.Browse ? SourceFilter : null, token, TargetFrameworks).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            details.Apply(result);
            UpdateDetailsInstallState(details);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            details.Error = ex.Message;
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                details.IsLoading = false;
            }
        }
    }

    internal Task SelectVersionAsync(string version) => _selected is { } row ? LoadDetailsAsync(row, version) : Task.CompletedTask;

    private async Task LoadWhyAsync(PackageDetailsViewModel details, string project, string id)
    {
        try
        {
            var paths = await _packages.WhyAsync(project, id, CancellationToken.None).ConfigureAwait(true);
            details.Why = paths.Count == 0
                ? null
                : string.Join("\n", paths.Select(p => (paths.Select(x => x.Framework).Distinct().Count() > 1 ? p.Framework + ": " : string.Empty) + string.Join(" → ", p.Chain)));
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            details.Why = ex.Message;
        }
    }

    /// <summary>Installed version, project choices (solution level) and what the buttons do.</summary>
    private void UpdateDetailsInstallState(PackageDetailsViewModel details)
    {
        var uses = InstalledGroups().GetValueOrDefault(details.Id) ?? [];
        details.InstalledVersion = uses.Count == 0 ? null : VersionText(uses);
        var package = uses.FirstOrDefault().Package;
        details.IsGlobal = package?.Kind == InstalledPackageKinds.Global;
        details.IsTransitiveOnly = uses.Count == 0 && Rows.Any(r => r.IsTransitive && r.Id.Equals(details.Id, StringComparison.OrdinalIgnoreCase));
        var central = _installed.FirstOrDefault(p => p.CentralPackageManagement);
        details.IsCentral = !IsSolution && central is not null;
        details.CentralNote = details.IsCentral
            ? package?.VersionOverride is not null
                ? $"This project overrides the central version ({package.CentralVersion}) with VersionOverride."
                : $"Versions are managed centrally in {Path.GetFileName(central!.CentralPackagesFile) ?? "Directory.Packages.props"}."
            : null;
        if (IsSolution)
        {
            var checkedPaths = details.Projects.Where(p => p.IsChecked).Select(p => p.Path).ToHashSet(SolutionService.PathComparer);
            details.Projects.Clear();
            foreach (var project in _solutionInstalled.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                var installed = project.Packages.FirstOrDefault(p => p.Id.Equals(details.Id, StringComparison.OrdinalIgnoreCase));
                details.Projects.Add(new ProjectChoiceViewModel(project.Project, project.Name, installed?.Resolved ?? installed?.Requested, details.RefreshCommands)
                {
                    IsChecked = checkedPaths.Contains(project.Project),
                });
            }
        }

        details.RefreshCommands();
    }

    // ------------------------------------------------------------------ install, update, uninstall

    internal async Task InstallAsync(PackageDetailsViewModel details)
    {
        if (details.SelectedVersion is not { } version)
        {
            return;
        }

        var targets = IsSolution
            ? details.Projects.Where(p => p.IsChecked).Select(p => p.Path).ToList()
            : [Project!];
        if (targets.Count == 0)
        {
            _context.Notifications.Info(Localizer.Get("msg.packageManagerViewModel.chooseTheProjectsToInstallThe", "Choose the projects to install the package into."));
            return;
        }

        if (details.RequireLicenseAcceptance && !await AcceptLicenseAsync(details, version.Version).ConfigureAwait(true))
        {
            return;
        }

        var versionOverride = details.UseVersionOverride;
        if (!versionOverride)
        {
            if (await CentralChangeAsync(details.Id, version.Version, targets).ConfigureAwait(true) is not { } choice)
            {
                return;
            }

            versionOverride = choice == CentralChoice.Override;
        }

        await RunEditAsync(
            $"Installing {details.Id} {version.Version}…",
            () => _packages.InstallAsync([.. targets.Select(t => new PackageEditTarget(t))], details.Id, version.Version, versionOverride, CancellationToken.None)).ConfigureAwait(true);
    }

    internal async Task UninstallAsync(PackageDetailsViewModel details)
    {
        var targets = IsSolution
            ? details.Projects.Where(p => p.IsChecked && p.InstalledVersion is not null).Select(p => p.Path).ToList()
            : [Project!];
        if (targets.Count == 0)
        {
            _context.Notifications.Info(Localizer.Get("msg.packageManagerViewModel.chooseTheProjectsToUninstallThe", "Choose the projects to uninstall the package from."));
            return;
        }

        var answer = await _context.Dialogs.ShowAsync(new DialogRequest(
            $"Uninstall {details.Id}?",
            $"{details.Id} will be removed from {string.Join(", ", targets.Select(Path.GetFileNameWithoutExtension))}.",
            ["Uninstall", "Cancel"]) { IsDanger = true }).ConfigureAwait(true);
        if (answer.Button != "Uninstall")
        {
            return;
        }

        await RunEditAsync($"Uninstalling {details.Id}…", () => _packages.UninstallAsync(targets, details.Id, CancellationToken.None)).ConfigureAwait(true);
    }

    /// <summary>Updates the checked packages (Updates tab) to their latest versions in every project that uses them.</summary>
    private async Task UpdateSelectedAsync()
    {
        var rows = Rows.Where(r => r is { IsHeader: false, IsChecked: true, LatestVersion: not null }).ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var groups = InstalledGroups();
        await RunEditAsync($"Updating {rows.Count} package(s)…", async () =>
        {
            var changed = new List<string>();
            foreach (var row in rows)
            {
                var targets = groups.GetValueOrDefault(row.Id)?.Where(u => u.Package.Kind == InstalledPackageKinds.Direct).Select(u => new PackageEditTarget(u.Project.Project)).ToArray() ?? [];
                if (targets.Length > 0)
                {
                    changed.AddRange(await _packages.InstallAsync(targets, row.Id, row.LatestVersion!, versionOverride: false, CancellationToken.None).ConfigureAwait(true));
                }
            }

            return changed;
        }).ConfigureAwait(true);
    }

    private async Task RunEditAsync(string status, Func<Task<IReadOnlyList<string>>> edit)
    {
        IsBusy = true;
        Error = null;
        RestoreStatus = status;
        try
        {
            var changed = await edit().ConfigureAwait(true);
            RestoreStatus = changed.Count == 0 ? null : "Restoring packages…";
            if (changed.Count == 0)
            {
                _context.Notifications.Info(Localizer.Get("msg.packageManagerViewModel.nothingChanged", "Nothing changed."));
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            RestoreStatus = null;
            Error = ex.Message;
            _context.Notifications.Error(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> AcceptLicenseAsync(PackageDetailsViewModel details, string version)
    {
        var licence = details.LicenseText ?? details.LicenseUrl ?? "(see the package's licence)";
        var answer = await _context.Dialogs.ShowAsync(new DialogRequest(
            "License Acceptance",
            $"{details.Id} {version} requires you to accept its licence terms before installing it.\n\nLicence: {licence}\n\n"
            + "By clicking \"I Accept\", you agree to the licence terms of the package. Codalus IDE does not grant licences to third-party packages.",
            ["I Accept", "I Decline"]) { DefaultButton = "I Decline" }).ConfigureAwait(true);
        return answer.Button == "I Accept";
    }

    private enum CentralChoice
    {
        Change,
        Override,
    }

    /// <summary>
    /// Under CPM, changing an existing central version affects every project that uses it: say so, and offer an
    /// override in the target project instead. Null: cancelled.
    /// </summary>
    private async Task<CentralChoice?> CentralChangeAsync(string id, string version, List<string> targets)
    {
        var central = _solutionInstalled.Where(p => p.CentralPackageManagement).ToList();
        var targetProjects = central.Where(p => targets.Contains(p.Project, SolutionService.PathComparer)).ToList();
        var existing = targetProjects.SelectMany(p => p.Packages)
            .FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && p.CentralVersion is not null);
        if (existing is null || string.Equals(existing.CentralVersion, version, StringComparison.OrdinalIgnoreCase))
        {
            return CentralChoice.Change;
        }

        var files = targetProjects.Select(p => p.CentralPackagesFile).Where(f => f is not null).ToHashSet(SolutionService.PathComparer);
        var affected = central
            .Where(p => p.CentralPackagesFile is { } file && files.Contains(file)
                        && p.Packages.Any(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && i.VersionOverride is null))
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var buttons = targets.Count == 1 ? new[] { "Change Central Version", "Override in This Project", "Cancel" } : ["Change Central Version", "Cancel"];
        var answer = await _context.Dialogs.ShowAsync(new DialogRequest(
            "Change the central version?",
            $"{id} {existing.CentralVersion} is set in {string.Join(", ", files.Select(Path.GetFileName))}. Changing it to {version} affects {affected.Count} project(s): {string.Join(", ", affected)}.",
            buttons)).ConfigureAwait(true);
        return answer.Button switch
        {
            "Change Central Version" => CentralChoice.Change,
            "Override in This Project" => CentralChoice.Override,
            _ => null,
        };
    }

    // ------------------------------------------------------------------ helpers

    internal void OpenUrl(string url) => _context.OpenUrl(url);

    private static string? Describe(PackageSourceError[] errors) =>
        errors.Length == 0 ? null : string.Join("\n", errors.Select(e => $"{e.Source}: {e.Message}"));

    internal static bool IsExpected(Exception ex) =>
        ex is JsonRpcRemoteException or JsonRpcConnectionLostException or InvalidOperationException or TimeoutException or IOException;

    /// <summary>Compares NuGet versions well enough for "is there an update" (numeric parts, then prerelease labels).</summary>
    internal static int Compare(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a is null ? (b is null ? 0 : -1) : 1;
        }

        static (Version Number, string Label) Parse(string text)
        {
            var plus = text.IndexOf('+', StringComparison.Ordinal);
            if (plus >= 0)
            {
                text = text[..plus];
            }

            var dash = text.IndexOf('-', StringComparison.Ordinal);
            var number = dash >= 0 ? text[..dash] : text;
            var label = dash >= 0 ? text[(dash + 1)..] : string.Empty;
            var parts = number.Split('.').Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).ToList();
            while (parts.Count < 4)
            {
                parts.Add(0);
            }

            return (new Version(parts[0], parts[1], parts[2], parts[3]), label);
        }

        var (numberA, labelA) = Parse(a);
        var (numberB, labelB) = Parse(b);
        var byNumber = numberA.CompareTo(numberB);
        if (byNumber != 0)
        {
            return byNumber;
        }

        // A release is newer than its prereleases.
        return labelA.Length == 0 ? (labelB.Length == 0 ? 0 : 1) : labelB.Length == 0 ? -1 : string.Compare(labelA, labelB, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _packages.PackagesChanged -= _onPackagesChanged;
        _packages.SourcesChanged -= _onSourcesChanged;
        _solutions.RestoreStateChanged -= _onRestoreState;
        _listLoad.Cancel();
        _listLoad.Dispose();
        _detailsLoad.Cancel();
        _detailsLoad.Dispose();
    }
}

/// <summary>A package in the list (or a section header).</summary>
public sealed class PackageRowViewModel(string id) : ObservableObject
{
    private string? _installedVersion;
    private bool _isChecked;

    public string Id { get; } = id;

    public bool IsHeader { get; private init; }

    public static PackageRowViewModel Header(string title) => new(title) { IsHeader = true };

    public string? Version { get; init; }

    public string? LatestVersion { get; init; }

    public string? Description { get; init; }

    public string? Authors { get; init; }

    public long? Downloads { get; init; }

    public string? Source { get; init; }

    public bool IsDeprecated { get; init; }

    public int VulnerabilitySeverity { get; init; } = -1;

    public bool IsVulnerable => VulnerabilitySeverity >= 0;

    public bool IsTransitive { get; init; }

    /// <summary><c>central</c>, <c>override</c> or <c>global</c> (CPM), shown as a tag.</summary>
    public string? Note { get; init; }

    public bool HasNote => Note is not null;

    public bool HasCheckBox { get; init; }

    public bool IsChecked
    {
        get => _isChecked;
        set => SetField(ref _isChecked, value);
    }

    public string? InstalledVersion
    {
        get => _installedVersion;
        set
        {
            if (SetField(ref _installedVersion, value))
            {
                OnPropertyChanged(nameof(IsInstalled));
                OnPropertyChanged(nameof(VersionLine));
            }
        }
    }

    public bool IsInstalled => _installedVersion is not null && !IsTransitive;

    public bool HasUpdate => LatestVersion is not null && PackageManagerViewModel.Compare(_installedVersion, LatestVersion) < 0 && _installedVersion != "multiple";

    /// <summary>"by Authors, 1.2M downloads".</summary>
    public string? Byline
    {
        get
        {
            var parts = new List<string>();
            if (Authors is { Length: > 0 })
            {
                parts.Add("by " + Authors);
            }

            if (Downloads is { } downloads)
            {
                parts.Add(FormatDownloads(downloads) + " downloads");
            }

            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
    }

    public string VersionLine => IsInstalled && _installedVersion != Version
        ? $"{_installedVersion} → {Version}"
        : Version ?? string.Empty;

    public string Initial => IsHeader || Id.Length == 0 ? string.Empty : Id[..1].ToUpperInvariant();

    public string AccessibleName => IsHeader ? Id : $"{Id} {VersionLine}{(IsDeprecated ? ", deprecated" : string.Empty)}{(IsVulnerable ? ", vulnerable" : string.Empty)}{(IsInstalled ? ", installed" : string.Empty)}";

    internal static string FormatDownloads(long downloads) => downloads switch
    {
        >= 1_000_000_000 => (downloads / 1_000_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (downloads / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (downloads / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => downloads.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>A version in the details' version picker.</summary>
public sealed record PackageVersionChoice(string Version, string Label);

/// <summary>A project in the solution-level details: install into it (checked) and its installed version.</summary>
public sealed class ProjectChoiceViewModel(string path, string name, string? installedVersion, Action changed) : ObservableObject
{
    private bool _isChecked;

    public string Path { get; } = path;

    public string Name { get; } = name;

    public string? InstalledVersion { get; } = installedVersion;

    public string InstalledText => InstalledVersion ?? "—";

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetField(ref _isChecked, value))
            {
                changed();
            }
        }
    }
}

/// <summary>The details pane: one package, a version picker and the install, update and uninstall actions.</summary>
public sealed class PackageDetailsViewModel : ObservableObject
{
    private readonly PackageManagerViewModel _owner;
    private PackageVersionChoice? _selectedVersion;
    private bool _isLoading;
    private string? _error;
    private string? _installedVersion;
    private string? _why;
    private bool _useVersionOverride;
    private bool _applying;

    public PackageDetailsViewModel(string id, PackageManagerViewModel owner)
    {
        Id = id;
        _owner = owner;
        InstallCommand = new RelayCommand(() => _ = owner.InstallAsync(this), () => CanInstall);
        UninstallCommand = new RelayCommand(() => _ = owner.UninstallAsync(this), () => CanUninstall);
        OpenLinkCommand = new RelayCommand(url => owner.OpenUrl((string)url!), url => url is string { Length: > 0 });
    }

    public string Id { get; }

    public PackageDetails? Package { get; private set; }

    public ObservableCollection<PackageVersionChoice> Versions { get; } = [];

    public ObservableCollection<ProjectChoiceViewModel> Projects { get; } = [];

    public bool HasProjects => Projects.Count > 0;

    public ICommand InstallCommand { get; }

    public ICommand UninstallCommand { get; }

    public ICommand OpenLinkCommand { get; }

    public PackageVersionChoice? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetField(ref _selectedVersion, value))
            {
                RefreshCommands();
                if (!_applying && value is not null && Package?.Version != value.Version)
                {
                    _ = _owner.SelectVersionAsync(value.Version);
                }
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetField(ref _isLoading, value);
    }

    public string? Error
    {
        get => _error;
        set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _error is not null;

    public string? InstalledVersion
    {
        get => _installedVersion;
        set
        {
            if (SetField(ref _installedVersion, value))
            {
                OnPropertyChanged(nameof(InstalledText));
                OnPropertyChanged(nameof(IsInstalled));
                RefreshCommands();
            }
        }
    }

    public bool IsInstalled => _installedVersion is not null;

    public string InstalledText => _installedVersion is null ? "Not installed" : "Installed: " + _installedVersion;

    public bool IsGlobal { get; set; }

    public bool IsTransitiveOnly { get; set; }

    public bool IsCentral { get; set; }

    public string? CentralNote { get; set; }

    public bool HasCentralNote => CentralNote is not null;

    /// <summary>Under CPM, install with <c>VersionOverride</c> in this project instead of changing the central version.</summary>
    public bool UseVersionOverride
    {
        get => _useVersionOverride;
        set => SetField(ref _useVersionOverride, value);
    }

    /// <summary>"Why is this here": the dependency chains to a transitive package.</summary>
    public string? Why
    {
        get => _why;
        set
        {
            if (SetField(ref _why, value))
            {
                OnPropertyChanged(nameof(HasWhy));
            }
        }
    }

    public bool HasWhy => _why is not null;

    public bool CanInstall =>
        !IsGlobal && _selectedVersion is not null
        && (Projects.Count == 0 ? !string.Equals(_installedVersion, _selectedVersion.Version, StringComparison.OrdinalIgnoreCase) : Projects.Any(p => p.IsChecked));

    public bool CanUninstall => !IsGlobal && (Projects.Count == 0 ? IsInstalled : Projects.Any(p => p.IsChecked && p.InstalledVersion is not null));

    public string InstallLabel => _installedVersion is null || Projects.Count > 0 ? "Install" : "Update";

    public void RefreshCommands()
    {
        ((RelayCommand)InstallCommand).RaiseCanExecuteChanged();
        ((RelayCommand)UninstallCommand).RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanUninstall));
        OnPropertyChanged(nameof(InstallLabel));
        OnPropertyChanged(nameof(HasProjects));
        OnPropertyChanged(nameof(IsGlobal));
        OnPropertyChanged(nameof(IsCentral));
        OnPropertyChanged(nameof(CentralNote));
        OnPropertyChanged(nameof(HasCentralNote));
    }

    // ------------------------------------------------------------------ the package's metadata

    public string? Title => Package?.Title is { } title && title != Id ? title : null;

    public string? Description => Package?.Description;

    public string? Authors => Package?.Authors;

    public string? LicenseText => Package?.LicenseExpression;

    public string? LicenseUrl => Package?.LicenseUrl;

    public bool HasLicenseUrl => LicenseUrl is not null;

    public string? ProjectUrl => Package?.ProjectUrl;

    public bool HasProjectUrl => ProjectUrl is not null;

    public string? Published => Package?.Published;

    public string? DownloadsText => Package?.Downloads is { } downloads ? PackageRowViewModel.FormatDownloads(downloads) : null;

    public string? Tags => Package?.Tags;

    public string? Source => Package?.Source;

    public string? Readme => Package?.Readme;

    public bool HasReadme => Readme is not null;

    public bool RequireLicenseAcceptance => Package?.RequireLicenseAcceptance == true;

    public string Dependencies
    {
        get
        {
            if (Package is not { } package || package.DependencyGroups.Length == 0)
            {
                return "No dependencies";
            }

            return string.Join("\n", package.DependencyGroups.Select(g =>
                g.TargetFramework + (g.Dependencies.Length == 0
                    ? "\n    No dependencies"
                    : string.Concat(g.Dependencies.Select(d => $"\n    {d.Id} ({d.Range})")))));
        }
    }

    public string? DeprecationText => Package?.Deprecation is { } deprecation
        ? "This version is deprecated" + (deprecation.Reasons.Length > 0 ? $" ({string.Join(", ", deprecation.Reasons)})" : string.Empty) + "."
          + (deprecation.Message is { } message ? " " + message : string.Empty)
          + (deprecation.AlternateId is { } alternate ? $" Alternative: {alternate}{(deprecation.AlternateRange is { } range ? " " + range : string.Empty)}." : string.Empty)
        : null;

    public bool IsDeprecated => DeprecationText is not null;

    public string? VulnerabilityText => Package is { Vulnerabilities.Length: > 0 } package
        ? $"This version has {package.Vulnerabilities.Length} known vulnerabilit{(package.Vulnerabilities.Length == 1 ? "y" : "ies")} ({string.Join(", ", package.Vulnerabilities.Select(v => Severity(v.Severity)).Distinct())})."
        : null;

    public bool IsVulnerable => VulnerabilityText is not null;

    public IReadOnlyList<string> AdvisoryUrls => Package?.Vulnerabilities.Select(v => v.AdvisoryUrl).ToList() ?? [];

    internal static string Severity(int severity) => severity switch
    {
        0 => "low",
        1 => "moderate",
        2 => "high",
        3 => "critical",
        _ => "unknown severity",
    };

    public void Apply(PackageDetails package)
    {
        _applying = true;
        try
        {
            Package = package;
            Versions.Clear();
            foreach (var version in package.Versions)
            {
                var notes = new List<string>();
                if (string.Equals(version.Version, _installedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add("installed");
                }

                if (version == package.Versions[0])
                {
                    notes.Add(version.Version.Contains('-', StringComparison.Ordinal) ? "latest prerelease" : "latest");
                }
                else if (version.Compatible == true && package.Versions.TakeWhile(v => v != version).All(v => v.Compatible == false))
                {
                    notes.Add("latest compatible");
                }

                if (version.Compatible == false)
                {
                    notes.Add("not for " + string.Join(", ", _owner.TargetFrameworks));
                }

                if (version.Deprecated)
                {
                    notes.Add("deprecated");
                }

                if (version.VulnerabilitySeverity >= 0)
                {
                    notes.Add("vulnerable");
                }

                Versions.Add(new PackageVersionChoice(version.Version, notes.Count == 0 ? version.Version : $"{version.Version} ({string.Join(", ", notes)})"));
            }

            SelectedVersion = Versions.FirstOrDefault(v => v.Version == package.Version);
        }
        finally
        {
            _applying = false;
        }

        foreach (var name in new[]
                 {
                     nameof(Package), nameof(Title), nameof(Description), nameof(Authors), nameof(LicenseText), nameof(LicenseUrl), nameof(HasLicenseUrl),
                     nameof(ProjectUrl), nameof(HasProjectUrl), nameof(Published), nameof(DownloadsText), nameof(Tags), nameof(Source), nameof(Readme),
                     nameof(HasReadme), nameof(Dependencies), nameof(DeprecationText), nameof(IsDeprecated), nameof(VulnerabilityText), nameof(IsVulnerable),
                     nameof(AdvisoryUrls), nameof(RequireLicenseAcceptance),
                 })
        {
            OnPropertyChanged(name);
        }

        RefreshCommands();
    }
}
