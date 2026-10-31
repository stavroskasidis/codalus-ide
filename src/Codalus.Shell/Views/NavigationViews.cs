using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// The Document Outline (M12a): click or Enter moves the caret to a symbol; the caret's symbol is selected. Its rows expand
/// and collapse as a tree view's: the chevron, Left and Right (Left on a child goes to its parent), and a screen reader's
/// Expand and Collapse (a tree item's, M57). It tells its view model whether it's shown (the rows follow the editor only then).
/// </summary>
public sealed partial class DocumentOutlineView : UserControl, IPaneView
{
    private DocumentOutlineViewModel? _model;

    public DocumentOutlineView()
    {
        InitializeComponent();
        List.Tapped += OnTapped;
        List.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        List.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        List.ExpandRow = (row, expanded) => _model?.SetExpanded((SymbolNodeViewModel)row, expanded);
        FilterBox.AddHandler(KeyDownEvent, OnFilterKeyDown, RoutingStrategies.Tunnel);
        ShownTracker.Track(this);
    }

    public void FocusPrimary() => FilterBox.Focus();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
        }

        _model = DataContext as DocumentOutlineViewModel;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelChanged;
        }
    }

    /// <summary>The caret's symbol is selected and brought into view, unless the user is in the list.</summary>
    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentOutlineViewModel.Current) && _model?.Current is { } current && !List.IsKeyboardFocusWithin)
        {
            List.SelectedItem = current;
            List.ScrollIntoView(current);
        }
    }

    private static SymbolNodeViewModel? RowAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as SymbolNodeViewModel;

    private static bool IsChevron(object? source) =>
        (source as Visual)?.FindAncestorOfType<Border>(includeSelf: true) is { } border && border.Classes.Contains("chevron");

    /// <summary>
    /// The chevron collapses and expands its row at each press: on a tap, a second click soon after the first was a double
    /// tap, and the row stayed as the first click left it.
    /// </summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsChevron(e.Source) && RowAt(e.Source) is { } row && _model is { } vm && e.GetCurrentPoint(List).Properties.IsLeftButtonPressed)
        {
            vm.Toggle(row);
            e.Handled = true;
        }
    }

    /// <summary>A click on a row (not its chevron) goes to its symbol.</summary>
    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (RowAt(e.Source) is { } row && _model is { } vm && !IsChevron(e.Source))
        {
            vm.Navigate(row);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (List.SelectedItem is not SymbolNodeViewModel row || _model is not { } vm || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                vm.Navigate(row);
                break;
            case Key.Right when row.HasChildren && !row.IsExpanded:
                vm.SetExpanded(row, true);
                break;
            case Key.Left when row.HasChildren && row.IsExpanded:
                vm.SetExpanded(row, false);
                break;
            case Key.Left when vm.Rows.IndexOf(row) is var index and > 0:
                // To the parent's row.
                for (var i = index - 1; i >= 0; i--)
                {
                    if (vm.Rows[i].Depth < row.Depth)
                    {
                        List.SelectedItem = vm.Rows[i];
                        List.ScrollIntoView(vm.Rows[i]);
                        List.ContainerFromItem(vm.Rows[i])?.Focus();
                        break;
                    }
                }

                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && _model is { Rows.Count: > 0 } vm)
        {
            List.SelectedItem = vm.Rows[0];
            List.ScrollIntoView(vm.Rows[0]);
            List.ContainerFromItem(vm.Rows[0])?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _model is { Rows.Count: > 0 } first)
        {
            first.Navigate(first.Rows[0]);
            e.Handled = true;
        }
    }
}

/// <summary>Call Hierarchy and Type Hierarchy (M12a): expanding loads callers, callees, bases or derived types; Enter or double-click opens.</summary>
public sealed partial class HierarchyView : UserControl, IPaneView
{
    public HierarchyView()
    {
        InitializeComponent();
        Tree.DoubleTapped += OnDoubleTapped;
        Tree.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
    }

    public void FocusPrimary()
    {
        Tree.SelectedItem ??= (DataContext as HierarchyViewModel)?.Roots.FirstOrDefault();
        if (Tree.SelectedItem is not { } item || Tree.ContainerFromItem(item)?.Focus() != true)
        {
            Tree.Focus();
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual visual && visual.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is HierarchyNodeViewModel node)
        {
            _ = (DataContext as HierarchyViewModel)?.OpenAsync(node);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Tree.SelectedItem is HierarchyNodeViewModel node && DataContext is HierarchyViewModel vm)
        {
            _ = vm.OpenAsync(node);
            e.Handled = true;
        }
    }
}

