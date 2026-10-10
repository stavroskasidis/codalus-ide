using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Shell.Views;

/// <summary>
/// The Solution Explorer: Enter or double-click opens, Left/Right collapse and expand (or move to the parent),
/// the context menu comes from the <c>solutionExplorer/context</c> menu location, and files and folders can be
/// dragged onto folders and projects (Ctrl copies).
/// </summary>
public sealed partial class SolutionExplorerView : UserControl, IPaneView
{
    public const string ContextMenuLocation = "solutionExplorer/context";

    /// <summary>Drag payload: paths of the dragged files and folders, within this process.</summary>
    private static readonly DataFormat<string[]> s_paths = DataFormat.CreateInProcessFormat<string[]>("daedalus.explorer.paths");

    private PointerPressedEventArgs? _dragStart;
    private Point _dragOrigin;
    private ListBoxItem? _dropTarget;

    public SolutionExplorerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (ViewModel is { } vm)
            {
                vm.SearchRequested -= OnSearchRequested;
                vm.SearchRequested += OnSearchRequested;
            }
        };
        List.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        List.DoubleTapped += (_, e) =>
        {
            if (RowAt(e.Source) is { } row && !IsChevron(e.Source))
            {
                _ = ViewModel?.ActivateAsync(row);
            }
        };
        List.Tapped += (_, e) =>
        {
            if (RowAt(e.Source) is not { } row)
            {
                return;
            }

            if (IsChevron(e.Source))
            {
                ViewModel?.Toggle(row);
            }
            else
            {
                _ = ViewModel?.PreviewAsync(row);
            }
        };
        List.ContextRequested += OnContextRequested;
        List.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        List.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        DragDrop.AddDragOverHandler(List, OnDragOver);
        DragDrop.AddDragLeaveHandler(List, (_, _) => SetDropTarget(null));
        DragDrop.AddDropHandler(List, OnDrop);
    }

    private SolutionExplorerViewModel? ViewModel => DataContext as SolutionExplorerViewModel;

    private ShellHost? Shell => (TopLevel.GetTopLevel(this) as MainWindow)?.DataContext as ShellHost;

    public void FocusPrimary()
    {
        if (ViewModel is { HasSolution: true } vm)
        {
            vm.Selected ??= vm.Rows.FirstOrDefault();
            if (vm.Selected is { } selected && List.ContainerFromItem(selected) is { } container && container.Focus())
            {
                return;
            }

            List.Focus();
        }
        else
        {
            this.GetVisualDescendants().OfType<Button>().FirstOrDefault()?.Focus();
        }
    }

    private static ExplorerRowViewModel? RowAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ExplorerRowViewModel;

    private static bool IsChevron(object? source) =>
        (source as Visual)?.FindAncestorOfType<Border>(includeSelf: true) is { } border && border.Classes.Contains("chevron");

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { Selected: { } row } vm || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                _ = vm.ActivateAsync(row);
                break;
            case Key.Right when row.HasChildren && !row.IsExpanded:
                vm.SetExpanded(row, true);
                break;
            case Key.Left when row.HasChildren && row.IsExpanded:
                vm.SetExpanded(row, false);
                break;
            case Key.Left when vm.Rows.IndexOf(row) is var index and > 0:
                // Move to the parent row.
                for (var i = index - 1; i >= 0; i--)
                {
                    if (vm.Rows[i].Depth < row.Depth)
                    {
                        vm.Selected = vm.Rows[i];
                        List.ScrollIntoView(vm.Rows[i]);
                        break;
                    }
                }

                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // ------------------------------------------------------------------ context menu

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (ViewModel is not { } vm || Shell is not { } shell)
        {
            return;
        }

        if (RowAt(e.Source) is { } row)
        {
            vm.Selected = row;
        }

        var items = shell.Workbench.Menus.GetItems(ContextMenuLocation).Where(i => i.Submenu is null).ToList();
        if (items.Count == 0)
        {
            return;
        }

        var menu = new ContextMenu();
        string? group = null;
        foreach (var item in items)
        {
            if (group is not null && item.Group != group)
            {
                menu.Items.Add(new Separator());
            }

            group = item.Group;
            var header = new DockPanel();
            if (item.KeyLabel is { } keys)
            {
                var label = new TextBlock { Text = keys, Margin = new Thickness(32, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                label.Classes.Add("muted");
                DockPanel.SetDock(label, Avalonia.Controls.Dock.Right);
                header.Children.Add(label);
            }

            header.Children.Add(new TextBlock { Text = item.Title, VerticalAlignment = VerticalAlignment.Center });
            var entry = new MenuItem { Header = header, IsEnabled = item.Enabled, Command = shell.CommandFor(item.Command) };
            AutomationProperties.SetName(entry, item.Title);
            menu.Items.Add(entry);
        }

        menu.Open(List);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ drag and drop

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragStart = e.GetCurrentPoint(List).Properties.IsLeftButtonPressed && RowAt(e.Source)?.Node is { Kind: ExplorerNodeKind.File or ExplorerNodeKind.Folder, IsLinked: false }
            ? e
            : null;
        _dragOrigin = e.GetPosition(List);
    }

    private async void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start || !e.GetCurrentPoint(List).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var delta = e.GetPosition(List) - _dragOrigin;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
        {
            return;
        }

        _dragStart = null;
        if (RowAt(start.Source)?.Node.Path is not { } path)
        {
            return;
        }

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(s_paths, new[] { path }));
        try
        {
            await DragDrop.DoDragDropAsync(start, transfer, DragDropEffects.Move | DragDropEffects.Copy).ConfigureAwait(true);
        }
        finally
        {
            SetDropTarget(null);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var target = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        var valid = e.DataTransfer.Contains(s_paths)
                    && target?.DataContext is ExplorerRowViewModel { Node.Kind: ExplorerNodeKind.Folder or ExplorerNodeKind.Project or ExplorerNodeKind.File };
        e.DragEffects = !valid ? DragDropEffects.None : e.KeyModifiers.HasFlag(KeyModifiers.Control) ? DragDropEffects.Copy : DragDropEffects.Move;
        SetDropTarget(valid ? target : null);
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        SetDropTarget(null);
        if (ViewModel is { } vm && RowAt(e.Source) is { } row && e.DataTransfer.TryGetValue(s_paths) is { Length: > 0 } paths)
        {
            e.Handled = true;
            await vm.DropAsync(paths, row.Node, copy: e.KeyModifiers.HasFlag(KeyModifiers.Control)).ConfigureAwait(true);
        }
    }

    private void SetDropTarget(ListBoxItem? item)
    {
        _dropTarget?.Classes.Remove("dropTarget");
        _dropTarget = item;
        _dropTarget?.Classes.Add("dropTarget");
    }

    private void OnSearchRequested(object? sender, EventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }, Avalonia.Threading.DispatcherPriority.Loaded);
}
