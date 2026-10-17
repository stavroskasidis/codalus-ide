using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.Docking;
using Codalus.Shell.Theming;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// M34d (UI polish, round 2): WCAG AA contrast for every token pair in every theme (AAA for text in the theme families'
/// High contrast variants, M45), no text cut off without an ellipsis at the tool windows' minimum width, toolbars that
/// overflow instead of wrapping, and the document tabs' states (selected without the focus, preview on the right,
/// pinned, colored by project).
/// </summary>
public partial class PolishTests
{
    /// <summary>The narrowest a docked tool window is expected to be (Shell.Size.ToolWindowMinWidth).</summary>
    private const double ToolWindowMinWidth = 280;

    /// <summary>Every built-in theme: Classic's four and the families' twelve (M45).</summary>
    private static readonly (string Name, ThemeVariant Variant)[] s_themes = [.. ThemeService.Themes.Select(t => (ThemeService.Title(t.Id), t.Variant))];

    // ------------------------------------------------------------------ contrast

    [AvaloniaFact]
    public void Every_token_pair_meets_WCAG_AA_in_every_theme()
    {
        var pairs = File.ReadAllLines(Path.Combine(ShellFixture.RepoRoot, "tests", "Codalus.Shell.Tests", "TestData", "contrast-pairs.txt"))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(p => (Foreground: p[0], Background: p[1], Minimum: double.Parse(p[2], CultureInfo.InvariantCulture)))
            .ToList();
        var failures = new List<string>();
        foreach (var (name, variant) in s_themes)
        {
            foreach (var (foreground, background, minimum) in pairs)
            {
                var fg = Color(foreground, variant);
                var bg = Color(background, variant);
                var ratio = Contrast(fg, bg);
                // A theme family's High contrast variant holds text to 7:1 (AAA, M45); Classic's keeps AA.
                var required = minimum >= 4.5 && ThemeService.IsHighContrast(variant) && variant != ThemeService.HighContrast ? 7 : minimum;
                if (ratio < required)
                {
                    failures.Add($"{name}: {foreground} {fg} on {background} {bg} is {ratio:0.00}:1, below {required}:1");
                }
            }
        }

        Assert.Empty(failures);

        // Every foreground token is checked against what it is drawn on: a new one must be added to the list.
        var tokens = File.ReadAllText(Path.Combine(ShellFixture.RepoRoot, "src", "Codalus.Shell", "Theming", "Tokens.axaml"));
        var foregrounds = KeyPattern().Matches(tokens).Select(m => m.Groups[1].Value).Where(k => k.StartsWith("Shell.", StringComparison.Ordinal) && k.EndsWith("Foreground", StringComparison.Ordinal)).Distinct();
        var listed = pairs.Select(p => p.Foreground).ToHashSet(StringComparer.Ordinal);
        Assert.All(foregrounds, key => Assert.Contains(key, listed));
    }

    private static Color Color(string key, ThemeVariant variant)
    {
        Assert.True(Application.Current!.TryGetResource(key, variant, out var value) && value is ISolidColorBrush, $"{key} is not a color in {variant}");
        return ((ISolidColorBrush)value!).Color;
    }

    /// <summary>WCAG 2.1's contrast ratio of two opaque colors.</summary>
    internal static double Contrast(Color a, Color b)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [GeneratedRegex("x:Key=\"([^\"]+)\"")]
    private static partial Regex KeyPattern();

    // ------------------------------------------------------------------ clipped text

