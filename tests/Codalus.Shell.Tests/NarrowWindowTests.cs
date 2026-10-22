using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Codalus.Platform;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>
/// 0.6.0, the maintainer's report: as the window got narrower the title row ran under the caption buttons and the standard
/// toolbar wrapped onto a second line. Like the classic IDE, the solution's name trims, the search box shrinks to its
/// icon, the menu wraps onto a second row with the search box at its end, the caption buttons keep the first row's right
/// end, and the toolbar's groups fold into "…" drop-downs from the right.
/// </summary>
public class NarrowWindowTests
{
    private const double CaptionButtons = 138; // MainWindow's on Windows
    private const double MinDrag = 40;
    private static readonly double[] s_widths = [1400, 1000, 800, 650];

    [AvaloniaFact]
    public async Task The_title_row_keeps_the_caption_buttons_clear_and_wraps_the_menu_with_the_search_box_at_its_end()
    {
        await using var f = ShellFixture.Create();
        ShowSolution(f);
        foreach (var caption in new[] { CaptionButtons, 0 })
        {
            // Windows reserves the caption buttons' room in the extended title bar; elsewhere the system draws the title bar.
            f.Window.CaptionButtonsSpace.Width = caption;
            f.Window.TitleIcon.IsVisible = caption > 0;
            double? fullName = null;
            foreach (var width in s_widths)
            {
                var at = $"{width} px, caption {caption}";
                Resize(f, width);
                var window = f.Window;
                Assert.Equal(width, window.Bounds.Width);

                // The caption buttons' room is at the first row's right end, and nothing in the row reaches into it.
                var captionRect = Rect(window.CaptionButtonsSpace, window);
                Assert.True(Math.Abs(captionRect.Right - width) < 1, $"{at}: the caption buttons' room ends at {captionRect.Right}");
                Assert.Equal(caption, captionRect.Width);
                Assert.True(captionRect.Top < 1, $"{at}: the caption buttons' room starts at {captionRect.Top}");
                var parts = TitleParts(window).ToList();
                foreach (var (name, rect) in parts)
                {
                    Assert.True(rect.Right <= width - caption + 0.5, $"{at}: {name} ({rect}) reaches into the caption buttons' room");
                    Assert.True(rect.X >= -0.5, $"{at}: {name} ({rect}) starts left of the window");
                }

                // The window can still be dragged by its title row.
                var drag = Rect(window.TitleDragArea, window);
                Assert.True(drag.Width >= MinDrag - 0.5, $"{at}: the drag area is {drag.Width} px wide");
                Assert.True(drag.Right <= width - caption + 0.5, $"{at}: the drag area ({drag}) reaches into the caption buttons' room");

                // Every menu is shown, inside the window; on one row when there is room, on two when it is narrow.
                var menus = MenuItems(window);
                if (window.MenuBar.IsVisible)
                {
                    Assert.Equal(12, menus.Count);
                    Assert.All(menus, m => Assert.True(m.IsEffectivelyVisible && Rect(m, window) is { Width: > 0 } r && r.X >= 0 && r.Right <= width - caption + 0.5, $"{at}: {m.Header} at {Rect(m, window)}"));

                    // Drawn, not clipped: the pointer at each one's center finds it.
                    ShellFixture.RenderFrame(window);
                    foreach (var menu in menus)
                    {
                        var hit = window.InputHitTest(Rect(menu, window).Center) as Visual;
                        Assert.True(hit is not null && (hit == menu || menu.IsVisualAncestorOf(hit)), $"{at}: the pointer over {menu.Header} ({Rect(menu, window)}) finds {hit}");
                    }

                    var rows = menus.Select(m => Math.Round(Rect(m, window).Y)).Distinct().Count();
                    if (width >= 1400)
                    {
                        Assert.Equal(1, rows);
                    }

                    if (width <= 650)
                    {
                        Assert.Equal(2, rows);
                    }
                }

                // The solution's name trims with an ellipsis rather than pushing the rest out.
                var chip = window.TitleBar.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("solutionName"));
                var nameText = (TextBlock)chip.Child!;
                Assert.True(chip.IsEffectivelyVisible, at);
                Assert.Equal(TextTrimming.CharacterEllipsis, nameText.TextTrimming);
                fullName ??= chip.Bounds.Width;
                if (width <= 800 && window.MenuBar.IsVisible)
                {
                    Assert.True(chip.Bounds.Width < fullName - 1, $"{at}: the solution's name is {chip.Bounds.Width} px wide, as at 1400 ({fullName})");
                    Assert.True(chip.Bounds.Width >= 40, $"{at}: the solution's name is {chip.Bounds.Width} px wide");
                }

                // The search box: full when there is room; shrunk to its icon, or at the end of the menu's second row, when not.
                var search = Rect(window.SearchBox, window);
                var searchText = window.SearchBox.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("searchText"));
                if (width >= 1400)
                {
                    Assert.True(search.Width >= 200 && searchText.IsEffectivelyVisible, $"{at}: the search box is {search.Width} px wide");
                }
                else if (width <= 650 && window.MenuBar.IsVisible)
                {
                    var firstRow = menus.Min(m => Rect(m, window).Y);
                    var compact = search.Width < 60 && !searchText.IsEffectivelyVisible;
                    var wrapped = search.Y > firstRow + 10;
                    Assert.True(compact || wrapped, $"{at}: the search box ({search}) neither shrank nor wrapped");
                    Assert.True(wrapped, $"{at}: the search box ({search}) is not on the menu's second row");
                    var lastMenu = menus.Select(m => Rect(m, window)).Where(r => Math.Abs(r.Center.Y - search.Center.Y) < 6).ToList();
                    Assert.True(lastMenu.Count > 0 && search.X >= lastMenu.Max(r => r.Right) - 0.5, $"{at}: the search box ({search}) is not after the last row's menus");
                }

                // The rows don't overlap one another, and the row is as tall as its content.
                foreach (var (name, rect) in parts)
                {
                    foreach (var (other, otherRect) in parts.Where(p => p.Name != name))
                    {
                        Assert.False(rect.Intersects(otherRect) && rect.Intersect(otherRect) is { Width: > 1, Height: > 1 }, $"{at}: {name} ({rect}) overlaps {other} ({otherRect})");
                    }

                    Assert.True(rect.Bottom <= window.TitleBar.Bounds.Height + 0.5, $"{at}: {name} ({rect}) is below the title row");
                }
            }
        }
    }

    [AvaloniaFact]
    public async Task The_toolbar_folds_its_groups_into_overflow_drop_downs_instead_of_wrapping()
    {
        await using var f = ShellFixture.Create();
        ShowSolution(f);
        var toolbar = f.Window.GetVisualDescendants().OfType<StandardToolbar>().Single();
        foreach (var width in s_widths)
        {
            Resize(f, width);
            var at = $"{width} px";
            var row = Rect(toolbar.Panel, f.Window);
            var items = toolbar.Panel.Children.Where(c => c.IsVisible).ToList();
            var shown = items.Where(c => Rect(c, f.Window).Intersects(row)).ToList();
            var folded = items.Except(shown).ToList();
            var overflows = toolbar.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("overflow") && b.IsEffectivelyVisible).ToList();

            // One row: everything shown sits on it, inside the window and the row (which clips).
            Assert.All(shown.Concat(overflows), c => Assert.True(Rect(c, f.Window) is var r && r.Right <= Math.Min(row.Right, width) + 0.5 && r.Bottom <= row.Bottom + 0.5 && r.Y >= row.Y - 0.5, $"{at}: {Describe(c)} at {Rect(c, f.Window)}, the row is {row}"));
            Assert.True(row.Height < 40, $"{at}: the toolbar is {row.Height} px tall");

            if (width >= 1400)
            {
                Assert.Empty(folded);
                Assert.Empty(overflows);
                continue;
            }

            if (width <= 650)
            {
                Assert.NotEmpty(folded);
            }

            if (folded.Count == 0)
            {
                continue;
            }

            // Each "…" lists the commands its group folded, and together they list every folded command, in order.
            Assert.NotEmpty(overflows);
            Assert.All(overflows, o => Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(o))));
            var listed = new List<string>();
            foreach (var overflow in overflows)
            {
                var flyout = Assert.IsType<MenuFlyout>(overflow.Flyout);
                flyout.ShowAt(overflow);
                ShellFixture.Pump();
                var entries = flyout.Items.OfType<MenuItem>().Select(i => (string)i.Header!).ToList();
                flyout.Hide();
                Assert.NotEmpty(entries);

                // All of one group: no separator between the first and the last of them.
                var indexes = entries.Select(e => items.FindIndex(c => AutomationProperties.GetName(c) == e)).ToList();
                Assert.DoesNotContain(-1, indexes);
                Assert.DoesNotContain(items.Skip(indexes.Min()).Take(indexes.Max() - indexes.Min()), c => c.Classes.Contains("toolbarSeparator"));
                listed.AddRange(entries);
            }

            Assert.Equal(folded.Where(c => c is not Border and not TextBlock).Select(c => AutomationProperties.GetName(c)), listed);

            // The folded commands still run from their drop-down.
            Assert.All(folded, c => Assert.False(KeyboardNavigation.GetIsTabStop(c) && c is Button, $"{at}: {Describe(c)} is folded but still a tab stop"));
        }

        // Wide again: everything is back on the row.
        Resize(f, 1400);
        Assert.DoesNotContain(toolbar.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("overflow") && b.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void The_toolbar_leaves_room_for_its_first_overflow_drop_down()
    {
        // A "…" that hasn't shown yet measures as nothing (a hidden control's size). The first time the row folds, from a
        // width where nothing did, it still makes room for it: the smoke test's 650 px window, after 800 px, had More
        // commands at 627 in a row 642 px wide. Two groups of three 100 px items: 601 px with the separator; at 510 px,
        // folding one item leaves 501, room for the items but not for the "…" after them, so two fold.
        var panel = new ToolbarGroupsPanel { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, Width = 700 };
        for (var i = 0; i < 7; i++)
        {
            panel.Children.Add(i == 3 ? new Border { Classes = { "toolbarSeparator" }, Width = 1, Height = 20 } : new Border { Width = 100, Height = 20 });
        }

        var window = new Window { Width = 800, Height = 100, Content = panel };
        window.Show();
        try
        {
            ShellFixture.Pump();
            Assert.Empty(panel.Folded);
            Assert.Empty(panel.Overflows);

            panel.Width = 510;
            ShellFixture.Pump();
            var overflow = Assert.Single(panel.Overflows);
            Assert.True(overflow.Bounds.Width > 0, "the \"…\" has no width");
            Assert.Equal(2, panel.Folded.Count);
            foreach (var item in panel.Children.Where(c => !panel.Folded.Contains(c)).Append(overflow))
            {
                Assert.True(item.Bounds.X >= 0 && item.Bounds.Right <= panel.Bounds.Width + 0.5, $"{Describe(item)} at {item.Bounds}, the row {panel.Bounds.Width} px wide");
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task The_wrapped_menu_still_opens_and_the_keyboard_reaches_every_menu()
    {
        if (PlatformInfo.Current == PlatformKind.MacOS)
        {
            return; // the global menu bar (NativeMenu)
        }

        await using var f = ShellFixture.Create();
        f.Window.CaptionButtonsSpace.Width = CaptionButtons;
        Resize(f, 650);
        var menus = MenuItems(f.Window);
        Assert.True(menus.Select(m => Math.Round(Rect(m, f.Window).Y)).Distinct().Count() > 1, "the menu is on one row");

        // Alt selects the first menu; Right goes along the first row, onto the second and back round to the first.
        f.Window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, null);
        f.Window.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);
        ShellFixture.Pump();
        var reached = new List<string>();
        for (var i = 0; i <= menus.Count; i++)
        {
            var selected = menus.SingleOrDefault(m => m.IsSelected);
            Assert.True(selected is not null, $"no menu is selected after {string.Join(", ", reached)}");
            reached.Add(AutomationProperties.GetName(selected)!);
            f.Press(Key.Right);
        }

        Assert.Equal(menus.Select(m => AutomationProperties.GetName(m)).Append(AutomationProperties.GetName(menus[0])), reached);
        f.Press(Key.Escape);

        // Left from the first goes back round to the last, on the second row.
        f.Window.KeyPress(Key.LeftAlt, RawInputModifiers.Alt, PhysicalKey.AltLeft, null);
        f.Window.KeyRelease(Key.LeftAlt, RawInputModifiers.None, PhysicalKey.AltLeft, null);
        ShellFixture.Pump();
        f.Press(Key.Left);
        Assert.Same(menus[^1], menus.SingleOrDefault(m => m.IsSelected));
        f.Press(Key.Escape);
        f.Press(Key.Escape);

        // A menu on the second row opens under its item.
        var help = menus.Single(m => AutomationProperties.GetName(m) == "Help");
        Assert.True(Rect(help, f.Window).Y > Rect(menus[0], f.Window).Y + 10, "Help is on the first row");
        help.Open();
        ShellFixture.Pump();
        Assert.True(help.IsSubMenuOpen);
        var popup = help.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().First();
        var content = (Visual)popup.Child!;
        var top = content.PointToScreen(default).Y;
        var under = help.PointToScreen(new Point(0, help.Bounds.Height)).Y;
        Assert.True(Math.Abs(top - under) <= 8, $"Help's menu opens at {top}, its item ends at {under}");
        help.Close();
    }

    private static void ShowSolution(ShellFixture f)
    {
        f.Shell.Toolbar.SetSolution([("Debug", "Any CPU"), ("Release", "Any CPU")], ("Debug", "Any CPU"), "TabletopAssistant.Everything");
        f.Shell.Toolbar.SetStartup([new StartupProjectChoice("TabletopAssistant", "/work/TabletopAssistant/TabletopAssistant.csproj")], "/work/TabletopAssistant/TabletopAssistant.csproj", null);
        ShellFixture.Pump();
    }

    private static void Resize(ShellFixture f, double width)
    {
        f.Window.Width = width;
        f.Window.Height = 800;
        ShellFixture.Pump();
        ShellFixture.Pump();
    }

    private static List<MenuItem> MenuItems(MainWindow window) => [.. window.MenuBar.Items.OfType<MenuItem>().Where(m => m.IsVisible)];

    private static IEnumerable<(string Name, Rect Rect)> TitleParts(MainWindow window)
    {
        if (window.TitleIcon.IsVisible)
        {
            yield return ("the icon", Rect(window.TitleIcon, window));
        }

        if (window.MenuBar.IsVisible)
        {
            foreach (var item in MenuItems(window))
            {
                yield return ($"the {AutomationProperties.GetName(item)} menu", Rect(item, window));
            }
        }

        yield return ("the search box", Rect(window.SearchBox, window));
        foreach (var chip in window.TitleBar.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("solutionName") && b.IsEffectivelyVisible))
        {
            yield return ("the solution's name", Rect(chip, window));
        }
    }

    private static Rect Rect(Visual visual, Visual root) =>
        visual.TranslatePoint(default, root) is { } origin ? new Rect(origin, visual.Bounds.Size) : default;

    private static string Describe(Control control) => AutomationProperties.GetName(control) ?? control.GetType().Name;
}
