using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Projects.Protocol;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Notifications;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.ViewModels;

/// <summary>A document hosting one of the solution-management views (ADR 0013).</summary>
public sealed class SolutionManagementDocument : ShellDocument
{
    public SolutionManagementDocument(string id, string title, ObservableObject viewModel)
    {
        Id = id;
        Title = title;
        ViewModel = viewModel;
    }

    public override string Kind => "solutionManagement";

    public ObservableObject ViewModel { get; }
}

/// <summary>Something that loads when its view first shows.</summary>
public interface ILoadable
{
    Task LoadAsync();
}

// ------------------------------------------------------------------ Reference Manager

public sealed class ReferenceChoiceViewModel(string path, string name, bool isChecked) : ObservableObject
{
    private bool _isChecked = isChecked;

    public string Path { get; } = path;

    public string Name { get; } = name;

    public bool WasChecked { get; } = isChecked;

    public bool IsChecked
    {
        get => _isChecked;
        set => SetField(ref _isChecked, value);
    }
}

public sealed record FileReferenceRow(string Kind, string Name, string? Path)
{
    public string Display => Path is null ? Name : $"{Name}  ({Path})";
}

/// <summary>
/// The Reference Manager of a project: project references (check the projects to reference, with a filter), assembly
/// references (browse for a .dll) and analyzers (browse for an analyzer .dll).
/// </summary>
public sealed class ReferenceManagerViewModel : ObservableObject, ILoadable
{
    private readonly ProjectManagementService _management;
    private readonly SolutionService _solutions;
    private readonly Func<string, string, string[], Task<string?>> _pickFile;
    private string _filter = string.Empty;
    private string? _error;
    private FileReferenceRow? _selectedFile;
    private long _watchUntil;
    private bool _watching;

    public ReferenceManagerViewModel(ProjectManagementService management, SolutionService solutions, string project, Func<string, string, string[], Task<string?>> pickFile)
    {
        _management = management;
        _solutions = solutions;
        _pickFile = pickFile;
        Project = project;
        ApplyCommand = new RelayCommand(() => _ = ApplyAsync());
        AddAssemblyCommand = new RelayCommand(() => _ = AddFileAsync(ReferenceEditKinds.Assembly));
        AddAnalyzerCommand = new RelayCommand(() => _ = AddFileAsync(ReferenceEditKinds.Analyzer));
        RemoveFileCommand = new RelayCommand(() => _ = RemoveFileAsync(), () => _selectedFile?.Path is not null);
    }

    public string Project { get; }

    public string Title => "References: " + System.IO.Path.GetFileNameWithoutExtension(Project);

    public ObservableCollection<ReferenceChoiceViewModel> Projects { get; } = [];

    public ObservableCollection<ReferenceChoiceViewModel> VisibleProjects { get; } = [];

    public ObservableCollection<FileReferenceRow> Files { get; } = [];

    public ICommand ApplyCommand { get; }

    public ICommand AddAssemblyCommand { get; }

    public ICommand AddAnalyzerCommand { get; }

