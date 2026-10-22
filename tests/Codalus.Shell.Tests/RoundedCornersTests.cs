using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.Theming;
using Codalus.Workbench.Settings;
using Dock.Avalonia.Controls;

namespace Codalus.Shell.Tests;

/// <summary>
/// Rounded corners for Classic (#84, D46, <c>docs/design/controls/RoundedCorners.dc.html</c>): one 4 px radius on its
/// cards, tabs and controls, 6 px of window between the cards and around the dock area, the document well as one card
/// holding its tabs with the selected tab open into the active document's frame (<see cref="TabStripEdge"/>), the focused
/// tool window framed in the accent, the toolbar as a card, inset rows with Solution Explorer's pill, and the shared popup
/// radii. The families keep their shapes.
/// </summary>
public sealed class RoundedCornersTests
{
    private static readonly string[] s_classic = ["light", "dark", "blue", "highContrast"];

    private static object Resource(string key, string theme)
    {
        Assert.True(Application.Current!.TryGetResource(key, ThemeService.VariantOf(theme), out var value), $"{theme}: {key} is missing");
        return value!;
    }

    private static Color ColorOf(string key, string theme) => ((ISolidColorBrush)Resource(key, theme)).Color;

    [AvaloniaFact]
    public void Classic_takes_one_4px_radius_and_6px_gaps_and_the_families_keep_theirs()
    {
        foreach (var theme in s_classic)
        {
            Assert.Equal(new CornerRadius(4), Resource("Shell.Control.CornerRadius", theme));
            Assert.Equal(new CornerRadius(4), Resource("ControlCornerRadius", theme)); // Fluent's controls Codalus doesn't restyle
            Assert.Equal(new CornerRadius(3), Resource("Shell.Check.CornerRadius", theme));
            Assert.Equal(new CornerRadius(6), Resource("Shell.Focus.CornerRadius", theme));
            Assert.Equal(new CornerRadius(4), Resource("Shell.Card.CornerRadius", theme));
            Assert.Equal(new CornerRadius(4, 4, 0, 0), Resource("Shell.Panel.CornerRadius.Top", theme));
            Assert.Equal(new CornerRadius(0, 0, 4, 4), Resource("Shell.Panel.CornerRadius.Bottom", theme));
            Assert.Equal(new CornerRadius(4, 4, 0, 0), Resource("DockDocumentTabItemCornerRadius", theme));
            Assert.Equal(new CornerRadius(0, 0, 4, 4), Resource("Shell.ToolTab.CornerRadius", theme));
            Assert.Equal(new Thickness(1, 1, 1, 0), Resource("Shell.Tab.Active.Indicator", theme));
            Assert.Equal(6d, Resource("Shell.Gutter", theme));
            Assert.Equal(ColorOf("Shell.Background", theme), ColorOf("Shell.Gutter.Background", theme)); // the gaps show the window
            Assert.Equal(new Thickness(6, 0, 6, 6), Resource("Shell.Dock.Margin", theme));
            Assert.Equal(0d, Resource("DockDocumentTabStripSeparatorSize", theme));
            Assert.Equal(new Thickness(1, 0, 1, 1), Resource("DockDocumentContentBorderThickness", theme));
        }

        // The families: Harbor's square panels on 6 px of window and 3 px controls, Ember's 10 px cards, Dock's own values.
        foreach (var family in new[] { "harbor", "graphite", "daylight", "ember" })
        {
            foreach (var variant in new[] { "light", "dark", "highContrast" })
            {
                var theme = $"{family}.{variant}";
                Assert.Equal(new CornerRadius(3), Resource("ControlCornerRadius", theme));
                Assert.Equal(new CornerRadius(0), Resource("Shell.Card.CornerRadius", theme));
                Assert.Equal(new Thickness(0), Resource("Shell.Card.BorderThickness", theme));
                Assert.Equal(new Thickness(0), Resource("Shell.Dock.Margin", theme));
                Assert.Equal(HorizontalAlignment.Stretch, Resource("Shell.Toolbar.HorizontalAlignment", theme));
                Assert.Equal(0d, Resource("Shell.TabStripEdge.Height", theme));
                Assert.Equal(2d, Resource("DockDocumentTabStripSeparatorSize", theme));
                Assert.Equal(new Thickness(1), Resource("DockDocumentContentBorderThickness", theme));
                Assert.Equal(Colors.Transparent, ColorOf("Shell.Selection.Indicator", theme));
            }
        }

        Assert.Equal(new CornerRadius(3), Resource("Shell.Control.CornerRadius", "harbor.dark"));
        Assert.Equal(new CornerRadius(10, 10, 0, 0), Resource("Shell.Panel.CornerRadius.Top", "ember.dark"));

        // Two overlay radii for every family: menus, lists and toasts 6 (Fluent's 5 too), dialogs 8.
        Assert.Equal(new CornerRadius(6), Application.Current!.FindResource("OverlayCornerRadius"));
        Assert.Equal(new CornerRadius(6), Application.Current!.FindResource("Shell.CornerRadius.Overlay"));
        Assert.Equal(new CornerRadius(8), Application.Current!.FindResource("Shell.CornerRadius.Dialog"));
    }

