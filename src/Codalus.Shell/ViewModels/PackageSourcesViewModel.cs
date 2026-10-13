using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Projects.Protocol;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Notifications;
using Codalus.Workbench.Packages;

namespace Codalus.Shell.ViewModels;

public sealed class PackageSourcesDocument : ShellDocument
{
    public const string DocumentId = "nuget:sources";

    public PackageSourcesDocument(PackageSourcesViewModel viewModel)
    {
        Id = DocumentId;
        Title = "Package Sources";
        ViewModel = viewModel;
    }

    public override string Kind => "packageSources";

    public PackageSourcesViewModel ViewModel { get; }
}

/// <summary>A package source being edited, with the package patterns mapped to it (one per line).</summary>
public sealed class PackageSourceRowViewModel(PackageSourceInfo source, IEnumerable<string> patterns, Action changed) : ObservableObject
{
    private string _name = source.Name;
    private string _url = source.Url;
    private bool _enabled = source.Enabled;
    private string _patterns = string.Join("\n", patterns);

    public string OriginalName { get; } = source.Name;

    public bool IsMachineWide { get; } = source.IsMachineWide;

    public bool CanEdit => !IsMachineWide;

    public string? ConfigFile { get; } = source.ConfigFile;

    public string Origin => IsMachineWide ? "machine-wide (read-only)" : ConfigFile ?? "new: saved to the solution's nuget.config hierarchy";

    public string Name
    {
        get => _name;
        set
        {
            if (SetField(ref _name, value ?? string.Empty))
            {
                changed();
            }
        }
    }

