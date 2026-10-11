using System.Collections.ObjectModel;
using System.Windows.Input;
using DaedalusStudio.Shell.Docking;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>A document of M26 that isn't tied to the open solution (the template manager, External Tools).</summary>
public sealed class PageDocument : ShellDocument
{
    public PageDocument(string id, string title, string kind, ObservableObject viewModel)
    {
        Id = id;
        Title = title;
        Kind = kind;
        ViewModel = viewModel;
    }

    public override string Kind { get; }

    public ObservableObject ViewModel { get; }
}

/// <summary>An installed template package in the template manager.</summary>
public sealed class TemplatePackageRowViewModel(TemplatePackage package, string? latest)
{
    public TemplatePackage Package { get; } = package;

    public string Id => Package.Id;

    public string Version => Package.Version ?? "(folder)";

    public string? Latest { get; } = latest;

    public bool HasUpdate => Latest is not null;

    public string Detail => string.Join(" · ", new[] { Package.Author, Package.Source }.Where(s => !string.IsNullOrEmpty(s)));

    public string TemplatesText => string.Join("\n", Package.Templates);

    public string AccessibleName => $"{Id} {Version}" + (HasUpdate ? $", update to {Latest}" : string.Empty);
}

/// <summary>
/// The template manager (ADR 0033): the installed <c>dotnet new</c> template packages, with install (a package id, a
/// <c>.nupkg</c> or a folder), uninstall, the check for updates and Update All.
/// </summary>
public sealed class TemplateManagerViewModel : ObservableObject, ILoadable
{
    private readonly TemplateService _templates;
    private readonly DialogViewModel _dialogs;
    private TemplatePackageRowViewModel? _selected;
    private string _package = string.Empty;
    private string _output = string.Empty;
    private string? _status;
    private bool _isBusy;
    private IReadOnlyList<TemplatePackageUpdate> _updates = [];

    public TemplateManagerViewModel(TemplateService templates, DialogViewModel dialogs)
    {
        _templates = templates;
        _dialogs = dialogs;
        InstallCommand = new RelayCommand(() => _ = InstallAsync(), () => !_isBusy && _package.Trim().Length > 0);
        UninstallCommand = new RelayCommand(() => _ = UninstallAsync(), () => !_isBusy && _selected is not null);
        CheckUpdatesCommand = new RelayCommand(() => _ = CheckUpdatesAsync(), () => !_isBusy);
        UpdateAllCommand = new RelayCommand(() => _ = UpdateAllAsync(), () => !_isBusy && _updates.Count > 0);
        RefreshCommand = new RelayCommand(() => _ = LoadAsync(), () => !_isBusy);
    }

    public ObservableCollection<TemplatePackageRowViewModel> Packages { get; } = [];

    public ICommand InstallCommand { get; }

    public ICommand UninstallCommand { get; }

    public ICommand CheckUpdatesCommand { get; }

    public ICommand UpdateAllCommand { get; }

    public ICommand RefreshCommand { get; }

    public TemplatePackageRowViewModel? Selected
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

    /// <summary>What to install: <c>Id</c>, <c>Id@version</c>, a <c>.nupkg</c> or a template folder.</summary>
    public string Package
    {
        get => _package;
        set
        {
            if (SetField(ref _package, value ?? string.Empty))
            {
                RaiseCommands();
            }
        }
    }

    /// <summary>The last <c>dotnet new</c> command's output.</summary>
    public string Output
    {
        get => _output;
        private set => SetField(ref _output, value);
    }

    public string? Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

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

    public async Task LoadAsync() => await RunAsync(async () =>
    {
        var packages = await _templates.PackagesAsync(CancellationToken.None).ConfigureAwait(true);
        var selected = _selected?.Id;
        Packages.Clear();
        foreach (var package in packages.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            Packages.Add(new TemplatePackageRowViewModel(package, _updates.FirstOrDefault(u => u.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase))?.Latest));
        }

        Selected = Packages.FirstOrDefault(p => p.Id == selected) ?? Packages.FirstOrDefault();
        Status = Packages.Count == 1 ? "1 template package installed." : $"{Packages.Count} template packages installed.";
    }).ConfigureAwait(true);

    public Task InstallAsync() => ChangeAsync(() => _templates.InstallAsync(_package, CancellationToken.None), $"Installed {_package.Trim()}.", clearPackage: true);

    public async Task UninstallAsync()
    {
        if (_selected is { } package && await _dialogs.ConfirmAsync("Uninstall Template Package", $"Uninstall {package.Id} and its templates?", "Uninstall", danger: true).ConfigureAwait(true))
        {
            await ChangeAsync(() => _templates.UninstallAsync(package.Id, CancellationToken.None), $"Uninstalled {package.Id}.").ConfigureAwait(true);
        }
    }

    public async Task CheckUpdatesAsync()
    {
        await RunAsync(async () => _updates = await _templates.CheckUpdatesAsync(CancellationToken.None).ConfigureAwait(true)).ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
        Status = _updates.Count == 0 ? "Every template package is up to date." : $"{_updates.Count} update(s) available.";
    }

    public Task UpdateAllAsync() => ChangeAsync(() => _templates.UpdateAllAsync(CancellationToken.None), "Updated the template packages.", afterwards: () => _updates = []);

    private async Task ChangeAsync(Func<Task<(bool Succeeded, string Output)>> change, string success, bool clearPackage = false, Action? afterwards = null)
    {
        var succeeded = false;
        await RunAsync(async () =>
        {
            var (ok, output) = await change().ConfigureAwait(true);
            Output = output.Trim();
            succeeded = ok;
        }).ConfigureAwait(true);
        if (succeeded)
        {
            afterwards?.Invoke();
            if (clearPackage)
            {
                Package = string.Empty;
            }
        }

        await LoadAsync().ConfigureAwait(true);
        Status = succeeded ? success : "dotnet new failed: see its output below.";
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseCommands()
    {
        foreach (var command in new[] { InstallCommand, UninstallCommand, CheckUpdatesCommand, UpdateAllCommand, RefreshCommand })
        {
            ((RelayCommand)command).RaiseCanExecuteChanged();
        }
    }
}

