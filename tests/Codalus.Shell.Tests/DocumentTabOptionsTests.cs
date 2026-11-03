using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Workbench.Settings;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace Codalus.Shell.Tests;

/// <summary>
/// The buttons beside a document group's tabs, as the classic IDE's: the tab list (every open document by name, the
/// hidden ones too) and the tab options (placement on the top, left or right, colors by project or file extension,
/// multiple rows, which the mouse wheel turns on and off).
/// </summary>
public sealed class DocumentTabOptionsTests
{
    [Fact]
    public void Tabs_wrap_onto_rows_of_what_fits()
    {
        Assert.Equal([3], DocumentTabPanel.Wrap([100, 100, 100], 300));
        Assert.Equal([2, 2, 1], DocumentTabPanel.Wrap([100, 100, 100, 100, 100], 250));
        // A tab wider than the group has a row of its own.
        Assert.Equal([1, 1, 1], DocumentTabPanel.Wrap([100, 400, 100], 300));
        Assert.Empty(DocumentTabPanel.Wrap([], 300));
    }

    [Fact]
    public void A_file_extension_always_has_the_same_color()
    {
        Assert.Equal(ShellHost.ExtensionColor(".cs"), ShellHost.ExtensionColor(".CS"));
        Assert.InRange(ShellHost.ExtensionColor(".razor"), 0, 7);
        // The common kinds of file get colors of their own.
        string[] common = [".cs", ".razor", ".json", ".xaml"];
        Assert.Equal(common.Length, common.Select(ShellHost.ExtensionColor).Distinct().Count());
    }

