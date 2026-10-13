using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Codalus.Projects.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>How a row of the Properties window is edited.</summary>
public enum PropertyRowKind
{
    ReadOnly,
    Text,
    Choice,
}

/// <summary>One row of the Properties window: a name, a value and how it is edited.</summary>
public sealed class PropertyRowViewModel : ObservableObject
{
    private readonly Func<string?, Task>? _commit;
    private readonly string _value;
    private string _text;

    public PropertyRowViewModel(string name, string value, PropertyRowKind kind, string description, Func<string?, Task>? commit = null, IReadOnlyList<PropertyOption>? options = null, bool isSet = false)
    {
        Name = name;
        _value = value;
        _text = value;
        Kind = commit is null ? PropertyRowKind.ReadOnly : kind;
        Description = description;
        _commit = commit;
        IsSet = isSet;
        Options = [.. options ?? []];
        if (Kind == PropertyRowKind.Choice && !Options.Any(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase)))
        {
            Options.Add(new PropertyOption(value, value.Length == 0 ? "(none)" : value));
        }
    }

    public string Name { get; }

    public string Description { get; }

    public PropertyRowKind Kind { get; }

    public bool IsReadOnly => Kind == PropertyRowKind.ReadOnly;

    public bool IsText => Kind == PropertyRowKind.Text;

    public bool IsChoice => Kind == PropertyRowKind.Choice;

    /// <summary>Set in the project file (shown in bold, as a classic IDE's property grid does for non-default values).</summary>
    public bool IsSet { get; }

    public string Value => _value;

    public List<PropertyOption> Options { get; }

    public string AccessibleName => $"{Name}: {(Kind == PropertyRowKind.Choice ? SelectedOption?.Label : _value)}";

    /// <summary>The text editor's value, committed when the box loses focus.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (SetField(ref _text, value ?? string.Empty) && _text != _value && _commit is not null)
            {
                _ = _commit(_text);
            }
        }
    }

    public PropertyOption? SelectedOption
    {
        get => Options.FirstOrDefault(o => string.Equals(o.Value, _value, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null && !string.Equals(value.Value, _value, StringComparison.OrdinalIgnoreCase) && _commit is not null)
            {
                _ = _commit(value.Value);
            }
        }
    }
}

public sealed record PropertyCategoryViewModel(string Title, IReadOnlyList<PropertyRowViewModel> Rows);

/// <summary>A command shown under the grid (Project Properties, Configuration Manager, Include In Project…).</summary>
public sealed record PropertyLinkViewModel(string Title, ICommand Command);

/// <summary>
/// The Properties window (F4, ADR 0033): the Solution Explorer's selection as a property grid. A file shows its Build
/// Action, Copy to Output Directory, Custom Tool and its namespace, its link and every metadata the project file sets,
/// all editable through the build host (<c>items/*</c>); folders, projects, the solution and dependencies show a summary
/// with links to the pages that edit them. Plugins add rows through <c>contributes.propertyProviders</c>.
/// </summary>
public sealed class PropertiesWindowViewModel : ObservableObject, IDisposable
{
    public const string ToolWindowId = "properties";

    /// <summary>Copy to Output Directory's choices: MSBuild's values and the labels a classic IDE shows.</summary>
    public static IReadOnlyList<PropertyOption> CopyToOutputOptions { get; } =
    [
        new("Never", "Do not copy"),
        new("Always", "Copy always"),
        new("PreserveNewest", "Copy if newer"),
        new("IfDifferent", "Copy if different"),
    ];

    private readonly ShellHost _shell;
    private CancellationTokenSource _load = new();
    private string _title = "Properties";
    private string? _message = "Select an item in the Solution Explorer.";
    private bool _isLoading;
    private PropertyRowViewModel? _selectedRow;
    private ExplorerNode? _node;
    private bool _refreshQueued;

    public PropertiesWindowViewModel(ShellHost shell)
    {
        _shell = shell;
        shell.Explorer.PropertyChanged += OnExplorerChanged;
        shell.ProjectItems.Changed += (_, _) => QueueRefresh();
        shell.Solutions.ProjectChanged += (_, project) =>
        {
            if (_node?.ProjectPath is { } path && SolutionService.PathComparer.Equals(path, project.Path))
            {
                QueueRefresh();
            }
        };
        AddMetadataCommand = new RelayCommand(() => _ = AddMetadataAsync(), () => CanAddMetadata);
    }

