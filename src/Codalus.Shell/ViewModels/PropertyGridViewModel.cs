using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>How a row of the Properties window is edited.</summary>
public enum PropertyRowKind
{
    ReadOnly,
    Text,
    Choice,

    /// <summary>Flags of an enum: the names typed, or checked in the row's drop-down (M41a, #68).</summary>
    Flags,

    /// <summary>A color as text (<c>Red</c>, <c>255, 128, 0</c>) with a swatch, and the Custom, Web and System drop-down (M41a, #68).</summary>
    Color,

    /// <summary>A Windows Forms control's Dock: the classic IDE's picker (Top, Left, Fill, Right, Bottom around, None below; M41b).</summary>
    Dock,

    /// <summary>A Windows Forms control's Anchor: the classic IDE's picker (a bar to each edge to toggle; M41b).</summary>
    Anchor,

    /// <summary>A <c>ContentAlignment</c>: the 3×3 picker (#68).</summary>
    ContentAlignment,

    /// <summary>Text with line breaks: the multi-line drop-down, written with CRLF (#68).</summary>
    Multiline,

    /// <summary>A menu item's shortcut keys: modifiers and a key (#68).</summary>
    ShortcutKeys,

    /// <summary>A date: a month calendar (#68).</summary>
    Date,

    /// <summary>A font: its text, its parts and the font editor behind "…" (#68).</summary>
    Font,
}

/// <summary>A flag of a <see cref="PropertyRowKind.Flags"/> row.</summary>
public sealed class FlagOptionViewModel(string name, bool isChecked, Action changed) : ObservableObject
{
    private bool _isChecked = isChecked;

    public string Name { get; } = name;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetField(ref _isChecked, value))
            {
                changed();
            }
        }
    }
}

/// <summary>
/// One row of the Properties window: a name, a value and how it is edited (#68: as the classic IDE's property grid). A
/// row may have parts (<see cref="Children"/>: a font's Name and Size, a point's X and Y), shown under it when expanded;
/// Reset and an editor behind "…"; and a commit whose task is a <see cref="Task{TResult}"/> of the host's error, which
/// the grid reports and lets the user correct (the invalid-value dialog).
/// </summary>
public sealed class PropertyRowViewModel : ObservableObject
{
    private readonly PropertyRowKind _kind;
    private Func<string?, Task>? _commit;
    private string _value;
    private string _text;
    private string _description;
    private bool _isSet;
    private IReadOnlyList<PropertyOption> _baseOptions;
    private List<PropertyOption> _options = [];
    private IReadOnlyList<FlagOptionViewModel> _flags = [];
    private IReadOnlyList<PropertyRowViewModel> _children = [];
    private bool _isExpanded;
    private bool _isSelected;
    private int _depth;
    private Func<Task>? _reset;
    private bool _canReset;
    private ICommand? _modalEditor;
    private bool _choicesEditable;
    private bool _isMixed;
    private string? _path;
    private string? _committed;

    public PropertyRowViewModel(string name, string value, PropertyRowKind kind, string description, Func<string?, Task>? commit = null, IReadOnlyList<PropertyOption>? options = null, bool isSet = false)
    {
        Name = name;
        _value = value;
        _text = value;
        _kind = commit is null ? PropertyRowKind.ReadOnly : kind;
        _description = description;
        _commit = commit;
        _isSet = isSet;
        _baseOptions = [.. options ?? []];
        RefreshOptions();
        DockCommand = new RelayCommand(dock => _ = dock is string style ? CommitAsync(style) : Task.CompletedTask);
        ResetCommand = new RelayCommand(() => _ = _reset?.Invoke(), () => _reset is not null && _canReset);
        ExpandCommand = new RelayCommand(() => Owner?.Toggle(this));
    }

