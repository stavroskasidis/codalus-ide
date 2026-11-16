using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Commands;
using Dock.Avalonia.Controls;
using Dock.Model.Core;

namespace Codalus.Shell.Tests;

/// <summary>
/// A single click on a bottom tool window tab selects it, also once its tooltip shows (#394: a tooltip shows at the
/// pointer, 20 below it; near the screen's bottom edge it flips above and keeps the 20, so it opened over the pointer,
/// took the click, and the tab only took its hover color).
/// </summary>
public sealed class ToolTabClickTests
{
    [AvaloniaFact]
    public async Task A_bottom_tab_takes_a_single_click_with_its_tooltip_open()
    {
        await using var f = ShellFixture.Create();
        await f.StartViewShownAsync(); // the Start page's column, shown late, moves the bottom tabs right
        var layout = f.Shell.Layout;
        var output = layout.ShowToolWindow(BuiltIns.OutputWindow, focus: false)!;
        var errorList = layout.ShowToolWindow(ErrorListViewModel.ToolWindowId, focus: false)!;
        layout.Factory.SetActiveDockable(errorList);
        ShellFixture.Pump();
        ShellFixture.RenderFrame(f.Window);

        // The pointer rests on the Output tab and its tooltip shows: the strip is near the window's bottom edge, where
        // a tooltip below the pointer doesn't fit.
        var tab = f.Window.GetVisualDescendants().OfType<ToolTabStripItem>().Single(t => t.DataContext == output);
        var header = tab.GetSelfAndVisualDescendants().OfType<Control>().First(c => ToolTip.GetTip(c) is not null);
        var at = tab.TranslatePoint(new Point(tab.Bounds.Width / 2, tab.Bounds.Height / 2), f.Window)!.Value;

        // The tooltip opens here rather than after the service's delay, and the service leaves it alone: the service
        // is one for every window, and another test's pointer in its own window closed it while this one rendered.
        ToolTip.SetServiceEnabled(header, false);
        f.Window.MouseMove(at);
        ToolTip.SetIsOpen(header, true);
        ShellFixture.RenderFrame(f.Window);
        var tip = f.Window.GetVisualDescendants().OfType<ToolTip>().Single(t => t.IsEffectivelyVisible);
        var tipBounds = new Rect(tip.TranslatePoint(default, f.Window)!.Value, tip.Bounds.Size);
        var tabBounds = new Rect(tab.TranslatePoint(default, f.Window)!.Value, tab.Bounds.Size);
        Assert.False(tipBounds.Intersects(tabBounds), $"the tooltip ({tipBounds}) covers its tab ({tabBounds})");

        // One click selects the tab.
        f.Window.MouseDown(at, MouseButton.Left);
        f.Window.MouseUp(at, MouseButton.Left);
        ShellFixture.Pump();
        Assert.Same(output, ((IDock)output.Owner!).ActiveDockable);
    }
}
