using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Shell.Docking;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.CustomTools;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Views;

/// <summary>A document that saves through the shell's Save, Save All and the save prompt on close.</summary>
public interface ISavableDocument
{
    /// <summary>Saves the document; throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when it can't.</summary>
    Task SaveAsync();
}

/// <summary>One resource row of the <c>.resx</c> editor: strings are editable, other resources show their type.</summary>
public sealed class ResxRowViewModel(ResxEntry entry, Action changed) : ObservableObject
{
    private string _name = entry.Name;
    private string _value = entry.Value ?? string.Empty;
    private string _comment = entry.Comment ?? string.Empty;

    /// <summary>The name the resource has in the file (null for a new one).</summary>
    public string? OriginalName { get; set; } = entry.Name.Length == 0 ? null : entry.Name;

    public bool IsString { get; } = entry.IsString;

    public bool IsReadOnly => !IsString;

    public string? Type { get; } = entry.IsString ? null : entry.Type;

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

    public string Value
    {
        get => _value;
        set
        {
            if (SetField(ref _value, value ?? string.Empty))
            {
                changed();
            }
        }
    }

    public string Comment
    {
        get => _comment;
        set
        {
            if (SetField(ref _comment, value ?? string.Empty))
            {
                changed();
            }
        }
    }

    public string AccessibleName => IsString ? $"{Name}: {Value}" : $"{Name} ({Type})";
}

/// <summary>
/// The <c>.resx</c> resource editor (deferred from M11): a file's resources as a table. Strings are edited in place
/// (name, value, comment), added and removed; other resources (images, files, typed values) are listed read-only.
/// Saving rewrites only what changed (<see cref="ResxFile"/>), and the file's custom tool runs as after any save.
/// View Code opens the XML in the text editor.
/// </summary>
public sealed class ResxEditorDocument : ShellDocument, ISavableDocument
{
    private readonly Action<string> _viewCode;
    private readonly Action<string> _saved;
    private string _text = string.Empty;
    private TextFileFormat _format = TextFileFormat.Default;
    private string _filter = string.Empty;
    private string? _error;
    private ResxRowViewModel? _selected;
    private bool _loading;

    public ResxEditorDocument(string path, Action<string> viewCode, Action<string> saved)
    {
        Path = path;
        _viewCode = viewCode;
        _saved = saved;
        Id = IdFor(path);
        Title = System.IO.Path.GetFileName(path);
        AddCommand = new RelayCommand(Add);
        RemoveCommand = new RelayCommand(Remove);
        ViewCodeCommand = new RelayCommand(() => _viewCode(Path));
    }

    public override string Kind => "resx";

    public override string? FilePath => Path;

    public string Path { get; }

    public static string IdFor(string path) => "resx:" + path;

    public static bool IsResx(string path) => path.EndsWith(".resx", StringComparison.OrdinalIgnoreCase);

    public ObservableCollection<ResxRowViewModel> Rows { get; } = [];

    /// <summary>The rows the filter shows (by name, value or comment).</summary>
    public ObservableCollection<ResxRowViewModel> Visible { get; } = [];

    public ResxRowViewModel? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>Why the file can't be read or saved, or null.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _error is not null;

    public ICommand AddCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand ViewCodeCommand { get; }

    /// <summary>Reads the file (again).</summary>
    public async Task LoadAsync()
    {
        try
        {
            (_text, _format) = await TextFiles.ReadAsync(Path, CancellationToken.None).ConfigureAwait(true);
            var file = ResxFile.Parse(_text);
            _loading = true;
            Rows.Clear();
            foreach (var entry in file.Entries)
            {
                Rows.Add(new ResxRowViewModel(entry, MarkModified));
            }

            _loading = false;
            Error = null;
            IsModified = false;
            ApplyFilter();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            _loading = false;
            Error = Localizer.Format("msg.resx.couldNotRead", "This file couldn't be read as resources: {0} Use View Code to edit it as XML.", ex.Message);
        }
    }

    /// <summary>Writes the changes to the file: edited, renamed, added and removed strings.</summary>
    public async Task SaveAsync()
    {
        if (ResxFile.Validate(Rows.Select(r => r.Name)) is { } invalid)
        {
            Error = invalid;
            throw new IOException(invalid);
        }

        var file = ResxFile.Parse(_text);
        var kept = Rows.Where(r => r.OriginalName is not null).Select(r => r.OriginalName!).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in file.Entries.Where(e => !kept.Contains(e.Name)))
        {
            file.Remove(entry.Name);
        }

        // Renames through temporary names, so swapping two names works.
        var renamed = Rows.Where(r => r.OriginalName is { } original && original != r.Name).ToList();
        foreach (var row in renamed)
        {
            file.Rename(row.OriginalName!, "\u0001" + row.OriginalName);
        }

        foreach (var row in renamed)
        {
            file.Rename("\u0001" + row.OriginalName, row.Name);
        }

        foreach (var row in Rows.Where(r => r.IsString))
        {
            file.Set(row.Name, row.Value, row.Comment.Length == 0 ? null : row.Comment);
        }

        var text = file.Save();
        await TextFiles.WriteAsync(Path, text, _format, CancellationToken.None).ConfigureAwait(true);
        _text = text;
        foreach (var row in Rows)
        {
            row.OriginalName = row.Name;
        }

        Error = null;
        IsModified = false;
        _saved(Path);
    }

    private void Add()
    {
        var name = "String";
        for (var n = 1; Rows.Any(r => r.Name == name); n++)
        {
            name = "String" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var row = new ResxRowViewModel(new ResxEntry(string.Empty, string.Empty, null, null), MarkModified) { Name = name };
        Rows.Add(row);
        Filter = string.Empty;
        ApplyFilter();
        Selected = row;
        MarkModified();
    }

    private void Remove()
    {
        if (_selected is { } row && Rows.Remove(row))
        {
            Visible.Remove(row);
            Selected = null;
            MarkModified();
        }
    }

    private void MarkModified()
    {
        if (!_loading)
        {
            IsModified = true;
        }
    }

    private void ApplyFilter()
    {
        Visible.Clear();
        foreach (var row in Rows.Where(r => _filter.Length == 0
                                            || r.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                                            || r.Value.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                                            || r.Comment.Contains(_filter, StringComparison.OrdinalIgnoreCase)))
        {
            Visible.Add(row);
        }
    }
}