    /// <summary>The choices: the given ones, and the current value when an exclusive choice's list lacks it.</summary>
    private void RefreshOptions()
    {
        List<PropertyOption> list = [.. _baseOptions];
        if (_kind == PropertyRowKind.Choice && !_choicesEditable && !_isMixed && !list.Any(o => string.Equals(o.Value, _value, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(new PropertyOption(_value, _value.Length == 0 ? "(none)" : _value));
        }

        _options = list;
        _flags = FlagsOf(_options, _value);
    }

    private IReadOnlyList<FlagOptionViewModel> FlagsOf(List<PropertyOption> options, string value)
    {
        if (_kind is not (PropertyRowKind.Flags or PropertyRowKind.Anchor))
        {
            return [];
        }

        var set = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. options.Select(o => new FlagOptionViewModel(o.Value, set.Contains(o.Value), CommitFlags))];
    }

    /// <summary>The grid that shows the row (set by <see cref="PropertyGridViewModel.Show"/>): it reports a rejected value and toggles expansion.</summary>
    internal PropertyGridViewModel? Owner { get; set; }

    public string Name { get; }

    /// <summary>What identifies the row across refreshes: the edit path (<c>Font.Bold</c>), or the name.</summary>
    public string Path
    {
        get => _path ?? Name;
        init => _path = value;
    }

    public string Description => _description;

    /// <summary>The trimmed name's tooltip: the whole name, and the description when there is one (M34g: rows without one had none).</summary>
    public string NameToolTip => string.IsNullOrEmpty(_description) ? Name : $"{Name}: {_description}";

    /// <summary>The trimmed value's tooltip (D41 question 19): the whole value.</summary>
    public string? ValueToolTip => DisplayValue.Length == 0 ? null : DisplayValue;

    public PropertyRowKind Kind => _kind;

    public bool IsReadOnly => _kind == PropertyRowKind.ReadOnly;

    public bool IsText => _kind == PropertyRowKind.Text;

    public bool IsChoice => _kind == PropertyRowKind.Choice;

    public bool IsFlags => _kind == PropertyRowKind.Flags;

    public bool IsColor => _kind == PropertyRowKind.Color;

    public bool IsDock => _kind == PropertyRowKind.Dock;

    public bool IsAnchor => _kind == PropertyRowKind.Anchor;

    /// <summary>The value shown on a picker's button (Dock, Anchor).</summary>
    public bool IsPicker => _kind is PropertyRowKind.Dock or PropertyRowKind.Anchor;

    /// <summary>
    /// The value is typed in the cell's text box (#68: every settable row but an exclusive choice and a picker, so a
    /// test or the smoke test can set <see cref="Text"/> on a multi-line Text row, a font, a color, a date).
    /// </summary>
    public bool IsTextEditor => _kind is PropertyRowKind.Text or PropertyRowKind.Color or PropertyRowKind.Multiline or PropertyRowKind.ShortcutKeys or PropertyRowKind.Date
        or PropertyRowKind.Font or PropertyRowKind.Flags or PropertyRowKind.ContentAlignment
        || (_kind == PropertyRowKind.Choice && _choicesEditable);

    /// <summary>A value with line breaks shows each as ⏎ and is edited in the multi-line drop-down only (D41 question 16), so a commit never joins its lines.</summary>
    public bool HasLineBreaks => _kind is PropertyRowKind.Multiline or PropertyRowKind.Text && _value.Contains('\n', StringComparison.Ordinal);

    /// <summary>The cell holds a text box (a text editor whose value has no line break).</summary>
    public bool ShowsTextBox => IsTextEditor && !HasLineBreaks;

    /// <summary>The cell shows the value as text (read-only, an exclusive choice, a picker, a value with line breaks).</summary>
    public bool ShowsValue => !ShowsTextBox;

    /// <summary>Whether the row opens a drop-down from its arrow (F4, Alt+Down).</summary>
    public bool HasDropDown => _kind is PropertyRowKind.Color or PropertyRowKind.ContentAlignment or PropertyRowKind.Multiline or PropertyRowKind.ShortcutKeys
        or PropertyRowKind.Date or PropertyRowKind.Dock or PropertyRowKind.Anchor or PropertyRowKind.Flags
        || (_kind == PropertyRowKind.Choice && _options.Count > 0);

    /// <summary>The arrow shows on the selected row only, as in the classic IDE (D41 question 10).</summary>
    public bool ShowsDropDownButton => _isSelected && HasDropDown;

    /// <summary>The "…" shows on the selected row only.</summary>
    public bool ShowsModalButton => _isSelected && HasModalEditor;

    /// <summary>The value as the cell shows it: blank when the selection doesn't share it; a choice's label; line breaks as ⏎.</summary>
    public string DisplayValue
    {
        get
        {
            if (_isMixed)
            {
                return string.Empty;
            }

            if (_kind == PropertyRowKind.Choice && SelectedOption is { } option)
            {
                return option.Label;
            }

            return HasLineBreaks ? string.Join(" ⏎ ", _value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) : _value;
        }
    }

    /// <summary>Read-only values are muted (D41 question 11).</summary>
    public bool IsValueMuted => IsReadOnly;

    /// <summary>Set in the code (bold in the value cell, as the classic IDE marks a value that isn't the default; D41 question 11).</summary>
    public bool IsSet => _isSet;

    public string Value => _value;

    public List<PropertyOption> Options => _options;

    /// <summary>The selected components don't share the value (D41 question 11: blank; typing sets them all).</summary>
    public bool IsMixed
    {
        get => _isMixed;
        init
        {
            _isMixed = value;
            _text = value ? string.Empty : _text;
            RefreshOptions();
        }
    }

    /// <summary>A choice that also takes a typed value (#68: Cursor; the converter's standard values aren't exclusive).</summary>
    public bool ChoicesEditable
    {
        get => _choicesEditable;
        init
        {
            _choicesEditable = value;
            RefreshOptions();
        }
    }

    /// <summary>A Cursor's choices: each drawn with its picture (D41 question 13).</summary>
    public bool IsCursor { get; init; }

    public string AccessibleName => $"{Name}: {DisplayValue}";

    // ------------------------------------------------------------------ parts

    /// <summary>The value's parts (a font's, a point's), in the converter's order; the grid shows them under the row when expanded.</summary>
    public IReadOnlyList<PropertyRowViewModel> Children
    {
        get => _children;
        init => _children = value;
    }

    public bool IsExpandable => _children.Count > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        internal set
        {
            if (SetField(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(Chevron));
                OnPropertyChanged(nameof(ExpanderName));
            }
        }
    }

