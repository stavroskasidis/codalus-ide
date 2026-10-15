using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Projects.Protocol;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Build;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.ViewModels;

public sealed class ProjectPropertiesDocument : ShellDocument
{
    public ProjectPropertiesDocument(ProjectPropertiesViewModel viewModel)
    {
        Id = DocumentIdFor(viewModel.Project);
        Title = viewModel.Title;
        ViewModel = viewModel;
    }

    public override string Kind => "projectProperties";

    public ProjectPropertiesViewModel ViewModel { get; }

    public static string DocumentIdFor(string project) => "properties:" + project;

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

/// <summary>
/// A project's property pages (ADR 0013): schema-driven pages that show each property's value and where it comes
/// from, and write the minimal change for every configuration or one; the Debug page edits launch profiles.
/// </summary>
public sealed class ProjectPropertiesViewModel : ObservableObject, IDisposable
{
    public const string AllConfigurations = "All configurations";

    private static readonly string[] s_frameworkProperties = ["TargetFramework", "TargetFrameworks"];

    private readonly ProjectManagementService _management;
    private readonly SolutionService _solutions;
    private readonly DialogViewModel _dialogs;
    private readonly Func<string, Task> _openFile;
    private readonly EventHandler<ProjectInfo> _onProjectChanged;
    private PropertyPage _selectedPage;
    private string _scope = AllConfigurations;
    private string? _error;
    private bool _isLoading;
    private CancellationTokenSource _load = new();
    private bool _disposed;

    public ProjectPropertiesViewModel(ProjectManagementService management, SolutionService solutions, string project, DialogViewModel dialogs, Func<string, Task> openFile, IReadOnlyList<PropertyPage>? pages = null, BuildDepthService? buildDepth = null)
    {
        _management = management;
        _solutions = solutions;
        _dialogs = dialogs;
        _openFile = openFile;
        Project = project;
        Pages = [.. (pages ?? ProjectPropertyPages.For(project)).Where(p => !p.IsBuildEvents || buildDepth is not null)];
        BuildEvents = buildDepth is null ? null : new BuildEventsViewModel(buildDepth, project);
        _selectedPage = Pages[0];
        Scopes.Add(AllConfigurations);
        LaunchProfiles = new LaunchProfilesViewModel(Path.Combine(Path.GetDirectoryName(project)!, "Properties", "launchSettings.json"), dialogs, openFile);
        OpenProjectFileCommand = new RelayCommand(() => _ = openFile(project));

        // Edits outside the pages (the project file in the editor, a git pull) show up after re-evaluation.
        _onProjectChanged = (_, info) =>
        {
            if (SolutionService.PathComparer.Equals(info.Path, Project))
            {
                Ui.Post(() => _ = LoadAsync());
            }
        };
        solutions.ProjectChanged += _onProjectChanged;
    }

    public string Project { get; }

    public string Title => "Properties: " + Path.GetFileNameWithoutExtension(Project);

    public IReadOnlyList<PropertyPage> Pages { get; }

    public ObservableCollection<string> Scopes { get; } = [];

    public ObservableCollection<PropertySectionViewModel> Sections { get; } = [];

    public LaunchProfilesViewModel LaunchProfiles { get; }

    /// <summary>The Build Events page (null when the page isn't offered).</summary>
    public BuildEventsViewModel? BuildEvents { get; }

    public ICommand OpenProjectFileCommand { get; }