    [AvaloniaFact]
    public async Task The_tab_list_shows_every_open_document_by_name_and_opens_a_hidden_one()
    {
        await using var f = ShellFixture.Create();
        var files = await OpenAsync(f, "list", 12);
        f.Window.Width = 900;
        ShellFixture.Pump();
        f.Window.UpdateLayout();
        ShellFixture.Pump();

        var strip = Strip(f);
        var panel = strip.GetVisualDescendants().OfType<DocumentTabPanel>().Single();
        Assert.Contains(Tabs(panel), t => !Shown(t, panel)); // some tabs don't fit

        var buttons = strip.GetVisualDescendants().OfType<TabStripButtons>().Single();
        Assert.True(buttons.ListButton.IsEffectivelyVisible);
        Assert.True(buttons.OptionsButton.IsEffectivelyVisible);
        // Right of the tabs, in the strip's row.
        Assert.True(buttons.TranslatePoint(default, strip)!.Value.X >= panel.TranslatePoint(default, strip)!.Value.X + panel.Bounds.Width - 0.5);

        buttons.ListButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        ShellFixture.Pump();
        var list = f.Shell.OpenTabList;
        Assert.NotNull(list);
        var titles = list.Entries.Select(e => e.Title).ToList();
        Assert.Equal(files.Select(Path.GetFileName).Order(StringComparer.CurrentCultureIgnoreCase), titles);
        Assert.Equal(f.Shell.Layout.ActiveDocument, list.Entries.Single(e => e.IsActive).Dockable);
        Assert.Same(list.Entries.Single(e => e.IsActive), list.List.SelectedItem);
        if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is { Length: > 0 } snapshots)
        {
            Directory.CreateDirectory(snapshots);
            using var frame = f.Window.CaptureRenderedFrame();
            frame!.Save(Path.Combine(snapshots, "document-tab-list.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        // A hidden tab, chosen from the list, opens and shows.
        var hidden = Tabs(panel).First(t => !Shown(t, panel)).DataContext as IDockable;
        Assert.NotNull(hidden);
        list.List.SelectedItem = list.Entries.Single(e => ReferenceEquals(e.Dockable, hidden));
        list.List.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
        ShellFixture.Pump();
        f.Window.UpdateLayout();
        ShellFixture.Pump();
        Assert.Same(hidden, f.Shell.Layout.ActiveDocument);
        Assert.True(Shown(Tabs(panel).Single(t => ReferenceEquals(t.DataContext, hidden)), panel));
        Assert.Null(f.Shell.OpenTabList);
    }

    [AvaloniaFact]
    public async Task Tabs_in_multiple_rows_all_show_and_the_mouse_wheel_turns_them_on_and_off()
    {
        await using var f = ShellFixture.Create();
        await OpenAsync(f, "rows", 12);
        f.Window.Width = 900;
        ShellFixture.Pump();
        f.Window.UpdateLayout();
        ShellFixture.Pump();
        var strip = Strip(f);
        var panel = strip.GetVisualDescendants().OfType<DocumentTabPanel>().Single();
        var oneRow = panel.Bounds.Height;
        Assert.Contains(Tabs(panel), t => !Shown(t, panel));

        // Every wheel event: where it went, whether the strip saw it and whether the shell took it, for the failures at the
        // wheels below (their cause isn't known yet, TODO(issue)).
        var wheels = new List<string>();
        strip.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) => wheels.Add($"strip {e.Delta.Y} handled {e.Handled}"), RoutingStrategies.Tunnel, handledEventsToo: true);
        f.Window.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) => wheels.Add($"{e.Delta.Y} on {Chain(e.Source as Visual)} handled {e.Handled}"), RoutingStrategies.Bubble, handledEventsToo: true);
        var over = strip.TranslatePoint(new Point(20, strip.Bounds.Height / 2), f.Window)!.Value;
        // The tool windows beside the documents: on macOS CI the strip had moved 198 px right by the wheel up, a tool
        // window's title under the wheel's point (run 41621106416).
        var toolDocksBefore = ToolDocks(f);
        string Wheeled(string rows, string before) =>
            $"the tabs {rows}: setting {f.Shell.Workbench.Settings.GetBoolean(ShellHost.TabsInMultipleRowsSetting)}, resource "
            + $"{Avalonia.Application.Current?.Resources["Shell.TabsInMultipleRows"]}, wheels the shell got {f.Shell.TabStripWheels} (the last: "
            + $"{f.Shell.LastTabStripWheel}), the wheel events: [{string.Join("; ", wheels)}], the panel's still the strip's "
            + $"{ReferenceEquals(panel, Strip(f).GetVisualDescendants().OfType<DocumentTabPanel>().FirstOrDefault())} ({(TopLevel.GetTopLevel(panel) is null ? "detached" : "attached")}), "
            + $"strip {strip.Bounds.Height:0} px high at {strip.TranslatePoint(default, f.Window)}, under {over} before the wheel: {before}; now: {Under(f, over)}; "
            + $"the overlay: [{string.Join(", ", OverlayLayer.GetOverlayLayer(f.Window)?.Children.Select(c => $"{c.GetType().Name} {c.Bounds}") ?? [])}]; "
            + $"the tool docks: [{ToolDocks(f)}] (before the wheels: [{toolDocksBefore}])";

        // Wheel down over the tabs: multiple rows, for every group, saved. (Windows CI, run 41554892222: this wheel left one
        // row.)
        var beforeDown = Under(f, over);
        f.Window.MouseWheel(over, new Vector(0, -1));
        await ShellFixture.PumpUntil(() => panel.MultipleRows, describe: () => Wheeled("stayed in one row", beforeDown));
        Assert.True(f.Shell.Workbench.Settings.GetBoolean(ShellHost.TabsInMultipleRowsSetting));
        f.Window.UpdateLayout();
        ShellFixture.Pump();
        Assert.All(Tabs(panel), t => Assert.True(Shown(t, panel), $"{t.DataContext} shows"));
        Assert.True(panel.Bounds.Height > oneRow * 1.5, $"more rows: {panel.Bounds.Height:0} px, one was {oneRow:0}");
        Assert.True(Tabs(panel).Select(t => t.Bounds.Y).Distinct().Count() > 1);
        // The buttons stay beside the first row.
        var options = strip.GetVisualDescendants().OfType<TabStripButtons>().Single().OptionsButton;
        var firstRow = Tabs(panel).First(t => t.Bounds.Y == 0);
        var middle = options.TranslatePoint(new Point(0, options.Bounds.Height / 2), firstRow)!.Value.Y;
        Assert.InRange(middle, 0, firstRow.Bounds.Height);
        if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is { Length: > 0 } snapshots)
        {
            Directory.CreateDirectory(snapshots);
            using var frame = f.Window.CaptureRenderedFrame();
            frame!.Save(Path.Combine(snapshots, "document-tabs-multiple-rows.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        // Up: one row again. (Windows and macOS CI, runs 41518626383 and 41524121911: this wheel left the rows on, while it
        // passes on Linux; the failure says what the wheel reached and did.)
        var before = Under(f, over);
        f.Window.MouseWheel(over, new Vector(0, 1));
        await ShellFixture.PumpUntil(() => !panel.MultipleRows, describe: () => Wheeled("stayed in multiple rows", before));
        f.Window.UpdateLayout();
        ShellFixture.Pump();
        Assert.Equal(oneRow, panel.Bounds.Height, 0.5);

        // Turned off, the wheel leaves the rows alone.
        await f.RunAsync("workbench.tabs.toggleMultipleRowsWithMouseWheel");
        await ShellFixture.PumpUntil(() => !f.Shell.Workbench.Settings.GetBoolean(ShellHost.MultipleRowsWithMouseWheelSetting, true));
        f.Window.MouseWheel(over, new Vector(0, -1));
        ShellFixture.Pump();
        Assert.False(panel.MultipleRows);

        // The menu's entry turns them on too.
        await f.RunAsync("workbench.tabs.toggleMultipleRows");
        await ShellFixture.PumpUntil(() => panel.MultipleRows);
    }

    [AvaloniaFact]
    public async Task A_wheel_up_right_after_a_wheel_down_leaves_one_row()
    {
        await using var f = ShellFixture.Create();
        await OpenAsync(f, "quick", 3);
        ShellFixture.Pump();
        var strip = Strip(f);
        var panel = strip.GetVisualDescendants().OfType<DocumentTabPanel>().Single();
        var over = strip.TranslatePoint(new Point(20, strip.Bounds.Height / 2), f.Window)!.Value;

        // Down, then up before the down's write has landed (a trackpad's flick and back): the up wins. The settings' writes
        // wait while the test holds their lock (on Windows a small write often lands before the wheel's handler returns).
        var settings = f.Shell.Workbench.Settings;
        var writes = WriteLock(settings);
        await writes.WaitAsync(TestContext.Current.CancellationToken);
        f.Window.MouseWheel(over, new Vector(0, -1));
        f.Window.MouseWheel(over, new Vector(0, 1));
        writes.Release();
        await ShellFixture.PumpUntil(() => settings.IsModified(ShellHost.TabsInMultipleRowsSetting) && !settings.GetBoolean(ShellHost.TabsInMultipleRowsSetting),
            describe: () => $"the up's write (the setting {(settings.IsModified(ShellHost.TabsInMultipleRowsSetting) ? settings.GetBoolean(ShellHost.TabsInMultipleRowsSetting) : "not written")})");
        await ShellFixture.PumpUntil(() => !panel.MultipleRows);
    }

    [AvaloniaFact]
    public async Task Tabs_go_on_the_left_or_right_of_every_group_new_ones_too()
    {
        await using var f = ShellFixture.Create();
        await OpenAsync(f, "placement", 3);
        ShellFixture.Pump();

        await f.RunAsync("workbench.tabs.placeLeft");
        await ShellFixture.PumpUntil(() => Strip(f).Orientation == Avalonia.Layout.Orientation.Vertical);
        f.Window.UpdateLayout();
        ShellFixture.Pump();
        var strip = Strip(f);
        Assert.Equal(DocumentTabLayout.Left, ((IDocumentDock)strip.DataContext!).TabsLayout);
        var documentView = strip.FindAncestorOfType<DocumentControl>()!.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Border");
        Assert.True(strip.TranslatePoint(default, f.Window)!.Value.X < documentView.TranslatePoint(default, f.Window)!.Value.X, "the tabs are left of the document");
        // The buttons above the column of tabs, which has no frame edge.
        var buttons = strip.GetVisualDescendants().OfType<TabStripButtons>().Single();
        var tabs = strip.GetVisualDescendants().OfType<DocumentTabStripItem>().ToList();
        Assert.True(buttons.TranslatePoint(default, strip)!.Value.Y + buttons.Bounds.Height <= tabs.Min(t => t.TranslatePoint(default, strip)!.Value.Y) + 0.5);
        Assert.Empty(strip.GetVisualDescendants().OfType<TabStripEdge>());
        Assert.Equal(3, tabs.Select(t => t.TranslatePoint(default, strip)!.Value.Y).Distinct().Count()); // one under another
        if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is { Length: > 0 } snapshots)
        {
            Directory.CreateDirectory(snapshots);
            using var frame = f.Window.CaptureRenderedFrame();
            frame!.Save(Path.Combine(snapshots, "document-tabs-left.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        // A group made later takes the placement.
        await f.RunAsync("workbench.splitEditorRight");
        ShellFixture.Pump();
        Assert.All(DocumentDocks(f), d => Assert.Equal(DocumentTabLayout.Left, d.TabsLayout));

        await f.RunAsync("workbench.tabs.placeRight");
        await ShellFixture.PumpUntil(() => DocumentDocks(f).All(d => d.TabsLayout == DocumentTabLayout.Right));

        await f.RunAsync("workbench.tabs.placeTop");
        await ShellFixture.PumpUntil(() => DocumentDocks(f).All(d => d.TabsLayout == DocumentTabLayout.Top));
        f.Window.UpdateLayout();
        ShellFixture.Pump();
        Assert.All(f.Window.GetVisualDescendants().OfType<DocumentTabStrip>(), s => Assert.Equal(Avalonia.Layout.Orientation.Horizontal, s.Orientation));
    }

    [AvaloniaFact]
    public async Task The_tab_options_menu_checks_the_current_choices()
    {
        await using var f = ShellFixture.Create();
        await OpenAsync(f, "options", 2);
        var menu = f.Shell.TabOptionsMenu();
        Assert.NotNull(menu);
        var entries = menu.Items.OfType<MenuItem>().ToList();
        Assert.Equal(
            ["Place Tabs on the Left", "Place Tabs on the Top", "Place Tabs on the Right", "Color Document Tabs by", "Show Tabs in Multiple Rows", "Toggle Multiple Rows with Mouse Wheel", "Options…"],
            entries.Select(e => AutomationName(e)));
        // The defaults, as the classic IDE's: tabs on top, the wheel toggles rows, no colors.
        Assert.Equal([false, true, false], entries.Take(3).Select(e => e.IsChecked));
        Assert.False(entries[4].IsChecked);
        Assert.True(entries[5].IsChecked);
        var colorBy = entries[3].Items.OfType<MenuItem>().ToList();
        Assert.Equal(["None", "Project", "File Extension"], colorBy.Select(e => AutomationName(e)));
        Assert.Equal([true, false, false], colorBy.Select(e => e.IsChecked));

        // Options… opens Settings on the tab settings.
        await f.RunAsync("workbench.tabs.options");
        ShellFixture.Pump();
        Assert.IsType<Codalus.Shell.ViewModels.SettingsDocument>(f.Shell.Layout.ActiveDocument);
    }

    [AvaloniaFact]
    public async Task Tabs_take_their_file_extensions_colors_and_the_old_project_setting_still_works()
    {
        await using var f = ShellFixture.Create();
        var folder = Path.Combine(f.UserData, "extensions");
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "a.txt", "b.txt", "c.json" })
        {
            var file = Path.Combine(folder, name);
            await File.WriteAllTextAsync(file, "{}", TestContext.Current.CancellationToken);
            await f.Shell.OpenFileAsync(file, preview: false);
        }

        ShellFixture.Pump();
        Assert.Equal("none", f.Shell.TabColorBy);
        Assert.All(f.Shell.Layout.Documents, d => Assert.Null(d.TabAccent));

        await f.RunAsync("workbench.tabs.colorByFileExtension");
        await ShellFixture.PumpUntil(() => f.Shell.Layout.Documents.All(d => d.TabAccent is not null));
        var byName = f.Shell.Layout.Documents.ToDictionary(d => Path.GetFileName(d.FilePath!)!, d => d.TabAccent!.ToString());
        Assert.Equal(byName["a.txt"], byName["b.txt"]);
        Assert.Equal(ShellHost.ExtensionColor(".txt") == ShellHost.ExtensionColor(".json"), byName["a.txt"] == byName["c.json"]);

        await f.RunAsync("workbench.tabs.colorByNone");
        await ShellFixture.PumpUntil(() => f.Shell.Layout.Documents.All(d => d.TabAccent is null));

        // The old on/off setting colors by project while colorTabsBy isn't set.
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.ColorTabsBySetting, (System.Text.Json.JsonElement?)null, SettingScope.User, CancellationToken.None);
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.ColorTabsByProjectSetting, true, SettingScope.User, CancellationToken.None);
        Assert.Equal("project", f.Shell.TabColorBy);
        await f.RunAsync("workbench.tabs.colorByNone");
        await ShellFixture.PumpUntil(() => f.Shell.TabColorBy == "none");
    }

    [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = "_writeLock")]
    private static extern ref SemaphoreSlim WriteLock(SettingsService settings);

    private static async Task<List<string>> OpenAsync(ShellFixture f, string folderName, int count)
    {
        var folder = Path.Combine(f.UserData, folderName);
        Directory.CreateDirectory(folder);
        var files = Enumerable.Range(1, count).Select(i => Path.Combine(folder, $"A-rather-long-document-name-{i:00}.txt")).ToList();
        foreach (var file in files)
        {
            await File.WriteAllTextAsync(file, "text", TestContext.Current.CancellationToken);
            await f.Shell.OpenFileAsync(file, preview: false);
        }

        return files;
    }

    private static DocumentTabStrip Strip(ShellFixture f) =>
        f.Window.GetVisualDescendants().OfType<DocumentTabStrip>().First(s => s.IsEffectivelyVisible);

    private static List<IDocumentDock> DocumentDocks(ShellFixture f) =>
        [.. f.Window.GetVisualDescendants().OfType<DocumentTabStrip>().Select(s => s.DataContext).OfType<IDocumentDock>()];

    private static List<TabStripItem> Tabs(DocumentTabPanel panel) => [.. panel.Children.OfType<TabStripItem>()];

    /// <summary>The tool docks shown, with their tool windows, place and size (failure messages).</summary>
    private static string ToolDocks(ShellFixture f) => string.Join("; ", f.Window.GetVisualDescendants().OfType<ToolDockControl>().Where(c => c.IsEffectivelyVisible)
        .Select(c => $"{string.Join(", ", (c.DataContext as IDock)?.VisibleDockables?.Select(d => d.Title) ?? [])} at {c.TranslatePoint(default, f.Window)} {c.Bounds.Size}"));

    /// <summary>What the pointer hits at <paramref name="point"/>, innermost first (failure messages).</summary>
    private static string Under(ShellFixture f, Point point) => Chain(InputExtensions.InputHitTest(f.Window, point) as Visual);

    /// <summary>Whether the panel arranged the tab inside itself (a tab left out is arranged past its right edge).</summary>
    private static bool Shown(TabStripItem tab, DocumentTabPanel panel) => tab.Bounds.Right <= panel.Bounds.Width + 0.5 && tab.Bounds.Width > 0;

    /// <summary>A visual and its ancestors up to the document group, by type, innermost first.</summary>
    private static string Chain(Visual? visual) =>
        visual is null ? "nothing" : string.Join(" < ", visual.GetSelfAndVisualAncestors().TakeWhile(v => v is not DocumentDockControl).Take(12).Select(v => v.GetType().Name));

    private static string? AutomationName(MenuItem item) => Avalonia.Automation.AutomationProperties.GetName(item);
}