/// <summary>
/// Export Template (ADR 0033): a project, or the selected item, made into a <c>dotnet new</c> template in the user's
/// templates folder, and installed.
/// </summary>
public sealed class ExportTemplateViewModel : ObservableObject
{
    public const string ProjectKind = "project";
    public const string ItemKind = "item";

    private readonly TemplateService _templates;
    private readonly string _templatesFolder;
    private string _kind;
    private SolutionProjectChoice? _project;
    private string _name;
    private string _shortName;
    private string _description = string.Empty;
    private string _author = Environment.UserName;
    private string? _status;
    private string _output = string.Empty;
    private bool _isBusy;

    public ExportTemplateViewModel(TemplateService templates, string templatesFolder, IReadOnlyList<SolutionProjectChoice> projects, string? project, string? item)
    {
        _templates = templates;
        _templatesFolder = templatesFolder;
        Projects = [.. projects];
        Item = item;
        _kind = item is not null ? ItemKind : ProjectKind;
        _project = Projects.FirstOrDefault(p => project is not null && Workbench.Projects.SolutionService.PathComparer.Equals(p.Path, project)) ?? (Projects.Count > 0 ? Projects[0] : null);
        _name = DefaultName();
        _shortName = ShortNameFor(_name);
        ExportCommand = new RelayCommand(() => _ = ExportAsync(), () => !_isBusy && Error is null);
    }

    public IReadOnlyList<string> Kinds { get; } = [ProjectKind, ItemKind];

    public IReadOnlyList<SolutionProjectChoice> Projects { get; }

    /// <summary>The item to export (the Solution Explorer's selected file), if any.</summary>
    public string? Item { get; }

    public string ItemDisplay => Item is null ? "(select a file in the Solution Explorer first)" : Path.GetFileName(Item);

    public ICommand ExportCommand { get; }

    /// <summary><c>project</c> or <c>item</c>.</summary>
    public string Kind
    {
        get => _kind;
        set
        {
            if (SetField(ref _kind, value ?? ProjectKind))
            {
                OnPropertyChanged(nameof(IsProject));
                OnPropertyChanged(nameof(IsItem));
                Name = DefaultName();
                Changed();
            }
        }
    }

    public bool IsProject => _kind == ProjectKind;

    public bool IsItem => _kind == ItemKind;

    public SolutionProjectChoice? Project
    {
        get => _project;
        set
        {
            if (SetField(ref _project, value))
            {
                Name = DefaultName();
                Changed();
            }
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetField(ref _name, value ?? string.Empty))
            {
                ShortName = ShortNameFor(_name);
                Changed();
            }
        }
    }

    public string ShortName
    {
        get => _shortName;
        set
        {
            if (SetField(ref _shortName, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(Destination));
                Changed();
            }
        }
    }

    public string Description
    {
        get => _description;
        set => SetField(ref _description, value ?? string.Empty);
    }

    public string Author
    {
        get => _author;
        set => SetField(ref _author, value ?? string.Empty);
    }

    public string Destination => Path.Combine(_templatesFolder, _shortName.Trim());

    public string? Error =>
        IsItem && Item is null ? "Select a file in the Solution Explorer to export it as an item template."
        : IsProject && _project is null ? "The solution has no project to export."
        : _name.Trim().Length == 0 ? "Enter a name."
        : _shortName.Trim().Length == 0 || !_shortName.Trim().All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.') ? "The short name may have letters, digits, '-', '_' and '.'."
        : null;

    public bool HasError => Error is not null;

    public string? Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public string Output
    {
        get => _output;
        private set => SetField(ref _output, value);
    }

    public bool Succeeded { get; private set; }

    /// <summary>Exports and installs the template; <see cref="Status"/> says how it went.</summary>
    public async Task ExportAsync()
    {
        if (Error is not null)
        {
            return;
        }

        _isBusy = true;
        ((RelayCommand)ExportCommand).RaiseCanExecuteChanged();
        try
        {
            var source = IsProject ? _project!.Path : Item!;
            var language = ItemTemplateProject.LanguageOf(IsProject ? source : _project?.Path ?? "x.csproj");
            var (ok, output) = await _templates.ExportAsync(new TemplateExportRequest(_kind, source, IsProject ? [] : [source], _name.Trim(), _shortName.Trim(), _description, _author, language, Destination), CancellationToken.None).ConfigureAwait(true);
            Succeeded = ok;
            Output = output.Trim();
            Status = ok ? $"Exported and installed: dotnet new {_shortName.Trim()} (or Add New Item) uses it." : "The template was written, but dotnet new couldn't install it: see the output below.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Status = ex.Message;
        }
        finally
        {
            _isBusy = false;
            ((RelayCommand)ExportCommand).RaiseCanExecuteChanged();
        }
    }

    private string DefaultName() => IsItem && Item is not null ? Path.GetFileNameWithoutExtension(Item) : _project?.Name ?? "Template";

    private static string ShortNameFor(string name) => new([.. name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')]);

    private void Changed()
    {
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
        ((RelayCommand)ExportCommand).RaiseCanExecuteChanged();
    }
}

/// <summary>A project to choose (Export Template).</summary>
public sealed record SolutionProjectChoice(string Name, string Path);