    public PropertyPage SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (value is not null && SetField(ref _selectedPage, value))
            {
                OnPropertyChanged(nameof(IsLaunchProfilesPage));
                OnPropertyChanged(nameof(IsBuildEventsPage));
                OnPropertyChanged(nameof(IsPropertiesPage));
                _ = LoadAsync();
            }
        }
    }

    public bool IsLaunchProfilesPage => _selectedPage.IsLaunchProfiles;

    public bool IsBuildEventsPage => _selectedPage.IsBuildEvents;

    public bool IsPropertiesPage => !_selectedPage.IsLaunchProfiles && !_selectedPage.IsBuildEvents;

    /// <summary>All configurations, or one (Debug, Release…): edits go to that configuration's scope.</summary>
    public string Scope
    {
        get => _scope;
        set
        {
            if (SetField(ref _scope, value ?? AllConfigurations))
            {
                _ = LoadAsync();
            }
        }
    }

    private string? ConfigurationScope => _scope == AllConfigurations ? null : _scope;

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

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    /// <summary>Loads the selected page's values (or the launch profiles).</summary>
    public async Task LoadAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_selectedPage.IsLaunchProfiles)
        {
            await LaunchProfiles.LoadAsync().ConfigureAwait(true);
            return;
        }

        if (_selectedPage.IsBuildEvents)
        {
            if (BuildEvents is { } events)
            {
                await events.LoadAsync().ConfigureAwait(true);
            }

            return;
        }

        _load.Cancel();
        _load.Dispose();
        _load = new CancellationTokenSource();
        var token = _load.Token;
        var page = _selectedPage;
        IsLoading = true;
        try
        {
            var names = page.Properties.SelectMany(p => p.Name == ProjectPropertyPages.TargetFrameworksPseudo ? s_frameworkProperties : [p.Name]).ToList();
            var result = await _management.GetPropertiesAsync(Project, names, ConfigurationScope, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            foreach (var configuration in result.Configurations.Where(c => !Scopes.Contains(c)))
            {
                Scopes.Add(configuration);
            }

            var values = result.Properties.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
            Sections.Clear();
            foreach (var section in page.Sections)
            {
                Sections.Add(new PropertySectionViewModel(section.Title, [.. section.Properties.Select(d => new PropertyItemViewModel(this, d, Describe(d, values)))]));
            }

            Error = null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (PackageManagerViewModel.IsExpected(ex))
        {
            Error = ex.Message;
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>The target frameworks pseudo property combines TargetFramework and TargetFrameworks.</summary>
    private static ProjectPropertyValue Describe(PropertyDefinition definition, Dictionary<string, ProjectPropertyValue> values)
    {
        if (definition.Name != ProjectPropertyPages.TargetFrameworksPseudo)
        {
            return values.GetValueOrDefault(definition.Name) ?? new ProjectPropertyValue(definition.Name, string.Empty, null, PropertyOrigins.Unset, null, 0, null, null);
        }

        var many = values.GetValueOrDefault("TargetFrameworks");
        return many is { Value.Length: > 0 } ? many with { Name = definition.Name } : (values.GetValueOrDefault("TargetFramework") ?? many!) with { Name = definition.Name };
    }

    /// <summary>Writes a property (null or empty: back to the inherited value) and reloads the page.</summary>
    internal async Task SetAsync(PropertyDefinition definition, string? value)
    {
        var scope = definition.PerConfiguration ? ConfigurationScope : null;
        value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        try
        {
            if (definition.Name == ProjectPropertyPages.TargetFrameworksPseudo)
            {
                if (value is null)
                {
                    Error = "A project needs at least one target framework.";
                    return;
                }

                var frameworks = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var (set, clear) = frameworks.Length > 1 ? ("TargetFrameworks", "TargetFramework") : ("TargetFramework", "TargetFrameworks");
                await _management.SetPropertyAsync(Project, set, string.Join(';', frameworks), null, CancellationToken.None).ConfigureAwait(true);
                await _management.SetPropertyAsync(Project, clear, null, null, CancellationToken.None).ConfigureAwait(true);
            }
            else
            {
                await _management.SetPropertyAsync(Project, definition.Name, value, scope, CancellationToken.None).ConfigureAwait(true);
            }

            Error = null;
        }
        catch (Exception ex) when (PackageManagerViewModel.IsExpected(ex))
        {
            Error = ex.Message;
        }

        await LoadAsync().ConfigureAwait(true);
    }

    internal bool CanEditInScope(PropertyDefinition definition) => definition.PerConfiguration || ConfigurationScope is null;

    internal Task OpenFileAsync(string path) => _openFile(path);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _solutions.ProjectChanged -= _onProjectChanged;
        _load.Cancel();
        _load.Dispose();
    }
}

public sealed record PropertySectionViewModel(string Title, IReadOnlyList<PropertyItemViewModel> Items);

/// <summary>One property on a page: its editor, value, provenance and Reset.</summary>
public sealed class PropertyItemViewModel : ObservableObject
{
    private readonly ProjectPropertiesViewModel _owner;
    private readonly ProjectPropertyValue _value;
    private string _text;

    public PropertyItemViewModel(ProjectPropertiesViewModel owner, PropertyDefinition definition, ProjectPropertyValue value)
    {
        _owner = owner;
        Definition = definition;
        _value = value;
        _text = value.Value;
        Options = [.. definition.Options.Select(o => new PropertyOption(o.Value, o.Label))];
        if (definition.Editor == PropertyEditor.Choice && value.Value.Length > 0 && !Options.Any(o => string.Equals(o.Value, value.Value, StringComparison.OrdinalIgnoreCase)))
        {
            Options.Add(new PropertyOption(value.Value, value.Value));
        }

        ResetCommand = new RelayCommand(() => _ = _owner.SetAsync(Definition, null), () => IsSetInProject && CanEdit);
        OpenDefinitionCommand = new RelayCommand(() => _ = _owner.OpenFileAsync(_value.DefinedIn!), () => _value.DefinedIn is not null);
    }

    public PropertyDefinition Definition { get; }

    public string Label => Definition.Label;

    public string Description => Definition.Description;

    public bool HasDescription => Description.Length > 0;

    public string Name => Definition.Name == ProjectPropertyPages.TargetFrameworksPseudo ? "TargetFramework(s)" : Definition.Name;

    public bool IsText => Definition.Editor is PropertyEditor.Text or PropertyEditor.List or PropertyEditor.File;

    public bool IsBool => Definition.Editor == PropertyEditor.Bool;

    public bool IsChoice => Definition.Editor == PropertyEditor.Choice;

    public List<PropertyOption> Options { get; }

    public ICommand ResetCommand { get; }

    public ICommand OpenDefinitionCommand { get; }

    /// <summary>Editable here: per-configuration properties always; the others only for all configurations.</summary>
    public bool CanEdit => _owner.CanEditInScope(Definition);

    public string? ScopeNote => CanEdit ? null : "Applies to all configurations: choose All configurations to change it.";

    public bool IsSetInProject => _value.Origin == PropertyOrigins.Project && (_value.ProjectValue is not null || Definition.Name == ProjectPropertyPages.TargetFrameworksPseudo);

    /// <summary>"Set in App.csproj (line 3)", "Inherited from Directory.Build.props (line 4)", "SDK default", "Not set".</summary>
    public string Origin => _value.Origin switch
    {
        PropertyOrigins.Project when _value.ProjectValue is null && _value.Condition is { } condition => $"Set in {File(_value.DefinedIn)} (line {_value.Line}) when {condition}",
        PropertyOrigins.Project => $"Set in {File(_value.DefinedIn)} (line {_value.Line})" + (_value.Condition is { } condition ? $" when {condition}" : string.Empty),
        PropertyOrigins.Import => $"Inherited from {File(_value.DefinedIn)} (line {_value.Line})" + (_value.Condition is { } condition ? $" when {condition}" : string.Empty),
        PropertyOrigins.Sdk => _value.Value.Length == 0 ? "Not set" : "Default from the SDK",
        PropertyOrigins.Global => "Set for the build (global property)",
        _ => "Not set",
    };

    public bool IsInherited => _value.Origin is PropertyOrigins.Import;

    public string? EvaluatedNote => _value.Unevaluated is { } unevaluated && unevaluated != _value.Value && _value.Value.Length > 0 ? "Evaluates to: " + _value.Value : null;

    public bool HasEvaluatedNote => EvaluatedNote is not null;

    private static string File(string? path) => path is null ? "?" : Path.GetFileName(path);

    /// <summary>The text editor's value; committed when the box loses focus (or on Enter).</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (SetField(ref _text, value ?? string.Empty) && _text != _value.Value)
            {
                _ = _owner.SetAsync(Definition, _text);
            }
        }
    }

    public bool? BoolValue
    {
        get => string.Equals(_value.Value, "true", StringComparison.OrdinalIgnoreCase);
        set => _ = _owner.SetAsync(Definition, value == true ? "true" : "false");
    }

    public PropertyOption? SelectedOption
    {
        get => Options.FirstOrDefault(o => string.Equals(o.Value, _value.Value, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null && !string.Equals(value.Value, _value.Value, StringComparison.OrdinalIgnoreCase))
            {
                _ = _owner.SetAsync(Definition, value.Value);
            }
        }
    }
}

