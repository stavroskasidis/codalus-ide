using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Setup;

namespace Codalus.Shell.Tests;

/// <summary>
/// The first-run setup (M46): it shows once for a new user data folder, before the start window, and not for automation,
/// a command line that opens something, a synced profile or a folder used before; each step writes its setting; the
/// window and the IDE repaint live in the chosen theme; Skip keeps the defaults; Done opens the start window.
/// </summary>
public sealed class FirstRunSetupTests
{
    // ------------------------------------------------------------------ when it shows

    [Fact]
    public void The_setup_shows_only_for_a_new_folder_started_without_automation_or_something_to_open()
    {
        var plain = new ShellOptions();
        Assert.Equal(SetupDecision.Show, ShellHost.Decide(plain, completed: false, syncOn: false, usedBefore: false, skipVariable: null));

        // Recorded, synced, or a folder from before the setup existed.
        Assert.Equal(SetupDecision.Completed, ShellHost.Decide(plain, completed: true, syncOn: true, usedBefore: true, skipVariable: null));
        Assert.Equal(SetupDecision.SyncedProfile, ShellHost.Decide(plain, completed: false, syncOn: true, usedBefore: true, skipVariable: null));
        Assert.Equal(SetupDecision.UsedBefore, ShellHost.Decide(plain, completed: false, syncOn: false, usedBefore: true, skipVariable: null));

        // The switch and its variable, for automation.
        Assert.Equal(SetupDecision.SkippedBySwitch, ShellHost.Decide(ShellOptions.Parse(["--skip-setup"]), false, false, false, null));
        Assert.Equal(SetupDecision.SkippedBySwitch, ShellHost.Decide(plain, false, false, false, "1"));
        Assert.Equal(SetupDecision.Show, ShellHost.Decide(plain, false, false, false, "0"));
        Assert.Contains("--skip-setup", ShellOptions.HelpText, StringComparison.Ordinal);
        Assert.True(ShellOptions.Parse(["--skip-setup"]).SkipSetup);
        Assert.Empty(ShellOptions.Parse(["--skip-setup"]).Errors);

        // The smoke test, benchmarks and screenshots.
        foreach (var automation in new[] { plain with { SmokeTest = "samples" }, plain with { Bench = "typing" }, plain with { ExitOn = "interactive" }, plain with { Screenshot = "shot.png" } })
        {
            Assert.Equal(SetupDecision.Automation, ShellHost.Decide(automation, false, false, false, null));
        }

        // A command line that opens something specific: a path, a file, a remote, a diff, a merge, --wait.
        foreach (var opens in new[]
                 {
                     plain with { Path = "App.slnx" }, plain with { OpenFile = "Program.cs" }, plain with { Remote = "ssh:host" },
                     plain with { Diff = ("a", "b") }, plain with { Merge = ("l", "r", "b", "m") }, plain with { WaitFile = "COMMIT_EDITMSG" },
                 })
        {
            Assert.Equal(SetupDecision.OpensSomething, ShellHost.Decide(opens, false, false, false, null));
        }
    }

