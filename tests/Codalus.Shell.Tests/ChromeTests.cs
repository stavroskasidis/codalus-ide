using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>M29: the title row, the standard toolbar, the status bar's states, the search box, the keyboard schemes and the Blue theme.</summary>
public class ChromeTests
{
    [AvaloniaFact]
    public async Task The_toolbar_follows_and_switches_the_configuration_and_the_status_bar_its_state()
    {
        await using var f = ShellFixture.Create();
        Assert.True(f.Shell.StatusBar is { IsEmptyState: true, DisplayMessage: "Ready" });
        Assert.False(f.Shell.Toolbar.HasSolution);
        Assert.Single(f.Window.GetVisualDescendants().OfType<StandardToolbar>());

        var solution = f.CopyFixture();
        f.Shell.Trust.Trust(Path.GetDirectoryName(Path.GetDirectoryName(solution))!);
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Progress is { Completed: true } && f.Shell.Solutions.Projects.Count == 3, seconds: 90);

        Assert.True(f.Shell.StatusBar is { HasSolution: true, IsEmptyState: false });
        Assert.Equal("ProjectSystem", f.Shell.Toolbar.SolutionName);
        Assert.Equal(["Debug", "Release"], f.Shell.Toolbar.Configurations);
        Assert.Equal(("Debug", "Any CPU"), (f.Shell.Toolbar.Configuration, f.Shell.Toolbar.Platform));
        await ShellFixture.PumpUntil(() => f.Shell.Toolbar.StartupProject is { Name: "App" });
        Assert.True(f.Shell.Toolbar.CanStart);

