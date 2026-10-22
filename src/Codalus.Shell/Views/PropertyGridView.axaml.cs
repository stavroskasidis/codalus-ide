using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views.PropertyEditors;

namespace Codalus.Shell.Views;

/// <summary>
/// One editor per row (#68): the cell's text box, a read-only value as selectable text (M34g: a Full Path is selected
/// and copied), or the value as text (an exclusive choice, a picker, a value with line breaks). Only the template a row
/// needs is built, unlike a template that hides the others.
/// </summary>
public sealed class PropertyEditorSelector : IDataTemplate
{
    public IDataTemplate? TextBox { get; set; }

    public IDataTemplate? Value { get; set; }

    public IDataTemplate? ReadOnly { get; set; }

    public Control? Build(object? param) => param is PropertyRowViewModel row
        ? (row.ShowsTextBox ? TextBox : row.IsReadOnly && !row.HasModalEditor ? ReadOnly ?? Value : Value)?.Build(param)
        : null;

    public bool Match(object? data) => data is PropertyRowViewModel;
}

/// <summary>
/// The property grid's view (#68, D41): the Properties window's and the collection editor's. Rows are virtualized, a
/// row's drop-down is made the first time one of its kind opens (one per grid) and reused; the keyboard moves, expands,
/// commits and opens drop-downs as the classic IDE's grid does.
/// </summary>
public sealed partial class PropertyGridView : UserControl
{
    public static readonly StyledProperty<double> NameWidthProperty = AvaloniaProperty.Register<PropertyGridView, double>(nameof(NameWidth), 120);

    public static readonly StyledProperty<object?> FooterProperty = AvaloniaProperty.Register<PropertyGridView, object?>(nameof(Footer));

    private readonly Dictionary<string, Control> _dropDowns = new(StringComparer.Ordinal);
    private PropertyGridViewModel? _vm;
    private PropertyGridViewModel? _subscribed;
    private PropertyRowViewModel? _dropDownRow;
    private Point? _splitterDrag;

    static PropertyGridView()
    {
        // A name or a value shows its tooltip only while it is clipped (D41 question 19): the tooltip is always set, so a
        // clipped text always has one, and its opening is cancelled while the whole text shows.
        ToolTip.ToolTipOpeningEvent.AddClassHandler<Control>((control, e) =>
        {
            if (control.Classes.Contains("clipTip") && !IsClipped(control))
            {
                e.Cancel = true;
            }
        });
    }

    /// <summary>Whether a name's or a value's text is cut off: a trimmed text block, or a text box whose text is wider than it.</summary>
    internal static bool IsClipped(Control control) => control switch
    {
        TextBlock text => text.TextLayout.TextLines.Any(l => l.HasCollapsed),
        TextBox box => box.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().FirstOrDefault() is { } presenter
            && presenter.TextLayout.WidthIncludingTrailingWhitespace > presenter.Bounds.Width + 0.5,
        _ => true,
    };

    public PropertyGridView()
    {
        InitializeComponent();
        Rows.AddHandler(PointerPressedEvent, OnRowPointerPressed, RoutingStrategies.Tunnel);
        Rows.AddHandler(GotFocusEvent, OnRowGotFocus, RoutingStrategies.Bubble);
        Rows.AddHandler(LostFocusEvent, OnRowLostFocus, RoutingStrategies.Bubble);
        Rows.AddHandler(Button.ClickEvent, OnRowButtonClick, RoutingStrategies.Bubble);
        Rows.AddHandler(DoubleTappedEvent, OnRowDoubleTapped, RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Scroller.ScrollChanged += (_, _) => UpdateLayoutMetrics();
        SizeChanged += (_, _) => UpdateLayoutMetrics();
        Scroller.LayoutUpdated += (_, _) => UpdateRowMargin();
        ColumnSplitter.DragDelta += OnColumnSplitterDrag;
        DescriptionSplitter.PointerPressed += OnDescriptionSplitterPressed;
        DescriptionSplitter.PointerMoved += OnDescriptionSplitterMoved;
        DescriptionSplitter.PointerReleased += (_, e) =>
        {
            _splitterDrag = null;
            e.Pointer.Capture(null);
        };
        DropDown.Closed += OnDropDownClosed;
        if (Resources["RowMenu"] is ContextMenu menu)
        {
            menu.Opening += OnMenuOpening;
            if (menu.Items.OfType<MenuItem>().LastOrDefault() is { } description)
            {
                description.Click += (_, _) =>
                {
                    if (_vm is { } vm)
                    {
                        vm.ShowDescription = !vm.ShowDescription;
                    }
                };
            }

            Rows.ContextMenu = menu;
        }
    }

    /// <summary>The names' width: their share of the grid's width after the gutter.</summary>
    public double NameWidth
    {
        get => GetValue(NameWidthProperty);
        set => SetValue(NameWidthProperty, value);
    }

    /// <summary>What shows under the rows, above the description (the Properties window's links), or null.</summary>
    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    /// <summary>The drop-down that is open, or null (tests).</summary>
    public Control? OpenDropDownControl => DropDown.IsOpen ? DropDownHost.Content as Control : null;

    /// <summary>The drop-down's popup (the smoke test's screenshot takes it with the window).</summary>
    internal Avalonia.Controls.Primitives.Popup DropDownPopup => DropDown;

    /// <summary>The drop-downs made so far, by kind (tests: none until one opens).</summary>
    public IReadOnlyDictionary<string, Control> MadeDropDowns => _dropDowns;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm = DataContext as PropertyGridViewModel;
        Subscribe(this.IsAttachedToVisualTree() ? _vm : null);
        UpdateLayoutMetrics();
    }

