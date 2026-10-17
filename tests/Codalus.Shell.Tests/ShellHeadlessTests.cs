using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Codalus.Platform;
using Codalus.Shell.Controls;
using Codalus.Shell.Docking;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Plugins;

namespace Codalus.Shell.Tests;

/// <summary>Headless UI tests of the shell: real views, real workbench services, simulated input.</summary>
public class ShellHeadlessTests
{
    private static readonly RawInputModifiers s_primary = PlatformInfo.Current == PlatformKind.MacOS ? RawInputModifiers.Meta : RawInputModifiers.Control;

    [AvaloniaFact]
    public async Task The_main_window_opens_with_menus_status_bar_and_the_default_layout()
    {
        await using var f = ShellFixture.Create();

        // macOS uses the global menu bar; elsewhere the menu is in the window.
        var menus = PlatformInfo.Current == PlatformKind.MacOS
            ? NativeMenu.GetMenu(f.Window)!.Items.OfType<NativeMenuItem>().Select(m => m.Header).ToList()
            : f.Window.MenuBar.Items.OfType<MenuItem>().Select(m => AutomationProperties.GetName(m)).ToList();
        Assert.Equal(["File", "Edit", "View", "Git", "Project", "Build", "Debug", "Test", "Analyze", "Tools", "Window", "Help"], menus);
        Assert.Equal(PlatformInfo.Current != PlatformKind.MacOS, f.Window.MenuBar.IsVisible);
        Assert.True(f.Shell.Layout.IsVisible(BuiltIns.OutputWindow));
        Assert.Contains(f.Shell.StatusBar.Right, i => i.Item.Id == "codalus.notifications");
        Assert.Contains(f.Shell.Plugins.Plugins, p => p.Id == "codalus.welcome" && p.State is PluginState.Ready or PluginState.Active); // it may already have activated (the Start page)

        // Every button in the window chrome has an accessible name.
        var unnamed = f.Window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && string.IsNullOrEmpty(AutomationProperties.GetName(b)) && b.Content is not string)
            .Where(b => b.FindAncestorOfType<Dock.Avalonia.Controls.DockControl>() is null)
            .ToList();
        Assert.Empty(unnamed);
    }

    [AvaloniaFact]
    public async Task The_command_palette_filters_and_runs_a_command_from_the_keyboard()
    {
        await using var f = ShellFixture.Create();
        var executed = new List<string>();
        f.Shell.Workbench.Commands.Executed += (_, e) => executed.Add(e.CommandId);

        f.Press(Key.P, s_primary | RawInputModifiers.Shift);
        Assert.True(f.Shell.Palette.IsOpen);
        Assert.True(f.Window.Palette.Input.IsFocused);

        f.Type("rst layout");
        Assert.Equal("View: Reset Layout", f.Shell.Palette.Selected?.Label);
        f.Press(Key.Enter);

        await ShellFixture.PumpUntil(() => executed.Contains("workbench.resetLayout"));
        Assert.False(f.Shell.Palette.IsOpen);
        Assert.Equal("workbench.resetLayout", f.Shell.Workbench.Commands.RecentCommandIds[0]);
    }

    [AvaloniaFact]
    public async Task A_chord_key_binding_opens_keyboard_shortcuts()
    {
        await using var f = ShellFixture.Create();

        // Ctrl+K Ctrl+S opens the keyboard shortcuts in the VS Code scheme (in the default one it is Surround With).
        await f.Shell.Workbench.Settings.SetAsync(BuiltIns.KeyboardSchemeSetting, "vscode", Codalus.Workbench.Settings.SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => Equals(f.Shell.Workbench.Context.GetValue("keyboardScheme"), "vscode"));
        f.Press(Key.K, s_primary);
        Assert.Contains("Waiting for the next key", f.Shell.StatusBar.Message, StringComparison.Ordinal);
        f.Press(Key.S, s_primary);

        await ShellFixture.PumpUntil(() => f.Shell.Layout.ActiveDocument is KeyboardShortcutsDocument);
        Assert.Equal("keyboardShortcuts", f.Shell.Workbench.Context.GetValue("activeDocumentKind"));
    }

    [AvaloniaFact]
    public async Task Rebinding_a_command_writes_keybindings_json_and_the_new_keys_work()
    {
        await using var f = ShellFixture.Create();
        f.Shell.OpenKeyboardShortcuts();
        var vm = ((KeyboardShortcutsDocument)f.Shell.Layout.ActiveDocument!).ViewModel;
        vm.Search = "Show Logs";
        var row = Assert.Single(vm.Rows);

        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<KeyboardShortcutsView>().Any());
        vm.StartRecording(row);
        Assert.NotNull(f.Window.KeyInterceptor);
        f.Press(Key.L, RawInputModifiers.Control | RawInputModifiers.Alt);
        Assert.Equal(KeySequence.Parse("ctrl+alt+l", PlatformInfo.Current).ToLabel(PlatformInfo.Current), vm.RecordedLabel);
        f.Press(Key.Enter);

        await ShellFixture.PumpUntil(() => File.Exists(f.Shell.Paths.KeybindingsFile) && f.Window.KeyInterceptor is null);
        Assert.Contains("\"ctrl+alt+l\"", File.ReadAllText(f.Shell.Paths.KeybindingsFile), StringComparison.Ordinal);

        f.Shell.Output.Selected = null;
        f.Press(Key.L, RawInputModifiers.Control | RawInputModifiers.Alt);
        await ShellFixture.PumpUntil(() => f.Shell.Output.Selected == f.Shell.Workbench.Log);
    }

    [AvaloniaFact]
    public async Task Settings_ui_edits_persist_to_settings_json()
    {
        await using var f = ShellFixture.Create();
        f.Shell.OpenSettings();
        var settings = ((SettingsDocument)f.Shell.Layout.ActiveDocument!).ViewModel;
        settings.Search = "status bar";
        var visible = Assert.IsType<BoolSettingViewModel>(Assert.Single(settings.Items));

        visible.Value = false;

        await ShellFixture.PumpUntil(() => !f.Shell.StatusBar.IsVisible);
        Assert.Contains("\"workbench.statusBar.visible\": false", File.ReadAllText(f.Shell.Paths.SettingsFile), StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => visible.IsModified); // the row refreshes on the settings' change, posted to the UI

        var size = Assert.IsType<TextSettingViewModel>(FindSetting(settings, "editor.fontSize"));
        size.Text = "500";
        size.Commit();
        await ShellFixture.PumpUntil(() => size.HasError);

        visible.ResetCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.StatusBar.IsVisible);
    }

    private static SettingItemViewModel FindSetting(SettingsViewModel settings, string key)
    {
        settings.Search = key;
        return Assert.Single(settings.Items, i => i.Key == key);
    }

    [AvaloniaFact]
    public async Task Switching_the_theme_changes_resources_live()
    {
        await using var f = ShellFixture.Create();
        await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, "highContrast", Workbench.Settings.SettingScope.User, CancellationToken.None);

        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "highContrast");
        Assert.Equal(ThemeService.HighContrast, Application.Current!.ActualThemeVariant);
        Assert.True(f.Window.TryFindResource("Shell.Background", f.Window.ActualThemeVariant, out var background));
        Assert.Equal(Colors.Black, ((ISolidColorBrush)background!).Color);
        Assert.Equal("highContrast", f.Shell.Workbench.Context.GetValue("theme"));

        await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, "light", Workbench.Settings.SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => Application.Current.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light);
    }

    [AvaloniaFact]
    public async Task The_layout_and_window_placement_are_saved_and_restored()
    {
        await using var first = ShellFixture.Create();
        first.Shell.Layout.ShowToolWindow(BuiltIns.PluginsWindow);
        first.Shell.Layout.CloseToolWindow(BuiltIns.OutputWindow);
        first.Shell.SaveState(new WindowPlacement(1000, 700, 50, 60, Maximized: false));

        await using var second = ShellFixture.Create(first.UserData);
        Assert.True(second.Shell.Layout.IsVisible(BuiltIns.PluginsWindow));
        Assert.False(second.Shell.Layout.IsVisible(BuiltIns.OutputWindow));
        Assert.Equal(new WindowPlacement(1000, 700, 50, 60, false), second.Shell.InitialPlacement);

        second.Shell.Layout.ResetLayout();
        Assert.True(second.Shell.Layout.IsVisible(BuiltIns.OutputWindow));
        Assert.False(second.Shell.Layout.IsVisible(BuiltIns.PluginsWindow));
    }

    [AvaloniaFact]
    public async Task A_side_pane_added_to_the_default_layout_keeps_the_documents_share()
    {
        // The default layout has no left pane (no left tool window shows by default) and its center has no proportion
        // until the dock panel arranges it. The first left tool window shown before that (the smoke test's Start page
        // after View: Reset Layout) once made the proportions 0.22 + 0.39 + 0.78, normalized to a right pane twice the
        // documents' width: on the 1024-pixel macOS runner the terminal under the documents was 14 columns wide.
        await using var f = ShellFixture.Create();
        f.Shell.Layout.ResetLayout();
        Assert.False(f.Shell.Layout.IsVisible(TestExplorerViewModel.ToolWindowId));
        f.Shell.Layout.ShowToolWindow(TestExplorerViewModel.ToolWindowId);

        var main = f.Shell.Layout.Capture(null).Root;
        var panes = (main.Children ?? []).Where(c => c.Kind != "splitter").ToList();
        Assert.Equal(["LeftPane", "Center", "RightPane"], panes.Select(p => p.Id ?? string.Empty).ToList());
        Assert.Equal(1.0, panes.Sum(p => p.Proportion ?? double.NaN), 6);
        Assert.Equal(0.22, panes[0].Proportion!.Value, 6);
        Assert.Equal(0.78 * 0.78, panes[1].Proportion!.Value, 6);
        Assert.Equal(0.22 * 0.78, panes[2].Proportion!.Value, 6);
    }

    [AvaloniaFact]
    public async Task Unknown_tool_windows_are_dropped_on_restore()
    {
        await using var f = ShellFixture.Create();
        var snapshot = new LayoutSnapshot(LayoutSnapshot.CurrentVersion, new LayoutNode("proportional")
        {
            Orientation = "horizontal",
            Children =
            [
                new LayoutNode("tools") { Alignment = "left", Tools = ["uninstalled.view", BuiltIns.PluginsWindow] },
                new LayoutNode("splitter"),
                new LayoutNode("documents") { Id = LayoutManager.DocumentsId },
            ],
        });

        Assert.True(f.Shell.Layout.Restore(snapshot));
        Assert.True(f.Shell.Layout.IsVisible(BuiltIns.PluginsWindow));
        Assert.DoesNotContain(f.Shell.Layout.OpenToolWindows, t => t.Id == "uninstalled.view");

        // Without a documents area the snapshot is rejected.
        Assert.False(f.Shell.Layout.Restore(new LayoutSnapshot(LayoutSnapshot.CurrentVersion, new LayoutNode("tools") { Tools = [BuiltIns.OutputWindow] })));
    }

    [AvaloniaFact]
    public async Task The_output_window_shows_channel_text()
    {
        await using var f = ShellFixture.Create();
        f.Shell.Workbench.Log.AppendLine("hello from the log");
        f.Shell.ShowLogs();

        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<OutputView>().Any(v => v.Text.Contains("hello from the log", StringComparison.Ordinal)));

        f.Shell.Workbench.Log.AppendLine("appended later");
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<OutputView>().Single().Text.Contains("appended later", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Notifications_appear_as_toasts_and_in_the_bell()
    {
        await using var f = ShellFixture.Create();
        var notification = f.Shell.Workbench.Notifications.Warning("Something needs attention", "Fix It");

        await ShellFixture.PumpUntil(() => f.Window.Toasts.GetVisualDescendants().OfType<NotificationCard>().Any());
        Assert.Contains(f.Shell.StatusBar.Right, i => i.Item.Id == "codalus.notifications" && i.Text == "1");
        Assert.Equal(true, f.Shell.Workbench.Context.GetValue("toastsVisible"));

        f.Shell.Notifications.Toasts.Single().Actions.Single().Command.Execute(null);
        Assert.Equal("Fix It", await notification.Result);
        await ShellFixture.PumpUntil(() => f.Shell.Notifications.Count == 0);
    }

    [AvaloniaFact]
    public async Task F6_cycles_focus_through_the_visible_panes()
    {
        await using var f = ShellFixture.Create();
        f.Shell.OpenSettings();
        await ShellFixture.PumpUntil(() => PaneHost.VisiblePanes(f.Window).Count >= 2);

        var visited = new HashSet<object>();
        for (var i = 0; i < PaneHost.VisiblePanes(f.Window).Count; i++)
        {
            f.Press(Key.F6, RawInputModifiers.Alt); // Window.NextPane in the default scheme (F6 builds)
            PaneHost? focused = null;
            await ShellFixture.PumpUntil(() => (focused = PaneHost.Of(f.Window.FocusManager?.GetFocusedElement() as Visual)) is not null, seconds: 5);
            Assert.NotNull(focused);
            visited.Add(focused.Dockable);
        }

        Assert.Equal(PaneHost.VisiblePanes(f.Window).Count, visited.Count);
        Assert.NotNull(f.Shell.Workbench.Context.GetValue("focusedView"));
    }

    [AvaloniaFact]
    public async Task The_welcome_plugin_shows_the_start_page_and_its_items_run_commands()
    {
        await using var f = ShellFixture.Create();
        f.Shell.OnInteractive();

        await ShellFixture.PumpUntil(() => f.Shell.Layout.IsVisible("welcome.view"), seconds: 60);
        var tree = (TreeToolViewModel)f.Shell.Layout.OpenToolWindows.Single(t => t.Id == "welcome.view").Content!;
        await ShellFixture.PumpUntil(() => tree.Roots.Count > 0, seconds: 30);
        Assert.Equal(["Start", "Recent", "Tip", "Plugins", "Learn", "Show Start Page on Startup"], tree.Roots.Select(r => r.Label));
        Assert.Contains(f.Shell.StatusBar.Left, i => i.Text == "Start" && i.Icon == "home");

        // Every refresh (a setting changed, Next Tip) replaces the nodes, and each group's items arrive with their own
        // request after the groups do: a group shows only "Loading…" until then, and a refresh still on its way replaces
        // it again. So each item is looked up in the tree as it is when it has arrived, never in a group read earlier.
        async Task<TreeNodeViewModel> ItemAsync(string group, string label)
        {
            TreeNodeViewModel? item = null;
            await ShellFixture.PumpUntil(() => (item = tree.Roots.FirstOrDefault(r => r.Label == group)?.Children.FirstOrDefault(c => c.Label == label)) is not null, seconds: 30);
            return item!;
        }

        // The keyboard scheme from the plugin's quick pick (M23a).
        f.Shell.Run("welcome.chooseKeyboardScheme");
        await ShellFixture.PumpUntil(() => f.Shell.Palette.IsOpen, seconds: 30);
        f.Type("Emacs");
        f.Press(Key.Enter);
        await ShellFixture.PumpUntil(() => Equals(f.Shell.Workbench.Context.GetValue("keyboardScheme"), "emacs"), seconds: 30);

        string? TipShown() => tree.Roots.FirstOrDefault(r => r.Label == "Tip") is { Children: [{ IsPlaceholder: false } shown, _] } ? shown.Label : null;
        string? firstTip = null;
        await ShellFixture.PumpUntil(() => (firstTip = TipShown()) is not null, seconds: 30);
        tree.Activate(await ItemAsync("Tip", "Next Tip"));
        await ShellFixture.PumpUntil(() => TipShown() is { } next && next != firstTip, seconds: 30);

        // Get Started is the first-run setup (M46): Help → Get Started opens it.
        f.Shell.Run("welcome.showOnboarding");
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen, seconds: 30);
        await f.Shell.SetupModel!.FinishAsync(done: false);
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen);

        tree.Activate(await ItemAsync("Start", "Open Settings"));
        await ShellFixture.PumpUntil(() => f.Shell.Layout.ActiveDocument is SettingsDocument, seconds: 30);
        ShellFixture.Pump(); // Settings' first layout holds the UI thread for seconds on a loaded machine: not during the refresh below

        // The toggle goes through the shell's workbench.setSetting command and refreshes the tree: its roots come back
        // through the UI thread (the recent solutions) within the shell's 5 s limit for a tree request.
        tree.Activate(tree.Roots[^1]);
        await ShellFixture.PumpUntil(() => tree.Roots.Count > 0 && tree.Roots[^1].Description == "Off", seconds: 30);
        Assert.False(f.Shell.Workbench.Settings.GetBoolean("welcome.showOnStartup", true));
    }

    [AvaloniaFact]
    public async Task A_plugin_tree_whose_refresh_times_out_keeps_its_items_and_says_why()
    {
        await using var f = ShellFixture.Create();
        f.Shell.OnInteractive();
        await ShellFixture.PumpUntil(() => f.Shell.Layout.IsVisible("welcome.view"), seconds: 60);
        var tree = (TreeToolViewModel)f.Shell.Layout.OpenToolWindows.Single(t => t.Id == "welcome.view").Content!;
        await ShellFixture.PumpUntil(() => tree.Roots.Count > 0 && tree.Message is null, seconds: 30);
        var labels = tree.Roots.Select(r => r.Label).ToList();

        // The Start page's roots need the recent solutions from the UI thread, which stays busy past the shell's 5 s limit
        // for a tree request (opening Settings kept it for 4 s on an idle machine). The page used to go empty, with
        // "Nothing to show." in place of the reason.
        tree.Refresh(null);
        Thread.Sleep(TimeSpan.FromSeconds(7));
        await ShellFixture.PumpUntil(() => tree.Message == "The plugin did not respond in time.", seconds: 30);
        Assert.Equal(labels, tree.Roots.Select(r => r.Label));

        // The next refresh that succeeds clears the message.
        tree.Refresh(null);
        await ShellFixture.PumpUntil(() => tree.Message is null, seconds: 30);
        Assert.Equal(labels, tree.Roots.Select(r => r.Label));
    }
}
