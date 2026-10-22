using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// The Toolbox (M41a): a click (or Space) arms an item (the Pointer disarms), drawn as the selection; a double-click or
/// Enter adds it at once (#66); a drag carries it onto the designer's surface. The item is a Button, which handles the
/// press itself, so the press, the move and the release are watched as they tunnel through the view (as the component
/// tray's buttons do): on the button they never came, and no drag started (#66).
/// </summary>
public partial class ToolboxView : UserControl
{
    private PointerPressedEventArgs? _pressed;
    private Button? _pressedButton;

    /// <summary>The item whose next click doesn't arm it: the second click of a double-click, and the release after a drag.</summary>
    private ToolboxItemViewModel? _suppressClickFor;

    private ToolboxViewModel? _watched;

    public ToolboxView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnItemPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnItemMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, _) =>
        {
            _pressed = null;
            _pressedButton = null;
        }, RoutingStrategies.Tunnel);
        AddHandler(DoubleTappedEvent, OnItemDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnItemKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Starts the drag of an item onto the surface (tests replace it: Avalonia.Headless has no drag source).</summary>
    internal Func<PointerPressedEventArgs, IDataTransfer, DragDropEffects, Task<DragDropEffects>> StartDrag { get; set; } = DragDrop.DoDragDropAsync;

    private ToolboxViewModel? ViewModel => DataContext as ToolboxViewModel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Watch(ViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Watch(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this.IsAttachedToVisualTree())
        {
            Watch(ViewModel);
        }
    }

    /// <summary>Follows the Toolbox's armed item (the Toolbox outlives this view, so only while it's shown).</summary>
    private void Watch(ToolboxViewModel? toolbox)
    {
        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnToolboxChanged;
        }

        _watched = toolbox;
        if (toolbox is not null)
        {
            toolbox.PropertyChanged += OnToolboxChanged;
        }

        MarkArmed();
    }

    private void OnToolboxChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolboxViewModel.Armed))
        {
            Infrastructure.Ui.Post(MarkArmed);
        }
    }

    /// <summary>The armed item's button has the class <c>armed</c> (the selection's colors); the items are records, so after a rebuild the same item is still the armed one.</summary>
    private void MarkArmed()
    {
        foreach (var button in this.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("toolboxItem")))
        {
            Mark(button);
        }
    }

    private void Mark(Button button) => button.Classes.Set("armed", ViewModel?.Armed is { } armed && Equals(button.DataContext, armed));

    private void OnItemAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Button button)
        {
            Mark(button);
        }
    }

    private void OnItemDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is Button button)
        {
            Mark(button);
        }
    }

    /// <summary>The item button an event came from (its icon, its text), not the category's header.</summary>
    private static (Button Button, ToolboxItemViewModel Item)? ItemOf(object? source) =>
        (source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is { DataContext: ToolboxItemViewModel item } button && button.Classes.Contains("toolboxItem") ? (button, item) : null;

    private void OnItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ToolboxItemViewModel item } || ViewModel is not { } toolbox)
        {
            return;
        }

        if (Equals(_suppressClickFor, item))
        {
            _suppressClickFor = null;
            return;
        }

        toolbox.Armed = item.IsPointer || Equals(toolbox.Armed, item) ? null : item;
    }

    private void OnItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ItemOf(e.Source) is not { } hit || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressed = null;
            _pressedButton = null;
            return;
        }

        // Never handled: the button still presses and clicks. The second press of a double-click mustn't toggle the item.
        _pressed = e;
        _pressedButton = hit.Button;
        _suppressClickFor = e.ClickCount >= 2 ? hit.Item : null;
    }

    private async void OnItemMoved(object? sender, PointerEventArgs e)
    {
        // A press that ended elsewhere (its release went to another element) is stale: the button has the pointer only while it's pressed.
        if (_pressed is not { } pressed || _pressedButton is not { DataContext: ToolboxItemViewModel { IsPointer: false } item } button
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || e.Pointer.Captured is not Visual captured || !(ReferenceEquals(captured, button) || button.IsVisualAncestorOf(captured)))
        {
            return;
        }

        var point = e.GetPosition(this);
        var start = pressed.GetPosition(this);
        if (Math.Abs(point.X - start.X) < 4 && Math.Abs(point.Y - start.Y) < 4)
        {
            return;
        }

        _pressed = null;
        _pressedButton = null;
        _suppressClickFor = item; // the release after the drag doesn't arm it
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(WinFormsDesignerView.ToolboxFormat, item.Type));
        await StartDrag(pressed, transfer, DragDropEffects.Copy).ConfigureAwait(true);
    }

    /// <summary>A double-click on an item adds it (the list doesn't move when the first click arms it, so both presses are on it).</summary>
    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ItemOf(e.Source) is { Item.IsPointer: false } hit && ViewModel is { } toolbox)
        {
            _suppressClickFor = hit.Item;
            toolbox.Pick(hit.Item);
            e.Handled = true;
        }
    }

    /// <summary>Enter on an item adds it, before the button would click (Space still clicks, and arms it). A key ends any suppressed click.</summary>
    private void OnItemKeyDown(object? sender, KeyEventArgs e)
    {
        _suppressClickFor = null;
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && ItemOf(e.Source) is { Item.IsPointer: false } hit && ViewModel is { } toolbox)
        {
            toolbox.Pick(hit.Item);
            e.Handled = true;
        }
    }
}
