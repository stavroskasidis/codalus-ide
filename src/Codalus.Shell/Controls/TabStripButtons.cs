using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Codalus.Shell.Docking;
using Codalus.Workbench.Localization;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;

namespace Codalus.Shell.Controls;

/// <summary>
/// A document group's buttons beside its tabs, as a classic IDE's document well has: the tab list (<c>…</c>, every open
/// document of the group by name, the hidden ones too) and the tab options (the gear: where the tabs go, their colors,
/// multiple rows). The strip's <c>RightContent</c>, or its <c>TopContent</c> while the tabs are on the left or right.
/// The mouse wheel over the strip goes to the shell too (Toggle Multiple Rows with Mouse Wheel). The buttons reach the
/// shell through the group's factory (<see cref="ShellDockFactory"/>), so a floating window's work as the main one's.
/// </summary>
public sealed class TabStripButtons : StackPanel
{
    private DocumentTabStrip? _strip;

    public TabStripButtons()
    {
        Orientation = Orientation.Horizontal;
        HorizontalAlignment = HorizontalAlignment.Right;
        Margin = new Thickness(2, 0);
        ListButton = Add("more", Localizer.Get("msg.tabStripButtons.list", "Open Documents"), button =>
        {
            if (DataContext is IDocumentDock dock)
            {
                Factory?.ShowTabList?.Invoke(dock, button);
            }
        });
        OptionsButton = Add("gear", Localizer.Get("msg.tabStripButtons.options", "Tab Options"), button => Factory?.ShowTabOptions?.Invoke(button));
    }

    /// <summary>The tab list button (<c>…</c>).</summary>
    public Button ListButton { get; }

    /// <summary>The tab options button (the gear).</summary>
    public Button OptionsButton { get; }

    private ShellDockFactory? Factory => (DataContext as IDocumentDock)?.Factory as ShellDockFactory;

    private Button Add(string icon, string name, Action<Button> click)
    {
        var button = new Button { Width = 20, Height = 20, MinWidth = 20, MinHeight = 20, Padding = default, Focusable = false };
        button.Classes.Add("toolbar");
        button.Classes.Add("icon");
        ButtonIcon.SetKind(button, icon);
        ToolTip.SetTip(button, name);
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click(button);
        Children.Add(button);
        return button;
    }

    /// <summary>
    /// Beside a row of tabs, the buttons stay centered on its first row when the tabs take more (pinned tabs on a row of
    /// their own, multiple rows), as the classic IDE's.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_strip is { Orientation: Orientation.Horizontal }
            && _strip.GetVisualDescendants().OfType<DocumentTabStripItem>().FirstOrDefault(t => t.Bounds.Height > 0) is { } tab
            && tab.Bounds.Height < finalSize.Height)
        {
            base.ArrangeOverride(finalSize.WithHeight(tab.Bounds.Height));
            return finalSize;
        }

        return base.ArrangeOverride(finalSize);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _strip = this.FindAncestorOfType<DocumentTabStrip>();
        // Tunneling, so the wheel reaches the shell before Dock's strip scrolls its tabs with it.
        _strip?.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _strip?.RemoveHandler(PointerWheelChangedEvent, OnWheel);
        _strip = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        // Tabs on the left or right scroll with the wheel; only a row of tabs on top grows and shrinks.
        if (_strip is { Orientation: Orientation.Horizontal } && e.Delta.Y != 0 && Factory?.TabStripWheel?.Invoke(e.Delta.Y) == true)
        {
            e.Handled = true;
        }
    }
}