    [AvaloniaFact]
    public void Classics_state_cues_reach_3_to_1()
    {
        // The design's non-text state indicators: the selected tab's outline against the strip, in the active group and
        // the others; the frames of the active document and the focused tool window against the window; the pill.
        var failures = new List<string>();
        void Check(string theme, string foreground, string background)
        {
            var ratio = PolishTests.Contrast(ColorOf(foreground, theme), ColorOf(background, theme));
            if (ratio < 3)
            {
                failures.Add($"{theme}: {foreground} on {background} is {ratio:0.00}:1");
            }
        }

        foreach (var theme in new[] { "light", "dark" })
        {
            Check(theme, "Shell.Tab.Active.IndicatorBrush", "Shell.Card.Background");
            Check(theme, "Shell.Tab.Selected.IndicatorBrush", "Shell.Card.Background");
            Check(theme, "Shell.Card.Focus.BorderBrush", "Shell.Background");
            Check(theme, "Shell.Well.Frame.Active", "Shell.Background");
        }

        foreach (var theme in s_classic)
        {
            Check(theme, "Shell.Selection.Indicator", "Shell.Selection");
            Check(theme, "Shell.Selection.Indicator.Inactive", "Shell.Selection.Inactive");
        }

        // Blue's gold tab and High Contrast's accent tab keep their fill, which carries the state.
        Check("blue", "Shell.Tab.Active", "Shell.Card.Background");
        Check("highContrast", "Shell.Tab.Active", "Shell.Card.Background");

        Check("highContrast", "Shell.Well.Frame.Active", "Shell.Background");
        Check("highContrast", "Shell.Tab.Selected.IndicatorBrush", "Shell.Card.Background");
        Assert.Empty(failures);
    }