    /// <summary>The level: 0 for a property, 1 for its part, 2 for a part's part.</summary>
    public int Depth
    {
        get => _depth;
        internal set
        {
            if (SetField(ref _depth, value))
            {
                OnPropertyChanged(nameof(NameIndent));
                OnPropertyChanged(nameof(ChevronMargin));
            }
        }
    }

    /// <summary>The name's left padding: 16 px per level after the categories' gutter (D41 question 3).</summary>
    public Thickness NameIndent => new(4 + (_depth * 16), 0, 0, 0);

    /// <summary>The chevron's place: the gutter for a property, the level's indent for a part.</summary>
    public Thickness ChevronMargin => new(2 + (_depth * 16), 0, 0, 0);

    public string Chevron => _isExpanded ? "chevron-down" : "chevron-right";

    /// <summary>The expander's name for screen readers ("Expand Font"; <c>{l:T}</c> takes no arguments, so it is built here).</summary>
    public string ExpanderName => _isExpanded
        ? Localizer.Format("msg.propertyGrid.collapse", "Collapse {0}", Name)
        : Localizer.Format("msg.propertyGrid.expand", "Expand {0}", Name);

    public ICommand ExpandCommand { get; }

    // ------------------------------------------------------------------ Reset, the "…", the context menu

    /// <summary>Puts the default back (the context menu's Reset), enabled only when <see cref="CanReset"/>.</summary>
    public Func<Task>? Reset
    {
        get => _reset;
        init => _reset = value;
    }

    /// <summary>The host says Reset can put the value back (CanResetValue: not for (Name), nor for a value that has no default).</summary>
    public bool CanReset
    {
        get => _canReset;
        init => _canReset = value;
    }

    public ICommand ResetCommand { get; }

    /// <summary>The editor behind "…" (the font editor, the String Collection Editor, a collection's, Select Resource), or null.</summary>
    public ICommand? ModalEditor
    {
        get => _modalEditor;
        init => _modalEditor = value;
    }

    public bool HasModalEditor => _modalEditor is not null;

    public string ModalEditorName => Localizer.Format("msg.propertyGrid.editValue", "Edit {0}…", Name);

    public string DropDownName => Localizer.Get("msg.propertyGrid.openDropDown", "Open the drop-down (Alt+Down)");

