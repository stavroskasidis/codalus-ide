using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>The Document Outline (M12a): click or Enter moves the caret to a symbol; the caret's symbol is selected.</summary>
public sealed partial class DocumentOutlineView : UserControl, IPaneView
{
    private bool _selecting;

    public DocumentOutlineView()
    {
        InitializeComponent();
        Tree.Tapped += OnTapped;
        Tree.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        FilterBox.AddHandler(KeyDownEvent, OnFilterKeyDown, RoutingStrategies.Tunnel);
    }

    public void FocusPrimary() => FilterBox.Focus();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is DocumentOutlineViewModel vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(DocumentOutlineViewModel.Current) && vm.Current is { } current && !Tree.IsKeyboardFocusWithin)
                {
                    _selecting = true;
                    Tree.SelectedItem = current;
                    _selecting = false;
                }
            };
        }
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (_selecting || e.Source is not Visual visual || visual.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null)
        {
            return;
        }

        if (visual.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is SymbolNodeViewModel node)
        {
            (DataContext as DocumentOutlineViewModel)?.Navigate(node);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Tree.SelectedItem is SymbolNodeViewModel node && DataContext is DocumentOutlineViewModel vm)
        {
            vm.Navigate(node);
            e.Handled = true;
        }
    }

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && DataContext is DocumentOutlineViewModel { Roots.Count: > 0 } vm)
        {
            Tree.SelectedItem = vm.Roots[0];
            Tree.ContainerFromItem(vm.Roots[0])?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && DataContext is DocumentOutlineViewModel { Roots.Count: > 0 } first)
        {
            first.Navigate(first.Roots[0]);
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