    public ICommand RemoveFileCommand { get; }

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    public FileReferenceRow? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetField(ref _selectedFile, value))
            {
                ((RelayCommand)RemoveFileCommand).RaiseCanExecuteChanged();
            }
        }
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

    public Task LoadAsync()
    {
        var info = _solutions.Projects.GetValueOrDefault(Project);
        var referenced = info?.References.Where(r => r.Kind == ReferenceKinds.Project && r.Path is not null).Select(r => r.Path!).ToHashSet(SolutionService.PathComparer) ?? [];
        Projects.Clear();
        foreach (var entry in (_solutions.Solution?.Projects ?? []).Where(p => !SolutionService.PathComparer.Equals(p.Path, Project)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            Projects.Add(new ReferenceChoiceViewModel(entry.Path, entry.Name, referenced.Contains(entry.Path)));
        }

        Files.Clear();
        foreach (var reference in info?.References.Where(r => r.Kind is ReferenceKinds.Assembly).DistinctBy(r => r.Name) ?? [])
        {
            Files.Add(new FileReferenceRow(ReferenceEditKinds.Assembly, reference.Name, reference.Path));
        }

        foreach (var item in info?.Items.Where(i => i.Kind == "Analyzer") ?? [])
        {
            Files.Add(new FileReferenceRow(ReferenceEditKinds.Analyzer, System.IO.Path.GetFileName(item.Path), item.Path));
        }

        ApplyFilter();
        return Task.CompletedTask;
    }

    private void ApplyFilter()
    {
        VisibleProjects.Clear();
        foreach (var project in Projects.Where(p => _filter.Length == 0 || p.Name.Contains(_filter.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            VisibleProjects.Add(project);
        }
    }

    /// <summary>Adds and removes project references to match the checkboxes.</summary>
    public async Task ApplyAsync()
    {
        WatchEvaluations();
        try
        {
            foreach (var project in Projects.Where(p => p.IsChecked != p.WasChecked))
            {
                if (project.IsChecked)
                {
                    await _management.AddReferenceAsync(Project, ReferenceEditKinds.Project, project.Path, CancellationToken.None).ConfigureAwait(true);
                }
                else
                {
                    await _management.RemoveReferenceAsync(Project, ReferenceEditKinds.Project, project.Path, CancellationToken.None).ConfigureAwait(true);
                }
            }

            Error = null;
            await WaitForEvaluationAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (PackageManagerViewModel.IsExpected(ex))
        {
            Error = ex.Message;
        }
    }

    private async Task AddFileAsync(string kind)
    {
        if (await _pickFile(kind == ReferenceEditKinds.Analyzer ? "Add Analyzer" : "Add Assembly Reference", "Assemblies", ["*.dll"]).ConfigureAwait(true) is not { } file)
        {
            return;
        }

        WatchEvaluations();
        try
        {
            await _management.AddReferenceAsync(Project, kind, file, CancellationToken.None).ConfigureAwait(true);
            Error = null;
            await WaitForEvaluationAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (PackageManagerViewModel.IsExpected(ex))
        {
            Error = ex.Message;
        }
    }

    private async Task RemoveFileAsync()
    {
        if (_selectedFile is not { Path: { } path } row)
        {
            return;
        }

        WatchEvaluations();
        try
        {
            await _management.RemoveReferenceAsync(Project, row.Kind, path, CancellationToken.None).ConfigureAwait(true);
            Error = null;
            await WaitForEvaluationAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (PackageManagerViewModel.IsExpected(ex))
        {
            Error = ex.Message;
        }
    }

    /// <summary>
    /// For the next two minutes, every evaluation of the project reloads the lists: an edit's evaluation can arrive
    /// before the wait below starts, or after it gives up (a busy host), and an earlier evaluation can end it too soon.
    /// </summary>
    private void WatchEvaluations()
    {
        _watchUntil = Environment.TickCount64 + 120_000;
        if (_watching)
        {
            return;
        }

        _watching = true;
        _solutions.ProjectChanged += OnEvaluated;
    }

    private void OnEvaluated(object? sender, ProjectInfo info)
    {
        if (!SolutionService.PathComparer.Equals(info.Path, Project))
        {
            return;
        }

        Ui.Post(() =>
        {
            if (Environment.TickCount64 > _watchUntil)
            {
                _solutions.ProjectChanged -= OnEvaluated;
                _watching = false;
                return;
            }

            _ = LoadAsync();
        });
    }

    /// <summary>The host re-evaluates after the file changes; refresh once the project's new evaluation arrives.</summary>
    private async Task WaitForEvaluationAsync()
    {
        var evaluated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, ProjectInfo info)
        {
            if (SolutionService.PathComparer.Equals(info.Path, Project))
            {
                evaluated.TrySetResult();
            }
        }

        _solutions.ProjectChanged += OnChanged;
        await evaluated.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        _solutions.ProjectChanged -= OnChanged;
        await LoadAsync().ConfigureAwait(true);
    }
}

// ------------------------------------------------------------------ Configuration Manager

public sealed class ProjectConfigurationRowViewModel(ConfigurationManagerViewModel owner, ProjectConfigurationEntries project, ProjectConfigurationEntry entry) : ObservableObject
{
    public string Project { get; } = project.Project;

    public string Name { get; } = project.Name;

    public IReadOnlyList<string> Configurations { get; } = [.. project.ProjectConfigurations.Union([entry.Configuration], StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<string> Platforms { get; } = [.. project.ProjectPlatforms.Union([entry.Platform], StringComparer.OrdinalIgnoreCase)];

    public string Configuration
    {
        get => entry.Configuration;
        set
        {
            if (value is not null && value != entry.Configuration)
            {
                _ = owner.SetAsync(this, value, null, null);
            }
        }
    }

    public string Platform
    {
        get => entry.Platform;
        set
        {
            if (value is not null && value != entry.Platform)
            {
                _ = owner.SetAsync(this, null, value, null);
            }
        }
    }

    public bool Build
    {
        get => entry.Build;
        set
        {
            if (value != entry.Build)
            {
                _ = owner.SetAsync(this, null, null, value);
            }
        }
    }
}

/// <summary>
/// Visual Studio's Configuration Manager: for a solution configuration and platform, which configuration and platform
/// of each project builds (and whether it builds); new and removed solution configurations and platforms.
/// </summary>
public sealed class ConfigurationManagerViewModel : ObservableObject, ILoadable
{
    private readonly ProjectManagementService _management;
    private readonly DialogViewModel _dialogs;
    private readonly Func<SolutionConfigurationInfo, Task> _activate;
    private string? _buildType;
    private string? _platform;
    private string? _error;
    private SolutionConfigurationsResult? _data;
    private SolutionConfigurationInfo? _active;

    public ConfigurationManagerViewModel(ProjectManagementService management, DialogViewModel dialogs, SolutionConfigurationInfo? active, Func<SolutionConfigurationInfo, Task> activate)
    {
        _management = management;
        _dialogs = dialogs;
        _activate = activate;
        _active = active;
        _buildType = active?.Configuration;
        _platform = active?.Platform;
        AddBuildTypeCommand = new RelayCommand(() => _ = AddAsync(ConfigurationDimensions.BuildType));
        AddPlatformCommand = new RelayCommand(() => _ = AddAsync(ConfigurationDimensions.Platform));
        RemoveBuildTypeCommand = new RelayCommand(() => _ = RemoveAsync(ConfigurationDimensions.BuildType, _buildType), () => _buildType is not null && BuildTypes.Count > 1);
        RemovePlatformCommand = new RelayCommand(() => _ = RemoveAsync(ConfigurationDimensions.Platform, _platform), () => _platform is not null && Platforms.Count > 1);
        ActivateCommand = new RelayCommand(() =>
        {
            if (_buildType is not null && _platform is not null)
            {
                var pair = new SolutionConfigurationInfo(_buildType, _platform);
                _ = _activate(pair);
                _active = pair;
                StateChanged();
            }
        }, () => _buildType is not null && _platform is not null && !IsActivePair);
    }

    /// <summary>
    /// Whether the chosen configuration and platform are the solution's active pair: a chip says so, and Make Active is
    /// disabled (M34h).
    /// </summary>
    public bool IsActivePair => _active is { } active && active.Configuration == _buildType && active.Platform == _platform;

    /// <summary>The Remove buttons' names, with what they remove.</summary>
    public string RemoveBuildTypeName => Localizer.Format("msg.configurationManager.removeName", "Remove {0}", _buildType ?? string.Empty);

    public string RemovePlatformName => Localizer.Format("msg.configurationManager.removeName", "Remove {0}", _platform ?? string.Empty);

    private void StateChanged()
    {
        OnPropertyChanged(nameof(IsActivePair));
        OnPropertyChanged(nameof(RemoveBuildTypeName));
        OnPropertyChanged(nameof(RemovePlatformName));
        foreach (var command in new[] { RemoveBuildTypeCommand, RemovePlatformCommand, ActivateCommand })
        {
            ((RelayCommand)command).RaiseCanExecuteChanged();
        }
    }

    public ObservableCollection<string> BuildTypes { get; } = [];

    public ObservableCollection<string> Platforms { get; } = [];

    public ObservableCollection<ProjectConfigurationRowViewModel> Rows { get; } = [];

    public ICommand AddBuildTypeCommand { get; }

    public ICommand AddPlatformCommand { get; }

    public ICommand RemoveBuildTypeCommand { get; }

    public ICommand RemovePlatformCommand { get; }

    public ICommand ActivateCommand { get; }

    public string? BuildType
    {
        get => _buildType;
        set
        {
            if (value is not null && SetField(ref _buildType, value))
            {
                BuildRows();
                StateChanged();
            }
        }
    }

    public string? Platform
    {
        get => _platform;
        set
        {
            if (value is not null && SetField(ref _platform, value))
            {
                BuildRows();
                StateChanged();
            }
        }
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

    public async Task LoadAsync()
    {
        try
        {
            _data = await _management.GetConfigurationsAsync(CancellationToken.None).ConfigureAwait(true);
            Replace(BuildTypes, _data.BuildTypes);
            Replace(Platforms, _data.Platforms);
            var buildType = _data.BuildTypes.Contains(_buildType) ? _buildType : _data.BuildTypes.FirstOrDefault();
            var platform = _data.Platforms.Contains(_platform) ? _platform : _data.Platforms.FirstOrDefault();

            // The combo boxes lost their choice while their lists were replaced (and kept none chosen before the first
            // load): clear it, then choose again, so they show it (M34h, seen in the gallery).
            _buildType = null;
            _platform = null;
            OnPropertyChanged(nameof(BuildType));
            OnPropertyChanged(nameof(Platform));
            _buildType = buildType;
            _platform = platform;
            OnPropertyChanged(nameof(BuildType));
            OnPropertyChanged(nameof(Platform));
            BuildRows();
            StateChanged();
            Error = null;
        }
        catch (Exception ex) when (PackageManagerViewModel.IsExpected(ex))
        {
            Error = ex.Message;
        }
    }

    private static void Replace(ObservableCollection<string> target, IEnumerable<string> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private void BuildRows()
    {
        Rows.Clear();
        foreach (var project in (_data?.Projects ?? []).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (project.Entries.FirstOrDefault(e => e.SolutionConfiguration == _buildType && e.SolutionPlatform == _platform) is { } entry)
            {
                Rows.Add(new ProjectConfigurationRowViewModel(this, project, entry));
            }
        }
    }

    internal async Task SetAsync(ProjectConfigurationRowViewModel row, string? configuration, string? platform, bool? build)
    {
        if (_buildType is null || _platform is null)
        {
            return;
        }

        try
        {
            await _management.SetProjectConfigurationAsync(new SetProjectConfigurationParams(row.Project, _buildType, _platform, configuration, platform, build), CancellationToken.None).ConfigureAwait(true);
            Error = null;
        }
        catch (Exception ex) when (PackageManagerViewModel.IsExpected(ex))
        {
            Error = ex.Message;
        }

        await LoadAsync().ConfigureAwait(true);
    }

    private async Task AddAsync(string dimension)
    {
        var isBuildType = dimension == ConfigurationDimensions.BuildType;
        var name = await _dialogs.InputAsync(
            isBuildType ? "New Solution Configuration" : "New Solution Platform",
            isBuildType ? $"Its projects build like {_buildType} at first." : $"Its projects build like {_platform} at first.",
            string.Empty,
            text => string.IsNullOrWhiteSpace(text) ? "Enter a name." : (isBuildType ? BuildTypes : Platforms).Contains(text.Trim(), StringComparer.OrdinalIgnoreCase) ? "It exists already." : null).ConfigureAwait(true);
        if (name is null)
        {
            return;
        }

        try
        {
            await _management.AddConfigurationAsync(dimension, name.Trim(), isBuildType ? _buildType : _platform, CancellationToken.None).ConfigureAwait(true);
            if (isBuildType)
            {
                _buildType = name.Trim();
            }
            else
            {
                _platform = name.Trim();
            }

            Error = null;
        }
        catch (Exception ex) when (PackageManagerViewModel.IsExpected(ex))
        {
            Error = ex.Message;
        }

        await LoadAsync().ConfigureAwait(true);
    }

    private async Task RemoveAsync(string dimension, string? name)
    {
        if (name is null || !await _dialogs.ConfirmAsync($"Remove {name}?", $"The solution's '{name}' {(dimension == ConfigurationDimensions.BuildType ? "configuration" : "platform")} and its project mappings will be removed.", "Remove", danger: true).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await _management.RemoveConfigurationAsync(dimension, name, CancellationToken.None).ConfigureAwait(true);
            Error = null;
        }
        catch (Exception ex) when (PackageManagerViewModel.IsExpected(ex))
        {
            Error = ex.Message;
        }

        await LoadAsync().ConfigureAwait(true);
    }
}

// ------------------------------------------------------------------ Project Dependencies

/// <summary>Visual Studio's Project Dependencies: which projects a project waits for, and the resulting build order.</summary>
public sealed class ProjectDependenciesViewModel : ObservableObject, ILoadable
{
    private readonly ProjectManagementService _management;
    private readonly SolutionService _solutions;
    private IReadOnlyList<ProjectDependencies> _dependencies = [];
    private SolutionProjectInfo? _selected;
    private string? _error;

    public ProjectDependenciesViewModel(ProjectManagementService management, SolutionService solutions, string? project)
    {
        _management = management;
        _solutions = solutions;
        _selected = solutions.Solution?.Projects.FirstOrDefault(p => project is not null && SolutionService.PathComparer.Equals(p.Path, project)) ?? solutions.Solution?.Projects.FirstOrDefault();
        ApplyCommand = new RelayCommand(() => _ = ApplyAsync());
    }

    public IReadOnlyList<SolutionProjectInfo> Projects => _solutions.Solution?.Projects ?? [];

    public ObservableCollection<ReferenceChoiceViewModel> DependsOn { get; } = [];

    public ObservableCollection<string> BuildOrder { get; } = [];

    public ICommand ApplyCommand { get; }

    public SolutionProjectInfo? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                BuildChoices();
            }
        }
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

    public async Task LoadAsync()
    {
        try
        {
            _dependencies = await _management.GetDependenciesAsync(CancellationToken.None).ConfigureAwait(true);
            OnPropertyChanged(nameof(Projects));
            BuildChoices();
            BuildOrder.Clear();
            var paths = Projects.Select(p => p.Path).ToList();
            var names = Projects.ToDictionary(p => p.Path, p => p.Name, SolutionService.PathComparer);
            var index = 1;
            foreach (var path in ProjectManagementService.BuildOrder(paths, _solutions.Projects, _dependencies))
            {
                BuildOrder.Add($"{index++}. {names[path]}");
            }

            Error = null;
        }
        catch (Exception ex) when (PackageManagerViewModel.IsExpected(ex))
        {
            Error = ex.Message;
        }
    }

    private void BuildChoices()
    {
        DependsOn.Clear();
        if (_selected is not { } project)
        {
            return;
        }

        var declared = _dependencies.FirstOrDefault(d => SolutionService.PathComparer.Equals(d.Project, project.Path))?.DependsOn.ToHashSet(SolutionService.PathComparer) ?? [];
        var references = _solutions.Projects.GetValueOrDefault(project.Path)?.References.Where(r => r.Kind == ReferenceKinds.Project && r.Path is not null).Select(r => r.Path!).ToHashSet(SolutionService.PathComparer) ?? [];
        foreach (var other in Projects.Where(p => !SolutionService.PathComparer.Equals(p.Path, project.Path)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            DependsOn.Add(new ReferenceChoiceViewModel(other.Path, other.Name + (references.Contains(other.Path) ? " (project reference)" : string.Empty), declared.Contains(other.Path)));
        }
    }

    public async Task ApplyAsync()
    {
        if (_selected is not { } project)
        {
            return;
        }

        try
        {
            await _management.SetDependenciesAsync(project.Path, [.. DependsOn.Where(d => d.IsChecked).Select(d => d.Path)], CancellationToken.None).ConfigureAwait(true);
            Error = null;
        }
        catch (Exception ex) when (PackageManagerViewModel.IsExpected(ex))
        {
            Error = ex.Message;
        }

        await LoadAsync().ConfigureAwait(true);
    }
}

// ------------------------------------------------------------------ .NET SDKs

public sealed record SdkRow(string Version, string Path, bool IsResolved)
{
    private readonly (string Text, bool OutOfSupport) _support = Workbench.Projects.DotnetSupport.Status(Version, DateOnly.FromDateTime(DateTime.Today));

    public string Display => IsResolved ? $"{Version}  (in use)" : Version;

    /// <summary>"Supported until …", "Out of support since …" or "Preview of .NET 11".</summary>
    public string Support => _support.Text;

    public bool IsOutOfSupport => _support.OutOfSupport;
}

/// <summary>The SDK in use, the <c>global.json</c> that picks it, the installed SDKs and workloads; pin an SDK.</summary>
public sealed class SdkViewModel : ObservableObject, ILoadable
{
    private readonly ProjectManagementService _management;
    private readonly NotificationService _notifications;
    private readonly Func<Task> _restartHost;
    private SdkInfoResult? _info;
    private SdkRow? _selected;
    private string _rollForward = "latestFeature";
    private string? _error;

    public SdkViewModel(ProjectManagementService management, NotificationService notifications, Func<Task> restartHost)
    {
        _management = management;
        _notifications = notifications;
        _restartHost = restartHost;
        PinCommand = new RelayCommand(() => _ = PinAsync(_selected?.Version), () => _selected is not null);
        UnpinCommand = new RelayCommand(() => _ = PinAsync(null), () => _info?.RequestedVersion is not null);
        RefreshCommand = new RelayCommand(() => _ = LoadAsync());
    }

    public ObservableCollection<SdkRow> Sdks { get; } = [];

    public ObservableCollection<DotnetWorkload> Workloads { get; } = [];

    public static IReadOnlyList<string> RollForwardOptions { get; } = ["patch", "feature", "minor", "major", "latestPatch", "latestFeature", "latestMinor", "latestMajor", "disable"];

    public ICommand PinCommand { get; }

    public ICommand UnpinCommand { get; }

    public ICommand RefreshCommand { get; }

    public string ResolvedText => _info?.ResolvedVersion is { } version ? $"In use: .NET SDK {version} ({_info.ResolvedSdk})" : "In use: unknown";

    public string GlobalJsonText => _info?.GlobalJson is { } path
        ? $"global.json: {path}" + (_info.RequestedVersion is { } requested ? $" asks for {requested}" + (_info.RollForward is { } roll ? $", roll forward: {roll}" : string.Empty) : " (no SDK version)")
        : "No global.json: the newest installed SDK is used.";

    public bool HasWorkloads => Workloads.Count > 0;

    public SdkRow? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                ((RelayCommand)PinCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public string RollForward
    {
        get => _rollForward;
        set => SetField(ref _rollForward, value ?? "latestFeature");
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

    /// <summary>Why the SDK the solution's folder resolves to isn't the one in use (ADR 0025).</summary>
    public string? Warning => _info?.Warning;

    public bool HasWarning => _info?.Warning is not null;

    public async Task LoadAsync()
    {
        try
        {
            _info = await _management.GetSdkInfoAsync(CancellationToken.None).ConfigureAwait(true);
            Sdks.Clear();
            foreach (var sdk in _info.Sdks.OrderByDescending(s => Version.TryParse(s.Version.Split('-')[0], out var v) ? v : new Version()))
            {
                Sdks.Add(new SdkRow(sdk.Version, sdk.Path, sdk.Version == _info.ResolvedVersion));
            }

            Workloads.Clear();
            foreach (var workload in _info.Workloads)
            {
                Workloads.Add(workload);
            }

            _rollForward = _info.RollForward ?? _rollForward;
            Error = _info.Error;
            foreach (var name in new[] { nameof(ResolvedText), nameof(GlobalJsonText), nameof(HasWorkloads), nameof(RollForward), nameof(Warning), nameof(HasWarning) })
            {
                OnPropertyChanged(name);
            }

            ((RelayCommand)UnpinCommand).RaiseCanExecuteChanged();
        }
        catch (Exception ex) when (PackageManagerViewModel.IsExpected(ex))
        {
            Error = ex.Message;
        }
    }

    /// <summary>Writes global.json and restarts the build host, which picks its SDK when it starts.</summary>
    private async Task PinAsync(string? version)
    {
        try
        {
            await _management.SetGlobalJsonAsync(version, version is null ? null : _rollForward, CancellationToken.None).ConfigureAwait(true);
            _notifications.Info(version is null ? "Removed the SDK version from global.json. Reloading the solution…" : $"global.json now asks for .NET SDK {version}. Reloading the solution…");
            await _restartHost().ConfigureAwait(true);
        }
        catch (Exception ex) when (PackageManagerViewModel.IsExpected(ex))
        {
            Error = ex.Message;
        }

        await LoadAsync().ConfigureAwait(true);
    }
}