    /// <summary>Selected in the grid (its arrow and "…" show).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (SetField(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(ShowsDropDownButton));
                OnPropertyChanged(nameof(ShowsModalButton));
            }
        }
    }

    /// <summary>The cell's text box has the keyboard focus: a refresh keeps the text being typed.</summary>
    public bool IsEditing { get; set; }

    /// <summary>A command next to the value (the Events view's Go to or create the handler; a double-click on the name runs it), or null.</summary>
    public ICommand? Action { get; init; }

    public string ActionTitle { get; init; } = string.Empty;

    public bool HasAction => Action is not null;

    /// <summary>The action's icon.</summary>
    public string ActionIcon { get; init; } = "go-to";

    /// <summary>A second command next to the value, or null (kept for callers of M41b; the grid shows neither as a button).</summary>
    public ICommand? SecondaryAction { get; init; }

    public string SecondaryActionTitle { get; init; } = string.Empty;

    public bool HasSecondaryAction => SecondaryAction is not null;

    // ------------------------------------------------------------------ Dock, Anchor and flags

    /// <summary>The Dock picker's buttons (their parameter is the dock style).</summary>
    public ICommand DockCommand { get; }

    /// <summary>The Anchor picker's bars.</summary>
    public FlagOptionViewModel? AnchorTop => _flags.FirstOrDefault(f => f.Name == "Top");

    public FlagOptionViewModel? AnchorBottom => _flags.FirstOrDefault(f => f.Name == "Bottom");

    public FlagOptionViewModel? AnchorLeft => _flags.FirstOrDefault(f => f.Name == "Left");

    public FlagOptionViewModel? AnchorRight => _flags.FirstOrDefault(f => f.Name == "Right");

    /// <summary>Whether a Dock picker's button is the current value.</summary>
    public bool IsDocked(string dock) => string.Equals(_value, dock, StringComparison.OrdinalIgnoreCase);

    public bool DockTop => IsDocked("Top");

    public bool DockBottom => IsDocked("Bottom");

    public bool DockLeft => IsDocked("Left");

    public bool DockRight => IsDocked("Right");

    public bool DockFill => IsDocked("Fill");

    public bool DockNone => IsDocked("None");

    /// <summary>The toggles of a <see cref="PropertyRowKind.Flags"/> or <see cref="PropertyRowKind.Anchor"/> row.</summary>
    public IReadOnlyList<FlagOptionViewModel> Flags => _flags;

    private void CommitFlags()
    {
        var text = string.Join(", ", _flags.Where(f => f.IsChecked).Select(f => f.Name));
        _ = CommitAsync(text.Length == 0 ? "None" : text);
    }

    // ------------------------------------------------------------------ colors

    /// <summary>A color row's swatch, when its text names or spells a color (a system color's name too).</summary>
    public Avalonia.Media.IBrush? Swatch => _kind == PropertyRowKind.Color && !_isMixed && ParseColor(_value) is { } color ? new Avalonia.Media.SolidColorBrush(color) : null;

    public bool HasSwatch => Swatch is not null;

    /// <summary>
    /// <c>Red</c>, <c>#FF8000</c>, <c>255, 128, 0</c>, <c>128, 255, 128, 0</c>, or a system color's name (<c>GrayText</c>,
    /// <c>Control</c>: System.Drawing's known colors, the real ones on Windows and fixed values elsewhere), as a color;
    /// null otherwise.
    /// </summary>
    internal static Avalonia.Media.Color? ParseColor(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length is 3 or 4 && parts.All(p => byte.TryParse(p, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _)))
        {
            var bytes = parts.Select(p => byte.Parse(p, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            return bytes.Length == 3 ? Avalonia.Media.Color.FromRgb(bytes[0], bytes[1], bytes[2]) : Avalonia.Media.Color.FromArgb(bytes[0], bytes[1], bytes[2], bytes[3]);
        }

        if (text.Length == 0)
        {
            return null;
        }

        if (System.Drawing.Color.FromName(text) is { IsKnownColor: true } known)
        {
            return Avalonia.Media.Color.FromUInt32((uint)known.ToArgb());
        }

        return Avalonia.Media.Color.TryParse(text, out var named) ? named : null;
    }

    // ------------------------------------------------------------------ editing

    /// <summary>The text box's value, committed when it loses focus or on Enter.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (SetField(ref _text, value ?? string.Empty) && (_text != _value || _isMixed) && _commit is not null)
            {
                _committed = _text;
                _ = CommitAsync(_text);
            }
        }
    }

    /// <summary>Esc: the text box shows the value again, nothing committed.</summary>
    public void RevertText()
    {
        _committed = null;
        SetField(ref _text, _isMixed ? string.Empty : _value, nameof(Text));
    }

    public PropertyOption? SelectedOption
    {
        get => _isMixed ? null : _options.FirstOrDefault(o => string.Equals(o.Value, _value, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null && (_isMixed || !string.Equals(value.Value, _value, StringComparison.OrdinalIgnoreCase)) && _commit is not null)
            {
                _ = CommitAsync(value.Value);
            }
        }
    }

    /// <summary>Commits a value (a picker's, a drop-down's): through the row's commit; a host's error goes to the grid.</summary>
    public async Task CommitAsync(string? text)
    {
        if (_commit is not { } commit)
        {
            return;
        }

        var task = commit(text);
        await task.ConfigureAwait(true);
        if (task is Task<string?> { Result: { } error })
        {
            Owner?.OnRejected(this, text, error);
        }
    }

    /// <summary>Whether the row commits values (not read-only).</summary>
    public bool CanCommit => _commit is not null;

    /// <summary>A choice or a bool takes its next value, wrapping around (a double-click on the value, as in the classic IDE).</summary>
    public bool CycleValue()
    {
        if (_kind != PropertyRowKind.Choice || _options.Count == 0 || _commit is null)
        {
            return false;
        }

        var index = _options.FindIndex(o => string.Equals(o.Value, _value, StringComparison.OrdinalIgnoreCase));
        _ = CommitAsync(_options[(index + 1) % _options.Count].Value);
        return true;
    }

    /// <summary>
    /// A double-click: a row with parts expands or collapses; a choice cycles; an editor behind "…" opens; a drop-down
    /// opens; an action (an event's handler) runs.
    /// </summary>
    public void DoubleClick()
    {
        if (IsExpandable)
        {
            Owner?.Toggle(this);
        }
        else if (CycleValue())
        {
        }
        else if (_modalEditor is { } editor && editor.CanExecute(null))
        {
            editor.Execute(null);
        }
        else if (HasDropDown && Owner is { } owner)
        {
            owner.RequestDropDown(this);
        }
        else if (Action is { } action && action.CanExecute(null))
        {
            action.Execute(null);
        }
    }

    /// <summary>
    /// The row takes a fresh row's state after a refresh (same path and kind): its value, whether it is set, its choices,
    /// description and delegates. The text being typed stays unless it is what was just committed.
    /// </summary>
    internal void Update(PropertyRowViewModel fresh)
    {
        _commit = fresh._commit;
        _reset = fresh._reset;
        _modalEditor = fresh._modalEditor;
        var valueChanged = _value != fresh._value || _isMixed != fresh._isMixed;
        _value = fresh._value;
        _isMixed = fresh._isMixed;
        _description = fresh._description;
        _isSet = fresh._isSet;
        _canReset = fresh._canReset;
        _choicesEditable = fresh._choicesEditable;
        _baseOptions = fresh._baseOptions;
        RefreshOptions();
        if (!IsEditing || _committed == _text || valueChanged && !IsEditing)
        {
            _text = _isMixed ? string.Empty : _value;
            _committed = null;
        }

        foreach (var name in s_refreshed)
        {
            OnPropertyChanged(name);
        }

        ((RelayCommand)ResetCommand).RaiseCanExecuteChanged();
    }

    /// <summary>The parts after a refresh (merged by the grid).</summary>
    internal void SetChildren(IReadOnlyList<PropertyRowViewModel> children)
    {
        var wasExpandable = IsExpandable;
        _children = children;
        if (wasExpandable != IsExpandable)
        {
            OnPropertyChanged(nameof(IsExpandable));
        }
    }

    private static readonly string[] s_refreshed =
    [
        nameof(Value), nameof(Text), nameof(Description), nameof(NameToolTip), nameof(ValueToolTip), nameof(IsSet), nameof(Options), nameof(SelectedOption),
        nameof(DisplayValue), nameof(AccessibleName), nameof(Swatch), nameof(HasSwatch), nameof(HasLineBreaks), nameof(ShowsTextBox), nameof(ShowsValue),
        nameof(IsMixed), nameof(CanReset), nameof(ChoicesEditable), nameof(Flags), nameof(AnchorTop), nameof(AnchorBottom), nameof(AnchorLeft), nameof(AnchorRight),
        nameof(DockTop), nameof(DockBottom), nameof(DockLeft), nameof(DockRight), nameof(DockFill), nameof(DockNone), nameof(HasModalEditor), nameof(ShowsModalButton),
        nameof(HasDropDown), nameof(ShowsDropDownButton), nameof(IsTextEditor),
    ];
}