    /// <summary>In Classic and in each theme family (M45), whose shapes change sizes: one variant each, the sizes are the family's.</summary>
    [AvaloniaTheory]
    [InlineData("classic.dark")]
    [InlineData("harbor.light")]
    [InlineData("graphite.dark")]
    [InlineData("daylight.light")]
    [InlineData("ember.dark")]
    public async Task Tool_windows_at_their_minimum_width_cut_no_text_without_an_ellipsis(string theme)
    {
        await using var f = ShellFixture.Create();
        await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, theme, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == ThemeService.Resolve(theme, null));
        var solution = f.CopyFixture();
        f.Shell.Trust.Trust(Path.GetDirectoryName(Path.GetDirectoryName(solution))!);
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Explorer.Rows.Count > 3, seconds: 180);

        var problems = new List<string>();
        foreach (var descriptor in f.Shell.Workbench.ToolWindows.All.ToList())
        {
            if (f.Shell.Layout.ShowToolWindow(descriptor.Id, focus: false) is not { } tool)
            {
                continue;
            }

            ShellFixture.Pump();
            var window = new Window { Width = ToolWindowMinWidth, Height = 420, Content = new ContentControl { Content = tool } };
            window.Show();
            ShellFixture.Pump();
            ShellFixture.Pump();
            problems.AddRange(Clipped(window).Select(p => $"{descriptor.Id}: {p}"));
            window.Close();
            ShellFixture.Pump();
        }

        Assert.True(problems.Count == 0, $"Text cut off at {ToolWindowMinWidth} px in {theme}:\n" + string.Join('\n', problems));
    }

    /// <summary>
    /// TextBlocks under <paramref name="root"/> whose text is cut off: wider than what their clipping ancestors show,
    /// without an ellipsis (or with one but no tooltip to read the rest), where the content doesn't scroll sideways.
    /// </summary>
    internal static IEnumerable<string> Clipped(Visual root)
    {
        var rootRect = new Rect(root.Bounds.Size);
        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>())
        {
            var content = text.Text ?? text.Inlines?.Text;
            if (!text.IsEffectivelyVisible || string.IsNullOrWhiteSpace(content) || text.Bounds.Width < 1 || text.Bounds.Height < 1
                || text.TextWrapping != TextWrapping.NoWrap || text.FindAncestorOfType<TextBox>() is not null)
            {
                continue;
            }

            if (text.TranslatePoint(default, root) is not { } origin)
            {
                continue;
            }

            // What its clipping ancestors (and the root) show of it, sideways and up and down.
            var box = new Rect(origin, text.Bounds.Size);
            double left = Math.Max(box.Left, 0), right = Math.Min(box.Right, rootRect.Right);
            double top = Math.Max(box.Top, 0), bottom = Math.Min(box.Bottom, rootRect.Bottom);
            var scrolls = false;
            for (var v = text.GetVisualParent(); v is not null && v != root; v = v.GetVisualParent())
            {
                if (v is ScrollContentPresenter { CanHorizontallyScroll: true })
                {
                    scrolls = true;
                    break;
                }

                if (v.ClipToBounds && v.TranslatePoint(default, root) is { } at)
                {
                    left = Math.Max(left, at.X);
                    right = Math.Min(right, at.X + v.Bounds.Width);
                    top = Math.Max(top, at.Y);
                    bottom = Math.Min(bottom, at.Y + v.Bounds.Height);
                }
            }

            if (scrolls || bottom - top < 1 || InOverflow(text))
            {
                continue; // scrolls sideways, or is scrolled out of view
            }

            var visible = new Rect(left, top, Math.Max(0, right - left), bottom - top);
            var layout = new TextLayout(content, new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, null);
            var needed = layout.WidthIncludingTrailingWhitespace + text.Padding.Left + text.Padding.Right;
            if (needed <= visible.Width + 1)
            {
                continue;
            }

            var label = content.Length > 40 ? content[..40] + "…" : content;
            if (text.TextTrimming == TextTrimming.None)
            {
                yield return $"\"{label}\" needs {needed:0} px, {visible.Width:0} px shown, no ellipsis ({Describe(text, root)})";
            }
            else if (box.Width > visible.Width + 1)
            {
                yield return $"\"{label}\" is trimmed in a box wider than shown ({box.Width:0} > {visible.Width:0} px), so its ellipsis is cut off ({Describe(text, root)})";
            }
            else if (!HasTooltip(text))
            {
                yield return $"\"{label}\" is trimmed without a tooltip ({Describe(text, root)})";
            }
        }
    }

    /// <summary>Whether the text is on a toolbar item that moved into the toolbar's overflow menu.</summary>
    private static bool InOverflow(Visual text)
    {
        Visual? child = text;
        for (var v = text.GetVisualParent(); v is not null; child = v, v = v.GetVisualParent())
        {
            if (v is ToolbarPanel panel)
            {
                return child is Control item && panel.Overflowed.Contains(item);
            }
        }

        return false;
    }

    private static bool HasTooltip(Visual text)
    {
        var depth = 0;
        for (Visual? v = text; v is not null && depth < 8; v = v.GetVisualParent(), depth++)
        {
            if (v is Control control && ToolTip.GetTip(control) is not null)
            {
                return true;
            }

            if (v is ListBoxItem or TreeViewItem)
            {
                break;
            }
        }

        return false;
    }

    private static string Describe(Visual visual, Visual root)
    {
        var names = new List<string>();
        for (var v = visual; v is not null && v != root && names.Count < 6; v = v.GetVisualParent())
        {
            names.Add(v is Control { Name: { Length: > 0 } name } ? $"{v.GetType().Name}#{name}" : v.GetType().Name);
        }

        names.Reverse();
        return string.Join(" > ", names);
    }

    // ------------------------------------------------------------------ toolbars

    [AvaloniaFact]
    public void A_narrow_toolbar_moves_what_does_not_fit_into_its_overflow_menu()
    {
        var clicked = 0;
        var panel = new ToolbarPanel();
        for (var i = 0; i < 8; i++)
        {
            var button = new Button { Content = new Icon { Kind = "play" } };
            button.Classes.Add("toolbar");
            Avalonia.Automation.AutomationProperties.SetName(button, "Command " + i);
            button.Click += (_, _) => clicked++;
            panel.Children.Add(button);
        }

        var combo = new ComboBox { ItemsSource = new[] { "One", "Two" }, SelectedIndex = 0, Width = 90 };
        combo.Classes.Add("toolbar");
        Avalonia.Automation.AutomationProperties.SetName(combo, "Group by");
        panel.Children.Add(combo);
        var search = new TextBox { MinWidth = 60, PlaceholderText = "Search" };
        panel.Children.Add(search);

        var window = new Window { Width = 600, Height = 100, Content = panel };
        window.Show();
        ShellFixture.Pump();
        Assert.Empty(panel.Overflowed);
        Assert.True(search.Bounds.Width >= 60, $"the search box fills the rest ({search.Bounds.Width})");
        Assert.True(panel.Bounds.Height <= 30, $"one row ({panel.Bounds.Height} px)");

        window.Width = 200;
        ShellFixture.Pump();
        Assert.Contains(combo, panel.Overflowed);
        Assert.True(panel.Overflowed.Count >= 3, $"{panel.Overflowed.Count} items overflow");
        Assert.True(panel.Bounds.Height <= 30, "still one row");
        Assert.DoesNotContain(search, panel.Overflowed);

        // The overflow menu's entries run what the buttons run and choose in the combo box.
        var hiddenButton = panel.Overflowed.OfType<Button>().First();
        var entry = (MenuItem)ToolbarPanel.Entry(hiddenButton, panel)!;
        Assert.StartsWith("Command ", entry.Header as string);
        entry.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(1, clicked);
        var choices = (MenuItem)ToolbarPanel.Entry(combo, panel)!;
        Assert.Equal("Group by", choices.Header);
        ((MenuItem)choices.Items[1]!).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(1, combo.SelectedIndex);

        // Hidden items aren't reached with Tab (the overflow menu has them); they are again once they show.
        Assert.All(panel.Overflowed, c => Assert.False(Avalonia.Input.KeyboardNavigation.GetIsTabStop(c)));
        var wasHidden = hiddenButton;
        window.Width = 600;
        ShellFixture.Pump();
        Assert.Empty(panel.Overflowed);
        Assert.True(Avalonia.Input.KeyboardNavigation.GetIsTabStop(wasHidden));
        window.Close();
    }

    // ------------------------------------------------------------------ tool window tabs

    [Fact]
    public void Tabs_that_dont_fit_share_the_width_and_narrow_ones_keep_theirs()
    {
        Assert.True(double.IsPositiveInfinity(FittingTabPanel.Cap([80, 90, 100], 300)));
        Assert.True(double.IsPositiveInfinity(FittingTabPanel.Cap([80, 90], double.PositiveInfinity)));
        Assert.Equal(110, FittingTabPanel.Cap([80, 200, 150], 300), 3); // 80 keeps its width, the others share 220
        Assert.Equal(75, FittingTabPanel.Cap([100, 100, 100, 100], 300), 3);
    }

    [AvaloniaFact]
    public async Task Tool_window_tabs_fit_their_pane_with_ellipses_and_whole_names_in_tooltips()
    {
        // The maintainer's report: with many tool windows in one group, the tabs ran off the pane ("Database E").
        await using var f = ShellFixture.Create();
        foreach (var descriptor in f.Shell.Workbench.ToolWindows.All.ToList())
        {
            f.Shell.Layout.ShowToolWindow(descriptor.Id, focus: false);
        }

        f.Window.Width = 1000;
        ShellFixture.Pump();
        var strips = f.Window.GetVisualDescendants().OfType<Dock.Avalonia.Controls.ToolTabStrip>().Where(s => s.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(strips);
        var crowded = 0;
        foreach (var strip in strips)
        {
            var panel = strip.GetVisualDescendants().OfType<FittingTabPanel>().FirstOrDefault();
            Assert.NotNull(panel);
            var tabs = panel.Children.Where(c => c.IsVisible).ToList();
            // Every tab shown fits: those that don't, even collapsed, are left out whole (never cut at the edge).
            var used = tabs.Sum(t => t.Bounds.Width);
            Assert.True(used <= strip.Bounds.Width + 1, $"{tabs.Count} tabs take {used:0} px of {strip.Bounds.Width:0}");
            Assert.All(tabs.Where(t => t.Bounds.Width > 0), t => Assert.True(t.Bounds.Right <= strip.Bounds.Width + 1, $"a tab ends at {t.Bounds.Right:0} of {strip.Bounds.Width:0}"));
            Assert.Contains(tabs, t => t is Avalonia.Controls.Primitives.TabStripItem { IsSelected: true } && t.Bounds.Width > 0);
            var titles = tabs.SelectMany(t => t.GetVisualDescendants().OfType<TextBlock>().Where(b => b.Classes.Contains("tabTitle"))).ToList();
            Assert.All(titles, b => Assert.Equal(b.Text, ToolTip.GetTip((Control)b.GetVisualParent()!)));
            if (titles.Any(b => !double.IsPositiveInfinity(b.MaxWidth)) || tabs.Any(FittingTabPanel.GetIconOnly))
            {
                crowded++;
                Assert.All(titles, b => Assert.Equal(TextTrimming.CharacterEllipsis, b.TextTrimming));
            }
        }

        Assert.True(crowded > 0, "with every tool window open, some group has shrunk tabs");
        // The most crowded groups show only icons, each tab no wider than its icon and padding.
        var iconTabs = f.Window.GetVisualDescendants().OfType<FittingTabPanel>().SelectMany(p => p.Children).Where(FittingTabPanel.GetIconOnly).ToList();
        Assert.NotEmpty(iconTabs);
        Assert.All(iconTabs, t => Assert.True(t.Bounds.Width < FittingTabPanel.MinTabWidth, $"an icon-only tab is {t.Bounds.Width:0} px"));
        if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is { Length: > 0 } snapshots)
        {
            Directory.CreateDirectory(snapshots);
            using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(f.Window);
            frame!.Save(Path.Combine(snapshots, "tool-window-tabs.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }

    // ------------------------------------------------------------------ document tabs

    [AvaloniaFact]
    public async Task Tabs_keep_their_titles_readable_preview_on_the_right_pinned_on_the_left_and_colored_by_project()
    {
        await using var f = ShellFixture.Create();
        var solution = f.CopyFixture();
        f.Shell.Trust.Trust(Path.GetDirectoryName(Path.GetDirectoryName(solution))!);
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count > 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);
        // Three source files of the loaded projects (the fixture also has files no project of the solution owns).
        var files = f.Shell.Solutions.Projects.Values
            .SelectMany(p => p.Items.Where(i => i.Link is null).Select(i => i.Path))
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(3)
            .ToList();
        Assert.True(files.Count == 3, "three source files in the fixture's projects");

        await f.Shell.OpenFileAsync(files[0], preview: false);
        await f.Shell.OpenFileAsync(files[1], preview: true);
        await f.Shell.OpenFileAsync(files[2], preview: false);
        ShellFixture.Pump();
        var tabs = f.Shell.Layout.Documents.ToList();
        Assert.Equal(files[1], tabs[^1].FilePath); // the preview tab stays on the right
        Assert.True(tabs[^1].IsPreview);

        // Pinning moves a tab to the left; Close All But Pinned keeps it.
        var third = tabs.Single(d => d.FilePath == files[2]);
        f.Shell.Layout.SetPinned(third, true);
        Assert.Equal(third, f.Shell.Layout.Documents.First());
        Assert.False(third.IsPreview);

        // Colors by project, once the setting is on.
        Assert.All(f.Shell.Layout.Documents, d => Assert.Null(d.TabAccent));
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.ColorTabsByProjectSetting, true, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Layout.Documents.All(d => d.TabAccent is not null));
        Assert.Equal(new Thickness(0, 0, 0, 3), third.TabAccentThickness);

        // The selected tab of a pane without the focus is readable in every theme (it was white on light gray).
        foreach (var (name, variant) in s_themes)
        {
            Assert.True(Contrast(Color("Shell.Tab.Selected.Foreground", variant), Color("Shell.Tab.Selected", variant)) >= 4.5, name);
        }

        f.Shell.Run("workbench.closeUnpinnedDocuments");
        await ShellFixture.PumpUntil(() => f.Shell.Layout.Documents.Count() == 1);
        Assert.Equal(third, f.Shell.Layout.Documents.Single());
    }

    [Fact]
    public void Document_tabs_that_dont_fit_leave_from_the_right_but_pinned_and_selected_ones_stay()
    {
        Assert.All(DocumentTabPanel.Shown([(100, false), (100, false)], 300), Assert.True);
        // 5 × 100 in 300: the pinned first and the selected fourth stay, the others leave from the right until it fits.
        Assert.Equal([true, true, false, true, false], DocumentTabPanel.Shown([(100, true), (100, false), (100, false), (100, true), (100, false)], 300));
        // Kept tabs stay even when they alone don't fit.
        Assert.Equal([true, true, false], DocumentTabPanel.Shown([(200, true), (200, true), (100, false)], 300));
    }

    [AvaloniaFact]
    public async Task Pinned_and_selected_document_tabs_stay_in_view_when_tabs_dont_fit()
    {
        // The Mac's review: with many documents open, the strip scrolled the pinned tab out of view.
        await using var f = ShellFixture.Create();
        var folder = Path.Combine(f.UserData, "many-documents");
        Directory.CreateDirectory(folder);
        var files = Enumerable.Range(1, 16).Select(i => Path.Combine(folder, $"A-rather-long-document-name-{i:00}.txt")).ToList();
        foreach (var file in files)
        {
            await File.WriteAllTextAsync(file, "text", TestContext.Current.CancellationToken);
            await f.Shell.OpenFileAsync(file, preview: false);
        }

        f.Shell.Layout.SetPinned(f.Shell.Layout.Documents.Single(d => d.FilePath == files[0]), true);
        await f.Shell.OpenFileAsync(files[^1], preview: false); // the last one selected
        f.Window.Width = 1000;
        ShellFixture.Pump();

        f.Window.UpdateLayout(); // the panel narrows its scroll viewer when attached: one more pass
        ShellFixture.Pump();
        var strip = f.Window.GetVisualDescendants().OfType<Dock.Avalonia.Controls.DocumentTabStrip>().Single(s => s.IsEffectivelyVisible);
        var panel = strip.GetVisualDescendants().OfType<DocumentTabPanel>().FirstOrDefault();
        Assert.NotNull(panel);
        var tabs = panel.Children.OfType<Avalonia.Controls.Primitives.TabStripItem>().ToList();
        var shown = tabs.Where(t => t.Bounds.X < panel.Bounds.Width).ToList(); // the others are past the clipped edge
        var scrollers = string.Join(", ", strip.GetVisualDescendants().OfType<ScrollViewer>().Select(s => $"{s.Name}:{s.HorizontalScrollBarVisibility}:{s.Viewport.Width:0}/{s.Extent.Width:0}"));
        Assert.True(shown.Count < tabs.Count, $"16 long names don't all fit in 1000 px: the panel is {panel.Bounds.Width:0} px, the strip {strip.Bounds.Width:0}; scroll viewers {scrollers}");
        Assert.Contains(shown, t => DocumentTab.GetIsPinned(t));
        Assert.Contains(shown, t => t.IsSelected);
        Assert.All(shown, t => Assert.True(t.Bounds.Right <= panel.Bounds.Width + 1, $"a tab ends at {t.Bounds.Right:0} of {panel.Bounds.Width:0}"));
        var scroll = strip.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        Assert.True(scroll is null || scroll.Offset.X == 0, "the strip doesn't scroll the pinned tab away");

        // The tab's context menu is ours: right-clicking a tab makes its document the active one, and the menu has the
        // document commands (Dock's had Float and Close only).
        var commands = f.Shell.Workbench.Menus.GetItems(Workbench.Menus.MenuLocations.DocumentTabContext).Select(i => i.Command).ToList();
        Assert.Contains("workbench.pinEditor", commands);
        Assert.Contains("workbench.closeUnpinnedDocuments", commands);
        Assert.Contains("workbench.copyDocumentPath", commands);
        var pinnedTab = shown.First(t => DocumentTab.GetIsPinned(t));
        var request = new Avalonia.Input.ContextRequestedEventArgs();
        pinnedTab.RaiseEvent(request);
        ShellFixture.Pump();
        Assert.True(request.Handled, "our handler took the request");
        Assert.Equal(files[0], f.Shell.Layout.ActiveDocument?.FilePath);
        if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is { Length: > 0 } snapshots)
        {
            Directory.CreateDirectory(snapshots);
            using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(f.Window);
            frame!.Save(Path.Combine(snapshots, "document-tabs.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
}