    private static async Task<ShellFixture> OpenAsync(string theme)
    {
        var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;
        await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, theme, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == theme);
        f.Shell.Layout.ShowToolWindow(BuiltIns.OutputWindow, focus: false);
        foreach (var name in new[] { "Notes.txt", "Other.txt" })
        {
            var path = Path.Combine(f.UserData, name);
            await File.WriteAllTextAsync(path, name, TestContext.Current.CancellationToken);
            await f.Shell.OpenFileAsync(path, preview: false);
        }

        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<DocumentTabStripItem>().Count() == 2);
        for (var i = 0; i < 4; i++)
        {
            ShellFixture.Pump();
        }

        return f;
    }

    private static T Single<T>(ShellFixture f, Func<T, bool>? where = null)
        where T : Visual =>
        f.Window.GetVisualDescendants().OfType<T>().Single(v => v.IsEffectivelyVisible && (where is null || where(v)));

    [AvaloniaFact]
    public async Task Classic_draws_cards_with_the_document_well_holding_its_tabs()
    {
        await using var f = await OpenAsync("dark");

        // The window: 6 px around the dock area and between the cards, the toolbar a card as wide as its buttons.
        Assert.Equal(new Thickness(6, 0, 6, 6), Single<DockControl>(f).Margin);
        Assert.All(f.Window.GetVisualDescendants().OfType<Dock.Controls.ProportionalStackPanel.ProportionalStackPanelSplitter>().Where(s => s.IsEffectivelyVisible), s => Assert.Equal(6, s.Thickness));
        var toolbar = Single<Border>(f, b => b.Classes.Contains("standardToolbar"));
        Assert.Equal(new CornerRadius(4), toolbar.CornerRadius);
        Assert.Equal(new Thickness(1), toolbar.BorderThickness);
        Assert.Equal(HorizontalAlignment.Left, toolbar.HorizontalAlignment);
        Assert.True(toolbar.Bounds.Width < f.Window.Bounds.Width - 100, $"the toolbar is {toolbar.Bounds.Width} px wide");

        // The document well: one 4 px card, outlined, its tab strip inside on the card's fill.
        var card = Single<Border>(f, b => b.Name == "PART_Card");
        Assert.Equal(new CornerRadius(4), card.CornerRadius);
        Assert.Equal(new Thickness(1), card.BorderThickness);
        Assert.Equal(Color.Parse("#252526"), ((ISolidColorBrush)card.Background!).Color);
        var strip = Single<DocumentTabStrip>(f);
        Assert.Contains(card, strip.GetVisualAncestors());

        // The selected tab: outlined on three sides with rounded top corners, filled with the content's color.
        var selected = Single<DocumentTabStripItem>(f, t => t.IsSelected);
        var gutter = selected.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_TabGutter");
        Assert.Equal(new Thickness(1, 1, 1, 0), gutter.BorderThickness);
        Assert.Equal(new CornerRadius(4, 4, 0, 0), gutter.CornerRadius);
        Assert.Equal(FontWeight.SemiBold, selected.FontWeight);
        Assert.Equal(Color.Parse("#252526"), ((ISolidColorBrush)selected.Background!).Color);

        // The document's frame: no separator under the strip, no top edge, its sides and bottom over the card's.
        var control = Single<DocumentControl>(f);
        var frame = control.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Border");
        Assert.Equal(new Thickness(1, 0, 1, 1), frame.BorderThickness);
        Assert.Equal(new Thickness(-1, 0, -1, -1), frame.Margin);
        Assert.Equal(new CornerRadius(0, 0, 4, 4), frame.CornerRadius);
        Assert.DoesNotContain(control.GetVisualDescendants().OfType<Panel>(), p => p.Name == "PART_DocumentSeperatorHost" && p.Bounds.Height > 0);

        // The tool windows: cards outlined all round, the caption's top corners and the content's bottom ones rounded.
        var chrome = f.Window.GetVisualDescendants().OfType<ToolChromeControl>().First(c => c.IsEffectivelyVisible);
        Assert.Equal(new CornerRadius(4, 4, 0, 0), chrome.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_Border" && b.TemplatedParent == chrome).CornerRadius);
        var content = chrome.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(p => p.Name == "PART_ContentPresenter" && p.TemplatedParent == chrome);
        Assert.Equal(new Thickness(1, 0, 1, 1), content.BorderThickness);
        Assert.Equal(new CornerRadius(0, 0, 4, 4), content.CornerRadius);
        Assert.All(chrome.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "PART_Border" && b.TemplatedParent is ToolControl), b => Assert.Equal(new Thickness(0), b.BorderThickness));

        if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is { Length: > 0 } snapshots)
        {
            Directory.CreateDirectory(snapshots);
            using var picture = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(f.Window);
            picture!.Save(Path.Combine(snapshots, "rounded-corners-classic-dark.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }

    [AvaloniaFact]
    public async Task The_edge_leaves_the_selected_tabs_span_open_and_follows_the_selection()
    {
        await using var f = await OpenAsync("light");
        var edge = Single<TabStripEdge>(f);
        Assert.Equal(1, edge.Bounds.Height);
        var strip = Single<DocumentTabStrip>(f);

        void ExpectGapUnder(DocumentTabStripItem tab)
        {
            var gap = edge.Gap();
            Assert.NotNull(gap);
            var left = tab.TranslatePoint(default, edge)!.Value.X;
            Assert.Equal(left, gap.Value.Left, 1);
            Assert.Equal(left + tab.Bounds.Width, gap.Value.Right, 1);
            Assert.True(gap.Value.Left > 0 || gap.Value.Right < edge.Bounds.Width, "the line runs beside the tab");
        }

        var tabs = f.Window.GetVisualDescendants().OfType<DocumentTabStripItem>().OrderBy(t => t.TranslatePoint(default, strip)!.Value.X).ToList();
        Assert.True(tabs[1].IsSelected); // the last one opened
        ExpectGapUnder(tabs[1]);
        Assert.Same(tabs[1], edge.SelectedTab);

        strip.SelectedIndex = strip.IndexFromContainer(tabs[0]);
        await ShellFixture.PumpUntil(() => edge.SelectedTab == tabs[0]);
        ShellFixture.Pump();
        ExpectGapUnder(tabs[0]);

        // The frame's line is the accent while the group is active, the border otherwise (the focus in a tool window).
        f.Shell.Layout.ShowToolWindow(BuiltIns.OutputWindow, focus: true);
        await ShellFixture.PumpUntil(() => !edge.IsGroupActive);
        Assert.Equal(ColorOf("DockDocumentContentBorderBrush", "light"), ((ISolidColorBrush)edge.FrameBrush!).Color);
        var idle = f.Window.GetVisualDescendants().OfType<DocumentTabStripItem>().Single(t => t.IsSelected);
        Assert.Equal(Color.Parse("#80838F"), ((ISolidColorBrush)idle.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_TabGutter").BorderBrush!).Color);
        f.Shell.Layout.OpenDocument(f.Shell.Layout.ActiveDocument!, focus: true);
        await ShellFixture.PumpUntil(() => edge.IsGroupActive);
        Assert.Equal(Color.Parse("#007ACC"), ((ISolidColorBrush)edge.FrameBrush!).Color);
    }

    [AvaloniaFact]
    public async Task The_focused_tool_window_is_framed_in_the_accent()
    {
        await using var f = await OpenAsync("dark");
        ToolChromeControl Output() => f.Window.GetVisualDescendants().OfType<ToolChromeControl>().First(c => c.IsEffectivelyVisible && c.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Name == "PART_Title" && t.Text == "Output"));

        Color Frame() => (Output().BorderBrush as ISolidColorBrush)?.Color ?? default;

        // The frame's style follows IsActive on a later layout pass: wait for the color, then check the rest.
        f.Shell.Layout.ShowToolWindow(BuiltIns.OutputWindow, focus: true);
        await ShellFixture.PumpUntil(() => Output().IsActive && Frame() == Color.Parse("#3794FF"), describe: () => $"active {Output().IsActive}, frame {Frame()}");
        var title = Output().GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_Title");
        Assert.Equal(FontWeight.SemiBold, title.FontWeight);
        var grip = Output().GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "PART_Grip");
        Assert.Equal(Color.Parse("#252526"), ((ISolidColorBrush)grip.Background!).Color); // the caption stays on the card

        f.Shell.Layout.OpenDocument(f.Shell.Layout.ActiveDocument!, focus: true);
        await ShellFixture.PumpUntil(() => !Output().IsActive && Frame() == Color.Parse("#3F3F46"), describe: () => $"active {Output().IsActive}, frame {Frame()}");
    }

    [AvaloniaFact]
    public async Task The_families_keep_their_shapes()
    {
        await using var f = await OpenAsync("harbor.dark");
        Assert.Equal(new Thickness(0), Single<DockControl>(f).Margin);
        var card = Single<Border>(f, b => b.Name == "PART_Card");
        Assert.Equal(new CornerRadius(0), card.CornerRadius);
        Assert.Equal(new Thickness(0), card.BorderThickness);
        Assert.Equal(0, Single<TabStripEdge>(f, _ => true).Bounds.Height);
        Assert.Null(Single<TabStripEdge>(f, _ => true).Gap());
        var toolbar = Single<Border>(f, b => b.Classes.Contains("standardToolbar"));
        Assert.Equal(HorizontalAlignment.Stretch, toolbar.HorizontalAlignment);
        Assert.Equal(new CornerRadius(0), toolbar.CornerRadius);
        var selected = Single<DocumentTabStripItem>(f, t => t.IsSelected);
        Assert.Equal(new Thickness(0, 3, 0, 0), selected.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_TabGutter").BorderThickness);
        Assert.Equal(FontWeight.Normal, selected.FontWeight);
    }

    [AvaloniaFact]
    public async Task Rows_are_inset_and_solution_explorer_marks_the_selected_one_with_a_pill()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;
        await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, "dark", SettingScope.User, CancellationToken.None);
        var solution = f.CopyFixture();
        f.Shell.Trust.Trust(Path.GetDirectoryName(Path.GetDirectoryName(solution))!);
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Explorer.Rows.Count > 3, seconds: 180);
        f.Shell.Explorer.Selected = f.Shell.Explorer.Rows[1];
        ListBox? List() => f.Window.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(l => l.Name == "List" && l.IsEffectivelyVisible && l.FindAncestorOfType<Views.SolutionExplorerView>() is not null);
        await ShellFixture.PumpUntil(() => List()?.GetVisualDescendants().OfType<Border>().Any(b => b.Classes.Contains("selectionPill") && b.IsEffectivelyVisible) == true, describe: () =>
            $"lists {f.Window.GetVisualDescendants().OfType<ListBox>().Count(l => l.Name == "List")}, visible {List() is not null}, items {List()?.GetVisualDescendants().OfType<ListBoxItem>().Count()}, selected {List()?.GetVisualDescendants().OfType<ListBoxItem>().Count(i => i.IsSelected)}, pills {List()?.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("selectionPill"))}, selectedVm {f.Shell.Explorer.Selected?.Name}");
        var list = List()!;
        var rows = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        Assert.All(rows, r => Assert.Equal(new Thickness(3, 0), r.Margin));
        Assert.All(rows, r => Assert.Equal(new CornerRadius(4), r.CornerRadius));
        var pills = list.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("selectionPill") && b.IsEffectivelyVisible).ToList();
        var pill = Assert.Single(pills);
        Assert.True(pill.FindAncestorOfType<ListBoxItem>()!.IsSelected);
        Assert.Equal(new Size(3, 14), pill.Bounds.Size);

        // The accent with the focus in the list, muted without.
        pill.FindAncestorOfType<ListBoxItem>()!.Focus();
        await ShellFixture.PumpUntil(() => list.IsKeyboardFocusWithin);
        ShellFixture.Pump();
        Assert.Equal(Color.Parse("#3794FF"), ((ISolidColorBrush)pill.Background!).Color);
        f.Shell.Layout.ShowToolWindow(BuiltIns.OutputWindow, focus: true);
        await ShellFixture.PumpUntil(() => !list.IsKeyboardFocusWithin);
        ShellFixture.Pump();
        Assert.Equal(Color.Parse("#A6A6A6"), ((ISolidColorBrush)pill.Background!).Color);

        // In a family the rows stay full width and square, without the pill.
        await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, "harbor.dark", SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "harbor.dark");
        ShellFixture.Pump();
        Assert.All(list.GetVisualDescendants().OfType<ListBoxItem>(), r => Assert.Equal(new Thickness(0), r.Margin));
        Assert.Equal(Colors.Transparent, ((ISolidColorBrush)pill.Background!).Color);
    }
}
