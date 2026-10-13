using System.Collections.ObjectModel;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.ViewModels;

/// <summary>A symbol of the Document Outline and the navigation bar.</summary>
public sealed class SymbolNodeViewModel(DocumentSymbol symbol, string? container = null) : ObservableObject
{
    private bool _isExpanded = true;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public DocumentSymbol Symbol { get; } = symbol;

    public string Name => Symbol.Name;

    /// <summary>The signature or type, or (in a filtered list) the containing symbol.</summary>
    public string? Detail => container ?? Symbol.Detail;

    public string Icon => IconFor(Symbol.Kind);

    public IReadOnlyList<SymbolNodeViewModel> Children { get; } = [.. (symbol.Children ?? []).Select(c => new SymbolNodeViewModel(c))];

    public string AccessibleName => $"{KindName(Symbol.Kind)} {Name}";

    public override string ToString() => Name;

    /// <summary>
    /// A symbol kind's icon, colored as a classic IDE colors them (M34a: types amber, members purple, fields and
    /// properties blue; <c>kind@Family</c> takes the theme's token, monochrome in High Contrast).
    /// </summary>
    public static string IconFor(int kind) => kind switch
    {
        SymbolKind.Namespace or SymbolKind.Module or SymbolKind.Package => "namespace",
        SymbolKind.Class or SymbolKind.Struct => "class@Open",
        SymbolKind.Interface => "class@Navigate",
        SymbolKind.Enum => "list@Open",
        SymbolKind.EnumMember => "dot@Open",
        SymbolKind.Method or SymbolKind.Constructor or SymbolKind.Function or SymbolKind.Operator => "method@Build",
        SymbolKind.Property => "tools@Navigate",
        SymbolKind.Field or SymbolKind.Constant or SymbolKind.Variable => "column@Save",
        SymbolKind.Event => "flame@Open",
        SymbolKind.File => "file",
        _ => "dot",
    };

    public static string KindName(int kind) => kind switch
    {
        SymbolKind.Namespace => "Namespace",
        SymbolKind.Class => "Class",
        SymbolKind.Interface => "Interface",
        SymbolKind.Struct => "Struct",
        SymbolKind.Enum => "Enum",
        SymbolKind.EnumMember => "Enum member",
        SymbolKind.Method => "Method",
        SymbolKind.Constructor => "Constructor",
        SymbolKind.Property => "Property",
        SymbolKind.Field => "Field",
        SymbolKind.Constant => "Constant",
        SymbolKind.Event => "Event",
        SymbolKind.Operator => "Operator",
        SymbolKind.Function => "Delegate",
        _ => "Symbol",
    };
}

/// <summary>
/// The Document Outline tool window (M12a): the active document's namespaces, types and members, nested; a filter
/// flattens it to the matching symbols. It follows the caret, and selecting a symbol moves the caret there.
/// </summary>
public sealed class DocumentOutlineViewModel : ObservableObject
{
    public const string ToolWindowId = "documentOutline";

    private CodeEditorController? _editor;
    private string _filter = string.Empty;
    private string _message = "Open a document to see its outline.";
    private SymbolNodeViewModel? _current;

    public ObservableCollection<SymbolNodeViewModel> Roots { get; } = [];

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value ?? string.Empty))
            {
                Rebuild();
            }
        }
    }

    public string Message
    {
        get => _message;
        private set => SetField(ref _message, value);
    }

    public bool HasMessage => Roots.Count == 0;

    /// <summary>The innermost symbol at the caret (the view selects it).</summary>
    public SymbolNodeViewModel? Current
    {
        get => _current;
        private set => SetField(ref _current, value);
    }

    /// <summary>Follows another editor (null: none).</summary>
    public void SetEditor(CodeEditorController? editor)
    {
        if (_editor == editor)
        {
            return;
        }

        if (_editor is not null)
        {
            _editor.Insights.SymbolsChanged -= OnSymbolsChanged;
            _editor.CaretChanged -= OnCaretChanged;
        }

        _editor = editor;
        if (editor is not null)
        {
            editor.Insights.SymbolsChanged += OnSymbolsChanged;
            editor.CaretChanged += OnCaretChanged;
        }

        Rebuild();
    }

    private void OnSymbolsChanged(object? sender, EventArgs e) => Rebuild();

    private void OnCaretChanged(object? sender, EventArgs e) => UpdateCurrent();

    private void Rebuild()
    {
        Roots.Clear();
        if (_editor is null)
        {
            Message = "Open a document to see its outline.";
        }
        else if (_editor.Insights.Symbols.Count == 0)
        {
            Message = _editor.Insights.SymbolsVersion < 0 ? "No outline for this document." : "This document has no symbols.";
        }
        else if (_filter.Length > 0)
        {
            foreach (var match in Flatten(_editor.Insights.Symbols, null).Where(m => m.Symbol.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)))
            {
                Roots.Add(new SymbolNodeViewModel(match.Symbol with { Children = null }, match.Container));
            }

            Message = Roots.Count == 0 ? $"No symbols match '{_filter}'." : string.Empty;
        }
        else
        {
            foreach (var symbol in _editor.Insights.Symbols)
            {
                Roots.Add(new SymbolNodeViewModel(symbol));
            }

            Message = string.Empty;
        }

        OnPropertyChanged(nameof(HasMessage));
        UpdateCurrent();
    }

    private static IEnumerable<(DocumentSymbol Symbol, string? Container)> Flatten(IEnumerable<DocumentSymbol> symbols, string? container)
    {
        foreach (var symbol in symbols)
        {
            yield return (symbol, container);
            foreach (var child in Flatten(symbol.Children ?? [], symbol.Name))
            {
                yield return child;
            }
        }
    }

    private void UpdateCurrent()
    {
        if (_editor is null || _filter.Length > 0)
        {
            Current = null;
            return;
        }

        var caret = _editor.Model.PositionAt(_editor.Editor.CaretOffset);
        var path = _editor.Insights.SymbolPath(caret);
        SymbolNodeViewModel? node = null;
        IReadOnlyList<SymbolNodeViewModel> level = Roots;
        foreach (var symbol in path)
        {
            node = level.FirstOrDefault(n => n.Symbol == symbol);
            if (node is null)
            {
                break;
            }

            level = node.Children;
        }

        Current = node;
    }

    /// <summary>Moves the caret to a symbol (Enter or double-click).</summary>
    public void Navigate(SymbolNodeViewModel node)
    {
        if (_editor is { } editor)
        {
            editor.Navigate(node.Symbol.SelectionRange.Start);
        }
    }
}

