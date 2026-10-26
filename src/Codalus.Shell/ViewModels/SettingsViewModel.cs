using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.ViewModels;

public sealed class SettingsDocument : ShellDocument
{
    public const string DocumentId = "settings";

    public SettingsDocument(SettingsViewModel viewModel)
    {
        Id = DocumentId;
        Title = "Settings";
        ViewModel = viewModel;
    }

    public override string Kind => "settings";

    public SettingsViewModel ViewModel { get; }
}

/// <summary>
/// A node of the Options tree (M29): All Settings, a group (Environment, Text Editor…) or a category in it. A group
/// shows the settings of all its categories.
/// </summary>
public sealed record SettingCategoryViewModel(string Name, int Count, bool IsAll = false)
{
    public string Label => $"{Name} ({Count})";

    /// <summary>The node's icon, in its palette color (M34h, docs/design/controls/Options.dc.html).</summary>
    public string Icon => IsAll ? "list" : IconOf(Name);

    /// <summary>The icon of a group or category of the Options tree, by its name; a gear for the others.</summary>
    internal static string IconOf(string name) => name.ToLowerInvariant() switch
    {
        "environment" or "view" => "window",
        "appearance" => "color",
        "keyboard" => "keyboard",
        "notifications" => "bell",
        "updates" => "cloud-download",
        "privacy" => "shield",
        "output" => "output",
        "files" => "files",
        "welcome" => "home",
        "text editor" => "document-text",
        "code cleanup" => "broom",
        "json" => "braces",
        "xml" or "c# interactive" or "f#" => "code",
        "navigation" => "arrow-sort",
        "task list" => "task-list",
        "language services" => "lightbulb",
        "projects and solutions" => "cube",
        "projects" or "solution explorer" => "solution",
        "build and run" => "build",
        "nuget" => "package",
        "debugging" or "debug" => "debug",
        "source control" or "git" => "branch",
        "testing" or "test" => "beaker",
        "terminal and interactive" or "terminal" => "terminal",
        "other" => "layers",
        "ai" => "sparkle",
        "containers" => "container",
        "code search" => "search",
        "data" => "database",
        "error list" => "error",
        "mobile" => "phone",
        "plugins" => "extensions",
        "remote" => "window-remote",
        "settings sync" => "sync",
        "system integration" => "link",
        "web" => "globe",
        "windows forms" => "control-generic",
        _ => "gear",
    };

    public IReadOnlyList<SettingCategoryViewModel> Children { get; init; } = [];

    /// <summary>For a group: its categories.</summary>
    public IReadOnlySet<string>? Categories { get; init; }

    public bool IsExpanded => Children.Count > 0;