    [Fact]
    public void A_folder_with_settings_or_a_saved_window_counts_as_used_before()
    {
        var root = Path.Combine(Path.GetTempPath(), "codalus-firstrun-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = Codalus.Platform.AppPaths.Resolve(Codalus.Platform.PlatformInfo.Current, _ => null, root);
            paths.EnsureCreated();
            Assert.False(ShellHost.WasUsed(paths));

            File.WriteAllText(paths.SettingsFile, "{\n  // nothing set yet\n}");
            Assert.False(ShellHost.WasUsed(paths));

            File.WriteAllText(paths.SettingsFile, "{ \"editor.fontSize\": 14, }");
            Assert.True(ShellHost.WasUsed(paths));

            File.Delete(paths.SettingsFile);
            File.WriteAllText(Path.Combine(paths.State, "state.json"), "{}");
            Assert.True(ShellHost.WasUsed(paths));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task A_new_user_data_folder_shows_the_setup_once_and_Skip_keeps_the_defaults()
    {
        await using var f = ShellFixture.Create(firstRun: true);
        Assert.Equal(SetupDecision.Show, f.Shell.FirstRunDecision);
        // The main window's first frames make the shell interactive (OnInteractive), which opens the setup.
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen, seconds: 30);
        Assert.False(f.Shell.IsStartWindowOpen); // the setup comes first

        // The defaults (D32): Harbor following the system, the default keys.
        var model = f.Shell.SetupModel!;
        Assert.Equal(SetupStep.Appearance, model.Current);
        Assert.Equal(["harbor", "graphite", "daylight", "ember"], model.Families.Select(c => c.Id));
        Assert.Equal("harbor", model.SelectedFamily);
        Assert.True(model.IsMatchSystem);
        Assert.Equal(["Light", "Dark", "High contrast", "Match system"], model.Modes.Select(m => m.Title));
        Assert.Equal("Next: Keyboard", model.NextTitle);
        Assert.False(model.CanGoBack);

        // A family previews (the IDE and the window repaint) without saving.
        model.Families.Single(c => c.Id == "ember").SelectCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveFamily == "ember");
        Assert.Null(f.Shell.Workbench.Settings.GetLayerValue(ThemeService.SettingKey, Workbench.Settings.SettingScope.User));

        // Skip setup: nothing chosen is kept, the setup is recorded, and the start window follows.
        model.SkipCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen);
        await ShellFixture.PumpUntil(() => f.Shell.IsStartWindowOpen);
        Assert.False(model.WasDone);
        Assert.Equal("harbor", f.Shell.Theme.ActiveFamily);
        Assert.Null(f.Shell.Workbench.Settings.GetLayerValue(ThemeService.SettingKey, Workbench.Settings.SettingScope.User));
        Assert.Contains("\"workbench.setupCompleted\": true", await File.ReadAllTextAsync(f.Shell.Paths.SettingsFile, TestContext.Current.CancellationToken), StringComparison.Ordinal);

