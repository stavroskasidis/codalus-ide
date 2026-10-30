using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.Views;

/// <summary>
/// The Solution Explorer: Enter or double-click opens, Left/Right collapse and expand (or move to the parent),
/// the context menu comes from the <c>solutionExplorer/context</c> menu location, and files and folders can be
/// dragged onto folders and projects (Ctrl copies).
/// </summary>
public sealed partial class SolutionExplorerView : UserControl, IPaneView
{
    public const string ContextMenuLocation = "solutionExplorer/context";

    /// <summary>The context menu's Add ▸, Git ▸ and Entity Framework ▸ submenus (M34a).</summary>
    public const string AddMenuLocation = "solutionExplorer/context/add";

    public const string GitMenuLocation = "solutionExplorer/context/git";

    public const string EntityFrameworkMenuLocation = "solutionExplorer/context/entityFramework";

    /// <summary>Drag payload: paths of the dragged files and folders, within this process.</summary>
    private static readonly DataFormat<string[]> s_paths = DataFormat.CreateInProcessFormat<string[]>("codalus.explorer.paths");

    private PointerPressedEventArgs? _dragStart;
    private Point _dragOrigin;
    private ListBoxItem? _dropTarget;
    private ExplorerRowViewModel? _revealWhenShown;
    private bool _shown;

    public SolutionExplorerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (ViewModel is { } vm)
            {
                vm.SearchRequested -= OnSearchRequested;
                vm.SearchRequested += OnSearchRequested;
                vm.RevealRequested -= OnRevealRequested;
                vm.RevealRequested += OnRevealRequested;
                _shown = false;
                UpdateShown();
            }
        };
        AttachedToVisualTree += (_, _) => UpdateShown();
        DetachedFromVisualTree += (_, _) => UpdateShown();
        List.EffectiveViewportChanged += (_, _) => UpdateShown();
        List.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                UpdateShown();
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

    /// <summary>
    /// A revealed row comes into view at once (M50), with one scroll: the list doesn't follow its selection on its own
    /// (M56). A reveal while the explorer is hidden waits for it to be shown.
    /// </summary>
    private void OnRevealRequested(object? sender, ExplorerRowViewModel row)
    {
        if (!this.IsAttachedToVisualTree() || !List.IsEffectivelyVisible)
        {
            _revealWhenShown = row;
            return;
        }

        _revealWhenShown = null;
        List.ScrollIntoView(row); // lays the row out itself when it isn't realized
    }

    /// <summary>
    /// Tells the view model whether the explorer is shown (Track Active Item waits while it isn't), and brings a reveal
    /// that came while it was hidden into view.
    /// </summary>
    private void UpdateShown()
    {
        var shown = this.IsAttachedToVisualTree() && List.IsEffectivelyVisible;
        if (shown == _shown)
        {
            return;
        }

        _shown = shown;
        ViewModel?.SetViewShown(shown);
        if (shown && _revealWhenShown is { } row && ViewModel?.Rows.Contains(row) == true)
        {
            _revealWhenShown = null;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => List.ScrollIntoView(row), Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

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

        if (MenuBuilder.Context(shell, ContextMenuLocation) is not { } menu)
        {
            return;
        }

        menu.Open(List);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ drag and drop

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragStart = e.GetCurrentPoint(List).Properties.IsLeftButtonPressed && RowAt(e.Source)?.Node is { Kind: ExplorerNodeKind.File or ExplorerNodeKind.Folder }
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
        if (RowAt(start.Source)?.Node is not { Path: { } path } node)
        {
            return;
        }

        ViewModel?.BeginDrag(node);

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
        var external = !e.DataTransfer.Contains(s_paths) && e.DataTransfer.Contains(DataFormat.File);
        var valid = (e.DataTransfer.Contains(s_paths) || external)
                    && target?.DataContext is ExplorerRowViewModel { Node.Kind: ExplorerNodeKind.Folder or ExplorerNodeKind.Project or ExplorerNodeKind.File or ExplorerNodeKind.Solution };
        e.DragEffects = !valid ? DragDropEffects.None : external || e.KeyModifiers.HasFlag(KeyModifiers.Control) ? DragDropEffects.Copy : DragDropEffects.Move;
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
        else if (ViewModel is { } model && RowAt(e.Source) is { } target && e.DataTransfer.TryGetFiles() is { Length: > 0 } files)
        {
            // From the file manager: copied in (ADR 0033).
            e.Handled = true;
            await model.DropExternalAsync([.. files.Select(f => f.TryGetLocalPath()).OfType<string>()], target.Node).ConfigureAwait(true);
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
