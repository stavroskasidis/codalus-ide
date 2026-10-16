using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Shell.Docking;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Views;

/// <summary>One option of the EditorConfig settings page, in the chosen section.</summary>
public sealed class EditorConfigOptionRow : ObservableObject
{
    public const string NotSet = "(not set)";

    private readonly Action<EditorConfigOptionRow> _changed;
    private string _value;

    public EditorConfigOptionRow(EditorConfigOption option, string? value, Action<EditorConfigOptionRow> changed)
    {
        Option = option;
        _changed = changed;
        _value = value ?? string.Empty;
        Choices = option.Values is { } values
            ? [NotSet, .. values, .. value is not null && !values.Contains(value, StringComparer.OrdinalIgnoreCase) ? [value] : Array.Empty<string>()]
            : [];
    }

    public EditorConfigOption Option { get; }

    public string Key => Option.Key;

    public string Description => Option.Description;

    public string Group => Option.Group;

    public bool HasChoices => Choices.Count > 0;

    public bool IsFreeText => !HasChoices;

    public IReadOnlyList<string> Choices { get; }

    /// <summary>The value in the section ("" when the section doesn't set it).</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (SetField(ref _value, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(Choice));
                OnPropertyChanged(nameof(IsSet));
                _changed(this);
            }
        }
    }

    /// <summary>The combo box's item: the value, or "(not set)".</summary>
    public string Choice
    {
        get => _value.Length == 0 ? NotSet : _value;
        set => Value = value is null or NotSet ? string.Empty : value;
    }

    public bool IsSet => _value.Length > 0;

    public string AccessibleName => $"{Key}: {(IsSet ? Value : NotSet)}. {Description}";
}

/// <summary>
/// The EditorConfig settings page (deferred from M12b): an <c>.editorconfig</c> file's options for one section
/// (<c>[*]</c>, <c>[*.cs]</c>…), generated from the keys the markup host knows, in groups, with a search box. A choice
/// edits the file's text in place (<see cref="EditorConfigFile"/>); saving writes it, and the language services pick it
/// up as they do any edit. View Code opens the file in the text editor.
/// </summary>
public sealed class EditorConfigDocument : ShellDocument, ISavableDocument
{
    private readonly Action<string> _viewCode;
    private EditorConfigFile _file = EditorConfigFile.Parse(string.Empty);
    private TextFileFormat _format = TextFileFormat.Default;
    private string _section = "*";
    private string _filter = string.Empty;
    private bool _loading;

    public EditorConfigDocument(string path, Action<string> viewCode)
    {
        Path = path;
        _viewCode = viewCode;
        Id = IdFor(path);
        Title = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) + " – .editorconfig";
        ViewCodeCommand = new RelayCommand(() => _viewCode(Path));
    }

    public override string Kind => "editorconfig";

    public override string? FilePath => Path;

    public string Path { get; }

    public static string IdFor(string path) => "editorconfig:" + path;

    /// <summary>The file's sections, plus <c>*</c> and <c>*.cs</c> when it has neither.</summary>
    public ObservableCollection<string> Sections { get; } = [];

    public string Section
    {
        get => _section;
        set
        {
            if (value is not null && SetProperty(ref _section, value))
            {
                Rebuild();
            }
        }
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value ?? string.Empty))
            {
                Rebuild();
            }
        }
    }

    public ObservableCollection<EditorConfigOptionRow> Rows { get; } = [];

    public ICommand ViewCodeCommand { get; }

    /// <summary>Reads the file (an empty one when it doesn't exist yet: saving creates it).</summary>
    public async Task LoadAsync()
    {
        if (File.Exists(Path))
        {
            string text;
            (text, _format) = await TextFiles.ReadAsync(Path, CancellationToken.None).ConfigureAwait(true);
            _file = EditorConfigFile.Parse(text);
        }

        Sections.Clear();
        foreach (var section in _file.Sections.Concat(["*", "*.cs"]).Distinct(StringComparer.Ordinal))
        {
            Sections.Add(section);
        }

        _section = Sections.Contains("*.cs") && _file.Sections.Contains("*.cs") ? "*.cs" : Sections[0];
        OnPropertyChanged(nameof(Section));
        IsModified = false;
        Rebuild();
    }

    public async Task SaveAsync()
    {
        await TextFiles.WriteAsync(Path, _file.Save(), _format, CancellationToken.None).ConfigureAwait(true);
        IsModified = false;
    }

    private void Rebuild()
    {
        _loading = true;
        var values = _file.Values(_section);
        Rows.Clear();
        foreach (var option in EditorConfigFile.Options
                     .Where(o => _filter.Length == 0 || o.Key.Contains(_filter, StringComparison.OrdinalIgnoreCase) || o.Description.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(o => o.Group, StringComparer.Ordinal))
        {
            Rows.Add(new EditorConfigOptionRow(option, values.GetValueOrDefault(option.Key), OnChanged));
        }

        _loading = false;
    }

    private void OnChanged(EditorConfigOptionRow row)
    {
        if (_loading)
        {
            return;
        }

        _file.Set(_section, row.Key, row.IsSet ? row.Value.Trim() : null);
        IsModified = true;
    }
}