    public bool Includes(string category) =>
        IsAll || (Categories is { } categories ? categories.Contains(category) : string.Equals(Name, category, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The Settings UI: every registered setting (shell and plugin contributions), searchable and grouped by
/// category, with typed editors. Edits write the user's settings.json (comments and formatting are kept).
/// Search matches key, title, description and category; <c>@modified</c> shows only changed settings.
/// The Workspace tab (while a solution is open) edits <c>.codalus/settings.json</c> next to the solution; settings
/// that only make sense per user are read-only there.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly List<SettingItemViewModel> _all = [];
    private string _search = string.Empty;
    private SettingCategoryViewModel? _selectedCategory;
    private string? _diagnostics;

    private bool _workspaceScope;
    private readonly ShellHost? _shell;

    /// <param name="shell">With a shell, Options also has System Integration's toggles (M51), which change the OS, not settings.json.</param>
    public SettingsViewModel(SettingsService settings, Action openJson, ShellHost? shell = null)
    {
        _settings = settings;
        _shell = shell;
        settings.WorkspaceChanged += (_, _) => Ui.Post(() =>
        {
            if (!HasWorkspace)
            {
                _workspaceScope = false;
                OnPropertyChanged(nameof(IsWorkspaceScope));
            }

            OnPropertyChanged(nameof(HasWorkspace));
            OnScopeChanged();
        });
        OpenJsonCommand = new RelayCommand(openJson);
        ClearSearchCommand = new RelayCommand(() => Search = string.Empty);
        settings.Changed += (_, e) => Ui.Post(() => OnSettingsChanged(e));
        settings.LayersChanged += (_, e) => Ui.Post(() => OnSettingsChanged(e)); // Reset of a value equal to the default
        Rebuild();
    }

    public string FilePath => Scope == SettingScope.Workspace ? _settings.WorkspaceFile! : _settings.UserFile;

    public string FileLabel => Scope == SettingScope.Workspace ? $"Workspace settings: {FilePath}" : $"User settings: {FilePath}";

    public bool HasWorkspace => _settings.WorkspaceFile is not null;

    /// <summary>The layer edits go to: the user's settings.json, or the open solution's workspace settings.</summary>
    public SettingScope Scope => _workspaceScope && HasWorkspace ? SettingScope.Workspace : SettingScope.User;

    public bool IsWorkspaceScope
    {
        get => _workspaceScope;
        set
        {
            if (SetField(ref _workspaceScope, value && HasWorkspace))
            {
                OnScopeChanged();
            }
        }
    }

    public bool IsUserScope
    {
        get => !_workspaceScope;
        set => IsWorkspaceScope = !value;
    }

    /// <summary>User (0) or Workspace (1), the scope's segmented control (M34h).</summary>
    public int ScopeIndex
    {
        get => _workspaceScope ? 1 : 0;
        set => IsWorkspaceScope = value == 1;
    }

    /// <summary>The page's header (M34h): the chosen node of the tree, its icon, and how many settings it shows and are changed.</summary>
    public string PageTitle => _selectedCategory?.Name ?? "All Settings";

    public string PageIcon => _selectedCategory?.Icon ?? "list";

    public string PageSummary
    {
        get
        {
            var modified = Items.Count(i => i.IsModified);
            var count = Items.Count == 1 ? "1 setting" : $"{Items.Count.ToString(CultureInfo.CurrentCulture)} settings";
            return modified == 0 ? count : $"{count} · {modified.ToString(CultureInfo.CurrentCulture)} modified";
        }
    }

    private void OnScopeChanged()
    {
        OnPropertyChanged(nameof(Scope));
        OnPropertyChanged(nameof(IsUserScope));
        OnPropertyChanged(nameof(ScopeIndex));
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(FileLabel));
        foreach (var item in _all)
        {
            item.Refresh();
        }

        if (_search.Contains("@modified", StringComparison.OrdinalIgnoreCase))
        {
            Filter();
        }
    }

    public ObservableCollection<SettingCategoryViewModel> Categories { get; } = [];

    public ObservableCollection<SettingItemViewModel> Items { get; } = [];

    public ICommand OpenJsonCommand { get; }

    public ICommand ClearSearchCommand { get; }

    public string Search
    {
        get => _search;
        set
        {
            if (SetField(ref _search, value ?? string.Empty))
            {
                Filter();
            }
        }
    }

    /// <summary>The first entry, "All Settings", shows every category.</summary>
    public SettingCategoryViewModel? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetField(ref _selectedCategory, value))
            {
                Filter();
            }
        }
    }

    public string? Diagnostics
    {
        get => _diagnostics;
        private set
        {
            if (SetField(ref _diagnostics, value))
            {
                OnPropertyChanged(nameof(HasDiagnostics));
            }
        }
    }

    public bool HasDiagnostics => _diagnostics is not null;

    public bool IsEmpty => Items.Count == 0;

    private void OnSettingsChanged(SettingsChangedEventArgs e)
    {
        var known = _all.Where(i => i.IsSetting).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        if (!_settings.Descriptors.Select(d => d.Key).ToHashSet(StringComparer.Ordinal).SetEquals(known))
        {
            Rebuild();
            return;
        }

        foreach (var item in _all.Where(i => e.Keys.Contains(i.Key)))
        {
            item.Refresh();
        }

        OnPropertyChanged(nameof(PageSummary));

        UpdateDiagnostics();
        if (_search.Contains("@modified", StringComparison.OrdinalIgnoreCase))
        {
            Filter();
        }
    }

    private void Rebuild()
    {
        _all.Clear();
        foreach (var descriptor in _settings.Descriptors)
        {
            _all.Add(SettingItemViewModel.Create(descriptor, _settings, () => Scope));
        }

        if (_shell is { } shell)
        {
            _all.Add(new OsIntegrationSettingViewModel(shell, Workbench.Setup.OsIntegrationFeature.Folders, _settings, () => Scope));
            _all.Add(new OsIntegrationSettingViewModel(shell, Workbench.Setup.OsIntegrationFeature.FileTypes, _settings, () => Scope));
        }

        var selected = _selectedCategory?.Name;
        Categories.Clear();
        Categories.Add(new SettingCategoryViewModel("All Settings", _all.Count, IsAll: true));
        foreach (var group in _all.GroupBy(i => OptionsGroup(i.Category), StringComparer.Ordinal).OrderBy(g => Array.IndexOf(s_groups, g.Key)))
        {
            var categories = group.GroupBy(i => i.Category, StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(c => new SettingCategoryViewModel(c.Key, c.Count()))
                .ToList();
            Categories.Add(new SettingCategoryViewModel(group.Key, group.Count())
            {
                Children = categories,
                Categories = categories.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
            });
        }

        _selectedCategory = Categories.SelectMany(c => c.Children.Prepend(c)).FirstOrDefault(c => c.Name == selected) ?? Categories[0];
        OnPropertyChanged(nameof(SelectedCategory));
        UpdateDiagnostics();
        Filter();
    }

    private static readonly string[] s_groups =
        ["Environment", "Text Editor", "Projects and Solutions", "Debugging", "Source Control", "Testing", "Terminal and Interactive", "Other"];

    /// <summary>The Options group of a category (or of a key prefix, for settings without one), as a classic IDE arranges them.</summary>
    internal static string OptionsGroup(string category)
    {
        var name = category.ToLowerInvariant();
        string Prefix() => name.Split('.')[0];
        return name switch
        {
            "appearance" or "keyboard" or "view" or "notifications" or "privacy" or "output" or "files" or "welcome" or "general" or "updates" or "system integration" => "Environment",
            "text editor" or "navigation" or "code cleanup" or "task list" or "language services" or "json" => "Text Editor",
            "projects" or "build and run" or "nuget" => "Projects and Solutions",
            "debugging" or "debug" => "Debugging",
            "git" => "Source Control",
            "testing" or "test" => "Testing",
            "terminal" or "c# interactive" => "Terminal and Interactive",
            _ => Prefix() switch
            {
                "workbench" or "files" or "window" or "keyboard" or "telemetry" => "Environment",
                "editor" or "languageservices" or "json" or "yaml" or "xml" or "markdown" or "codecleanup" or "tasklist" or "navigation" => "Text Editor",
                "projects" or "build" or "run" or "nuget" or "solution" => "Projects and Solutions",
                "debug" or "debugger" => "Debugging",
                "git" => "Source Control",
                "testing" or "test" => "Testing",
                "terminal" or "interactive" => "Terminal and Interactive",
                _ => "Other",
            },
        };
    }

    private void UpdateDiagnostics()
    {
        var problems = _settings.Diagnostics;
        Diagnostics = problems.Count == 0 ? null : string.Join(Environment.NewLine, problems);
    }

    private void Filter()
    {
        var terms = _search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modifiedOnly = terms.Any(t => t.Equals("@modified", StringComparison.OrdinalIgnoreCase));
        var words = terms.Where(t => !t.StartsWith('@')).ToList();
        // A category's page names its settings by their titles; a group, All Settings and a search by category and title.
        var showCategory = _selectedCategory is null || _selectedCategory.IsAll || _selectedCategory.Children.Count > 0 || terms.Length > 0;
        Items.Clear();
        foreach (var item in _all)
        {
            if ((_selectedCategory is null || _selectedCategory.Includes(item.Category))
                && (!modifiedOnly || item.IsModified)
                && words.All(item.Matches))
            {
                item.ShowCategory = showCategory;
                Items.Add(item);
            }
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageIcon));
        OnPropertyChanged(nameof(PageSummary));
    }
}

