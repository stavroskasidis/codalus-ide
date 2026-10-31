using System.Collections.ObjectModel;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.ViewModels;

/// <summary>
/// A row of the Document Outline: a symbol at its depth in the outline's tree, flattened, with a chevron when it has
/// children (or, in a filtered list, a match with its containing symbol). Its icon and kind names serve the navigation bar
/// and the other symbol lists too.
/// </summary>
public sealed class SymbolNodeViewModel : ObservableObject, ITreeRow
{
    private DocumentSymbol _symbol;
    private bool _hasChildren;
    private bool _isExpanded;

    public SymbolNodeViewModel(DocumentSymbol symbol, string key, int depth, bool hasChildren, bool expanded, string? container = null)
    {
        _symbol = symbol;
        Key = key;
        Depth = depth;
        _hasChildren = hasChildren;
        _isExpanded = expanded;
        Container = container;
    }

    /// <summary>What the row is known by across the outline's rebuilds: its symbol's kind and name (and which of the same), under its parents'.</summary>
    public string Key { get; }

    public int Depth { get; }

    public Avalonia.Thickness Indent => new(4 + (Depth * 16), 0, 0, 0);

    /// <summary>The symbol, the latest the language service sent for the row (its range follows the edits).</summary>
    public DocumentSymbol Symbol => _symbol;

    /// <summary>In a filtered list: the containing symbol, shown as the detail.</summary>
    public string? Container { get; }

    public bool HasChildren => _hasChildren;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(Chevron));
            }
        }
    }

    public string? Chevron => _hasChildren ? (_isExpanded ? "chevron-down" : "chevron-right") : null;

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public string Name => _symbol.Name;

    /// <summary>The signature or type, or (in a filtered list) the containing symbol.</summary>
    public string? Detail => Container ?? _symbol.Detail;

    public string Icon => IconFor(_symbol.Kind);

    /// <summary>Its kind and name: a screen reader hears its expansion, level and place from its tree item (M57).</summary>
    public string AccessibleName => $"{KindName(_symbol.Kind)} {Name}";

    public override string ToString() => Name;

    /// <summary>The same row in a rebuild: its symbol's new range and signature, and whether it has children now.</summary>
    internal void Update(DocumentSymbol symbol, bool hasChildren)
    {
        var detail = Detail;
        _symbol = symbol;
        if (detail != Detail)
        {
            OnPropertyChanged(nameof(Detail));
            OnPropertyChanged(nameof(HasDetail));
        }

        if (_hasChildren != hasChildren)
        {
            _hasChildren = hasChildren;
            OnPropertyChanged(nameof(HasChildren));
            OnPropertyChanged(nameof(Chevron));
        }
    }

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
/// The Document Outline tool window (M12a): the active document's namespaces, types and members, nested, starting
/// expanded; a filter flattens it to the matching symbols. It follows the caret, and selecting a symbol moves the caret
/// there. The tree is a list of rows (M56): a list box creates only the rows in view, where the tree view built a
/// container for every symbol of the file after each pause in typing (seconds for a file of 10,000 symbols). The rows are
/// built off the UI thread, only while the window is shown, and the rows of the symbols that are still there stay, with
/// their expansion, selection and scrolling: each pause in typing replaced every node and expanded what was collapsed.
/// </summary>
public sealed class DocumentOutlineViewModel : ObservableObject, IShownAware
{
    public const string ToolWindowId = "documentOutline";