/// <summary>A node of the Call Hierarchy or Type Hierarchy; children load when it expands.</summary>
public sealed class HierarchyNodeViewModel : ObservableObject
{
    private readonly Func<Task<IReadOnlyList<HierarchyNodeViewModel>>>? _load;
    private bool _loaded;
    private bool _isExpanded;

    public HierarchyNodeViewModel(string label, string? detail, string icon, Location? location, Func<Task<IReadOnlyList<HierarchyNodeViewModel>>>? load)
    {
        Label = label;
        Detail = detail;
        Icon = icon;
        Location = location;
        _load = load;
        if (load is not null)
        {
            Children.Add(Placeholder("Loading…"));
        }
    }

    public string Label { get; }

    public string? Detail { get; }

    public string Icon { get; }

    /// <summary>Where Enter or double-click goes (a call site, or a declaration); null for group and placeholder nodes.</summary>
    public Location? Location { get; }

    public ObservableCollection<HierarchyNodeViewModel> Children { get; } = [];

    public string AccessibleName => Detail is null ? Label : $"{Label}, {Detail}";

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public bool IsPlaceholder => Location is null && Children.Count == 0 && _load is null;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value) && value)
            {
                _ = LoadAsync();
            }
        }
    }

    /// <summary>Loads the children (once); completes when they are there (tests).</summary>
    public async Task LoadAsync()
    {
        if (_loaded || _load is null)
        {
            return;
        }

        _loaded = true;
        IReadOnlyList<HierarchyNodeViewModel> children;
        try
        {
            children = await _load().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            children = [Placeholder("Could not load: " + ex.Message)];
        }

        Children.Clear();
        foreach (var child in children.Count == 0 ? [Placeholder("(none)")] : children)
        {
            Children.Add(child);
        }
    }

    public static HierarchyNodeViewModel Placeholder(string text) => new(text, null, "dot", null, null);
}

/// <summary>
/// The Call Hierarchy and Type Hierarchy tool window (M12a): a root symbol with "Calls To" and "Calls From" (or base
/// and derived types) groups that load lazily, recursively.
/// </summary>
public sealed class HierarchyViewModel(Func<Location, Task> open) : ObservableObject
{
    public const string ToolWindowId = "hierarchy";

    private string _title = "Call Hierarchy";

    public string Title
    {
        get => _title;
        private set => SetField(ref _title, value);
    }

    public ObservableCollection<HierarchyNodeViewModel> Roots { get; } = [];

    /// <summary>Raised after <see cref="Show"/>, so the view expands the roots.</summary>
    public event EventHandler? Shown;

    public void Show(string title, IEnumerable<HierarchyNodeViewModel> roots)
    {
        Title = title;
        Roots.Clear();
        foreach (var root in roots)
        {
            Roots.Add(root);
        }

        Shown?.Invoke(this, EventArgs.Empty);
    }

    public Task OpenAsync(HierarchyNodeViewModel node) => node.Location is { } location ? open(location) : Task.CompletedTask;
}

/// <summary>
/// The Code Definition window (M12a): follows the caret and shows the definition of the symbol under it, read-only,
/// with the definition's line highlighted.
/// </summary>
public sealed class CodeDefinitionViewModel(Func<Location, Task> open, IEditorServices services, EditorFont font) : ObservableObject
{
    /// <summary>For the view's read-only editor.</summary>
    public IEditorServices Services { get; } = services;

    public EditorFont Font { get; } = font;

    public const string ToolWindowId = "codeDefinition";

    private string _title = "No definition at the caret.";
    private string _text = string.Empty;
    private string? _languageId;
    private Location? _location;

    public string Title
    {
        get => _title;
        private set => SetField(ref _title, value);
    }

    public string Text
    {
        get => _text;
        private set => SetField(ref _text, value);
    }

    public string? LanguageId
    {
        get => _languageId;
        private set => SetField(ref _languageId, value);
    }

    /// <summary>The definition shown (its range is highlighted).</summary>
    public Location? Location
    {
        get => _location;
        private set => SetField(ref _location, value);
    }

    /// <summary>Raised when another definition is shown (the view scrolls to it).</summary>
    public event EventHandler? Changed;

    public void Show(Location location, string title, string text, string? languageId)
    {
        if (Location == location && Text == text)
        {
            return;
        }

        Title = title;
        Text = text;
        LanguageId = languageId;
        Location = location;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task OpenAsync() => Location is { } location ? open(location) : Task.CompletedTask;
}