/// <summary>The Code Definition window (M12a): a read-only view of the definition at the caret, with an Open button.</summary>
public sealed class CodeDefinitionView : UserControl, IPaneView
{
    private readonly CodeDefinitionViewModel _vm;
    private readonly CodeViewer _viewer;
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 4), TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };

    public CodeDefinitionView(CodeDefinitionViewModel vm, IEditorServices services, EditorFont font)
    {
        _vm = vm;
        _viewer = new CodeViewer(services, font);
        var open = new Button { Content = "Open", Classes = { "toolbar" }, Margin = new Thickness(4, 2) };
        open.Click += (_, _) => _ = vm.OpenAsync();
        var header = new DockPanel { Children = { open, _title } };
        DockPanel.SetDock(open, Avalonia.Controls.Dock.Right);
        var root = new DockPanel { Children = { header, _viewer } };
        DockPanel.SetDock(header, Avalonia.Controls.Dock.Top);
        Content = root;
        vm.Changed += (_, _) => Update();
        Update();
    }

    /// <summary>The viewer (tests).</summary>
    public CodeViewer Viewer => _viewer;

    private void Update()
    {
        _title.Text = _vm.Title;
        _viewer.Show(_vm.Text, _vm.LanguageId, _vm.Location?.Range);
    }

    public void FocusPrimary() => _viewer.Editor.TextArea.Focus();
}

/// <summary>The Bookmarks window (M12b): Enter or double-click goes to a bookmark, F2 renames, Delete removes.</summary>
public sealed partial class BookmarksView : UserControl, IPaneView
{
    public BookmarksView()
    {
        InitializeComponent();
        List.DoubleTapped += (_, _) => Open();
        List.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
    }

    public void FocusPrimary() => List.Focus();

    private void Open()
    {
        if (List.SelectedItem is BookmarkRowViewModel row && DataContext is BookmarksViewModel vm)
        {
            vm.Open(row);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (List.SelectedItem is not BookmarkRowViewModel row || DataContext is not BookmarksViewModel vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                vm.Open(row);
                e.Handled = true;
                break;
            case Key.F2:
                _ = vm.RenameAsync(row);
                e.Handled = true;
                break;
            case Key.Delete:
                vm.Remove(row);
                e.Handled = true;
                break;
        }
    }
}

/// <summary>The Task List (M12b): scans when first shown; Enter or double-click opens a task's line.</summary>
public sealed partial class TaskListView : UserControl, IPaneView
{
    public TaskListView()
    {
        InitializeComponent();
        List.DoubleTapped += (_, _) => Open();
        List.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Open();
                e.Handled = true;
            }
        }, RoutingStrategies.Bubble);
        RefreshButton.Click += (_, _) => _ = (DataContext as TaskListViewModel)?.RefreshAsync();
    }

    public void FocusPrimary() => List.Focus();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as TaskListViewModel)?.EnsureScanned();
    }

    private void Open()
    {
        if (List.SelectedItem is TaskRow row && DataContext is TaskListViewModel vm)
        {
            _ = vm.OpenAsync(row);
        }
    }
}

public sealed partial class SyntaxVisualizerView : UserControl, IPaneView
{
    public SyntaxVisualizerView()
    {
        InitializeComponent();

        // Opened from the View menu: show the tree for the active document now.
        AttachedToVisualTree += (_, _) =>
        {
            if (DataContext is SyntaxVisualizerViewModel vm)
            {
                _ = vm.RefreshAsync(force: true);
            }
        };
    }

    public void FocusPrimary() => Tree.Focus();
}

public sealed partial class CodeMetricsView : UserControl, IPaneView
{
    public CodeMetricsView()
    {
        InitializeComponent();
        Tree.DoubleTapped += (_, _) => Open();
        Tree.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Open();
                e.Handled = true;
            }
        }, RoutingStrategies.Bubble);
    }

    public void FocusPrimary() => Tree.Focus();

    private void Open()
    {
        if (Tree.SelectedItem is MetricsNodeViewModel node && DataContext is CodeMetricsViewModel vm)
        {
            _ = vm.OpenAsync(node);
        }
    }
}