    private readonly ShownViews _views = new();
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal); // rows' keys
    private CodeEditorController? _editor;
    private CodeEditorController? _shownEditor; // whose rows show
    private string _filter = string.Empty;
    private string _message = "Open a document to see its outline.";
    private SymbolNodeViewModel? _current;
    private bool _stale = true; // the rows wait for the window to be shown
    private int _generation; // the rebuild whose rows are wanted; an older one's are dropped
    private bool _building;

    /// <summary>The outline's rows, the tree flattened: a symbol, then its children's rows while it's expanded.</summary>
    public ResettableCollection<SymbolNodeViewModel> Rows { get; } = [];

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

    public bool HasMessage => Rows.Count == 0;

    /// <summary>Whether the window is shown (its rows follow the editor only then).</summary>
    public bool IsShown => _views.Any;

    /// <summary>How many times the rows were built (tests: a hidden outline builds none).</summary>
    internal int Builds { get; private set; }

    /// <summary>Whether a rebuild is on its way (tests).</summary>
    internal bool IsBuilding => _building;

    public void SetViewShown(object view, bool shown)
    {
        if (!_views.Set(view, shown) || !shown)
        {
            return;
        }

        if (_stale)
        {
            Rebuild();
        }
        else
        {
            UpdateCurrent(); // the caret moved meanwhile
        }
    }

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

        // Another document's outline starts expanded, as the window shows it first.
        _collapsed.Clear();
        Rebuild();
    }

    private void OnSymbolsChanged(object? sender, EventArgs e) => Rebuild();

    private void OnCaretChanged(object? sender, EventArgs e)
    {
        if (_views.Any)
        {
            UpdateCurrent();
        }
    }

    /// <summary>A row of a rebuild, made on the thread pool: the row it is (its key), its symbol and where it sits.</summary>
    private sealed record OutlineRow(string Key, DocumentSymbol Symbol, int Depth, bool HasChildren, string? Container);

    /// <summary>Builds the rows again while the window is shown; else when it's shown next.</summary>
    private void Rebuild()
    {
        if (!_views.Any)
        {
            _stale = true;
            return;
        }

        _stale = false;
        var generation = ++_generation;
        if (!ReferenceEquals(_editor, _shownEditor) && Rows.Count > 0)
        {
            // Another document's: its rows go at once, not when the new ones are flattened (they went to the old
            // document's symbols in the new one meanwhile). The same document's stay until then, so edits don't flicker.
            _shownEditor = null;
            Message = string.Empty;
            Rows.Reset([]);
            OnPropertyChanged(nameof(HasMessage));
            Current = null;
        }

        if (_editor is null || _editor.Insights.Symbols.Count == 0)
        {
            _building = false;
            Message = _editor is null ? "Open a document to see its outline."
                : _editor.Insights.SymbolsVersion < 0 ? "No outline for this document." : "This document has no symbols.";
            Show([], _editor);
            return;
        }

        _ = RebuildAsync(_editor, _editor.Insights.Symbols, new HashSet<string>(_collapsed, StringComparer.Ordinal), _filter, generation);
    }

    private async Task RebuildAsync(CodeEditorController editor, IReadOnlyList<DocumentSymbol> symbols, HashSet<string> collapsed, string filter, int generation)
    {
        _building = true;
        try
        {
            var rows = await Task.Run(() => Flatten(symbols, collapsed, filter)).ConfigureAwait(true);
            if (generation != _generation)
            {
                return;
            }

            Message = filter.Length > 0 && rows.Count == 0 ? $"No symbols match '{filter}'." : string.Empty;
            Show(rows, editor);
        }
        finally
        {
            if (generation == _generation)
            {
                _building = false;
            }
        }
    }

    /// <summary>
    /// Shows a rebuild's rows: those of the same symbols at the start and the end stay (updated), the rest are replaced at
    /// once, so editing a member's body changes no row, and adding one inserts one. Another document's replace them all.
    /// </summary>
    private void Show(IReadOnlyList<OutlineRow> rows, CodeEditorController? editor)
    {
        Builds++;
        SymbolNodeViewModel Create(OutlineRow item) => new(item.Symbol, item.Key, item.Depth, item.HasChildren, !_collapsed.Contains(item.Key), item.Container);
        if (!ReferenceEquals(editor, _shownEditor))
        {
            _shownEditor = editor;
            Rows.Reset(rows.Select(Create));
        }
        else
        {
            Rows.Patch(
                rows,
                (row, item) => row.Depth == item.Depth && string.Equals(row.Key, item.Key, StringComparison.Ordinal),
                Create,
                (row, item) =>
                {
                    row.Update(item.Symbol, item.HasChildren);
                    row.IsExpanded = !_collapsed.Contains(item.Key);
                });
        }

        OnPropertyChanged(nameof(HasMessage));
        UpdateCurrent();
    }

    /// <summary>
    /// The rows of <paramref name="symbols"/>: each symbol, then (while it's expanded) its children's; with a filter, the
    /// symbols whose names contain it, flat, each with its container.
    /// </summary>
    private static List<OutlineRow> Flatten(IReadOnlyList<DocumentSymbol> symbols, IReadOnlySet<string> collapsed, string filter)
    {
        var rows = new List<OutlineRow>();
        Add(rows, symbols, string.Empty, 0, null, collapsed, filter);
        return rows;
    }

    private static void Add(List<OutlineRow> rows, IReadOnlyList<DocumentSymbol> level, string parent, int depth, string? container, IReadOnlySet<string> collapsed, string filter)
    {
        // A symbol's key: its kind and name under its parent's, and which of the same it is (overloads).
        var seen = new Dictionary<(int, string), int>();
        foreach (var symbol in level)
        {
            var same = seen.GetValueOrDefault((symbol.Kind, symbol.Name));
            seen[(symbol.Kind, symbol.Name)] = same + 1;
            var key = $"{parent}/{symbol.Kind.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{symbol.Name}#{same.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            var children = symbol.Children ?? [];
            if (filter.Length > 0)
            {
                if (symbol.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    rows.Add(new OutlineRow(key, symbol, 0, false, container));
                }

                Add(rows, children, key, 0, symbol.Name, collapsed, filter);
            }
            else
            {
                rows.Add(new OutlineRow(key, symbol, depth, children.Length > 0, null));
                if (children.Length > 0 && !collapsed.Contains(key))
                {
                    Add(rows, children, key, depth + 1, null, collapsed, filter);
                }
            }
        }
    }

    /// <summary>Expands (true) or collapses a row: its descendants' rows are inserted below it, or removed, at once.</summary>
    public void SetExpanded(SymbolNodeViewModel row, bool expanded)
    {
        if (!row.HasChildren || row.IsExpanded == expanded || _filter.Length > 0 || Rows.IndexOf(row) is not (>= 0 and var index))
        {
            return;
        }

        if (expanded)
        {
            _collapsed.Remove(row.Key);
            var below = new List<OutlineRow>();
            Add(below, row.Symbol.Children ?? [], row.Key, row.Depth + 1, null, _collapsed, string.Empty);
            row.IsExpanded = true;
            Rows.InsertRange(index + 1, [.. below.Select(r => new SymbolNodeViewModel(r.Symbol, r.Key, r.Depth, r.HasChildren, !_collapsed.Contains(r.Key)))]);
        }
        else
        {
            _collapsed.Add(row.Key);
            var end = index + 1;
            while (end < Rows.Count && Rows[end].Depth > row.Depth)
            {
                end++;
            }

            row.IsExpanded = false;
            Rows.RemoveRange(index + 1, end - index - 1);
        }

        // A rebuild on its way was made with the expansion before this one: it's made again.
        if (_building)
        {
            Rebuild();
        }

        UpdateCurrent();
    }

    public void Toggle(SymbolNodeViewModel row) => SetExpanded(row, !row.IsExpanded);

    /// <summary>The innermost symbol at the caret, or its innermost ancestor shown (a collapsed one hides its descendants).</summary>
    private void UpdateCurrent()
    {
        if (_editor is null || _filter.Length > 0)
        {
            Current = null;
            return;
        }

        var caret = _editor.Model.PositionAt(_editor.Editor.CaretOffset);
        SymbolNodeViewModel? node = null;
        var from = 0;
        foreach (var symbol in _editor.Insights.SymbolPath(caret))
        {
            // Outermost first, as the rows: each one is after the one before.
            var at = -1;
            for (var i = from; i < Rows.Count; i++)
            {
                if (ReferenceEquals(Rows[i].Symbol, symbol))
                {
                    at = i;
                    break;
                }
            }

            if (at < 0)
            {
                break;
            }

            node = Rows[at];
            from = at + 1;
        }

        Current = node;
    }

    /// <summary>Moves the caret to a symbol (Enter or a click), one of the current document's rows.</summary>
    public void Navigate(SymbolNodeViewModel node)
    {
        if (_editor is { } editor && ReferenceEquals(editor, _shownEditor))
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
