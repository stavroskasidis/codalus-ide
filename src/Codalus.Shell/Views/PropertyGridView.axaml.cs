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
/// One editor per row (#68): the cell's text box, or its value as text (a read-only value, an exclusive choice, a
/// picker, a value with line breaks). Only the template a row needs is built, unlike a template that hides the others.
/// </summary>
public sealed class PropertyEditorSelector : IDataTemplate
{
    public IDataTemplate? TextBox { get; set; }

    public IDataTemplate? Value { get; set; }

    public Control? Build(object? param) => param is PropertyRowViewModel row ? (row.ShowsTextBox ? TextBox : Value)?.Build(param) : null;

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
    private PropertyRowViewModel? _dropDownRow;
    private Point? _splitterDrag;

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
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.DropDownRequested -= OnDropDownRequested;
            _vm.EditRequested -= OnEditRequested;
        }

        _vm = DataContext as PropertyGridViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.DropDownRequested += OnDropDownRequested;
            _vm.EditRequested += OnEditRequested;
        }

        UpdateLayoutMetrics();
    }

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
        if (e.Source is Visual visual && visual.FindAncestorOfType<TextBox>(includeSelf: true) is null)
        {
            // A click on a name (or a value shown as text) gives the grid the keyboard.
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
        if (_vm is not { IsEnabled: true } vm || DropDown.IsOpen)
        {
            return;
        }

        var box = (e.Source as Visual)?.FindAncestorOfType<TextBox>(includeSelf: true);
        if (box is not null && !box.Classes.Contains("editor"))
        {
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
