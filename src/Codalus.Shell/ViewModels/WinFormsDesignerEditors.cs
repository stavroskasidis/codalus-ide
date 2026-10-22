using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.WinForms;
using ActionItem = Codalus.Workbench.WinForms.DesignerActionItem;

namespace Codalus.Shell.ViewModels;

/// <summary>An item of a smart tag's panel: a header, a text, a property (edited in place) or a link that runs a method.</summary>
public sealed class DesignerActionRowViewModel : ObservableObject
{
    private readonly WinFormsDesignerDocument _document;
    private readonly string _component;
    private string _text;

    internal DesignerActionRowViewModel(WinFormsDesignerDocument document, string component, ActionItem item)
    {
        _document = document;
        _component = component;
        Item = item;
        _text = item.Value ?? string.Empty;
        Options = [.. item.StandardValues ?? []];
        RunCommand = new RelayCommand(() => _ = Run(), () => !item.Unavailable);
    }

    internal ActionItem Item { get; }

    /// <summary>The designer's text without its mnemonic marker ("&amp;Insert Standard Items"), as the classic panel's labels show it.</summary>
    public string Title => DesignerMnemonics.Strip(Item.DisplayName);

    public string Description => Item.Unavailable
        ? Localizer.Get("msg.winFormsDesigner.actionUnavailable", "This action opens a Windows Forms dialog the designer doesn't show.")
        : Item.Description ?? Title;

    public bool IsHeader => Item.Kind == DesignerActionKinds.Header;

    public bool IsText => Item.Kind == DesignerActionKinds.Text;

    public bool IsMethod => Item.Kind == DesignerActionKinds.Method;

    public bool IsProperty => Item.Kind == DesignerActionKinds.Property;

    public bool IsBoolean => IsProperty && Item.Editor == DesignerEditors.Boolean;

    public bool IsChoice => IsProperty && Item.Editor != DesignerEditors.Boolean && Options.Count > 0;

    public bool IsTextEditor => IsProperty && Item.Editor != DesignerEditors.Boolean && Options.Count == 0;

    public bool IsAvailable => !Item.Unavailable;

    public IReadOnlyList<string> Options { get; }

    public ICommand RunCommand { get; }

    /// <summary>A bool property's check box.</summary>
    public bool IsChecked
    {
        get => string.Equals(_text, "True", StringComparison.OrdinalIgnoreCase);
        set => _ = SetValueAsync(value ? "True" : "False");
    }

    /// <summary>A choice's selected value.</summary>
    public string? Selected
    {
        get => _text;
        set
        {
            if (value is not null && value != _text)
            {
                _ = SetValueAsync(value);
            }
        }
    }

    /// <summary>A text property's value, set when the box loses focus.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (value != _text)
            {
                _ = SetValueAsync(value ?? string.Empty);
            }
        }
    }

    public Task Run() => _document.InvokeActionAsync(_component, Item, text: null);

    public Task SetValueAsync(string value)
    {
        _text = value;
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(IsChecked));
        return _document.InvokeActionAsync(_component, Item, value);
    }
}

/// <summary>
/// A component's smart tag panel (M41b): its designer actions, as Visual Studio's panel shows them (headers,
/// properties edited in place, links that run methods), from the designer host's action lists.
/// </summary>
public sealed class DesignerActionPanelViewModel
{
    internal DesignerActionPanelViewModel(WinFormsDesignerDocument document, string component, IEnumerable<ActionItem> items)
    {
        Component = component;
        Title = Localizer.Format("msg.winFormsDesigner.actionsTitle", "{0} Tasks", document.Find(component)?.Type is { } type ? type[(type.LastIndexOf('.') + 1)..] : component);
        Rows = [.. items.Select(i => new DesignerActionRowViewModel(document, component, i))];
        CloseCommand = new RelayCommand(document.CloseActions);
    }

    public string Component { get; }

    public string Title { get; }

    public IReadOnlyList<DesignerActionRowViewModel> Rows { get; }

    public bool IsEmpty => Rows.Count == 0;

    public ICommand CloseCommand { get; }

    /// <summary>The row with this title (tests).</summary>
    public DesignerActionRowViewModel? Row(string title) => Rows.FirstOrDefault(r => r.Title == title);
}

/// <summary>An item in a collection editor: a component (its name and text), a value (its text, or a table style's size type and size), a list view's item, an image's key.</summary>
public sealed class DesignerCollectionItemViewModel : ObservableObject
{
    private string _text;
    private string _sizeType;
    private string _size;

