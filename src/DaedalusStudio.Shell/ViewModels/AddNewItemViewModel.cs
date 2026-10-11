using System.Collections.ObjectModel;
using System.Windows.Input;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>A template in Add New Item's list.</summary>
public sealed class ItemTemplateRowViewModel(ItemTemplate template)
{
    public ItemTemplate Template { get; } = template;

    public string Name => Template.Name;

    public string Description => Template.Description;

    public string Category => Template.Category;

    public string Detail => Template.Source == ItemTemplateSource.BuiltIn
        ? "Built in" + (Template.Languages.Count > 0 ? " · " + string.Join(", ", Template.Languages) : string.Empty)
        : "dotnet new " + Template.ShortName + (Template.Languages.Count > 0 ? " · " + string.Join(", ", Template.Languages) : string.Empty);

    public string Icon => Template.Extension switch
    {
        ".cs" or ".vb" or ".fs" or ".js" or ".ts" or ".html" or ".css" => "file-code",
        ".json" or ".xml" or ".resx" => "file-data",
        _ => Template.Source == ItemTemplateSource.DotnetNew ? "package" : "file",
    };

    public string AccessibleName => $"{Name}, {Detail}";
}

/// <summary>
/// Add New Item (ADR 0033): the item templates for a project's language and kind (the built-in set and <c>dotnet new</c>'s),
/// with search, categories and a preview. A built-in template is generated here and created through the build host's
/// file operations; a <c>dotnet new</c> template is run by the build host (trusted solutions only).
/// </summary>
public sealed class AddNewItemViewModel : ObservableObject, ILoadable
{
    public const string AllCategories = "All";

    private readonly ShellHost _shell;
    private readonly List<ItemTemplate> _all = [];
    private ItemTemplateProject _kind;
    private string _rootNamespace;
    private bool _fileScoped = true;
    private string _search = string.Empty;
    private string _category = AllCategories;
    private bool _showAll;
    private ItemTemplateRowViewModel? _selected;
    private string _name = string.Empty;
    private bool _nameEdited;
    private string? _error;
    private bool _isLoading;
    private bool _isAdding;

    public AddNewItemViewModel(ShellHost shell, string project, string folder)
    {
        _shell = shell;
        Project = project;
        Folder = folder;
        _kind = new ItemTemplateProject(ItemTemplateProject.LanguageOf(project));
        _rootNamespace = Path.GetFileNameWithoutExtension(project);
        AddCommand = new RelayCommand(() => _ = AddAsync(), () => CanAdd);
        CancelCommand = new RelayCommand(() => Closed?.Invoke(this, EventArgs.Empty));
        Categories.Add(AllCategories);
        _all.AddRange(ItemTemplates.BuiltIn);
        ApplyFilter();
    }

    public string Project { get; }

    /// <summary>The folder the new item goes into.</summary>
    public string Folder { get; }

    public string Title => "Add New Item: " + Path.GetFileNameWithoutExtension(Project);

    public string Location => Path.GetRelativePath(Path.GetDirectoryName(Project)!, Folder) is "." ? Path.GetFileNameWithoutExtension(Project) : Path.GetFileNameWithoutExtension(Project) + Path.DirectorySeparatorChar + Path.GetRelativePath(Path.GetDirectoryName(Project)!, Folder);

    public ObservableCollection<string> Categories { get; } = [];

    public ObservableCollection<ItemTemplateRowViewModel> Templates { get; } = [];

    public ICommand AddCommand { get; }

    public ICommand CancelCommand { get; }

    /// <summary>The document should close (Add finished, or Cancel).</summary>
    public event EventHandler? Closed;

    /// <summary>The file a successful Add created (and opened).</summary>
    public string? Created { get; private set; }