    public string Url
    {
        get => _url;
        set
        {
            if (SetField(ref _url, value ?? string.Empty))
            {
                changed();
            }
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetField(ref _enabled, value))
            {
                changed();
            }
        }
    }

    /// <summary>Package source mapping: package id patterns (<c>Contoso.*</c>, <c>*</c>) restored only from this source.</summary>
    public string Patterns
    {
        get => _patterns;
        set
        {
            if (SetField(ref _patterns, value ?? string.Empty))
            {
                changed();
            }
        }
    }

    public string[] PatternList => [.. _patterns.Split(['\n', ';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    public PackageSourceInfo ToInfo() => new(_name.Trim(), _url.Trim(), _enabled, IsMachineWide, ConfigFile);
}

/// <summary>
/// Package Sources (ADR 0012): the effective sources of the solution's <c>NuGet.Config</c> files in priority order,
/// enabled state, additions, removals and package source mapping. Saved through NuGet's own settings API.
/// </summary>
public sealed class PackageSourcesViewModel : ObservableObject
{
    private readonly PackageService _packages;
    private readonly NotificationService _notifications;
    private PackageSourceRowViewModel? _selected;
    private bool _isModified;
    private string? _error;
    private string _configFiles = string.Empty;
    private string _globalPackagesFolder = string.Empty;

    public PackageSourcesViewModel(PackageService packages, NotificationService notifications)
    {
        _packages = packages;
        _notifications = notifications;
        AddCommand = new RelayCommand(Add);
        RemoveCommand = new RelayCommand(Remove, () => _selected is { CanEdit: true });
        MoveUpCommand = new RelayCommand(() => Move(-1), () => _selected is not null && Sources.IndexOf(_selected) > 0);
        MoveDownCommand = new RelayCommand(() => Move(1), () => _selected is not null && Sources.IndexOf(_selected) < Sources.Count - 1);
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => _isModified);
        RevertCommand = new RelayCommand(() => _ = LoadAsync(), () => _isModified);
    }

    public ObservableCollection<PackageSourceRowViewModel> Sources { get; } = [];

    public ICommand AddCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand RevertCommand { get; }

    public PackageSourceRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                RaiseCommands();
            }
        }
    }

    public bool HasSelection => _selected is not null;

    public bool IsModified
    {
        get => _isModified;
        private set
        {
            if (SetField(ref _isModified, value))
            {
                RaiseCommands();
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

    public string ConfigFiles
    {
        get => _configFiles;
        private set => SetField(ref _configFiles, value);
    }

    public string GlobalPackagesFolder
    {
        get => _globalPackagesFolder;
        private set => SetField(ref _globalPackagesFolder, value);
    }

    public async Task LoadAsync()
    {
        try
        {
            var result = await _packages.SourcesAsync(CancellationToken.None).ConfigureAwait(true);
            var selected = _selected?.OriginalName;
            Sources.Clear();
            foreach (var source in result.Sources)
            {
                var patterns = result.Mappings.FirstOrDefault(m => string.Equals(m.Source, source.Name, StringComparison.OrdinalIgnoreCase))?.Patterns ?? [];
                Sources.Add(new PackageSourceRowViewModel(source, patterns, MarkModified));
            }

            ConfigFiles = "Configuration files (most specific first):\n" + string.Join("\n", result.ConfigFiles);
            GlobalPackagesFolder = "Global packages folder: " + result.GlobalPackagesFolder;
            Selected = Sources.FirstOrDefault(s => s.OriginalName == selected) ?? Sources.FirstOrDefault();
            IsModified = false;
            Error = null;
        }
        catch (Exception ex) when (PackageManagerViewModel.IsExpected(ex))
        {
            Error = ex.Message;
        }
    }

    private void Add()
    {
        var name = "Package source";
        for (var i = 2; Sources.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
        {
            name = $"Package source {i}";
        }

        var row = new PackageSourceRowViewModel(new PackageSourceInfo(name, "https://", true), [], MarkModified);
        Sources.Add(row);
        Selected = row;
        MarkModified();
    }

    private void Remove()
    {
        if (_selected is not { CanEdit: true } row)
        {
            return;
        }

        var index = Sources.IndexOf(row);
        Sources.Remove(row);
        Selected = Sources.Count == 0 ? null : Sources[Math.Min(index, Sources.Count - 1)];
        MarkModified();
    }

    private void Move(int delta)
    {
        if (_selected is not { } row)
        {
            return;
        }

        var index = Sources.IndexOf(row);
        var target = index + delta;
        if (target < 0 || target >= Sources.Count)
        {
            return;
        }

        Sources.Move(index, target);
        MarkModified();
        RaiseCommands();
    }

    public async Task SaveAsync()
    {
        try
        {
            var mappings = Sources.Where(s => s.PatternList.Length > 0).Select(s => new PackageSourceMapping(s.Name.Trim(), s.PatternList)).ToArray();
            await _packages.SetSourcesAsync([.. Sources.Select(s => s.ToInfo())], mappings, CancellationToken.None).ConfigureAwait(true);
            _notifications.Info("Package sources saved.");
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (PackageManagerViewModel.IsExpected(ex))
        {
            Error = ex.Message;
        }
    }

    private void MarkModified() => IsModified = true;

    private void RaiseCommands()
    {
        foreach (var command in new[] { RemoveCommand, MoveUpCommand, MoveDownCommand, SaveCommand, RevertCommand })
        {
            ((RelayCommand)command).RaiseCanExecuteChanged();
        }
    }
}

public sealed class DotnetToolsDocument : ShellDocument
{
    public const string DocumentId = "dotnet:tools";

    public DotnetToolsDocument(DotnetToolsViewModel viewModel)
    {
        Id = DocumentId;
        Title = ".NET Tools";
        ViewModel = viewModel;
    }

    public override string Kind => "dotnetTools";

    public DotnetToolsViewModel ViewModel { get; }
}

public sealed record DotnetToolRow(string Id, string Version, string Commands, bool IsGlobal)
{
    public string Scope => IsGlobal ? "global" : "local";
}

/// <summary>
/// .NET tools: the local tool manifest (<c>.config/dotnet-tools.json</c>) and global tools, installed, updated,
/// uninstalled and restored with <c>dotnet tool</c> in the build host.
/// </summary>
public sealed class DotnetToolsViewModel : ObservableObject
{
    private readonly PackageService _packages;
    private readonly DialogViewModel _dialogs;
    private readonly Func<string, Task> _openFile;
    private DotnetToolRow? _selected;
    private string? _manifest;
    private string _output = string.Empty;
    private bool _isBusy;

    public DotnetToolsViewModel(PackageService packages, DialogViewModel dialogs, Func<string, Task> openFile)
    {
        _packages = packages;
        _dialogs = dialogs;
        _openFile = openFile;
        RefreshCommand = new RelayCommand(() => _ = LoadAsync());
        InstallLocalCommand = new RelayCommand(() => _ = InstallAsync(global: false), () => !_isBusy);
        InstallGlobalCommand = new RelayCommand(() => _ = InstallAsync(global: true), () => !_isBusy);
        UpdateCommand = new RelayCommand(() => _ = RunAsync(ToolActions.Update, _selected!.Id, null, _selected.IsGlobal), () => _selected is not null && !_isBusy);
        UninstallCommand = new RelayCommand(() => _ = RunAsync(ToolActions.Uninstall, _selected!.Id, null, _selected.IsGlobal), () => _selected is not null && !_isBusy);
        RestoreCommand = new RelayCommand(() => _ = RunAsync(ToolActions.Restore, null, null, false), () => _manifest is not null && !_isBusy);
        CreateManifestCommand = new RelayCommand(() => _ = RunAsync(ToolActions.CreateManifest, null, null, false), () => _manifest is null && !_isBusy);
        OpenManifestCommand = new RelayCommand(() => _ = _openFile(_manifest!), () => _manifest is not null);
    }

    public ObservableCollection<DotnetToolRow> Tools { get; } = [];

    public ICommand RefreshCommand { get; }

    public ICommand InstallLocalCommand { get; }

    public ICommand InstallGlobalCommand { get; }

    public ICommand UpdateCommand { get; }

    public ICommand UninstallCommand { get; }

    public ICommand RestoreCommand { get; }

    public ICommand CreateManifestCommand { get; }

    public ICommand OpenManifestCommand { get; }

    public DotnetToolRow? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                RaiseCommands();
            }
        }
    }

    public string ManifestText => _manifest is null ? "No local tool manifest (.config/dotnet-tools.json) in the solution folder or above it." : "Manifest: " + _manifest;

    public bool HasManifest => _manifest is not null;

    public string Output
    {
        get => _output;
        private set
        {
            if (SetField(ref _output, value))
            {
                OnPropertyChanged(nameof(HasOutput));
            }
        }
    }

    public bool HasOutput => _output.Length > 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                RaiseCommands();
            }
        }
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var tools = await _packages.ToolsAsync(CancellationToken.None).ConfigureAwait(true);
            _manifest = tools.Manifest;
            Tools.Clear();
            foreach (var tool in tools.Local)
            {
                Tools.Add(new DotnetToolRow(tool.Id, tool.Version, string.Join(", ", tool.Commands), IsGlobal: false));
            }

            foreach (var tool in tools.Global)
            {
                Tools.Add(new DotnetToolRow(tool.Id, tool.Version, string.Join(", ", tool.Commands), IsGlobal: true));
            }

            OnPropertyChanged(nameof(ManifestText));
            OnPropertyChanged(nameof(HasManifest));
        }
        catch (Exception ex) when (PackageManagerViewModel.IsExpected(ex))
        {
            Output = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallAsync(bool global)
    {
        var answer = await _dialogs.ShowAsync(new DialogRequest(
            global ? "Install a Global Tool" : "Install a Local Tool",
            "The tool's package id, optionally with a version (id@1.2.3).",
            ["Install", "Cancel"])
        {
            Input = string.Empty,
            Validate = text => string.IsNullOrWhiteSpace(text) ? "Enter a package id." : null,
        }).ConfigureAwait(true);
        if (answer.Button != "Install" || answer.Input is not { } input)
        {
            return;
        }

        var parts = input.Trim().Split('@', 2, StringSplitOptions.TrimEntries);
        await RunAsync(ToolActions.Install, parts[0], parts.Length > 1 ? parts[1] : null, global).ConfigureAwait(true);
    }

    private async Task RunAsync(string action, string? id, string? version, bool global)
    {
        IsBusy = true;
        Output = $"dotnet tool {action} {id}…";
        try
        {
            var result = await _packages.RunToolCommandAsync(new ToolCommandParams(action, id, version, global), CancellationToken.None).ConfigureAwait(true);
            Output = (result.Succeeded ? string.Empty : "Failed.\n") + result.Output;
        }
        catch (Exception ex) when (PackageManagerViewModel.IsExpected(ex))
        {
            Output = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync().ConfigureAwait(true);
    }

    private void RaiseCommands()
    {
        foreach (var command in new[] { InstallLocalCommand, InstallGlobalCommand, UpdateCommand, UninstallCommand, RestoreCommand, CreateManifestCommand, OpenManifestCommand })
        {
            ((RelayCommand)command).RaiseCanExecuteChanged();
        }
    }
}