    public DesignerCollectionItemViewModel(DesignerCollectionItem item)
    {
        Item = item;
        _text = item.Text ?? item.Display ?? string.Empty;
        var (sizeType, size) = Style(item.Value);
        _sizeType = sizeType;
        _size = size;
    }

    public DesignerCollectionItem Item { get; }

    public string? Name => Item.Name;

    public string Display => Item.Name is { } name && Item.Type is not null ? $"{name}   {Item.Type[(Item.Type.LastIndexOf('.') + 1)..]}" : Item.Display ?? _text;

    public string Text
    {
        get => _text;
        set => SetField(ref _text, value ?? string.Empty);
    }

    /// <summary>A table style's size type (<c>Absolute</c>, <c>Percent</c>, <c>AutoSize</c>) and size.</summary>
    public string SizeType
    {
        get => _sizeType;
        set => SetField(ref _sizeType, value ?? "Percent");
    }

    public string Size
    {
        get => _size;
        set => SetField(ref _size, value ?? string.Empty);
    }

    public static IReadOnlyList<string> SizeTypes { get; } = ["Absolute", "Percent", "AutoSize"];

    private static (string, string) Style(DesignerValue? value) =>
        value is { Kind: DesignerValueKinds.New, Arguments: { } arguments }
            ? (arguments.FirstOrDefault()?.Members?.FirstOrDefault() ?? "AutoSize", arguments.Length > 1 ? arguments[1].Value ?? "0" : "0")
            : ("Percent", "50");
}

/// <summary>
/// A collection editor (M41b), as Visual Studio's: the items listed, added (of a type, for a strip's or a grid's),
/// removed and reordered, a component item's properties in a grid of their own. Component items (columns, menu items,
/// tab pages) and images change as you edit (each change undoable); texts (a list's items, a list view's), and a
/// table's styles, change together with OK.
/// </summary>
public sealed class DesignerCollectionEditorViewModel : ObservableObject
{
    private readonly WinFormsDesignerDocument _document;
    private DesignerCollectionItemViewModel? _selected;
    private string _itemType;
    private string _text = string.Empty;
    private bool _isBusy;

    internal DesignerCollectionEditorViewModel(WinFormsDesignerDocument document, string component, DesignerCollectionInfo info)
    {
        _document = document;
        Component = component;
        Info = info;
        _itemType = info.ItemTypes[0];
        Title = Localizer.Format("msg.winFormsDesigner.collectionTitle", "{0}.{1}", component, info.Property);
        AddCommand = new RelayCommand(() => _ = AddAsync());
        RemoveCommand = new RelayCommand(() => _ = RemoveAsync());
        MoveUpCommand = new RelayCommand(() => _ = MoveAsync(-1));
        MoveDownCommand = new RelayCommand(() => _ = MoveAsync(1));
        OkCommand = new RelayCommand(() => _ = OkAsync());
        CloseCommand = new RelayCommand(document.CloseCollectionEditor);
        Grid.ValueRejected += (_, e) => _ = PropertiesWindowViewModel.ShowRejectedAsync(document.Shell.Dialogs, Grid, e);
    }

    /// <summary>The document's values changed (an item's part set, a reset): the item grid reads them again, in place.</summary>
    internal void OnPropertiesChanged() => _ = LoadPropertiesAsync();

    public string Component { get; }

    public DesignerCollectionInfo Info { get; }

    public string Title { get; }

    public ObservableCollection<DesignerCollectionItemViewModel> Items { get; } = [];

    /// <summary>The selected component item's properties (#68: the shared property grid, refreshed in place after each change).</summary>
    public PropertyGridViewModel Grid { get; } = new();

    /// <summary>The selected item's properties (the grid's top-level rows).</summary>
    public IReadOnlyList<PropertyRowViewModel> Properties => [.. Grid.Categories.SelectMany(c => c.Rows)];

    /// <summary>Strings edited as text, a line each (a list's items, a list view's items): the String Collection Editor.</summary>
    public bool IsText => Info.Kind == DesignerCollections.Locals || (Info.Kind == DesignerCollections.Values && Info.ItemTypes[0] == "System.String");

    /// <summary>A table's column or row styles: a size type and a size each.</summary>
    public bool IsStyles => Info.Kind == DesignerCollections.Values && !IsText;

