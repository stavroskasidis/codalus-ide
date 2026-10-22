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
    }

    public string Component { get; }

    public DesignerCollectionInfo Info { get; }

    public string Title { get; }

    public ObservableCollection<DesignerCollectionItemViewModel> Items { get; } = [];

    /// <summary>The selected component item's properties.</summary>
    public ObservableCollection<PropertyRowViewModel> Properties { get; } = [];

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
        Properties.Clear();
        if (!HasProperties || _selected?.Name is not { } name)
        {
            return;
        }

        foreach (var row in await _document.ItemRowsAsync(name).ConfigureAwait(true))
        {
            Properties.Add(row);
        }
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