        // Once: the next start of this folder doesn't show it.
        Assert.Equal(SetupDecision.Completed, f.Shell.FirstRunDecision);
        f.Shell.StartWindowModel!.ContinueCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !f.Shell.IsStartWindowOpen);
    }

    // ------------------------------------------------------------------ the steps

    [AvaloniaFact]
    public async Task Each_step_writes_its_setting_and_Done_opens_the_start_window()
    {
        await using var f = ShellFixture.Create(firstRun: true);
        var settings = f.Shell.Workbench.Settings;
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen, seconds: 30);
        var model = f.Shell.SetupModel!;
        Snapshot(f, "first-run-setup-appearance");

        // Appearance: Graphite, Dark; Next saves it.
        model.Families.Single(c => c.Id == "graphite").SelectCommand.Execute(null);
        model.Modes.Single(m => m.Id == "dark").SelectCommand.Execute(null);
        Assert.Equal("graphite.dark", model.ThemeSetting);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "graphite.dark");
        await model.NextAsync();
        Assert.Equal("graphite.dark", settings.GetString(ThemeService.SettingKey));
        Assert.Equal(SetupStep.Keyboard, model.Current);
        Assert.True(model.CanGoBack);

        // Keyboard: VS Code's scheme.
        Snapshot(f, "first-run-setup-keyboard");
        Assert.Equal(["Default", "VS Code", "ReSharper", "Emacs"], model.Schemes.Select(s => s.Title));
        Assert.True(model.Schemes.Single(s => s.Id == "default").IsSelected);
        model.Schemes.Single(s => s.Id == "vscode").SelectCommand.Execute(null);
        await model.NextAsync();
        Assert.Equal("vscode", settings.GetString(BuiltIns.KeyboardSchemeSetting));
        await ShellFixture.PumpUntil(() => Equals(f.Shell.Workbench.Context.GetValue("keyboardScheme"), "vscode"));

        // Back shows what's saved; Next again.
        await model.BackAsync();
        Assert.Equal(SetupStep.Keyboard, model.Current);
        Assert.True(model.Schemes.Single(s => s.Id == "vscode").IsSelected);
        await model.NextAsync();
        Assert.Equal(SetupStep.Settings, model.Current);

        Snapshot(f, "first-run-setup-settings");

        // Your settings: VS Code's user folder (a stand-in), a .vssettings file and an export, without asking again.
        var vscode = Path.Combine(f.UserData, "vscode-user");
        Directory.CreateDirectory(vscode);
        await File.WriteAllTextAsync(Path.Combine(vscode, "settings.json"), """{ "editor.tabSize": 3 }""", TestContext.Current.CancellationToken);
        model.VsCodeFolder = vscode;
        Assert.True(model.HasVsCode);
        await model.ImportVsCodeAsync();
        Assert.Equal(3, settings.GetInt32("editor.tabSize"));
        Assert.Contains("Imported 1 settings", model.SettingsStatus, StringComparison.Ordinal);

        var vssettings = Path.Combine(f.UserData, "exported.vssettings");
        await File.WriteAllTextAsync(vssettings, """
            <UserSettings>
              <ApplicationIdentity version="17.0"/>
              <ToolsOptions>
                <ToolsOptionsCategory name="TextEditor" RegisteredName="TextEditor">
                  <ToolsOptionsSubCategory name="AllLanguages" RegisteredName="AllLanguages">
                    <PropertyValue name="ShowLineNumbers">false</PropertyValue>
                  </ToolsOptionsSubCategory>
                </ToolsOptionsCategory>
              </ToolsOptions>
            </UserSettings>
            """, TestContext.Current.CancellationToken);
        await model.ImportVsSettingsAsync(vssettings);
        Assert.False(settings.GetBoolean("editor.lineNumbers", true));

        var export = Path.Combine(f.UserData, "codalus-settings.json");
        await File.WriteAllTextAsync(export, """{ "codalusSettings": 1, "settings": { "editor.fontSize": 15 } }""", TestContext.Current.CancellationToken);
        await model.ImportExportAsync(export);
        Assert.Equal(15, settings.GetInt32("editor.fontSize"));
        Assert.Contains("1 settings", model.SettingsStatus, StringComparison.Ordinal);
        await model.NextAsync();

        // Tools: M37's check, the .NET SDK and git (this machine builds the repository, so both are found).
        Assert.Equal(SetupStep.Tools, model.Current);
        Assert.Equal("Done", model.NextTitle);
        Assert.True(model.VisitedTools);
        await ShellFixture.PumpUntil(() => model.Tools.Count == 2, seconds: 60);
        Assert.All(model.Tools, t => Assert.Equal(ToolState.Found, t.Tool.State));
        Assert.All(model.Tools, t => Assert.False(t.CanInstall));
        Snapshot(f, "first-run-setup-tools");

        // A missing SDK (a stand-in): Install… shows exactly what runs, and nothing runs without the yes.
        var runs = new List<string>();
        model.InstallRunner = (program, arguments, _, _) =>
        {
            runs.Add(program + " " + string.Join(' ', arguments));
            return Task.FromResult((0, "installed\n"));
        };
        model.ShowTools([new ToolStatus("dotnet", ".NET SDK", true, ToolState.Missing, "Opening .NET projects")]);
        var row = Assert.Single(model.Tools);
        Assert.True(row.CanInstall);
        row.InstallCommand.Execute(null);
        Assert.True(row.IsConfirming);
        Assert.Contains(row.Plan!.Command, row.Confirmation, StringComparison.Ordinal);
        Assert.Empty(runs);
        Snapshot(f, "first-run-setup-tools-confirm");
        row.CancelCommand.Execute(null);
        Assert.False(row.IsConfirming);
        Assert.Empty(runs);
        if (row.Plan.Kind == InstallKind.Run)
        {
            row.InstallCommand.Execute(null);
            await row.ConfirmAsync();
            Assert.Single(runs);
        }

        // Done: recorded, and the start window opens with everything applied.
        await model.NextAsync();
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen);
        await ShellFixture.PumpUntil(() => f.Shell.IsStartWindowOpen);
        Assert.True(model.WasDone);
        Assert.True(settings.GetBoolean(ShellHost.SetupCompletedSetting));
        Assert.Equal("graphite.dark", f.Shell.Theme.ActiveTheme);
        Assert.Equal("vscode", f.Shell.Workbench.Context.GetValue("keyboardScheme"));
        Assert.Equal(3, settings.GetInt32("editor.tabSize"));
        f.Shell.StartWindowModel!.ContinueCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !f.Shell.IsStartWindowOpen);
    }

    [AvaloniaFact]
    public async Task The_window_repaints_live_in_the_chosen_theme_and_works_from_the_keyboard()
    {
        await using var f = ShellFixture.Create(firstRun: true);
        f.Shell.Theme.SystemColors = () => new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Light };
        f.Shell.Theme.Apply();
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "harbor.light");

        // The first start's setup, skipped; then Help → Set Up Codalus IDE.
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen, seconds: 30);
        Assert.True(f.Shell.SetupModel!.IsFirstStart);
        await f.Shell.SetupModel.FinishAsync(done: false);
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen && f.Shell.IsStartWindowOpen);
        f.Shell.StartWindowModel!.ContinueCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !f.Shell.IsStartWindowOpen);
        f.Shell.Run("help.setUp");
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen);
        var model = f.Shell.SetupModel!;
        var window = f.Shell.SetupWindowView!;
        Assert.False(model.IsFirstStart);
        for (var i = 0; i < 4; i++)
        {
            ShellFixture.Pump();
        }

        // Ember, Dark: the IDE behind and the setup window take its tokens, and the cards are drawn in Dark.
        model.Families.Single(c => c.Id == "ember").SelectCommand.Execute(null);
        model.Modes.Single(m => m.Id == "dark").SelectCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "ember.dark");
        Assert.Equal(ThemeService.EmberDark, Application.Current!.ActualThemeVariant);
        Assert.Equal(ThemeService.EmberDark, window.ActualThemeVariant);
        Assert.Equal(Token("Shell.Panel", ThemeService.EmberDark), ((ISolidColorBrush)window.Background!).Color);
        foreach (var card in model.Families)
        {
            Assert.Equal(Token("Shell.Background", ThemeService.VariantOf(card.Id + ".dark")), ((ISolidColorBrush)card.Picture.Background).Color);
        }

        // The frame itself: the steps' column in Ember Dark's window color.
        for (var i = 0; i < 4; i++)
        {
            ShellFixture.Pump();
        }

        using (var frame = window.CaptureRenderedFrame())
        {
            Assert.NotNull(frame);
            Assert.Equal(Token("Shell.Background", ThemeService.EmberDark), Pixel(frame, 12, frame.PixelSize.Height - 12));
            if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is { Length: > 0 } snapshots)
            {
                Directory.CreateDirectory(snapshots);
                frame.Save(Path.Combine(snapshots, "first-run-setup-ember-dark.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }

        // Accessible names: every card, mode and button says what it is.
        var unnamed = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && string.IsNullOrEmpty(AutomationProperties.GetName(b)) && b.Content is not string { Length: > 0 })
            .ToList();
        Assert.Empty(unnamed);
        Assert.All(window.GetVisualDescendants().OfType<ToggleButton>().Where(b => b.IsEffectivelyVisible), b => Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(b))));

        // The keyboard: the arrows move between the cards (and choose), Alt+Right is Next, Escape skips.
        var ember = window.GetVisualDescendants().OfType<ToggleButton>().Single(b => AutomationProperties.GetName(b) == "Ember");
        ember.Focus();
        Press(window, Key.Left);
        Assert.Equal("daylight", model.SelectedFamily);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "daylight.dark");
        Press(window, Key.Right, RawInputModifiers.Alt);
        await ShellFixture.PumpUntil(() => model.Current == SetupStep.Keyboard);
        Assert.Equal("daylight.dark", f.Shell.Workbench.Settings.GetString(ThemeService.SettingKey));
        Press(window, Key.Left, RawInputModifiers.Alt);
        await ShellFixture.PumpUntil(() => model.Current == SetupStep.Appearance);

        // Escape skips: an unsaved preview ends, and from Help the start window doesn't open.
        model.Families.Single(c => c.Id == "harbor").SelectCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "harbor.dark");
        Press(window, Key.Escape);
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "daylight.dark");
        Assert.False(f.Shell.IsStartWindowOpen);
    }

    [AvaloniaFact]
    public async Task The_setup_doesnt_show_after_a_sync_and_its_own_sync_brings_the_profile()
    {
        // A profile that arrives with settings sync: the target is set, so the setup doesn't show.
        var synced = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-shell-" + Guid.NewGuid().ToString("N"));
        var remote = Path.Combine(synced, "sync");
        Directory.CreateDirectory(remote);
        await File.WriteAllTextAsync(Path.Combine(remote, Workbench.Settings.SettingsSync.FileName), """
            { "codalusSettings": 1, "settings": { "keyboard.scheme": "emacs", "workbench.colorTheme": "ember.light" }, "keybindings": [], "layouts": {}, "snippets": {} }
            """, TestContext.Current.CancellationToken);
        var settingsFile = Codalus.Platform.AppPaths.Resolve(Codalus.Platform.PlatformInfo.Current, _ => null, synced).SettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!);
        await File.WriteAllTextAsync(settingsFile, $$"""{ "settingsSync.target": {{System.Text.Json.JsonSerializer.Serialize(remote)}} }""", TestContext.Current.CancellationToken);
        await using (var f = ShellFixture.Create(synced, firstRun: true))
        {
            Assert.Equal(SetupDecision.SyncedProfile, f.Shell.FirstRunDecision);
        }

        // Turning sync on in the setup: what the target has replaces this machine's settings, without asking.
        await using var g = ShellFixture.Create(firstRun: true);
        Directory.CreateDirectory(remote);
        await File.WriteAllTextAsync(Path.Combine(remote, Workbench.Settings.SettingsSync.FileName), """
            { "codalusSettings": 1, "settings": { "keyboard.scheme": "emacs", "workbench.colorTheme": "ember.light" }, "keybindings": [], "layouts": {}, "snippets": {} }
            """, TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => g.Shell.IsSetupOpen, seconds: 30);
        var model = g.Shell.SetupModel!;
        model.Modes.Single(m => m.Id == "light").SelectCommand.Execute(null);
        await model.NextAsync(); // harbor.light saved: settings on both sides, and the synced ones win
        await model.NextAsync();
        await model.SyncAsync(remote);
        Assert.Contains("applied here", model.SettingsStatus, StringComparison.Ordinal);
        Assert.Equal("emacs", g.Shell.Workbench.Settings.GetString(BuiltIns.KeyboardSchemeSetting));
        await ShellFixture.PumpUntil(() => g.Shell.Theme.ActiveTheme == "ember.light");
        Assert.Equal(remote, g.Shell.Workbench.Settings.GetString(ShellHost.SyncTargetSetting));

        // Back shows what came: Emacs, then Ember Light.
        await model.BackAsync();
        Assert.True(model.Schemes.Single(s => s.Id == "emacs").IsSelected);
        await model.BackAsync();
        Assert.Equal("ember", model.SelectedFamily);
        Assert.Equal("light", model.SelectedVariant);
        await model.FinishAsync(done: true);
        await ShellFixture.PumpUntil(() => !g.Shell.IsSetupOpen);
        await g.Shell.TurnOffSettingsSyncAsync();
        Directory.Delete(synced, recursive: true);
    }

    /// <summary>With <c>CODALUS_UI_SNAPSHOTS</c>: the setup window's frame, for a look at each step.</summary>
    private static void Snapshot(ShellFixture f, string name)
    {
        if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is not { Length: > 0 } snapshots || f.Shell.SetupWindowView is not { } window)
        {
            return;
        }

        for (var i = 0; i < 4; i++)
        {
            ShellFixture.Pump();
        }

        Directory.CreateDirectory(snapshots);
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(snapshots, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static Color? Token(string key, Avalonia.Styling.ThemeVariant variant) =>
        Application.Current!.TryGetResource(key, variant, out var value) && value is ISolidColorBrush brush ? brush.Color : null;

    private static Color Pixel(Avalonia.Media.Imaging.WriteableBitmap frame, int x, int y)
    {
        using var buffer = frame.Lock();
        var offset = (y * buffer.RowBytes) + (x * 4);
        var (b0, b1, b2, b3) = (Marshal.ReadByte(buffer.Address, offset), Marshal.ReadByte(buffer.Address, offset + 1), Marshal.ReadByte(buffer.Address, offset + 2), Marshal.ReadByte(buffer.Address, offset + 3));
        return buffer.Format == PixelFormat.Rgba8888 ? Color.FromArgb(b3, b0, b1, b2) : Color.FromArgb(b3, b2, b1, b0);
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        if (window.IsVisible) // Escape closes it
        {
            window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        }

        ShellFixture.Pump();
    }
}