    public string Search
    {
        get => _search;
        set
        {
            if (SetField(ref _search, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    public string SelectedCategory
    {
        get => _category;
        set
        {
            if (SetField(ref _category, value ?? AllCategories))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>Lifts the filter by project kind (Web templates in a class library…).</summary>
    public bool ShowAll
    {
        get => _showAll;
        set
        {
            if (SetField(ref _showAll, value))
            {
                ApplyFilter();
            }
        }
    }

    public ItemTemplateRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                if (!_nameEdited && value is not null)
                {
                    _name = Unique(value.Template.FileName(value.Template.DefaultName));
                    OnPropertyChanged(nameof(Name));
                }

                OnPropertyChanged(nameof(Preview));
                OnPropertyChanged(nameof(PreviewTitle));
                Validate();
            }
        }
    }

    /// <summary>The new file's name (the template's extension is added when it isn't typed).</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (SetField(ref _name, value ?? string.Empty))
            {
                _nameEdited = true;
                OnPropertyChanged(nameof(Preview));
                Validate();
            }
        }
    }

    public string PreviewTitle => _selected?.Name ?? string.Empty;

    /// <summary>What the item will be: a built-in template's content, or what <c>dotnet new</c> will be asked to do.</summary>
    public string Preview
    {
        get
        {
            if (_selected?.Template is not { } template)
            {
                return string.Empty;
            }

            if (template.Content is { } content)
            {
                var file = template.FileName(_name.Trim().Length > 0 ? _name.Trim() : template.DefaultName);
                return content(new ItemTemplateContext(Path.GetFileNameWithoutExtension(file), Namespace, _fileScoped));
            }

            return $"{template.Description}\n\nRuns: dotnet new {template.ShortName} --name {Path.GetFileNameWithoutExtension(_name.Trim())} --output {Location}\n\n"
                   + $"Languages: {(template.Languages.Count > 0 ? string.Join(", ", template.Languages) : "any")}\nTags: {string.Join(", ", template.Tags)}";
        }
    }

    private string Namespace => ItemTemplates.NamespaceFor(_rootNamespace, Path.GetDirectoryName(Project)!, Folder);

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
                ((RelayCommand)AddCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasError => _error is not null;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    private bool CanAdd => _selected is not null && _error is null && _name.Trim().Length > 0 && !_isAdding;

    /// <summary>The project's kind (from its properties) and the <c>dotnet new</c> templates, which arrive after the built-in ones.</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            if (_shell.Solutions.Solution is { Trusted: true } && _shell.Solutions.Projects.GetValueOrDefault(Project) is { State: ProjectStates.Loaded } info)
            {
                try
                {
                    var properties = await _shell.ProjectManagement.GetPropertiesAsync(Project, ["UsingMicrosoftNETSdkWeb", "UsingMicrosoftNETSdkRazor", "UseMaui", "UseWPF", "UseWindowsForms", "IsTestProject", "RootNamespace"], null, CancellationToken.None).ConfigureAwait(true);
                    bool True(string name) => string.Equals(properties.Properties.FirstOrDefault(p => p.Name == name)?.Value, "true", StringComparison.OrdinalIgnoreCase);
                    var frameworks = info.TargetFrameworks;
                    _kind = new ItemTemplateProject(
                        ItemTemplateProject.LanguageOf(Project),
                        IsWeb: True("UsingMicrosoftNETSdkWeb") || True("UsingMicrosoftNETSdkRazor"),
                        IsMaui: True("UseMaui"),
                        IsTest: True("IsTestProject") || info.IsTestProject,
                        IsWpf: True("UseWPF"),
                        IsWinForms: True("UseWindowsForms"),
                        IsMobile: frameworks.Any(f => f.Contains("-android", StringComparison.OrdinalIgnoreCase) || f.Contains("-ios", StringComparison.OrdinalIgnoreCase) || f.Contains("-maccatalyst", StringComparison.OrdinalIgnoreCase)));
                    _rootNamespace = properties.Properties.FirstOrDefault(p => p.Name == "RootNamespace")?.Value is { Length: > 0 } ns ? ns : _rootNamespace;
                    _fileScoped = ItemTemplates.SupportsFileScopedNamespaces(frameworks);
                }
                catch (Exception ex) when (PackageManagerViewModel.IsExpected(ex))
                {
                    // The built-in templates work without the project's kind.
                }
            }

            ApplyFilter();
            var dotnetNew = await _shell.Templates.ItemTemplatesAsync(CancellationToken.None).ConfigureAwait(true);
            _all.Clear();
            _all.AddRange(ItemTemplates.Merge(dotnetNew));
            ApplyFilter();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        var selected = _selected?.Template.Id;
        var search = _search.Trim();
        var visible = _all.Where(t => _showAll || ItemTemplates.AppliesTo(t, _kind)).ToList();
        var categories = visible.Select(t => t.Category).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        foreach (var stale in Categories.Skip(1).Where(c => !categories.Contains(c)).ToList())
        {
            Categories.Remove(stale);
        }

        foreach (var category in categories.Where(c => !Categories.Contains(c)))
        {
            Categories.Add(category);
        }

        if (!Categories.Contains(_category))
        {
            _category = AllCategories;
            OnPropertyChanged(nameof(SelectedCategory));
        }

        Templates.Clear();
        foreach (var template in visible.Where(t => (_category == AllCategories || t.Category == _category)
                                                    && (search.Length == 0 || t.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || t.ShortName.Contains(search, StringComparison.OrdinalIgnoreCase) || t.Description.Contains(search, StringComparison.OrdinalIgnoreCase))))
        {
            Templates.Add(new ItemTemplateRowViewModel(template));
        }

        Selected = Templates.FirstOrDefault(t => t.Template.Id == selected) ?? Templates.FirstOrDefault();
    }

    /// <summary>A default name that isn't taken in the folder (Class1.cs, Class2.cs…).</summary>
    private string Unique(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        var extension = Path.GetExtension(fileName);
        var candidate = fileName;
        for (var i = 2; File.Exists(Path.Combine(Folder, candidate)); i++)
        {
            candidate = stem + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + extension;
        }

        return candidate;
    }

    private void Validate()
    {
        var name = _name.Trim();
        Error = _selected is null ? null
            : name.Length == 0 ? "Enter a name."
            : name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(['/', '\\']) >= 0 ? "The name contains characters that aren't allowed in file names."
            : File.Exists(Path.Combine(Folder, _selected.Template.FileName(name))) ? $"'{_selected.Template.FileName(name)}' already exists here."
            : _selected.Template.Source == ItemTemplateSource.DotnetNew && _shell.Solutions.Solution is not { Trusted: true } ? "dotnet new templates run only in trusted folders."
            : null;
    }

    /// <summary>Creates the item and opens it. Returns the created file, or null (the error says why).</summary>
    public async Task<string?> AddAsync()
    {
        if (_selected?.Template is not { } template || !CanAdd)
        {
            return null;
        }

        _isAdding = true;
        ((RelayCommand)AddCommand).RaiseCanExecuteChanged();
        try
        {
            var fileName = template.FileName(_name.Trim());
            var stem = Path.GetFileNameWithoutExtension(fileName) is { Length: > 0 } baseName ? baseName : fileName.TrimStart('.'); // .editorconfig
            string? created;
            if (template.Content is { } content)
            {
                var path = Path.Combine(Folder, fileName);
                await _shell.Solutions.CreateFileAsync(path, content(new ItemTemplateContext(ItemTemplates.Identifier(stem), Namespace, _fileScoped)), CancellationToken.None).ConfigureAwait(true);
                created = path;
                if ((template.ItemType is not null || template.Metadata is { Count: > 0 }) && _shell.Solutions.Solution is { Trusted: true })
                {
                    var metadata = template.Metadata?.ToDictionary(m => m.Key, m => (string?)m.Value.Replace("{name}", stem, StringComparison.Ordinal));
                    await _shell.ProjectItems.SetAsync(Project, path, template.ItemType, metadata, CancellationToken.None).ConfigureAwait(true);
                }
            }
            else
            {
                var result = await _shell.ProjectItems.InstantiateAsync(new InstantiateTemplateParams(template.ShortName, stem, Folder, Project, template.Languages.Count > 1 ? _kind.Language : null), CancellationToken.None).ConfigureAwait(true);
                created = result.CreatedPaths.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals(stem, StringComparison.OrdinalIgnoreCase)) ?? result.CreatedPaths.FirstOrDefault();
                if (created is null)
                {
                    Error = $"dotnet new {template.ShortName} created no file.";
                    return null;
                }
            }

            Created = created;
            _shell.Explorer.RevealWhenAvailable("f:" + created);
            await _shell.OpenFileAsync(created).ConfigureAwait(true);
            Closed?.Invoke(this, EventArgs.Empty);
            return created;
        }
        catch (Exception ex) when (PackageManagerViewModel.IsExpected(ex))
        {
            Error = ex.Message;
            return null;
        }
        finally
        {
            _isAdding = false;
            ((RelayCommand)AddCommand).RaiseCanExecuteChanged();
        }
    }
}