    // The view listens to its grid only while it is shown: a view taken out of the tree (the collection editor's, when
    // another item is chosen) neither keeps posting work nor stays alive through the grid's events.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe(_vm);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        DropDown.IsOpen = false;
        Subscribe(null);
    }

    private void Subscribe(PropertyGridViewModel? vm)
    {
        if (ReferenceEquals(_subscribed, vm))
        {
            return;
        }

        if (_subscribed is { } old)
        {
            old.PropertyChanged -= OnViewModelChanged;
            old.DropDownRequested -= OnDropDownRequested;
            old.EditRequested -= OnEditRequested;
        }

        _subscribed = vm;
        if (vm is not null)
        {
            vm.PropertyChanged += OnViewModelChanged;
            vm.DropDownRequested += OnDropDownRequested;
            vm.EditRequested += OnEditRequested;
        }
    }

    /// <summary>Whether the view listens to its grid's events (tests: not while it is out of the tree).</summary>
    internal bool IsListening => _subscribed is not null;

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PropertyGridViewModel.NameColumnRatio):
                UpdateLayoutMetrics();
                break;
            case nameof(PropertyGridViewModel.SelectedItem) when _vm?.SelectedItem is { } item:
                Ui.Post(() => ScrollIntoView(item));
                break;
            case nameof(PropertyGridViewModel.IsEnabled) when _vm is { IsEnabled: false }:
                DropDown.IsOpen = false;
                break;
        }
    }

    private void OnDropDownRequested(object? sender, PropertyRowViewModel row) => Ui.Post(() => OpenDropDown(row));

    private void OnEditRequested(object? sender, PropertyRowViewModel row) => Ui.Post(() => FocusEditor(row, selectAll: true));

    // ------------------------------------------------------------------ layout

    private double RowsWidth => Math.Max(0, Scroller.Bounds.Width - Rows.Margin.Right);

    private void UpdateLayoutMetrics()
    {
        var ratio = _vm?.NameColumnRatio ?? 0.45;
        NameWidth = Math.Max(24, Math.Round((RowsWidth - 16) * ratio));
        Canvas.SetLeft(ColumnSplitter, 16 + NameWidth - 2);
        ColumnSplitter.Height = Scroller.Bounds.Height;
        SplitLayer.Height = Scroller.Bounds.Height;
    }

    /// <summary>While the grid scrolls, its rows end 6 px before its right edge, clear of the overlay thumb (D41 question 12).</summary>
    private void UpdateRowMargin()
    {
        var right = Scroller.Extent.Height > Scroller.Viewport.Height + 0.5 ? 6 : 0;
        if (Rows.Margin.Right != right)
        {
            Rows.Margin = new Thickness(0, 0, right, 0);
            UpdateLayoutMetrics();
        }
    }

    private void OnColumnSplitterDrag(object? sender, VectorEventArgs e)
    {
        if (_vm is { } vm && RowsWidth > 40)
        {
            vm.NameColumnRatio = (NameWidth + e.Vector.X) / (RowsWidth - 16);
        }
    }

    private void OnDescriptionSplitterPressed(object? sender, PointerPressedEventArgs e)
    {
        _splitterDrag = e.GetPosition(this);
        e.Pointer.Capture(DescriptionSplitter);
        e.Handled = true;
    }

    private void OnDescriptionSplitterMoved(object? sender, PointerEventArgs e)
    {
        if (_splitterDrag is { } start && _vm is { } vm)
        {
            var at = e.GetPosition(this);
            vm.DescriptionHeight -= at.Y - start.Y;
            _splitterDrag = at;
        }
    }

    // ------------------------------------------------------------------ rows

    private static PropertyRowViewModel? RowOf(object? source) => (source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<PropertyRowViewModel>().FirstOrDefault();

    private static object? ItemOf(object? source) => (source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>()
        .FirstOrDefault(c => c.DataContext is PropertyRowViewModel or PropertyCategoryViewModel && c is Border { Classes: var classes } && (classes.Contains("pgRow") || classes.Contains("pgHeader")))?.DataContext;

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is null || ItemOf(e.Source) is not { } item)
        {
            return;
        }

        _vm.SelectedItem = item;
        if (e.Source is Visual visual && visual.FindAncestorOfType<TextBox>(includeSelf: true) is null && visual.FindAncestorOfType<SelectableTextBlock>(includeSelf: true) is null)
        {
            // A click on a name (or a value shown as text) gives the grid the keyboard; a read-only value takes it itself,
            // so a part of it can be selected and copied.
            Rows.Focus();
        }
    }

    private void OnRowGotFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox box && RowOf(box) is { } row && _vm is { } vm)
        {
            vm.SelectedItem = row;
            row.IsEditing = true;
        }
    }

    private static void OnRowLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox box && RowOf(box) is { } row)
        {
            row.IsEditing = false;
        }
    }

    private void OnRowButtonClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { Classes: var classes } button && classes.Contains("dropDown") && RowOf(button) is { } row)
        {
            OpenDropDown(row);
            e.Handled = true;
        }
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual visual && (visual.FindAncestorOfType<TextBox>(includeSelf: true) is not null || visual.FindAncestorOfType<Button>(includeSelf: true) is not null))
        {
            return;
        }

        switch (ItemOf(e.Source))
        {
            case PropertyRowViewModel row:
                row.DoubleClick();
                e.Handled = true;
                break;
            case PropertyCategoryViewModel category:
                _vm?.Toggle(category);
                e.Handled = true;
                break;
        }
    }

    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu || _vm?.SelectedRow is not { } row)
        {
            e.Cancel = true;
            return;
        }

        menu.DataContext = row;
        if (menu.Items.OfType<MenuItem>().LastOrDefault() is { } description)
        {
            description.IsChecked = _vm.ShowDescription;
        }
    }

    private Control? ContainerOf(object item) => Rows.ContainerFromItem(item);

    private void ScrollIntoView(object item)
    {
        if (_vm?.Items.IndexOf(item) is int index and >= 0)
        {
            Rows.ScrollIntoView(index);
        }
    }

    /// <summary>Gives the row's text box the keyboard (its text selected), or the grid when the row has none.</summary>
    public void FocusEditor(PropertyRowViewModel row, bool selectAll = false)
    {
        ScrollIntoView(row);
        Rows.UpdateLayout();
        if (ContainerOf(row)?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Classes.Contains("editor")) is { } box)
        {
            box.Focus();
            if (selectAll)
            {
                box.SelectAll();
            }
        }
        else
        {
            Rows.Focus();
        }
    }

    /// <summary>The selected row's editor, or the grid (the Properties window's F4 and F6).</summary>
    public void FocusPrimary()
    {
        if (_vm?.SelectedRow is { } row)
        {
            FocusEditor(row);
        }
        else
        {
            Rows.Focus();
        }
    }

    // ------------------------------------------------------------------ the keyboard

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Only keys pressed in the rows: the footer's links (Enter activates the focused one) and the description keep theirs.
        if (_vm is not { IsEnabled: true } vm || DropDown.IsOpen || e.Source is not Visual source || !source.GetSelfAndVisualAncestors().Contains(Rows))
        {
            return;
        }

        var box = source.FindAncestorOfType<TextBox>(includeSelf: true);
        if (box is not null && !box.Classes.Contains("editor"))
        {
            return;
        }

        // Ctrl+C (Cmd+C) on a row, outside a text box and a selection in a read-only value: the row's whole value.
        if (e.Key == Key.C && e.KeyModifiers == (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control) && box is null
            && vm.SelectedRow is { IsMixed: false } copied && !HasTextSelection(source))
        {
            _ = SafeClipboard.TrySetTextAsync(TopLevel.GetTopLevel(this)?.Clipboard, copied.Value);
            e.Handled = true;
            return;
        }

        var row = box is not null ? RowOf(box) : vm.SelectedRow;
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var plain = e.KeyModifiers == KeyModifiers.None;
        switch (e.Key)
        {
            case Key.Down when alt:
            case Key.F4 when plain:
                if (row is { HasDropDown: true })
                {
                    OpenDropDown(row);
                    e.Handled = true;
                }

                break;
            case Key.Up or Key.Down when plain:
                Commit(box, row);
                vm.MoveSelection(e.Key == Key.Up ? -1 : 1);
                if (vm.SelectedItem is { } item)
                {
                    ScrollIntoView(item);
                    if (box is not null && vm.SelectedRow is { } next)
                    {
                        Ui.Post(() => FocusEditor(next));
                    }
                    else
                    {
                        Rows.Focus();
                    }
                }

                e.Handled = true;
                break;
            case Key.Left when plain && box is null:
                vm.MoveLeft();
                e.Handled = true;
                break;
            case Key.Right when plain && box is null:
                vm.MoveRight();
                e.Handled = true;
                break;
            case Key.Enter when plain:
                if (box is not null && row is not null)
                {
                    if ((box.Text ?? string.Empty) != row.Value || row.IsMixed)
                    {
                        Commit(box, row);
                    }
                    else if (row.Action is { } action)
                    {
                        action.Execute(null);
                    }

                    e.Handled = true;
                }
                else if (vm.SelectedItem is PropertyCategoryViewModel category)
                {
                    vm.Toggle(category);
                    e.Handled = true;
                }
                else if (vm.SelectedRow is { } selected)
                {
                    if (selected.Action is { } action)
                    {
                        action.Execute(null);
                    }
                    else if (selected.ShowsTextBox)
                    {
                        FocusEditor(selected, selectAll: true);
                    }
                    else
                    {
                        selected.DoubleClick();
                    }

                    e.Handled = true;
                }

                break;
            case Key.Escape when box is not null && row is not null && ((box.Text ?? string.Empty) != row.Text || row.Text != (row.IsMixed ? string.Empty : row.Value)):
                row.RevertText();
                box.Text = row.Text;
                box.SelectAll();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Part of a read-only value is selected: Ctrl+C copies that part (the text block's own copy).</summary>
    private static bool HasTextSelection(Visual source) =>
        source.FindAncestorOfType<SelectableTextBlock>(includeSelf: true) is { } selectable && selectable.SelectionStart != selectable.SelectionEnd;

    /// <summary>Commits a text box's text (Enter, or moving away with the arrows), keeping the focus.</summary>
    private static void Commit(TextBox? box, PropertyRowViewModel? row)
    {
        if (box is not null && row is not null)
        {
            row.Text = box.Text ?? string.Empty;
        }
    }

    // ------------------------------------------------------------------ drop-downs

    /// <summary>Opens a row's drop-down under its value cell, made the first time one of its kind opens.</summary>
    public void OpenDropDown(PropertyRowViewModel row)
    {
        if (_vm is not { IsEnabled: true } vm || !row.HasDropDown)
        {
            return;
        }

        var kind = PropertyDropDowns.KindOf(row);
        if (!_dropDowns.TryGetValue(kind, out var control))
        {
            control = PropertyDropDowns.Create(kind);
            control.AddHandler(KeyDownEvent, (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    DropDown.IsOpen = false;
                    e.Handled = true;
                }
            }, RoutingStrategies.Bubble);
            _dropDowns[kind] = control;
        }

        vm.SelectedItem = row;
        ScrollIntoView(row);
        Rows.UpdateLayout();
        var anchor = ContainerOf(row)?.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("valueCell"));
        DropDown.PlacementTarget = anchor ?? (Control)Rows;
        DropDownHost.Content = control;
        _dropDownRow = row;
        ((IPropertyDropDown)control).Open(row, vm, () => DropDown.IsOpen = false);
        DropDown.IsOpen = true;
        Ui.Post(() => ((IPropertyDropDown)control).FocusFirst());
    }

    private void OnDropDownClosed(object? sender, EventArgs e)
    {
        if (DropDownHost.Content is IPropertyDropDown dropDown)
        {
            dropDown.Closed();
        }

        if (_dropDownRow is { } row)
        {
            _dropDownRow = null;
            Ui.Post(() => FocusEditor(row));
        }
    }
}