        // Picking Release in the toolbar switches the solution's active configuration.
        f.Shell.Toolbar.Configuration = "Release";
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Solution?.Active is { Configuration: "Release", Platform: "Any CPU" });

        // The Hot Reload button (a .NET 6+ startup project): Start with Hot Reload, and its menu's switch is the setting.
        await ShellFixture.PumpUntil(() => f.Shell.Toolbar.CanHotReload);
        Assert.False(f.Shell.Toolbar.IsHotReloadRunning);
        var hotReload = f.Window.GetVisualDescendants().OfType<SplitButton>().Single(b => Equals(b.Tag, "run.startWithHotReload"));
        Assert.Equal("Hot Reload", Avalonia.Automation.AutomationProperties.GetName(hotReload));
        Assert.True(hotReload.IsEffectivelyVisible);
        Assert.Equal(["Start with Hot Reload", "Apply Code Changes", "Stop Running Programs", "Hot Reload on Start Without Debugging", "Settings"],
            ((MenuFlyout)hotReload.Flyout!).Items.OfType<MenuItem>().Select(i => (string)i.Header!));
        f.Shell.Toolbar.ToggleHotReloadOnStartCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.Toolbar.HotReloadOnStart && f.Shell.Workbench.Settings.GetBoolean(ShellHost.HotReloadSetting));
        f.Shell.Toolbar.ToggleHotReloadOnStartCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !f.Shell.Toolbar.HotReloadOnStart);

        // The debug toolbar while debugging, with Continue and the steps when stopped.
        f.Shell.Toolbar.SetDebugState(debugging: true, stopped: true, running: false);
        Assert.True(f.Shell.Toolbar is { IsStopped: true, CanBreak: false, CanStop: true, CanStart: false });
        f.Shell.Toolbar.SetDebugState(debugging: false, stopped: false, running: false);

        await f.Shell.CloseSolutionAsync();
        await ShellFixture.PumpUntil(() => f.Shell.StatusBar.IsEmptyState && !f.Shell.Toolbar.HasSolution);
    }

    [AvaloniaFact]
    public async Task The_hot_reload_split_button_is_flat_like_the_run_buttons_beside_it_in_every_theme()
    {
        // The maintainer's report: the Hot Reload button and its arrow were a gray block among flat toolbar buttons.
        await using var f = ShellFixture.Create();
        var toolbar = f.Window.GetVisualDescendants().OfType<StandardToolbar>().Single();
        var hotReload = toolbar.GetLogicalDescendants().OfType<SplitButton>().Single(b => Equals(b.Tag, "run.startWithHotReload"));
        var run = toolbar.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Tag, "run.startWithoutDebugging"));

        // Both are hidden without a startup project; their templates and styles apply all the same.
        static ContentPresenter Presenter(TemplatedControl control)
        {
            control.ApplyTemplate();
            return control.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter" && p.TemplatedParent == control);
        }

        hotReload.ApplyTemplate();
        var parts = hotReload.GetVisualDescendants().OfType<Button>().Where(b => b.TemplatedParent == hotReload).ToList();
        Assert.Equal(["PART_PrimaryButton", "PART_SecondaryButton"], parts.Select(p => p.Name));
        static void SetPointerOver(Control control, bool value) => ((IPseudoClasses)control.Classes).Set(":pointerover", value);
        static IBrush? Brush(ContentPresenter presenter) => presenter.Background;
        foreach (var theme in ThemeService.Themes.Select(t => t.Variant))
        {
            f.Window.RequestedThemeVariant = theme;
            ShellFixture.Pump();
            var runPresenter = Presenter(run);
            Assert.All(parts, part => Assert.Equal(Brush(runPresenter), Brush(Presenter(part))));
            Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(Brush(runPresenter)).Color);

            // Under the pointer: the hover color and border, as the run button has.
            SetPointerOver(run, true);
            foreach (var part in parts)
            {
                SetPointerOver(hotReload, true);
                SetPointerOver(part, true);
                ShellFixture.Pump();
                Assert.Equal(Brush(runPresenter), Brush(Presenter(part)));
                Assert.Equal(runPresenter.BorderBrush, Presenter(part).BorderBrush);
                SetPointerOver(part, false);
                SetPointerOver(hotReload, false);
            }

            SetPointerOver(run, false);
        }

        f.Window.RequestedThemeVariant = null;
    }

    [AvaloniaFact]
    public async Task Solution_explorer_searches_and_syncs_with_the_active_document()
    {
        await using var f = ShellFixture.Create();
        var solution = f.CopyFixture();
        f.Shell.Trust.Trust(Path.GetDirectoryName(Path.GetDirectoryName(solution))!);
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Progress is { Completed: true } && f.Shell.Solutions.Projects.Count == 3, seconds: 90);
        var explorer = f.Shell.Explorer;
        await ShellFixture.PumpUntil(() => explorer.Rows.Any(r => r.Name == "App"));

        // The search box keeps the matching items and their parents.
        explorer.Filter = "greeter";
        Assert.Contains(explorer.Rows, r => r.Name == "Greeter.cs");
        Assert.Contains(explorer.Rows, r => r.Name == "Core");
        Assert.DoesNotContain(explorer.Rows, r => r.Name == "App");
        explorer.Filter = string.Empty;
        Assert.Contains(explorer.Rows, r => r.Name == "App");

        // Sync with Active Document selects the active file.
        var program = Path.Combine(Path.GetDirectoryName(solution)!, "src", "App", "Program.cs");
        await f.Shell.OpenFileAsync(program);
        await f.RunAsync("explorer.syncWithActiveDocument");
        await ShellFixture.PumpUntil(() => explorer.Selected?.Node.Path == program);

        // Home collapses back to the projects.
        explorer.Home();
        Assert.DoesNotContain(explorer.Rows, r => r.Name == "Program.cs");
    }

    [AvaloniaFact]
    public async Task The_search_box_finds_commands_settings_and_files()
    {
        await using var f = ShellFixture.Create();
        var file = Path.Combine(f.UserData, "Notes.txt");
        await File.WriteAllTextAsync(file, "notes");
        await f.Shell.OpenFileAsync(file);

        // Ctrl+Q opens Code Search's window on Feature Search (M47): commands with their keys, settings, options pages.
        f.Press(Key.Q, RawInputModifiers.Control);
        var search = f.Shell.CodeSearch;
        await ShellFixture.PumpUntil(() => search is { IsOpen: true, IsFeatureTab: true });
        search.Query = "color theme";
        await ShellFixture.PumpUntil(() => search.Rows.Count > 0);
        Assert.Contains(search.Rows, r => r.Name == "Preferences: Color Theme" && r.Feature?.Kind == FeatureSearchKind.Command);
        Assert.Contains(search.Rows, r => r.Name == "Color Theme" && r.Feature?.Kind == FeatureSearchKind.Setting && r.Detail!.StartsWith("Setting", StringComparison.Ordinal));

        // A setting opens in Settings, filtered to it.
        search.SelectedIndex = search.Rows.IndexOf(search.Rows.First(r => r.Name == "Color Theme" && r.Feature?.Kind == FeatureSearchKind.Setting));
        await search.AcceptAsync();
        Assert.False(search.IsOpen);
        await ShellFixture.PumpUntil(() => f.Shell.Layout.FindDocument<SettingsDocument>(SettingsDocument.DocumentId) is { ViewModel.Search: ThemeService.SettingKey });

        // Files are found by name, on the Code Search tab.
        await f.RunAsync("workbench.action.gotoAll");
        await ShellFixture.PumpUntil(() => search is { IsOpen: true, IsCodeTab: true });
        search.Query = "notes";
        await ShellFixture.PumpUntil(() => search.Rows.Any(r => r.Name == "Notes.txt"));
        search.Close();
    }

    [AvaloniaFact]
    public async Task The_view_menu_keeps_the_main_windows_and_the_rest_in_submenus()
    {
        await using var f = ShellFixture.Create();
        var menus = f.Shell.Workbench.Menus;
        var view = menus.GetItems(Workbench.Menus.MenuLocations.MenuBar("view"));

        // Short enough for a screen, without duplicates, with the main windows first and Other Windows after them.
        Assert.True(view.Count <= 20, string.Join(", ", view.Select(i => i.Title)));
        Assert.Equal(view.Count, view.Where(i => i.Submenu is null).Select(i => i.Command).Distinct().Count() + view.Count(i => i.Submenu is not null));
        Assert.Equal(["Solution Explorer", "Git Changes", "Test Explorer"], view.Where(i => i.Group == "view").Take(3).Select(i => i.Title));
        var other = Assert.Single(view, i => i.Submenu == BuiltIns.OtherWindowsMenu);
        Assert.Equal("Other Windows", other.Title);
        Assert.DoesNotContain(view, i => i.Title is "Locals" or "Find Results" or "C# Interactive");

        var others = menus.GetItems(BuiltIns.OtherWindowsMenu).Select(i => i.Title).ToList();
        Assert.Contains("Find Results", others);
        Assert.Contains("C# Interactive", others);
        Assert.Contains("Endpoints Explorer", others);
        Assert.Equal(others.Order(StringComparer.OrdinalIgnoreCase), others); // alphabetical
        Assert.Equal(others.Count, others.Distinct().Count());

        // The debugger's windows are under Debug → Windows.
        var debug = menus.GetItems(Workbench.Menus.MenuLocations.MenuBar("debug"));
        Assert.Contains(debug, i => i is { Submenu: BuiltIns.DebugWindowsMenu, Title: "Windows" });
        var windows = menus.GetItems(BuiltIns.DebugWindowsMenu).Select(i => i.Title).ToList();
        Assert.Contains("Locals", windows);
        Assert.Contains("Breakpoints", windows);
        Assert.Contains("Threads", windows);
        Assert.DoesNotContain(debug, i => i.Title == "Locals");
    }

    [AvaloniaFact]
    public async Task Settings_are_an_options_tree_reached_from_the_tools_menu()
    {
        await using var f = ShellFixture.Create();
        await f.RunAsync("workbench.openOptions");
        var document = f.Shell.Layout.FindDocument<SettingsDocument>(SettingsDocument.DocumentId);
        Assert.NotNull(document);
        var vm = document.ViewModel;

        Assert.Equal("All Settings", vm.Categories[0].Name);
        var environment = vm.Categories.Single(c => c.Name == "Environment");
        Assert.Contains(environment.Children, c => c.Name == "Appearance");
        Assert.Contains(environment.Children, c => c.Name == "Keyboard");
        Assert.Contains(vm.Categories, c => c.Name == "Text Editor");
        Assert.Equal("Projects and Solutions", SettingsViewModel.OptionsGroup("Build and Run"));
        Assert.Equal("Text Editor", SettingsViewModel.OptionsGroup("editor.inlayHints"));

        // A group shows the settings of all its categories.
        vm.SelectedCategory = environment;
        Assert.Contains(vm.Items, i => i.Key == ThemeService.SettingKey);
        Assert.Contains(vm.Items, i => i.Key == BuiltIns.KeyboardSchemeSetting);
        Assert.DoesNotContain(vm.Items, i => i.Category == "Terminal");
    }

    [AvaloniaFact]
    public async Task The_keyboard_scheme_switches_the_default_bindings()
    {
        await using var f = ShellFixture.Create();
        Assert.Equal("default", f.Shell.Workbench.Context.GetValue("keyboardScheme"));

        // Default: Ctrl+, (Cmd+, on macOS) is Go To All, Code Search (M47).
        var primary = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        f.Press(Key.OemComma, primary);
        await ShellFixture.PumpUntil(() => f.Shell.CodeSearch is { IsOpen: true, IsCodeTab: true });
        f.Shell.CodeSearch.Close();
        await ShellFixture.PumpUntil(() => !f.Shell.CodeSearch.IsOpen);

        // VS Code: Ctrl+, opens Settings and F1 the command palette.
        await f.Shell.Workbench.Settings.SetAsync(BuiltIns.KeyboardSchemeSetting, "vscode", SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => Equals(f.Shell.Workbench.Context.GetValue("keyboardScheme"), "vscode"));
        f.Press(Key.OemComma, primary);
        await ShellFixture.PumpUntil(() => f.Shell.Layout.FindDocument<SettingsDocument>(SettingsDocument.DocumentId) is not null);
        f.Press(Key.F1);
        await ShellFixture.PumpUntil(() => f.Shell.Palette.IsOpen);
        f.Shell.Palette.Cancel();
    }

    [AvaloniaFact]
    public async Task The_blue_theme_applies_its_chrome_and_light_editor_colors()
    {
        await using var f = ShellFixture.Create();
        await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, "blue", SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "blue");

        Assert.Equal("blue", f.Shell.Workbench.Context.GetValue("theme"));
        Assert.Same(Editor.EditorTheme.Light, f.Shell.EditorTheme);
        Assert.True(f.Window.TryFindResource("Shell.Tab.Active", f.Window.ActualThemeVariant, out var tab));
        Assert.Equal(Color.Parse("#F5CC84"), Assert.IsAssignableFrom<ISolidColorBrush>(tab).Color);
        Assert.True(f.Window.TryFindResource("Shell.StatusBar.Empty", f.Window.ActualThemeVariant, out var empty));
        Assert.Equal(Color.Parse("#68217A"), Assert.IsAssignableFrom<ISolidColorBrush>(empty).Color);
        var statusBar = f.Window.GetVisualDescendants().OfType<Avalonia.Controls.Border>().Single(b => b.Name == "StatusBarHost");
        Assert.Equal(Color.Parse("#68217A"), Assert.IsAssignableFrom<ISolidColorBrush>(statusBar.Background).Color);
    }
}
