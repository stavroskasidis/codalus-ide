using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.Views;
using MenuItem = Avalonia.Controls.MenuItem;
using MenuLocations = Codalus.Workbench.Menus.MenuLocations;

namespace Codalus.Shell.Tests;

/// <summary>
/// One look for every menu (the maintainer's 0.5 review: the Git Changes window's menu had no icons and its own sizes):
/// a view's entries are made like the command registry's, and every menu lines up the same way.
/// </summary>
public sealed partial class MenuEntryTests
{
    private const string ControlsNamespace = "using:Codalus.Shell.Controls";

    [AvaloniaFact]
    public async Task Every_menu_lines_up_its_text_in_24_px_rows_with_or_without_icons()
    {
        await using var f = ShellFixture.Create();
        var registry = MenuBuilder.Context(f.Shell, MenuLocations.EditorContext)!;
        var plain = new ContextMenu { Items = { new MenuItem { Header = "Compare with Unmodified" }, new Separator(), new MenuItem { Header = "Stage" } } };
        var entries = new ContextMenu
        {
            Items =
            {
                MenuEntry.Create("Compare with Unmodified", "compare"),
                MenuEntry.Create("Open File", null),
                new Separator(),
                MenuEntry.Create("Discard Changes", "discard", keys: "Delete"),
                MenuEntry.Submenu("Reset", "reset", MenuEntry.Create("Keep Changes", null)),
                new MenuItem { Header = "Word Wrap", ToggleType = MenuItemToggleType.CheckBox, IsChecked = true },
                new MenuItem { Header = "Opus 5.5", ToggleType = MenuItemToggleType.Radio, IsChecked = true }, // a picker's choice (AI Chat, M51)
                new MenuItem { Header = "Sonnet 5.5", ToggleType = MenuItemToggleType.Radio },
            },
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, Margin = new Thickness(10), VerticalAlignment = VerticalAlignment.Top };
        panel.Children.Add(registry);
        panel.Children.Add(plain);
        panel.Children.Add(entries);
        var window = new Window { Width = 1100, Height = 760, Content = panel };
        window.Show();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                ShellFixture.Pump();
            }

            // The title starts at the same place in every menu: the icon column is kept, and a check takes the icon's place.
            double TextStart(MenuItem item, Control menu) =>
                item.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "PART_HeaderPresenter").TranslatePoint(default, menu)!.Value.X;
            var starts = new[] { registry, plain, entries }
                .SelectMany(menu => menu.Items.OfType<MenuItem>().Where(i => i.IsVisible).Select(item => (Menu: menu, Item: item)))
                .Select(m => (Header: m.Item.Header?.ToString(), X: TextStart(m.Item, m.Menu), m.Item.Bounds.Height))
                .ToList();
            var first = starts[0].X;
            Assert.All(starts, s => Assert.True(Math.Abs(s.X - first) < 0.5, $"{s.Header} starts at {s.X:0.#}, not {first:0.#}"));
            Assert.All(starts, s => Assert.Equal(24, s.Height, 0));

            // Separators start under the text, not at the menu's edge; the submenu's chevron is a small one.
            Assert.All(new[] { plain, entries }.SelectMany(m => m.Items.OfType<Separator>()), s => Assert.Equal(28, s.Margin.Left));
            var chevron = ((MenuItem)entries.Items[4]!).GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(p => p.Name == "PART_ChevronPath");
            Assert.True(chevron.Bounds.Height <= 9, $"the chevron is {chevron.Bounds.Height} px");

            // Entries made by MenuEntry: their icon in its palette color, the shortcut on the right, and a name.
            var discard = (MenuItem)entries.Items[3]!;
            Assert.Equal("discard", Assert.IsType<Icon>(discard.Icon).Kind);
            Assert.Equal("Discard Changes", Avalonia.Automation.AutomationProperties.GetName(discard));
            Assert.Contains("Delete", discard.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.Equal("Open File", ((MenuItem)entries.Items[1]!).Header);
            if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is { Length: > 0 } snapshots)
            {
                Directory.CreateDirectory(snapshots);
                using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
                frame!.Save(Path.Combine(snapshots, "menus.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_xaml_entry_takes_its_icon_or_its_commands_icon()
    {
        var item = new MenuItem();
        MenuEntry.SetIcon(item, "compare");
        Assert.Equal("compare", Assert.IsType<Icon>(item.Icon).Kind);
        MenuEntry.SetCommandIcon(item, "git.merge");
        var icon = Assert.IsType<Icon>(item.Icon);
        Assert.Equal("merge", icon.Kind);
        Assert.Contains("accent-git", icon.Classes);
    }

    [Fact]
    public void Every_entry_of_a_views_menu_has_an_icon()
    {
        // A check or radio entry has its mark instead; a submenu's choices (Reset's Soft, Mixed, Hard) may go without.
        XNamespace avalonia = "https://github.com/avaloniaui";
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(ShellFixture.RepoRoot, "src", "Codalus.Shell"), "*.axaml", SearchOption.AllDirectories))
        {
            var xaml = XDocument.Load(file);
            var menus = xaml.Descendants(avalonia + "ContextMenu").Concat(xaml.Descendants(avalonia + "MenuFlyout"));
            foreach (var entry in menus.SelectMany(m => m.Elements(avalonia + "MenuItem")))
            {
                var hasIcon = entry.Attribute("Icon") is not null
                    || entry.Attribute(XName.Get("MenuEntry.Icon", ControlsNamespace)) is not null
                    || entry.Attribute(XName.Get("MenuEntry.CommandIcon", ControlsNamespace)) is not null;
                if (!hasIcon && entry.Attribute("ToggleType") is null)
                {
                    missing.Add($"{Path.GetFileName(file)}: {(string?)entry.Attribute("Header") ?? (string?)entry.Attribute("Tag")}");
                }
            }
        }

        Assert.True(missing.Count == 0, "Menu entries without an icon (controls:MenuEntry.Icon):\n" + string.Join('\n', missing));
    }

    [Fact]
    public void Views_make_their_menu_entries_with_MenuEntry()
    {
        // MenuBuilder makes the registry's, ToolbarPanel mirrors its buttons, and a check or radio entry is a plain MenuItem.
        string[] makers = ["MenuEntry.cs", "MenuBuilder.cs", "ToolbarPanel.cs"];
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(ShellFixture.RepoRoot, "src", "Codalus.Shell"), "*.cs", SearchOption.AllDirectories))
        {
            if (makers.Contains(Path.GetFileName(file)) || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (NewMenuItem().IsMatch(lines[i]) && !lines[i].Contains("ToggleType", StringComparison.Ordinal))
                {
                    found.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(found.Count == 0, "Menu entries made by hand (use MenuEntry.Create):\n" + string.Join('\n', found));
    }

    [GeneratedRegex(@"new MenuItem\b(?!Rule)")]
    private static partial Regex NewMenuItem();
}