    public ObservableCollection<PropertyCategoryViewModel> Categories { get; } = [];

    public ObservableCollection<PropertyLinkViewModel> Links { get; } = [];

    public ICommand AddMetadataCommand { get; }

    /// <summary>What is shown: "Program.cs File Properties", "App Project Properties"…</summary>
    public string Title
    {
        get => _title;
        private set => SetField(ref _title, value);
    }

    /// <summary>Shown instead of the grid (nothing selected, an error).</summary>
    public string? Message
    {
        get => _message;
        private set
        {
            if (SetField(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => _message is not null;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    public PropertyRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetField(ref _selectedRow, value))
            {
                OnPropertyChanged(nameof(Description));
                OnPropertyChanged(nameof(DescriptionTitle));
            }
        }
    }

    public string DescriptionTitle => _selectedRow?.Name ?? string.Empty;

    public string Description => _selectedRow?.Description ?? string.Empty;

    private bool CanAddMetadata => _node is { Kind: ExplorerNodeKind.File, IsExcluded: false, ProjectPath: not null } && _shell.Solutions.Solution is { Trusted: true };

    private void OnExplorerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SolutionExplorerViewModel.Selected))
        {
            QueueRefresh();
        }
    }

    private void QueueRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        Ui.Post(() =>
        {
            _refreshQueued = false;
            _ = RefreshAsync();
        });
    }

    /// <summary>Shows the Solution Explorer's selection.</summary>
    public async Task RefreshAsync()
    {
        _load.Cancel();
        _load.Dispose();
        _load = new CancellationTokenSource();
        var token = _load.Token;
        _node = _shell.Explorer.Selected?.Node;
        ((RelayCommand)AddMetadataCommand).RaiseCanExecuteChanged();
        if (_node is not { } node)
        {
            Show("Properties", [], [], "Select an item in the Solution Explorer.");
            return;
        }

        IsLoading = true;
        try
        {
            var (title, categories, links) = await DescribeAsync(node, token).ConfigureAwait(true);
            if (!token.IsCancellationRequested)
            {
                categories.AddRange(await _shell.PluginPropertyRowsAsync(node, token).ConfigureAwait(true));
                Show(title, categories, links, null);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (PackageManagerViewModel.IsExpected(ex))
        {
            if (!token.IsCancellationRequested)
            {
                Show(node.Name, [], [], ex.Message);
            }
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    private void Show(string title, IReadOnlyList<PropertyCategoryViewModel> categories, IReadOnlyList<PropertyLinkViewModel> links, string? message)
    {
        var selected = _selectedRow?.Name;
        Title = title;
        Categories.Clear();
        foreach (var category in categories.Where(c => c.Rows.Count > 0))
        {
            Categories.Add(category);
        }

        Links.Clear();
        foreach (var link in links)
        {
            Links.Add(link);
        }

        Message = message ?? (Categories.Count == 0 ? "No properties." : null);
        SelectedRow = Categories.SelectMany(c => c.Rows).FirstOrDefault(r => r.Name == selected);
    }

    private async Task<(string Title, List<PropertyCategoryViewModel> Categories, List<PropertyLinkViewModel> Links)> DescribeAsync(ExplorerNode node, CancellationToken cancellationToken)
    {
        var solution = _shell.Solutions.Solution;
        var trusted = solution?.Trusted == true;
        var links = new List<PropertyLinkViewModel>();
        switch (node)
        {
            case { Kind: ExplorerNodeKind.File, Path: { } path, ProjectPath: { } project } when trusted && !node.IsExcluded && _shell.Solutions.Projects.ContainsKey(project) && !SolutionTree.IsProjectFile(path):
                return (node.Name + " File Properties", await FileAsync(project, path, node, cancellationToken).ConfigureAwait(true), links);

            case { Kind: ExplorerNodeKind.File or ExplorerNodeKind.Folder, Path: { } path }:
            {
                var rows = new List<PropertyRowViewModel>
                {
                    new(node.Kind == ExplorerNodeKind.Folder ? "Folder Name" : "File Name", node.Name, PropertyRowKind.Text, "The name on disk.", node.IsLinked ? null : value => RenameAsync(path, value)),
                    new("Full Path", path, PropertyRowKind.ReadOnly, "Where it is on disk."),
                };
                if (node.IsExcluded && node.ProjectPath is { } owner)
                {
                    rows.Add(new PropertyRowViewModel("Build Action", "(not in the project)", PropertyRowKind.ReadOnly, "Use Include In Project to make it part of the project."));
                    links.Add(new PropertyLinkViewModel("Include In Project", new RelayCommand(() => _ = _shell.IncludeInProjectAsync(owner, [path]))));
                }

                if (SolutionTree.IsProjectFile(path))
                {
                    links.Add(new PropertyLinkViewModel("Open Project Properties", new RelayCommand(() => _shell.OpenProjectProperties(path))));
                }

                return ($"{node.Name} {(node.Kind == ExplorerNodeKind.Folder ? "Folder" : "File")} Properties", [new PropertyCategoryViewModel("Misc", rows)], links);
            }

            case { Kind: ExplorerNodeKind.Project, Path: { } project }:
                links.Add(new PropertyLinkViewModel("Open Project Properties (Alt+Enter)", new RelayCommand(() => _shell.OpenProjectProperties(project))));
                return (node.Name + " Project Properties", await ProjectAsync(project, trusted, cancellationToken).ConfigureAwait(true), links);

            case { Kind: ExplorerNodeKind.Solution } when solution is not null:
                if (!solution.IsFolder && solution.Format != SolutionFormats.Project)
                {
                    links.Add(new PropertyLinkViewModel("Configuration Manager…", new RelayCommand(() => _shell.OpenConfigurationManager())));
                }

                return (solution.IsFolder ? "Folder Properties" : "Solution Properties", [new PropertyCategoryViewModel("Misc",
                [
                    new(solution.IsFolder ? "Folder Name" : "Name", solution.Name, PropertyRowKind.ReadOnly, solution.IsFolder ? "The folder opened without a solution." : "The solution's name."),
                    new("Path", solution.Path, PropertyRowKind.ReadOnly, solution.IsFolder ? "The folder." : "The solution file."),
                    new("Format", solution.Format, PropertyRowKind.ReadOnly, "sln, slnx, a project opened on its own, or a folder."),
                    new(solution.IsFolder ? "Loaded Projects" : "Projects", solution.Projects.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), PropertyRowKind.ReadOnly, "How many projects it has."),
                    new("Active Configuration", solution.Active?.Name ?? string.Empty, PropertyRowKind.ReadOnly, "The configuration and platform builds use."),
                    new("Startup Project", _shell.Runs.StartupProject is { } startup ? System.IO.Path.GetFileNameWithoutExtension(startup) : "(none)", PropertyRowKind.ReadOnly, "The project Start and Start Without Debugging run."),
                    new("Trusted", trusted ? "Yes" : "No (restricted mode)", PropertyRowKind.ReadOnly, "Whether projects are evaluated and built."),
                ])], links);

            case { Kind: ExplorerNodeKind.Dependency }:
                return (node.Name + " Properties", [new PropertyCategoryViewModel("Misc",
                [
                    new("Name", node.Name, PropertyRowKind.ReadOnly, "The dependency's name."),
                    new("Version", node.Detail?.Trim('(', ')') ?? string.Empty, PropertyRowKind.ReadOnly, "The resolved version (the requested one when not restored)."),
                    new("Path", node.Path ?? string.Empty, PropertyRowKind.ReadOnly, "The referenced file, when there is one."),
                    new("Resolved", node.IsWarning ? "No" : "Yes", PropertyRowKind.ReadOnly, "Whether it was found."),
                ])], links);

            case { Kind: ExplorerNodeKind.SolutionItem, Path: { } path }:
                return (node.Name + " Properties", [new PropertyCategoryViewModel("Misc",
                [
                    new("File Name", node.Name, PropertyRowKind.ReadOnly, "A solution item: a file of the solution, not of a project."),
                    new("Full Path", path, PropertyRowKind.ReadOnly, "Where it is on disk."),
                ])], links);

            default:
                return (node.Name + " Properties", [new PropertyCategoryViewModel("Misc", [new("Name", node.Name, PropertyRowKind.ReadOnly, string.Empty)])], links);
        }
    }

    private async Task<List<PropertyCategoryViewModel>> FileAsync(string project, string path, ExplorerNode node, CancellationToken cancellationToken)
    {
        var info = await _shell.ProjectItems.GetAsync(project, path, cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        string Metadata(string name) => info.Metadata.FirstOrDefault(m => m.Name == name)?.Value ?? string.Empty;
        bool IsSet(string name) => info.Metadata.FirstOrDefault(m => m.Name == name)?.ProjectValue is not null;
        Func<string?, Task> SetMetadata(string name) => value => SetAsync(project, path, null, name, string.IsNullOrWhiteSpace(value) ? null : value.Trim());

        var copy = Metadata(ItemMetadataNames.CopyToOutputDirectory);
        var advanced = new List<PropertyRowViewModel>
        {
            new("Build Action", info.ItemType ?? "(not in the project)", PropertyRowKind.Choice,
                "How the file is used in the build: compiled, embedded in the assembly, content copied with the app, or nothing (None).",
                value => SetAsync(project, path, value, null, null), [.. info.ItemTypes.Select(t => new PropertyOption(t, t))], info.Elements.Any(e => e.Operation != ItemOperations.Update && e.ItemType == info.ItemType)),
            new("Copy to Output Directory", copy.Length == 0 ? "Never" : copy, PropertyRowKind.Choice,
                "Whether the build copies the file to the output folder: never, always, or when it is newer or different.",
                SetMetadata(ItemMetadataNames.CopyToOutputDirectory), CopyToOutputOptions, IsSet(ItemMetadataNames.CopyToOutputDirectory)),
            new("Custom Tool", Metadata(ItemMetadataNames.Generator), PropertyRowKind.Text,
                "The generator that makes a file from this one (TextTemplatingFileGenerator, ResXFileCodeGenerator…).",
                SetMetadata(ItemMetadataNames.Generator), isSet: IsSet(ItemMetadataNames.Generator)),
            new("Custom Tool Namespace", Metadata(ItemMetadataNames.CustomToolNamespace), PropertyRowKind.Text,
                "The namespace the custom tool's generated code uses (default: the folder's namespace).",
                SetMetadata(ItemMetadataNames.CustomToolNamespace), isSet: IsSet(ItemMetadataNames.CustomToolNamespace)),
        };
        var misc = new List<PropertyRowViewModel>
        {
            new("File Name", node.Name, PropertyRowKind.Text, "The file's name on disk.", info.IsLinked ? null : value => RenameAsync(path, value)),
            new("Full Path", path, PropertyRowKind.ReadOnly, "Where the file is on disk."),
        };
        if (info.IsLinked)
        {
            misc.Add(new PropertyRowViewModel("Link", info.Link ?? string.Empty, PropertyRowKind.Text, "Where the linked file shows in the project.", SetMetadata(ItemMetadataNames.Link), isSet: true));
        }

        if (info.FromGlob)
        {
            misc.Add(new PropertyRowViewModel("Included By", "A wildcard (default items)", PropertyRowKind.ReadOnly, "The project's globs include the file; its metadata is written as an Update item."));
        }

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ItemMetadataNames.CopyToOutputDirectory, ItemMetadataNames.Generator, ItemMetadataNames.CustomToolNamespace, ItemMetadataNames.Link };
        var metadata = info.Metadata.Where(m => !known.Contains(m.Name))
            .Select(m => new PropertyRowViewModel(m.Name, m.ProjectValue ?? m.Value, PropertyRowKind.Text, $"The item's {m.Name} metadata.", SetMetadata(m.Name), isSet: m.ProjectValue is not null))
            .ToList();
        return [new PropertyCategoryViewModel("Advanced", advanced), new PropertyCategoryViewModel("Misc", misc), new PropertyCategoryViewModel("Metadata", metadata)];
    }

    private async Task<List<PropertyCategoryViewModel>> ProjectAsync(string project, bool trusted, CancellationToken cancellationToken)
    {
        var rows = new List<PropertyRowViewModel>
        {
            new("Project File", System.IO.Path.GetFileName(project), PropertyRowKind.ReadOnly, "The project file."),
            new("Project Folder", System.IO.Path.GetDirectoryName(project)!, PropertyRowKind.ReadOnly, "The project's folder."),
        };
        if (_shell.Solutions.Projects.GetValueOrDefault(project) is { } info)
        {
            rows.Add(new PropertyRowViewModel("State", info.State, PropertyRowKind.ReadOnly, "loaded, unloaded, failed…"));
        }

        if (trusted && _shell.Solutions.Projects.GetValueOrDefault(project) is { State: ProjectStates.Loaded })
        {
            var properties = await _shell.ProjectManagement.GetPropertiesAsync(project, ["TargetFramework", "TargetFrameworks", "OutputType", "AssemblyName", "RootNamespace", "Version"], null, cancellationToken).ConfigureAwait(true);
            string Value(string name) => properties.Properties.FirstOrDefault(p => p.Name == name)?.Value ?? string.Empty;
            rows.Add(new PropertyRowViewModel("Target Framework", Value("TargetFrameworks") is { Length: > 0 } many ? many : Value("TargetFramework"), PropertyRowKind.ReadOnly, "The frameworks the project builds for."));
            rows.Add(new PropertyRowViewModel("Output Type", Value("OutputType"), PropertyRowKind.ReadOnly, "What the project builds."));
            rows.Add(new PropertyRowViewModel("Assembly Name", Value("AssemblyName"), PropertyRowKind.ReadOnly, "The output assembly's name."));
            rows.Add(new PropertyRowViewModel("Default Namespace", Value("RootNamespace"), PropertyRowKind.ReadOnly, "The namespace of new files."));
            rows.Add(new PropertyRowViewModel("Version", Value("Version"), PropertyRowKind.ReadOnly, "The assembly and package version."));
        }

        return [new PropertyCategoryViewModel("Misc", rows)];
    }

    private async Task SetAsync(string project, string path, string? itemType, string? metadata, string? value)
    {
        try
        {
            await _shell.ProjectItems.SetAsync(project, path, itemType, metadata is null ? null : new Dictionary<string, string?> { [metadata] = value }, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (PackageManagerViewModel.IsExpected(ex))
        {
            _shell.Workbench.Notifications.Error(ex.Message);
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task RenameAsync(string path, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            await RefreshAsync().ConfigureAwait(true);
            return;
        }

        try
        {
            await _shell.Solutions.RenameAsync(path, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, name.Trim()), CancellationToken.None).ConfigureAwait(true);
            _shell.Explorer.RevealWhenAvailable("f:" + System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, name.Trim()));
        }
        catch (Exception ex) when (PackageManagerViewModel.IsExpected(ex))
        {
            _shell.Workbench.Notifications.Error(ex.Message);
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    public void Dispose()
    {
        _load.Cancel();
        _load.Dispose();
    }

    /// <summary>Add Metadata: a name and a value for the selected file's item.</summary>
    public async Task AddMetadataAsync()
    {
        if (_node is not { Kind: ExplorerNodeKind.File, Path: { } path, ProjectPath: { } project })
        {
            return;
        }

        var name = await _shell.Dialogs.InputAsync(Localizer.Get("msg.propertiesWindowViewModel.addMetadata", "Add Metadata"), Localizer.Format("msg.propertiesWindowViewModel.theMetadatasNameFor", "The metadata's name for {0}.", System.IO.Path.GetFileName(path)), string.Empty,
            n => n.Length == 0 || !n.All(c => char.IsLetterOrDigit(c) || c is '_' or '-') || !char.IsLetter(n[0]) ? "Use letters, digits, '_' and '-', starting with a letter." : null).ConfigureAwait(true);
        if (name is null)
        {
            return;
        }

        var value = await _shell.Dialogs.InputAsync(Localizer.Get("msg.propertiesWindowViewModel.addMetadata", "Add Metadata"), Localizer.Format("msg.propertiesWindowViewModel.theValueOf", "The value of {0}.", name), string.Empty).ConfigureAwait(true);
        if (value is not null)
        {
            await SetAsync(project, path, null, name, value).ConfigureAwait(true);
        }
    }
}