public abstract class SettingItemViewModel : ObservableObject
{
    private readonly Func<SettingScope> _scope;
    private bool _isModified;
    private bool _showCategory = true;
    private string? _error;

    protected SettingItemViewModel(SettingDescriptor descriptor, SettingsService settings, Func<SettingScope> scope)
    {
        Descriptor = descriptor;
        Settings = settings;
        _scope = scope;
        ResetCommand = new RelayCommand(() => _ = WriteAsync(null));
        _isModified = settings.GetLayerValue(descriptor.Key, scope()) is not null;
    }

    /// <summary>User-only settings can't be changed on the Workspace tab.</summary>
    public bool CanEdit => _scope() == SettingScope.User || Descriptor.Scope == SettingScope.Workspace;

    public bool IsUserOnlyHere => !CanEdit;

    public SettingDescriptor Descriptor { get; }

    protected SettingsService Settings { get; }

    public string Key => Descriptor.Key;

    public string Title => Descriptor.DisplayTitle;

    public string Category => Descriptor.DisplayCategory;

    public string? Description => Descriptor.Description;

    public bool HasDescription => !string.IsNullOrEmpty(Descriptor.Description);

    /// <summary>False for an item of Options that isn't a setting in settings.json (System Integration, M51): no key is shown.</summary>
    public virtual bool IsSetting => true;