public sealed record PropertyOption(string Value, string Label);

/// <summary>The Debug page: the launch profiles of <c>Properties/launchSettings.json</c> (what F5 and Ctrl+F5 use).</summary>
public sealed class LaunchProfilesViewModel : ObservableObject
{
    private readonly string _path;
    private readonly DialogViewModel _dialogs;
    private readonly Func<string, Task> _openFile;
    private LaunchProfileViewModel? _selected;
    private bool _isModified;
    private string? _error;

    public LaunchProfilesViewModel(string path, DialogViewModel dialogs, Func<string, Task> openFile)
    {
        _path = path;
        _dialogs = dialogs;
        _openFile = openFile;
        AddCommand = new RelayCommand(() => _ = AddAsync());
        RemoveCommand = new RelayCommand(Remove, () => _selected is not null);
        RenameCommand = new RelayCommand(() => _ = RenameAsync(), () => _selected is not null);
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => _isModified);
        RevertCommand = new RelayCommand(() => _ = LoadAsync(force: true), () => _isModified);
        OpenFileCommand = new RelayCommand(() => _ = openFile(_path), () => File.Exists(_path));
    }

    public string FilePath => _path;

    public ObservableCollection<LaunchProfileViewModel> Profiles { get; } = [];

    public ICommand AddCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand RenameCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand RevertCommand { get; }

    public ICommand OpenFileCommand { get; }

    public static IReadOnlyList<string> CommandNames { get; } = ["Project", "Executable"];

    public LaunchProfileViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                Raise();
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
                Raise();
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

    public string Location => File.Exists(_path) ? _path : $"{_path} (created on save)";

    /// <summary>
    /// Reads the file. Unsaved edits are kept unless <paramref name="force"/> (Revert): re-evaluations of the project
    /// reload the page, and must not throw the user's work away.
    /// </summary>
    public async Task LoadAsync(bool force = false)
    {
        if (_isModified && !force)
        {
            return;
        }

        try
        {
            var text = File.Exists(_path) ? await File.ReadAllTextAsync(_path).ConfigureAwait(true) : null;
            if (_isModified && !force)
            {
                return; // edited while the file was read
            }

            var selected = _selected?.Name;
            Profiles.Clear();
            foreach (var profile in LaunchSettingsEditor.Read(text))
            {
                Profiles.Add(new LaunchProfileViewModel(profile, MarkModified));
            }

            Selected = Profiles.FirstOrDefault(p => p.Name == selected) ?? Profiles.FirstOrDefault();
            IsModified = false;
            Error = null;
            OnPropertyChanged(nameof(Location));
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Error = $"{_path}: {ex.Message}";
        }
    }

    public async Task SaveAsync()
    {
        try
        {
            var existing = File.Exists(_path) ? await File.ReadAllTextAsync(_path).ConfigureAwait(true) : null;
            var text = LaunchSettingsEditor.Write(existing, [.. Profiles.Select(p => p.Model)]);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await File.WriteAllTextAsync(_path, text).ConfigureAwait(true);
            IsModified = false;
            Error = null;
            OnPropertyChanged(nameof(Location));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
    }

    private async Task AddAsync()
    {
        var name = await _dialogs.InputAsync("New Launch Profile", "The profile's name.", Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(_path))) ?? "Profile", Validate).ConfigureAwait(true);
        if (name is null)
        {
            return;
        }

        var profile = new LaunchProfileViewModel(new EditableLaunchProfile(name, new System.Text.Json.Nodes.JsonObject { ["commandName"] = "Project" }), MarkModified);
        Profiles.Add(profile);
        Selected = profile;
        MarkModified();
    }

    private async Task RenameAsync()
    {
        if (_selected is not { } profile)
        {
            return;
        }

        var name = await _dialogs.InputAsync("Rename Launch Profile", "The profile's new name.", profile.Name, text => text == profile.Name ? null : Validate(text)).ConfigureAwait(true);
        if (name is not null && name != profile.Name)
        {
            profile.Name = name;
            MarkModified();
        }
    }

    private string? Validate(string name) =>
        string.IsNullOrWhiteSpace(name) ? "Enter a name." : Profiles.Any(p => p.Name == name.Trim()) ? "A profile with this name exists." : null;

    private void Remove()
    {
        if (_selected is not { } profile)
        {
            return;
        }

        var index = Profiles.IndexOf(profile);
        Profiles.Remove(profile);
        Selected = Profiles.Count == 0 ? null : Profiles[Math.Min(index, Profiles.Count - 1)];
        MarkModified();
    }

    private void MarkModified() => IsModified = true;

    private void Raise()
    {
        foreach (var command in new[] { RemoveCommand, RenameCommand, SaveCommand, RevertCommand, OpenFileCommand })
        {
            ((RelayCommand)command).RaiseCanExecuteChanged();
        }
    }
}