/// <summary>A category of the Properties window: its header (a chevron, collapsible) and its rows.</summary>
public sealed class PropertyCategoryViewModel(string Title, IReadOnlyList<PropertyRowViewModel> Rows) : ObservableObject
{
    private bool _isExpanded = true;
    private IReadOnlyList<PropertyRowViewModel> _rows = Rows;

    public string Title { get; } = Title;

    public IReadOnlyList<PropertyRowViewModel> Rows
    {
        get => _rows;
        internal set => _rows = value;
    }

    /// <summary>Its rows show (a click on the header, Left and Right toggle it; collapsed categories stay so across selections).</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        internal set
        {
            if (SetField(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(Chevron));
            }
        }
    }

    public string Chevron => _isExpanded ? "chevron-down" : "chevron-right";

    /// <summary>Alphabetical shows the rows without a header.</summary>
    public bool ShowHeader { get; internal set; } = true;

    internal PropertyGridViewModel? Owner { get; set; }

    public ICommand ToggleCommand => field ??= new RelayCommand(() => Owner?.Toggle(this));

    /// <summary>Selected in the grid (a header takes the keyboard's selection too).</summary>
    public bool IsSelected
    {
        get;
        internal set => SetField(ref field, value);
    }
}

/// <summary>A value the host rejected: the row, the text typed and the host's message (the converter's).</summary>
public sealed class PropertyValueRejectedEventArgs(PropertyRowViewModel row, string? text, string error) : EventArgs
{
    public PropertyRowViewModel Row { get; } = row;