    /// <summary>Items added and removed as you go (components, images), as opposed to with OK.</summary>
    public bool IsLive => Info.Kind is DesignerCollections.Components or DesignerCollections.Images;

    public bool IsList => !IsText;

    public bool HasProperties => Info.Kind == DesignerCollections.Components;

    public bool HasItemTypes => Info.ItemTypes.Length > 1;

    /// <summary>The types new items can have (a tool strip's buttons, labels, separators…), by their simple names.</summary>
    public IReadOnlyList<string> ItemTypes => [.. Info.ItemTypes.Select(t => t[(t.LastIndexOf('.') + 1)..])];

    public string ItemType
    {
        get => _itemType[(_itemType.LastIndexOf('.') + 1)..];
        set => _itemType = Info.ItemTypes.FirstOrDefault(t => t.EndsWith("." + value, StringComparison.Ordinal)) ?? Info.ItemTypes[0];
    }

    public string Text
    {
        get => _text;
        set => SetField(ref _text, value ?? string.Empty);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public DesignerCollectionItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                _ = LoadPropertiesAsync();
            }
        }
    }

    public ICommand AddCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public ICommand OkCommand { get; }

    public ICommand CloseCommand { get; }

    /// <summary>The items from the designer host (after each change of a live collection).</summary>
    public async Task RefreshAsync(string? select = null)
    {
        var result = await _document.CollectionItemsAsync(Component, Info.Property).ConfigureAwait(true);
        var selected = select ?? _selected?.Name;
        Items.Clear();
        foreach (var item in result?.Items ?? [])
        {
            Items.Add(new DesignerCollectionItemViewModel(item));
        }

        Text = string.Join("\n", Items.Select(i => i.Text));
        Selected = Items.FirstOrDefault(i => i.Name is not null && i.Name == selected) ?? (Items.Count > 0 && IsLive ? Items[^1] : null);
    }

    private async Task LoadPropertiesAsync()
    {
        if (!HasProperties || _selected?.Name is not { } name)
        {
            Grid.Clear();
            return;
        }

        var rows = await _document.ItemRowsAsync(name).ConfigureAwait(true);
        if (_selected?.Name != name)
        {
            return;
        }

        Grid.Context = name;
        Grid.Show([.. rows.GroupBy(r => r.Category.Length == 0 ? "Misc" : r.Category).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new PropertyCategoryViewModel(g.Key, [.. g.Select(r => r.Row).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)]))], alphabetical: false);
        OnPropertyChanged(nameof(Properties));
    }

    private IEnumerable<DesignerCollectionItem> Current => Items.Select(i => i.Item.Name is { } name ? new DesignerCollectionItem { Name = name } : i.Item);

    /// <summary>Add: a new component item of the chosen type (named as Visual Studio names it), an image from a file, a new style.</summary>
    public async Task AddAsync(string? imagePath = null)
    {
        switch (Info.Kind)
        {
            case DesignerCollections.Components:
            {
                var name = Info.Property == "TabPages" ? _document.NewName(_itemType) : _document.ItemName(_itemType, null);
                await ApplyAsync([.. Current, new DesignerCollectionItem { Name = name, Type = _itemType }], name).ConfigureAwait(true);
                break;
            }

            case DesignerCollections.Images:
            {
                imagePath ??= _document.Shell.Window is { } window
                    ? await window.PickFileAsync(Localizer.Get("msg.winFormsDesigner.pickImage", "Import an Image"), "Images", ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.ico"]).ConfigureAwait(true)
                    : null;
                if (imagePath is null)
                {
                    return;
                }

                var bytes = await File.ReadAllBytesAsync(imagePath).ConfigureAwait(true);
                var key = Path.GetFileName(imagePath);
                await ApplyAsync([.. Current, new DesignerCollectionItem { Name = key, Bytes = Convert.ToBase64String(bytes) }], key).ConfigureAwait(true);
                break;
            }

            default:
                var style = new DesignerCollectionItemViewModel(new DesignerCollectionItem { Display = "Percent 50" });
                Items.Add(style);
                Selected = style;
                break;
        }
    }

    public async Task RemoveAsync()
    {
        if (_selected is not { } selected)
        {
            return;
        }

        if (IsLive)
        {
            await ApplyAsync([.. Current.Where((_, i) => Items[i] != selected)], null).ConfigureAwait(true);
            return;
        }

        Items.Remove(selected);
    }

    public async Task MoveAsync(int delta)
    {
        if (_selected is not { } selected || Items.IndexOf(selected) is var index && (index + delta < 0 || index + delta >= Items.Count))
        {
            return;
        }

        Items.Move(index, index + delta);
        if (IsLive)
        {
            await ApplyAsync([.. Current], selected.Name).ConfigureAwait(true);
        }
    }

    /// <summary>OK: texts and styles written together; a live collection is already written.</summary>
    public async Task OkAsync()
    {
        if (IsText)
        {
            var lines = Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var lineItems = lines.Length == 1 && lines[0].Length == 0 ? [] : lines;
            if (Info.Kind == DesignerCollections.Locals)
            {
                // A list view's items: line i is the i-th item (its text changed), the rest new or gone.
                var existing = Items.Select(i => i.Name).ToList();
                await _document.SetCollectionAsync(Component, Info.Property,
                    [.. lineItems.Select((line, i) => new DesignerCollectionItem { Name = i < existing.Count ? existing[i] : null, Text = line })]).ConfigureAwait(true);
            }
            else
            {
                await _document.SetCollectionAsync(Component, Info.Property, [.. lineItems.Select(l => new DesignerCollectionItem { Value = DesignerValue.String(l) })]).ConfigureAwait(true);
            }
        }
        else if (IsStyles)
        {
            var styleType = Info.ItemTypes[0];
            await _document.SetCollectionAsync(Component, Info.Property, [.. Items.Select(i => new DesignerCollectionItem
            {
                Value = i.SizeType == "AutoSize"
                    ? DesignerValue.NewOf(styleType, DesignerValue.EnumOf("System.Windows.Forms.SizeType", "AutoSize"))
                    : DesignerValue.NewOf(styleType, DesignerValue.EnumOf("System.Windows.Forms.SizeType", i.SizeType),
                        DesignerValue.Float(float.TryParse(i.Size, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) ? size : 0F)),
            })]).ConfigureAwait(true);
        }

        _document.CloseCollectionEditor();
    }

    private async Task ApplyAsync(IReadOnlyList<DesignerCollectionItem> items, string? select)
    {
        IsBusy = true;
        try
        {
            await _document.SetCollectionAsync(Component, Info.Property, items).ConfigureAwait(true);
            await RefreshAsync(select).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>
/// The font editor (#68, D41 question 2): a panel over the designer surface in the in-window dialog's frame (the title,
/// the component and property as its context, the buttons in the OS's order), as the classic IDE's font dialog: the
/// families from the font's Name choices, a style, a size, Strikeout and Underline, and a sample. OK sends only the parts
/// that changed, in one request: one change, one undo step (the other parts, GdiCharSet and Unit among them, kept by the
/// host's CreateInstance).
/// </summary>
public sealed class DesignerFontEditorViewModel : ObservableObject
{
    private readonly WinFormsDesignerDocument _document;
    private readonly IReadOnlyList<string> _components;
    private readonly string _path;
    private readonly (string Family, string Size, bool Bold, bool Italic, bool Strikeout, bool Underline) _initial;
    private string _family;
    private string _style;
    private string _size;
    private bool _strikeout;
    private bool _underline;

    internal DesignerFontEditorViewModel(WinFormsDesignerDocument document, IReadOnlyList<string> components, string path, string context, IReadOnlyList<string> families,
        string family, string size, bool bold, bool italic, bool strikeout, bool underline)
    {
        _document = document;
        _components = components;
        _path = path;
        Context = context;
        Families = families;
        _family = family;
        _size = size;
        _style = StyleOf(bold, italic);
        _strikeout = strikeout;
        _underline = underline;
        _initial = (family, size, bold, italic, strikeout, underline);
        OkCommand = new RelayCommand(() => _ = OkAsync());
        CancelCommand = new RelayCommand(document.CloseFontEditor);
    }

    public string Title => Localizer.Get("msg.winFormsDesigner.fontEditor", "Font");

    /// <summary>What it edits: "placeOrderButton · Font".</summary>
    public string Context { get; }

    /// <summary>The families the host lists (Font.Name's choices); empty when it lists none, and the family is typed.</summary>
    public IReadOnlyList<string> Families { get; }

    public IReadOnlyList<string> Styles { get; } =
    [
        Localizer.Get("msg.winFormsDesigner.fontRegular", "Regular"),
        Localizer.Get("msg.winFormsDesigner.fontItalic", "Italic"),
        Localizer.Get("msg.winFormsDesigner.fontBold", "Bold"),
        Localizer.Get("msg.winFormsDesigner.fontBoldItalic", "Bold Italic"),
    ];

    public IReadOnlyList<string> Sizes { get; } = ["8", "9", "10", "11", "12", "14", "16", "18", "20", "22", "24", "26", "28", "36", "48", "72"];

    private string StyleOf(bool bold, bool italic) => Styles[(bold ? 2 : 0) + (italic ? 1 : 0)];

    private int StyleIndex => Math.Max(0, Styles.ToList().IndexOf(_style));

    public string Family
    {
        get => _family;
        set => SetField(ref _family, value ?? string.Empty);
    }

    public string Style
    {
        get => _style;
        set => SetField(ref _style, value ?? Styles[0]);
    }

    public string Size
    {
        get => _size;
        set => SetField(ref _size, value ?? string.Empty);
    }

    public bool Strikeout
    {
        get => _strikeout;
        set => SetField(ref _strikeout, value);
    }

    public bool Underline
    {
        get => _underline;
        set => SetField(ref _underline, value);
    }

    public bool IsBold => StyleIndex >= 2;

    public bool IsItalic => StyleIndex % 2 == 1;

    public ICommand OkCommand { get; }

    public ICommand CancelCommand { get; }

    /// <summary>The parts that changed, as dotted paths (<c>Font.Size</c>), for every selected component.</summary>
    public IReadOnlyList<DesignerPropertyEdit> Edits()
    {
        var parts = new List<(string Part, string Text)>();
        if (_family != _initial.Family)
        {
            parts.Add(("Name", _family));
        }

        if (_size != _initial.Size)
        {
            parts.Add(("Size", _size));
        }

        if (IsBold != _initial.Bold)
        {
            parts.Add(("Bold", IsBold ? "True" : "False"));
        }

        if (IsItalic != _initial.Italic)
        {
            parts.Add(("Italic", IsItalic ? "True" : "False"));
        }

        if (_strikeout != _initial.Strikeout)
        {
            parts.Add(("Strikeout", _strikeout ? "True" : "False"));
        }

        if (_underline != _initial.Underline)
        {
            parts.Add(("Underline", _underline ? "True" : "False"));
        }

        return [.. _components.SelectMany(c => parts.Select(p => new DesignerPropertyEdit(c, _path + "." + p.Part) { Text = p.Text }))];
    }

    /// <summary>OK: the changed parts in one request; the editor closes (the host's error, if any, stays in the designer's status).</summary>
    public async Task OkAsync()
    {
        var edits = Edits();
        _document.CloseFontEditor();
        if (edits.Count > 0)
        {
            await _document.SetPropertiesAsync(edits).ConfigureAwait(true);
        }
    }
}

/// <summary>
/// The String Collection Editor (#68): a string array's items a line each (a text box's Lines), split on any line
/// ending; OK writes the array.
/// </summary>
public sealed class DesignerStringsEditorViewModel : ObservableObject
{
    private readonly WinFormsDesignerDocument _document;
    private readonly IReadOnlyList<string> _components;
    private readonly string _path;
    private string _text;

    internal DesignerStringsEditorViewModel(WinFormsDesignerDocument document, IReadOnlyList<string> components, string path, string context, string text)
    {
        _document = document;
        _components = components;
        _path = path;
        Context = context;
        _text = text;
        OkCommand = new RelayCommand(() => _ = OkAsync());
        CancelCommand = new RelayCommand(document.CloseStringsEditor);
    }

    public string Title => Localizer.Get("msg.winFormsDesigner.stringsEditor", "String Collection Editor");

    public string Context { get; }

    public string Prompt => Localizer.Get("msg.winFormsDesigner.stringsPrompt", "Enter the strings in the collection (one per line):");

    public string Text
    {
        get => _text;
        set => SetField(ref _text, value ?? string.Empty);
    }

    public ICommand OkCommand { get; }

    public ICommand CancelCommand { get; }

    /// <summary>OK: the lines as a <c>string[]</c>, in one request.</summary>
    public async Task OkAsync()
    {
        var items = PropertyText.Lines(_text);
        _document.CloseStringsEditor();
        await _document.SetPropertiesAsync([.. _components.Select(c => new DesignerPropertyEdit(c, _path) { Value = DesignerValue.ArrayOf("System.String", [.. items.Select(DesignerValue.String)]) })]).ConfigureAwait(true);
    }
}
