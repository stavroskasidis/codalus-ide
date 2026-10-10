using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using DaedalusStudio.Shell.Theming;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Shell.Views;
using DaedalusStudio.Workbench.Settings;

namespace DaedalusStudio.Shell.Tests;

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

        // The debug toolbar while debugging, with Continue and the steps when stopped.
        f.Shell.Toolbar.SetDebugState(debugging: true, stopped: true, running: false);
        Assert.True(f.Shell.Toolbar is { IsStopped: true, CanBreak: false, CanStop: true, CanStart: false });
        f.Shell.Toolbar.SetDebugState(debugging: false, stopped: false, running: false);

        await f.Shell.CloseSolutionAsync();
        await ShellFixture.PumpUntil(() => f.Shell.StatusBar.IsEmptyState && !f.Shell.Toolbar.HasSolution);
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
        f.Shell.Run("explorer.syncWithActiveDocument");
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

        f.Press(Key.Q, RawInputModifiers.Control);
        await ShellFixture.PumpUntil(() => f.Shell.Palette.IsOpen);
        f.Shell.Palette.Query = "color theme";
        await ShellFixture.PumpUntil(() => f.Shell.Palette.Rows.Count > 0);
        Assert.Contains(f.Shell.Palette.Rows, r => r.Label == "Preferences: Color Theme" && r.Detail == "Command");
        Assert.Contains(f.Shell.Palette.Rows, r => r.Label == "Color Theme" && r.Detail!.StartsWith("Setting", StringComparison.Ordinal));

        // A setting opens in Settings, filtered to it.
        f.Shell.Palette.Accept(f.Shell.Palette.Rows.First(r => r.Label == "Color Theme"));
        await ShellFixture.PumpUntil(() => f.Shell.Layout.FindDocument<SettingsDocument>(SettingsDocument.DocumentId) is { ViewModel.Search: ThemeService.SettingKey });

        // Files are found by name.
        f.Shell.Run("workbench.action.featureSearch");
        await ShellFixture.PumpUntil(() => f.Shell.Palette.IsOpen);
        f.Shell.Palette.Query = "notes";
        await ShellFixture.PumpUntil(() => f.Shell.Palette.Rows.Any(r => r.Label == "Notes.txt"));
        f.Shell.Palette.Cancel();
    }

    [AvaloniaFact]
    public async Task Settings_are_an_options_tree_reached_from_the_tools_menu()
    {
        await using var f = ShellFixture.Create();
        f.Shell.Run("workbench.openOptions");
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

        // Default: Ctrl+, (Cmd+, on macOS) is Go To All.
        var primary = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        f.Press(Key.OemComma, primary);
        await ShellFixture.PumpUntil(() => f.Shell.Palette.IsOpen);
        f.Shell.Palette.Cancel();
        await ShellFixture.PumpUntil(() => !f.Shell.Palette.IsOpen);

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