/// <summary>One launch profile's fields.</summary>
public sealed class LaunchProfileViewModel(EditableLaunchProfile model, Action changed) : ObservableObject
{
    public EditableLaunchProfile Model { get; } = model;

    public string Name
    {
        get => Model.Name;
        set
        {
            Model.Name = value.Trim();
            OnPropertyChanged();
            changed();
        }
    }

    public string CommandName
    {
        get => Model.Get("commandName") ?? "Project";
        set => Update(() => Model.Set("commandName", value));
    }

    public bool IsExecutable => CommandName == "Executable";

    public string ExecutablePath
    {
        get => Model.Get("executablePath") ?? string.Empty;
        set => Update(() => Model.Set("executablePath", value));
    }

    public string CommandLineArgs
    {
        get => Model.Get("commandLineArgs") ?? string.Empty;
        set => Update(() => Model.Set("commandLineArgs", value));
    }

    public string WorkingDirectory
    {
        get => Model.Get("workingDirectory") ?? string.Empty;
        set => Update(() => Model.Set("workingDirectory", value));
    }

    public string ApplicationUrl
    {
        get => Model.Get("applicationUrl") ?? string.Empty;
        set => Update(() => Model.Set("applicationUrl", value));
    }

    public bool LaunchBrowser
    {
        get => Model.GetBool("launchBrowser");
        set => Update(() => Model.SetBool("launchBrowser", value, keepFalse: Model.Properties.ContainsKey("launchBrowser")));
    }

    public string LaunchUrl
    {
        get => Model.Get("launchUrl") ?? string.Empty;
        set => Update(() => Model.Set("launchUrl", value));
    }

    public string EnvironmentVariables
    {
        get => Model.EnvironmentText;
        set => Update(() => Model.EnvironmentText = value);
    }

    private void Update(Action apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        apply();
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(IsExecutable));
        changed();
    }
}