    public string? Text { get; } = text;

    public string Error { get; } = error;
}

/// <summary>
/// The property grid of the Properties window and the collection editor (#68, D41): categories that collapse (or one
/// list, alphabetically), rows whose parts expand under them, the selection with its description, and a refresh that
/// updates the rows in place by their path, so the editor being typed in and the expanded rows survive a commit.
/// </summary>
public sealed class PropertyGridViewModel : ObservableObject
{
    private readonly HashSet<string> _expandedPaths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedCategories = new(StringComparer.Ordinal);
    private object? _selectedItem;
    private bool _showDescription = true;
    private double _descriptionHeight = 76;
    private double _nameColumnRatio = 0.45;
    private bool _isEnabled = true;

    public ObservableCollection<PropertyCategoryViewModel> Categories { get; } = [];

    /// <summary>What the grid shows, in order: the headers and the visible rows (a row's parts after it when it is expanded).</summary>
    public ObservableCollection<object> Items { get; } = [];

    public bool Alphabetical { get; private set; }

    /// <summary>The selected header or row.</summary>
    public object? SelectedItem
    {
        get => _selectedItem;
        set
        {
            var old = _selectedItem;
            if (!SetField(ref _selectedItem, value))
            {
                return;
            }

            Mark(old, false);
            Mark(value, true);
            OnPropertyChanged(nameof(SelectedRow));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(DescriptionTitle));
        }
    }

    private static void Mark(object? item, bool selected)
    {
        switch (item)
        {
            case PropertyRowViewModel row:
                row.IsSelected = selected;
                break;
            case PropertyCategoryViewModel category:
                category.IsSelected = selected;
                break;
        }
    }

    public PropertyRowViewModel? SelectedRow
    {
        get => _selectedItem as PropertyRowViewModel;
        set => SelectedItem = value;
    }

    public string DescriptionTitle => _selectedItem switch
    {
        PropertyRowViewModel row => row.Name,
        PropertyCategoryViewModel category => category.Title,
        _ => string.Empty,
    };

    public string Description => SelectedRow?.Description ?? string.Empty;

    /// <summary>The description pane shows (the context menu's Description).</summary>
    public bool ShowDescription
    {
        get => _showDescription;
        set => SetField(ref _showDescription, value);
    }

    /// <summary>The description pane's height (its splitter drags it): 76 px, a title and three lines.</summary>
    public double DescriptionHeight
    {
        get => _descriptionHeight;
        set => SetField(ref _descriptionHeight, Math.Clamp(value, 40, 400));
    }

    /// <summary>The names' share of the width (the line between names and values drags it): 45 %.</summary>
    public double NameColumnRatio
    {
        get => _nameColumnRatio;
        set => SetField(ref _nameColumnRatio, Math.Clamp(value, 0.15, 0.85));
    }

    /// <summary>The grid takes input (false while an editor panel is open over the designer, D41 question 2).</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }

    /// <summary>What the grid shows, for the invalid-value dialog's context line ("placeOrderButton"), or null.</summary>
    public string? Context { get; set; }

    /// <summary>The Custom tab's 16 colors of the session (null: empty slots), shared by the color drop-downs of this grid.</summary>
    public Avalonia.Media.Color?[] CustomColors { get; } = new Avalonia.Media.Color?[16];

    /// <summary>A row's value was rejected by the host: the owner shows the invalid-value dialog.</summary>
    public event EventHandler<PropertyValueRejectedEventArgs>? ValueRejected;

    /// <summary>A row's drop-down should open (a double-click, F4, Alt+Down): the view opens it.</summary>
    public event EventHandler<PropertyRowViewModel>? DropDownRequested;

    /// <summary>A row's text box should take the keyboard focus, its text selected (the invalid-value dialog's OK).</summary>
    public event EventHandler<PropertyRowViewModel>? EditRequested;

    internal void OnRejected(PropertyRowViewModel row, string? text, string error) => ValueRejected?.Invoke(this, new PropertyValueRejectedEventArgs(row, text, error));

    public void RequestDropDown(PropertyRowViewModel row) => DropDownRequested?.Invoke(this, row);

    public void RequestEdit(PropertyRowViewModel row) => EditRequested?.Invoke(this, row);

    /// <summary>Every row (parts included), in order.</summary>
    public IEnumerable<PropertyRowViewModel> AllRows => Categories.SelectMany(c => Flatten(c.Rows));

    private static IEnumerable<PropertyRowViewModel> Flatten(IEnumerable<PropertyRowViewModel> rows) => rows.SelectMany(r => Flatten(r.Children).Prepend(r));

    /// <summary>The row with this path, parts included.</summary>
    public PropertyRowViewModel? Find(string path) => AllRows.FirstOrDefault(r => r.Path == path);

    public void Clear() => Show([], Alphabetical);

    /// <summary>
    /// Shows categories (or, <paramref name="alphabetical"/>, their rows in one list without a header, sorted ordinally
    /// ignoring case so "(" comes first, the parts in their own order). Rows with the same path and kind as the ones shown
    /// keep their instance and take the fresh state; expanded rows and collapsed categories stay so; the selection is
    /// restored by path (to the nearest visible parent when it is hidden).
    /// </summary>
    public void Show(IReadOnlyList<PropertyCategoryViewModel> categories, bool alphabetical, string? alphabeticalTitle = null)
    {
        var selectedPath = _selectedItem switch
        {
            PropertyRowViewModel row => "r:" + row.Path,
            PropertyCategoryViewModel category => "c:" + category.Title,
            _ => null,
        };
        Alphabetical = alphabetical;
        var fresh = alphabetical
            ? [new PropertyCategoryViewModel(alphabeticalTitle ?? Localizer.Get("msg.propertiesWindowViewModel.properties", "Properties"),
                [.. categories.SelectMany(c => c.Rows).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)]) { ShowHeader = false }]
            : categories.Where(c => c.Rows.Count > 0).ToList();

        var old = Categories.ToDictionary(c => (c.Title, c.ShowHeader), c => c);
        var merged = new List<PropertyCategoryViewModel>();
        foreach (var category in fresh)
        {
            var keep = old.GetValueOrDefault((category.Title, category.ShowHeader)) ?? category;
            keep.Rows = Merge(ReferenceEquals(keep, category) ? [] : keep.Rows, category.Rows, 0);
            keep.ShowHeader = category.ShowHeader;
            keep.Owner = this;
            keep.IsExpanded = !keep.ShowHeader || !_collapsedCategories.Contains(keep.Title);
            merged.Add(keep);
        }

        Sync(Categories, merged);
        Rebuild();
        SelectedItem = selectedPath switch
        {
            ['r', ':', .. var path] => Visible(path),
            ['c', ':', .. var title] => Categories.FirstOrDefault(c => c.Title == title && c.ShowHeader),
            _ => null,
        };
    }

    /// <summary>The rows after a refresh: an old row with the same path and kind takes the fresh one's state.</summary>
    private List<PropertyRowViewModel> Merge(IReadOnlyList<PropertyRowViewModel> old, IReadOnlyList<PropertyRowViewModel> fresh, int depth)
    {
        var byPath = new Dictionary<string, PropertyRowViewModel>(StringComparer.Ordinal);
        foreach (var row in old)
        {
            byPath.TryAdd(row.Path, row);
        }

        var rows = new List<PropertyRowViewModel>(fresh.Count);
        foreach (var row in fresh)
        {
            PropertyRowViewModel keep;
            // A row whose kind or editor changes (a value that gains a line break: no text box) is replaced, so its one editor is made again.
            if (byPath.TryGetValue(row.Path, out var existing) && existing.Kind == row.Kind && existing.Name == row.Name && existing.ShowsTextBox == row.ShowsTextBox && !ReferenceEquals(existing, row))
            {
                existing.Update(row);
                existing.SetChildren(Merge(existing.Children, row.Children, depth + 1));
                keep = existing;
            }
            else
            {
                keep = row;
                keep.SetChildren(Merge([], row.Children, depth + 1));
            }

            keep.Owner = this;
            keep.Depth = depth;
            keep.IsExpanded = keep.IsExpandable && _expandedPaths.Contains(keep.Path);
            rows.Add(keep);
        }

        return rows;
    }

    /// <summary>The visible row with this path, or its nearest visible parent.</summary>
    private object? Visible(string path)
    {
        while (true)
        {
            if (Items.OfType<PropertyRowViewModel>().FirstOrDefault(r => r.Path == path) is { } row)
            {
                return row;
            }

            var dot = path.LastIndexOf('.');
            if (dot < 0)
            {
                // A row under a collapsed category: the category's header.
                return Categories.FirstOrDefault(c => c.ShowHeader && Flatten(c.Rows).Any(r => r.Path == path || path.StartsWith(r.Path + ".", StringComparison.Ordinal)));
            }

            path = path[..dot];
        }
    }

    /// <summary>Expands or collapses a category or a row with parts.</summary>
    public void Toggle(object item)
    {
        switch (item)
        {
            case PropertyCategoryViewModel category when category.ShowHeader:
                SetExpanded(category, !category.IsExpanded);
                break;
            case PropertyRowViewModel row when row.IsExpandable:
                SetExpanded(row, !row.IsExpanded);
                break;
        }
    }

    public void SetExpanded(object item, bool expanded)
    {
        switch (item)
        {
            case PropertyCategoryViewModel category when category.ShowHeader && category.IsExpanded != expanded:
                category.IsExpanded = expanded;
                _ = expanded ? _collapsedCategories.Remove(category.Title) : _collapsedCategories.Add(category.Title);
                break;
            case PropertyRowViewModel row when row.IsExpandable && row.IsExpanded != expanded:
                row.IsExpanded = expanded;
                _ = expanded ? _expandedPaths.Add(row.Path) : _expandedPaths.Remove(row.Path);
                break;
            default:
                return;
        }

        Rebuild();
        if (_selectedItem is not null && !Items.Contains(_selectedItem))
        {
            SelectedItem = item;
        }
    }

    /// <summary>The visible items again, changed in place (the containers of the rows that stay are kept).</summary>
    private void Rebuild()
    {
        var items = new List<object>();
        foreach (var category in Categories)
        {
            if (category.ShowHeader)
            {
                items.Add(category);
            }

            if (category.IsExpanded)
            {
                Add(items, category.Rows);
            }
        }

        Sync(Items, items);
    }

    private static void Add(List<object> items, IEnumerable<PropertyRowViewModel> rows)
    {
        foreach (var row in rows)
        {
            items.Add(row);
            if (row.IsExpanded)
            {
                Add(items, row.Children);
            }
        }
    }

    /// <summary>Makes <paramref name="target"/> hold <paramref name="items"/> with the fewest changes (removes, inserts, moves).</summary>
    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
        where T : class
    {
        var wanted = new HashSet<T>(items, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], items[i]))
            {
                continue;
            }

            var at = -1;
            for (var j = i + 1; j < target.Count; j++)
            {
                if (ReferenceEquals(target[j], items[i]))
                {
                    at = j;
                    break;
                }
            }

            if (at >= 0)
            {
                target.Move(at, i);
            }
            else
            {
                target.Insert(i, items[i]);
            }
        }
    }

    // ------------------------------------------------------------------ the keyboard (the view calls these)

    /// <summary>Up and Down: the previous or next item, headers included.</summary>
    public void MoveSelection(int delta)
    {
        if (Items.Count == 0)
        {
            return;
        }

        var index = _selectedItem is null ? -1 : Items.IndexOf(_selectedItem);
        SelectedItem = Items[Math.Clamp(index < 0 ? (delta > 0 ? 0 : Items.Count - 1) : index + delta, 0, Items.Count - 1)];
    }

    /// <summary>Left: collapses the selection, or goes to its parent (a part's property, a row's category).</summary>
    public void MoveLeft()
    {
        switch (_selectedItem)
        {
            case PropertyCategoryViewModel { IsExpanded: true, ShowHeader: true } category:
                SetExpanded(category, false);
                break;
            case PropertyRowViewModel { IsExpanded: true } row:
                SetExpanded(row, false);
                break;
            case PropertyRowViewModel row:
                var index = Items.IndexOf(row);
                for (var i = index - 1; i >= 0; i--)
                {
                    if (Items[i] is PropertyRowViewModel parent && parent.Depth < row.Depth || Items[i] is PropertyCategoryViewModel && row.Depth == 0)
                    {
                        SelectedItem = Items[i];
                        break;
                    }
                }

                break;
        }
    }

    /// <summary>Right: expands the selection, or goes to its first part or row.</summary>
    public void MoveRight()
    {
        switch (_selectedItem)
        {
            case PropertyCategoryViewModel { IsExpanded: false, ShowHeader: true } category:
                SetExpanded(category, true);
                break;
            case PropertyRowViewModel { IsExpandable: true, IsExpanded: false } row:
                SetExpanded(row, true);
                break;
            case PropertyCategoryViewModel category when category.Rows.Count > 0:
                SelectedItem = category.Rows[0];
                break;
            case PropertyRowViewModel { IsExpanded: true } row when row.Children.Count > 0:
                SelectedItem = row.Children[0];
                break;
        }
    }
}