    /// <summary>A line under the description saying how the item stands, or null.</summary>
    public virtual string? Status => null;

    public bool HasStatus => !string.IsNullOrEmpty(Status);

    public string Heading => $"{Category}: {Title}";

    /// <summary>Whether the page shows the category before the title (a group, All Settings or a search; M34h).</summary>
    public bool ShowCategory
    {
        get => _showCategory;
        set
        {
            if (SetField(ref _showCategory, value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    /// <summary>The setting's title on its page: the title alone on its category's page.</summary>
    public string DisplayTitle => _showCategory ? Heading : Title;

    /// <summary>A boolean is one check box whose label is its title (M34h).</summary>
    public virtual bool IsBool => false;

    public string AccessibleName => IsModified ? $"{Heading} (modified)" : Heading;

    public bool IsModified
    {
        get => _isModified;
        private set
        {
            if (SetField(ref _isModified, value))
            {
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    public string? Error
    {
        get => _error;
        protected set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _error is not null;

    public ICommand ResetCommand { get; }

    /// <summary>True while values are being re-read, so editors don't write them back.</summary>
    protected bool Refreshing { get; private set; }

    public static SettingItemViewModel Create(SettingDescriptor descriptor, SettingsService settings, Func<SettingScope> scope) => descriptor.Type switch
    {
        _ when descriptor.Key == Theming.ThemeService.SettingKey => new ColorThemeSettingViewModel(descriptor, settings, scope),
        SettingType.Boolean => new BoolSettingViewModel(descriptor, settings, scope),
        SettingType.String when descriptor.Enum is { Count: > 0 } => new EnumSettingViewModel(descriptor, settings, scope),
        SettingType.String when descriptor.Browse != SettingBrowse.None => new PathSettingViewModel(descriptor, settings, scope),
        _ => new TextSettingViewModel(descriptor, settings, scope),
    };

    public bool Matches(string word) =>
        Key.Contains(word, StringComparison.OrdinalIgnoreCase)
        || Title.Contains(word, StringComparison.OrdinalIgnoreCase)
        || Category.Contains(word, StringComparison.OrdinalIgnoreCase)
        || (Description?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false);

    public void Refresh()
    {
        Refreshing = true;
        try
        {
            IsModified = Settings.GetLayerValue(Key, _scope()) is not null;
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(IsUserOnlyHere));
            Load(Settings.GetValue(Key));
        }
        finally
        {
            Refreshing = false;
        }
    }

    protected abstract void Load(JsonElement value);

    /// <summary>Writes the value in the selected layer (null removes it there).</summary>
    protected async Task WriteAsync(JsonElement? value)
    {
        if (!CanEdit)
        {
            return;
        }

        try
        {
            Error = null;
            await Settings.SetAsync(Key, value, _scope(), CancellationToken.None).ConfigureAwait(true);
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message.Replace(Key + ": ", string.Empty, StringComparison.Ordinal);
        }
        catch (IOException ex)
        {
            Error = $"Could not write settings.json: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            Error = $"Could not write settings.json: {ex.Message}";
        }
    }

    protected static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

public sealed class BoolSettingViewModel : SettingItemViewModel
{
    private bool _value;

    public BoolSettingViewModel(SettingDescriptor descriptor, SettingsService settings, Func<SettingScope> scope)
        : base(descriptor, settings, scope) => Refresh();

    public override bool IsBool => true;

    public bool Value
    {
        get => _value;
        set
        {
            if (SetField(ref _value, value) && !Refreshing)
            {
                _ = WriteAsync(Json(value ? "true" : "false"));
            }
        }
    }

    protected override void Load(JsonElement value) => Value = value.ValueKind == JsonValueKind.True;
}

public sealed record EnumOption(string Value, string Label, string? Description);

public sealed class EnumSettingViewModel : SettingItemViewModel
{
    private EnumOption? _selected;

    public EnumSettingViewModel(SettingDescriptor descriptor, SettingsService settings, Func<SettingScope> scope)
        : base(descriptor, settings, scope)
    {
        Options = [.. descriptor.Enum!.Select((value, i) => new EnumOption(value, value, descriptor.EnumDescriptions?.ElementAtOrDefault(i)))];
        Refresh();
    }

    public IReadOnlyList<EnumOption> Options { get; }

    public EnumOption? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                OnPropertyChanged(nameof(SelectedDescription));
                if (!Refreshing && value is not null)
                {
                    _ = WriteAsync(JsonSerializer.SerializeToElement(value.Value, ShellJsonContext.Default.String));
                }
            }
        }
    }

    public string? SelectedDescription => _selected?.Description;

    protected override void Load(JsonElement value) =>
        Selected = Options.FirstOrDefault(o => value.ValueKind == JsonValueKind.String && o.Value == value.GetString());
}

/// <summary>Strings, numbers and string arrays (one value per line); validated when committed.</summary>
public class TextSettingViewModel : SettingItemViewModel
{
    private string _text = string.Empty;
    private string _committed = string.Empty;

    public TextSettingViewModel(SettingDescriptor descriptor, SettingsService settings, Func<SettingScope> scope)
        : base(descriptor, settings, scope) => Refresh();

    public string Text
    {
        get => _text;
        set => SetField(ref _text, value ?? string.Empty);
    }

    public bool IsMultiline => Descriptor.Type is SettingType.StringArray or SettingType.Object;

    public string? Watermark => Descriptor.Type switch
    {
        SettingType.StringArray => "One value per line",
        SettingType.Object => "A JSON object, such as { \"comments\": \"#6A9955\" }",
        SettingType.Integer => "An integer" + Range(),
        SettingType.Number => "A number" + Range(),
        _ => null,
    };

    /// <summary>Writes the edited text (Enter or leaving the field).</summary>
    public void Commit()
    {
        if (_text == _committed)
        {
            return;
        }

        var text = _text.Trim();
        JsonElement value;
        switch (Descriptor.Type)
        {
            case SettingType.Integer when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer):
                value = Json(integer.ToString(CultureInfo.InvariantCulture));
                break;
            case SettingType.Number when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number):
                value = Json(number.ToString("R", CultureInfo.InvariantCulture));
                break;
            case SettingType.Integer:
                Error = "Expected an integer.";
                return;
            case SettingType.Number:
                Error = "Expected a number.";
                return;
            case SettingType.Object:
                try
                {
                    using var document = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        Error = "Expected a JSON object.";
                        return;
                    }

                    value = document.RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    Error = "Not valid JSON: " + ex.Message;
                    return;
                }

                break;
            case SettingType.StringArray:
                value = JsonSerializer.SerializeToElement(
                    _text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), ShellJsonContext.Default.StringArray);
                break;
            default:
                value = JsonSerializer.SerializeToElement(_text, ShellJsonContext.Default.String);
                break;
        }

        _committed = _text;
        _ = WriteAsync(value);
    }

    protected override void Load(JsonElement value)
    {
        Text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Array => string.Join('\n', value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())),
            JsonValueKind.Object => value.GetRawText(),
            _ => string.Empty,
        };
        _committed = _text;
        Error = null;
    }

    private string Range() => (Descriptor.Minimum, Descriptor.Maximum) switch
    {
        ({ } min, { } max) => string.Create(CultureInfo.InvariantCulture, $" from {min} to {max}"),
        ({ } min, null) => string.Create(CultureInfo.InvariantCulture, $" ≥ {min}"),
        (null, { } max) => string.Create(CultureInfo.InvariantCulture, $" ≤ {max}"),
        _ => string.Empty,
    };
}

/// <summary>A path setting (<see cref="SettingDescriptor.Browse"/>): its text with a Browse button (<see cref="Controls.PathBox"/>).</summary>
public sealed class PathSettingViewModel(SettingDescriptor descriptor, SettingsService settings, Func<SettingScope> scope) : TextSettingViewModel(descriptor, settings, scope)
{
    /// <summary>What Browse asks for.</summary>
    public Controls.PathKind Kind => Descriptor.Browse == SettingBrowse.File ? Controls.PathKind.File : Controls.PathKind.Folder;
}
