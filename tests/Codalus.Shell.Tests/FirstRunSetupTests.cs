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
using Avalonia.Styling;
using Avalonia.VisualTree;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Tests;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Acp;
using Codalus.Workbench.Settings;
using Codalus.Workbench.Setup;

namespace Codalus.Shell.Tests;

/// <summary>
/// The first-run setup (M46): it shows once for a new user data folder, before the start window, and not for automation,
/// a command line that opens something, a synced profile or a folder used before; a first start opens on the welcome
/// page, whose "coming from" preselects the keys and marks an importer; each step writes its setting; the
/// window and the IDE repaint live in the chosen theme; Skip keeps the defaults; the summary's Done opens the start
/// window. The AI step (ADR 0096) changes nothing until the setup is done: then Connect, or the provider's prompts, open
/// in the main window, and the start window waits for them.
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

        // A first start opens on the welcome page: Get Started, or Use the defaults; nothing to go back to.
        var model = f.Shell.SetupModel!;
        Assert.Equal(SetupPage.Welcome, model.Page);
        Assert.True(model.IsWelcome);
        Assert.Equal("Get Started", model.NextTitle);
        Assert.Equal("Use the defaults", model.SkipTitle);
        Assert.False(model.CanGoBack);
        Assert.Equal(["The classic IDE", "VS Code", "Rider or ReSharper", "Somewhere else"], model.Origins.Select(o => o.Title));
        Assert.Null(model.SelectedOrigin);
        Assert.All(model.Steps, s => Assert.False(s.IsCurrent));
        Assert.Equal(["Appearance", "Keyboard", "Your settings", "Tools", "AI"], model.Steps.Select(s => s.Title));
        Assert.Equal("Step 5 of 5: AI", model.Steps[4].AccessibleName);
        await model.NextAsync();

        // The defaults (D36): Classic, the first card, following the system; the default keys.
        Assert.Equal(SetupPage.Steps, model.Page);
        Assert.Equal("Skip setup", model.SkipTitle);
        Assert.Equal(SetupStep.Appearance, model.Current);
        Assert.Equal(["classic", "harbor", "graphite", "daylight", "ember"], model.Families.Select(c => c.Id));
        Assert.Equal("classic", model.SelectedFamily);
        Assert.True(model.IsMatchSystem);
        Assert.Equal(["Light", "Dark", "High contrast", "Match system"], model.Modes.Select(m => m.Title));
        Assert.Equal("Next: Keyboard", model.NextTitle);
        Assert.True(model.CanGoBack); // to the welcome page
        Assert.Equal(13, model.FontSize);

        // A family previews (the IDE and the window repaint) without saving.
        model.Families.Single(c => c.Id == "ember").SelectCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveFamily == "ember");
        Assert.Null(f.Shell.Workbench.Settings.GetLayerValue(ThemeService.SettingKey, Workbench.Settings.SettingScope.User));

        // Skip setup: nothing chosen is kept, the setup is recorded, and the start window follows.
        model.SkipCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen);
        await ShellFixture.PumpUntil(() => f.Shell.IsStartWindowOpen);
        Assert.False(model.WasDone);
        Assert.Equal("classic", f.Shell.Theme.ActiveFamily);
        Assert.Null(f.Shell.Workbench.Settings.GetLayerValue(ThemeService.SettingKey, Workbench.Settings.SettingScope.User));
        Assert.Null(f.Shell.Workbench.Settings.GetLayerValue(AiService.EnabledSetting, Workbench.Settings.SettingScope.User));
        Assert.False(f.Shell.ConnectAgent.IsOpen);
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
        Snapshot(f, "first-run-setup-welcome");
        await model.NextAsync();
        Snapshot(f, "first-run-setup-appearance");

        // Appearance: Graphite, Dark, and the editor's font three sizes up, on the sample; Next saves both.
        model.Families.Single(c => c.Id == "graphite").SelectCommand.Execute(null);
        model.Modes.Single(m => m.Id == "dark").SelectCommand.Execute(null);
        Assert.Equal("graphite.dark", model.ThemeSetting);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "graphite.dark");
        Assert.Equal(7, model.CodeSample.Count);
        for (var i = 0; i < 3; i++)
        {
            model.IncreaseFontSizeCommand.Execute(null);
        }

        Assert.Equal(16, model.FontSize);
        Assert.Equal("16 px", model.FontSizeText);
        Assert.Null(settings.GetLayerValue("editor.fontSize", SettingScope.User)); // shown on the sample, not saved yet
        ShellFixture.Pump();
        var sample = f.Shell.SetupWindowView!.FindControl<ItemsControl>("CodeSample")!;
        Assert.All(sample.GetVisualDescendants().OfType<TextBlock>(), t => Assert.Equal(16, t.FontSize));
        model.FontSize = 500; // the setup offers 8 to 32
        Assert.Equal(SetupViewModel.MaxFontSize, model.FontSize);
        Assert.False(model.IncreaseFontSizeCommand.CanExecute(null));
        model.FontSize = 16;
        await model.NextAsync();
        Assert.Equal("graphite.dark", settings.GetString(ThemeService.SettingKey));
        Assert.Equal(16, settings.GetInt32("editor.fontSize"));
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
        Assert.Equal("Next: AI", model.NextTitle);
        Assert.True(model.VisitedTools);
        await ShellFixture.PumpUntil(() => model.Tools.Count == 2, seconds: 60);
        Assert.All(model.Tools, t => Assert.Equal(ToolState.Found, t.Tool.State));
        Assert.All(model.Tools, t => Assert.False(t.CanInstall));
        Snapshot(f, "first-run-setup-tools");

        // M49: SDKs alone are a Found SDK row; the runtime the IDE needs, when it's missing, is a row with its install.
        var sdk = new ToolStatus("dotnet", ".NET SDK", true, ToolState.Found, "Opening .NET projects") { Version = "10.0.109" };
        var git = new ToolStatus("git", "Git", true, ToolState.Found, "Source control") { Version = "2.47.1" };
        var runtime = new ToolStatus("dotnet-runtime", ".NET runtime for Codalus IDE", true, ToolState.Missing, "Codalus IDE's helper processes") { WantedVersion = "11.0.0-rc.2.26504.105", WantedChannel = "11.0", Found = "10.0.9 in /usr/local/share/dotnet" };
        model.ShowTools([sdk, runtime, git]);
        Assert.Equal(["dotnet", "dotnet-runtime", "git"], model.Tools.Select(t => t.Tool.Id));
        Assert.True(model.Tools.Single(t => t.Tool.Id == "dotnet-runtime").CanInstall);
        model.ShowTools([sdk, runtime with { State = ToolState.Found, Version = "11.0.0-rc.2.26504.105" }, git]);
        Assert.Equal(["dotnet", "git"], model.Tools.Select(t => t.Tool.Id));

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

        // AI: no AI unless chosen. The summary says what was set, with a few of the chosen scheme's keys.
        model.ShowTools([sdk, git]);
        await model.NextAsync();
        Assert.Equal(SetupStep.Ai, model.Current);
        Assert.Equal("Next: Summary", model.NextTitle);
        Assert.Equal(SetupAiPath.None, model.AiPath);
        Assert.False(model.AiChanged);
        Snapshot(f, "first-run-setup-ai");
        await model.NextAsync();
        Assert.Equal(SetupPage.Finish, model.Page);
        Assert.All(model.Steps, s => Assert.False(s.IsCurrent));
        Assert.Equal("Done", model.NextTitle);
        Assert.False(model.CanSkip);
        Assert.True(model.CanGoBack);
        Assert.False(model.HasAiNext);
        Assert.Equal(["Theme", "Keys", "Editor font size", "Tools", "AI"], model.Summary.Select(r => r.Label));
        Assert.Equal("Graphite, Dark", model.Summary[0].Value);
        Assert.Equal("VS Code", model.Summary[1].Value);
        Assert.Equal("15 px", model.Summary[2].Value); // the imported export's
        Assert.Equal("Off", model.Summary[4].Value);
        Assert.Equal("Good to know (VS Code keys)", model.ShortcutsTitle);
        Assert.Contains(model.Shortcuts, k => k.Title == "Start debugging" && k.Keys == "F5");
        Assert.Contains(model.Shortcuts, k => k.Title == "Run any command");
        Snapshot(f, "first-run-setup-summary");

        // Back from the summary is the AI step; Next comes back.
        await model.BackAsync();
        Assert.Equal(SetupStep.Ai, model.Current);
        Assert.True(model.IsAi);
        await model.NextAsync();

        // Done: recorded, and the start window opens with everything applied. AI stays as it was: off.
        await model.NextAsync();
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen);
        await ShellFixture.PumpUntil(() => f.Shell.IsStartWindowOpen);
        Assert.True(model.WasDone);
        Assert.Null(settings.GetLayerValue(AiService.EnabledSetting, SettingScope.User));
        Assert.False(f.Shell.ConnectAgent.IsOpen);
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
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "light");

        // The first start's setup, skipped; then Help → Set Up Codalus IDE.
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen, seconds: 30);
        Assert.True(f.Shell.SetupModel!.IsFirstStart);
        await f.Shell.SetupModel.FinishAsync(done: false);
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen && f.Shell.IsStartWindowOpen);
        f.Shell.StartWindowModel!.ContinueCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !f.Shell.IsStartWindowOpen);
        await f.RunAsync("help.setUp");
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen);
        var model = f.Shell.SetupModel!;
        var window = f.Shell.SetupWindowView!;
        Assert.False(model.IsFirstStart);
        Assert.Equal(SetupPage.Steps, model.Page); // no welcome page from Help
        Assert.True(model.IsAppearance);
        Assert.False(model.CanGoBack);
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
            Assert.Equal(Token("Shell.Background", ThemeService.VariantOf(ThemeService.Resolve(card.Id + ".dark", null))), ((ISolidColorBrush)card.Picture.Background).Color);
        }

        // The font sample follows the theme the window is drawn in: Ember Dark's syntax colors, and another theme's when
        // the window's theme changes without a card being clicked (the system going dark under Match system).
        Assert.Equal(Brushes(new ThemePicture(ThemeService.EmberDark).Code), Brushes(model.CodeSample));
        window.RequestedThemeVariant = ThemeService.HighContrast;
        ShellFixture.Pump();
        Assert.Equal(Brushes(new ThemePicture(ThemeService.HighContrast).Code), Brushes(model.CodeSample));
        Assert.NotEqual(Brushes(new ThemePicture(ThemeService.EmberDark).Code), Brushes(model.CodeSample));
        window.RequestedThemeVariant = null;
        ShellFixture.Pump();
        Assert.Equal(Brushes(new ThemePicture(ThemeService.EmberDark).Code), Brushes(model.CodeSample));

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
        await model.NextAsync(); // Get Started
        model.Modes.Single(m => m.Id == "light").SelectCommand.Execute(null);
        await model.NextAsync(); // classic.light saved: settings on both sides, and the synced ones win
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

    // ------------------------------------------------------------------ Classic, the default (D36)

    [AvaloniaFact]
    public async Task A_new_profile_starts_in_Classic_following_each_mode_of_the_system()
    {
        await using var f = ShellFixture.Create(firstRun: true);
        var system = new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Light };
        f.Shell.Theme.SystemColors = () => system;
        f.Shell.Theme.Apply();

        // No theme in the settings: Classic, following the OS's light, dark and high contrast (high contrast first).
        Assert.Null(f.Shell.Workbench.Settings.GetLayerValue(ThemeService.SettingKey, Workbench.Settings.SettingScope.User));
        Assert.Equal("classic", f.Shell.Theme.Setting);
        foreach (var (os, theme, variant) in new[]
        {
            (new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Light }, "light", ThemeVariant.Light),
            (new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Dark }, "dark", ThemeVariant.Dark),
            (new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Light, ContrastPreference = ColorContrastPreference.High }, "highContrast", ThemeService.HighContrast),
            (new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Dark, ContrastPreference = ColorContrastPreference.High }, "highContrast", ThemeService.HighContrast),
        })
        {
            system = os;
            f.Shell.Theme.Apply(); // what the OS's ColorValuesChanged does
            Assert.Equal(theme, f.Shell.Theme.ActiveTheme);
            Assert.Equal("classic", f.Shell.Theme.ActiveFamily);
            Assert.Equal(variant, Application.Current!.ActualThemeVariant);
            Assert.Equal(ThemeService.KindOf(theme), f.Shell.Workbench.Context.GetValue("theme"));
            Assert.Equal("classic", f.Shell.Workbench.Context.GetValue("themeFamily"));
        }

        // The setup opens on Classic, the first card, with Match system; its picture is the system's mode (high contrast).
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen, seconds: 30);
        var model = f.Shell.SetupModel!;
        Assert.Equal("classic", model.Families[0].Id);
        Assert.Equal("Classic", model.Families[0].Title);
        Assert.False(string.IsNullOrEmpty(model.Families[0].Description));
        Assert.Equal("classic", model.SelectedFamily);
        Assert.True(model.IsMatchSystem);
        model.Families[0].SelectCommand.Execute(null); // the pictures as of now (the setup may have opened in another mode)
        Assert.Equal("classic", model.ThemeSetting);
        Assert.Equal(ThemeService.HighContrast, model.Families[0].Picture.Variant);
        Assert.Equal(ThemeService.HarborHighContrast, model.Families.Single(c => c.Id == "harbor").Picture.Variant);
        await model.FinishAsync(done: false);
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen && f.Shell.IsStartWindowOpen);
        f.Shell.StartWindowModel!.ContinueCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !f.Shell.IsStartWindowOpen);
    }

    [AvaloniaFact]
    public async Task Classic_is_the_first_card_and_each_mode_applies_its_Classic_variant()
    {
        await using var f = ShellFixture.Create(firstRun: true);
        f.Shell.Theme.SystemColors = () => new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Dark };
        f.Shell.Theme.Apply();
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen, seconds: 30);
        var model = f.Shell.SetupModel!;
        var window = f.Shell.SetupWindowView!;
        await model.NextAsync(); // Get Started
        for (var i = 0; i < 4; i++)
        {
            ShellFixture.Pump();
        }

        // The window draws Classic's card first, chosen.
        var cards = window.FindControl<ItemsControl>("FamilyList")!.GetVisualDescendants().OfType<ToggleButton>().ToList();
        Assert.Equal(["Classic", "Harbor", "Graphite", "Daylight", "Ember"], cards.Select(AutomationProperties.GetName));
        Assert.True(cards[0].IsChecked);

        // Each mode on the Classic card previews Classic's variant, in the IDE, the window and the card's picture.
        model.Families.Single(c => c.Id == "harbor").SelectCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "harbor.dark");
        model.Families[0].SelectCommand.Execute(null);
        foreach (var (mode, theme, variant) in new[] { ("light", "light", ThemeVariant.Light), ("dark", "dark", ThemeVariant.Dark), ("highContrast", "highContrast", ThemeService.HighContrast) })
        {
            model.Modes.Single(m => m.Id == mode).SelectCommand.Execute(null);
            Assert.Equal("classic." + mode, model.ThemeSetting);
            await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == theme);
            Assert.Equal("classic", f.Shell.Theme.ActiveFamily);
            Assert.Equal(variant, Application.Current!.ActualThemeVariant);
            Assert.Equal(variant, window.ActualThemeVariant);
            Assert.Equal(variant, model.Families[0].Picture.Variant);
        }

        Assert.True(ThemeService.IsHighContrast(Application.Current!.ActualThemeVariant));
        Assert.Null(f.Shell.Workbench.Settings.GetLayerValue(ThemeService.SettingKey, Workbench.Settings.SettingScope.User)); // a preview

        // Match system: Classic following the OS (dark here); Next writes it, though it's the default, so the choice stays
        // the user's if the default changes again.
        model.Modes.Single(m => m.Id is null).SelectCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "dark");
        await model.NextAsync();
        Assert.Equal("classic", f.Shell.Workbench.Settings.GetLayerValue(ThemeService.SettingKey, Workbench.Settings.SettingScope.User)?.GetString());

        // Back, Classic High Contrast, Next: saved.
        await model.BackAsync();
        Assert.Equal("classic", model.SelectedFamily);
        Assert.True(model.IsMatchSystem);
        model.Modes.Single(m => m.Id == "highContrast").SelectCommand.Execute(null);
        await model.NextAsync();
        Assert.Equal("classic.highContrast", f.Shell.Workbench.Settings.GetString(ThemeService.SettingKey));
        Assert.Equal("highContrast", f.Shell.Theme.ActiveTheme);
        await model.FinishAsync(done: false);
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen && f.Shell.IsStartWindowOpen);
        f.Shell.StartWindowModel!.ContinueCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !f.Shell.IsStartWindowOpen);
    }

    [AvaloniaFact]
    public async Task Classic_Blue_and_a_value_from_before_show_on_the_Classic_card_and_are_kept()
    {
        await using var f = ShellFixture.Create(firstRun: true);
        var settings = f.Shell.Workbench.Settings;
        f.Shell.Theme.SystemColors = () => new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Light };
        f.Shell.Theme.Apply();
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen, seconds: 30);
        await f.Shell.SetupModel!.FinishAsync(done: false);
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen && f.Shell.IsStartWindowOpen);
        f.Shell.StartWindowModel!.ContinueCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !f.Shell.IsStartWindowOpen);

        // Classic Blue (chosen in Options): the Classic card, no mode, a note on where Blue is; Next keeps it.
        await settings.SetAsync(ThemeService.SettingKey, "classic.blue", Workbench.Settings.SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "blue");
        await f.RunAsync("help.setUp");
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen);
        var model = f.Shell.SetupModel!;
        Assert.Equal("classic", model.SelectedFamily);
        Assert.Equal("blue", model.SelectedVariant);
        Assert.True(model.IsClassicBlue);
        Assert.All(model.Modes, m => Assert.False(m.IsSelected));
        Assert.Equal(ThemeService.Blue, model.Families[0].Picture.Variant);
        Assert.Equal(ThemeService.HarborLight, model.Families.Single(c => c.Id == "harbor").Picture.Variant); // Harbor matches the system
        await model.NextAsync();
        Assert.Equal("classic.blue", settings.GetString(ThemeService.SettingKey));
        Assert.Equal("blue", f.Shell.Theme.ActiveTheme);

        // Another card has no Blue: it matches the system, as in Options; a mode on Classic leaves Blue.
        await model.BackAsync();
        Assert.True(model.IsClassicBlue);
        model.Families.Single(c => c.Id == "harbor").SelectCommand.Execute(null);
        Assert.True(model.IsMatchSystem);
        Assert.False(model.IsClassicBlue);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "harbor.light");
        model.Families[0].SelectCommand.Execute(null);
        model.Modes.Single(m => m.Id == "dark").SelectCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "dark");

        // Skip ends the preview: Blue stays.
        await model.FinishAsync(done: false);
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen && f.Shell.Theme.ActiveTheme == "blue");
        Assert.Equal("classic.blue", settings.GetString(ThemeService.SettingKey));

        // A value from before the families ("dark", Classic Dark) shows as Classic Dark, and Next doesn't rewrite it.
        await settings.SetAsync(ThemeService.SettingKey, "dark", Workbench.Settings.SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == "dark");
        await f.RunAsync("help.setUp");
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen);
        model = f.Shell.SetupModel!;
        Assert.Equal("classic", model.SelectedFamily);
        Assert.Equal("dark", model.SelectedVariant);
        await model.NextAsync();
        Assert.Equal("dark", settings.GetString(ThemeService.SettingKey));
        await model.FinishAsync(done: false);
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen);
    }


    // ------------------------------------------------------------------ the welcome page

    [AvaloniaFact]
    public async Task Where_the_user_comes_from_preselects_the_keys_and_marks_an_importer_and_saves_nothing()
    {
        await using var f = ShellFixture.Create(firstRun: true);
        var settings = f.Shell.Workbench.Settings;
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen, seconds: 30);
        var model = f.Shell.SetupModel!;
        var window = f.Shell.SetupWindowView!;
        for (var i = 0; i < 4; i++)
        {
            ShellFixture.Pump();
        }

        // The welcome page in the window: the logo, the four origins as cards with names, Get Started focused.
        var origins = window.FindControl<ItemsControl>("OriginList")!.GetVisualDescendants().OfType<ToggleButton>().ToList();
        Assert.Equal(["The classic IDE", "VS Code", "Rider or ReSharper", "Somewhere else"], origins.Select(AutomationProperties.GetName));
        Assert.All(origins, o => Assert.True(o.IsEffectivelyVisible && o.IsChecked == false));
        Assert.Equal("Get Started", window.FindControl<Button>("NextButton")!.Content);
        Assert.Equal("Use the defaults", window.FindControl<Button>("SkipButton")!.Content);
        Assert.True(window.FindControl<Button>("NextButton")!.IsFocused);

        // VS Code: its keys are chosen on the Keyboard step, and its importer is marked on Your settings. Nothing is saved
        // by saying where you come from.
        origins[1].Focus();
        Press(window, Key.Space);
        Assert.Equal("vscode", model.SelectedOrigin);
        Press(window, Key.Right, RawInputModifiers.Alt);
        await ShellFixture.PumpUntil(() => model.IsAppearance);
        Assert.Null(settings.GetLayerValue(BuiltIns.KeyboardSchemeSetting, SettingScope.User));
        await model.NextAsync();
        Assert.Equal("vscode", model.SelectedScheme);
        Assert.True(model.Schemes.Single(s => s.Id == "vscode").IsSelected);
        Assert.Null(settings.GetLayerValue(BuiltIns.KeyboardSchemeSetting, SettingScope.User));

        // The step can still choose another: Emacs, saved on Next, and shown again on Back (the origin applies once).
        model.Schemes.Single(s => s.Id == "emacs").SelectCommand.Execute(null);
        await model.NextAsync();
        Assert.Equal("emacs", settings.GetString(BuiltIns.KeyboardSchemeSetting));
        Assert.True(model.SuggestsVsCode);
        Assert.False(model.SuggestsVsSettings);
        for (var i = 0; i < 4; i++)
        {
            ShellFixture.Pump();
        }

        Assert.Contains("suggested", window.FindControl<Border>("VsCodeSection")!.Classes);
        Assert.DoesNotContain("suggested", window.FindControl<Border>("VsSettingsSection")!.Classes);
        Assert.Equal(1, window.GetVisualDescendants().OfType<TextBlock>().Count(t => t.IsEffectivelyVisible && t.Text == "Suggested for where you come from."));
        Assert.Null(settings.GetLayerValue("editor.tabSize", SettingScope.User)); // nothing imported by the mark
        await model.BackAsync();
        Assert.Equal("emacs", model.SelectedScheme);

        // Back to the welcome page: the classic IDE has the Default keys and the .vssettings importer; Rider, ReSharper's.
        await model.BackAsync();
        await model.BackAsync();
        Assert.Equal(SetupPage.Welcome, model.Page);
        Assert.True(model.Origins.Single(o => o.Id == "vscode").IsSelected);
        model.Origins.Single(o => o.Id == "classic").SelectCommand.Execute(null);
        Assert.True(model.SuggestsVsSettings);
        await model.NextAsync();
        await model.NextAsync();
        Assert.Equal("default", model.SelectedScheme);
        await model.NextAsync();
        Assert.Equal("default", settings.GetString(BuiltIns.KeyboardSchemeSetting));
        for (var i = 0; i < 4; i++)
        {
            ShellFixture.Pump();
        }

        Assert.Contains("suggested", window.FindControl<Border>("VsSettingsSection")!.Classes);
        Assert.True(window.FindControl<Button>("ImportVsSettingsButton")!.IsFocused);
        await model.BackAsync();
        await model.BackAsync();
        await model.BackAsync();
        model.Origins.Single(o => o.Id == "rider").SelectCommand.Execute(null);
        Assert.Null(model.SuggestedImport);
        await model.NextAsync();
        await model.NextAsync();
        Assert.Equal("resharper", model.SelectedScheme);

        // Somewhere else preselects nothing: the step shows what is saved.
        await model.BackAsync(); // saves ReSharper's
        await model.BackAsync();
        model.Origins.Single(o => o.Id == "other").SelectCommand.Execute(null);
        await model.NextAsync();
        await model.NextAsync();
        Assert.Equal("resharper", model.SelectedScheme);
        await model.FinishAsync(done: false);
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen && f.Shell.IsStartWindowOpen);
        f.Shell.StartWindowModel!.ContinueCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !f.Shell.IsStartWindowOpen);
    }

    // ------------------------------------------------------------------ the AI step (ADR 0096)

    [AvaloniaFact]
    public async Task The_AI_step_compares_the_three_ways_and_an_agent_connects_only_after_the_summary()
    {
        await using var f = ShellFixture.Create(firstRun: true);
        var settings = f.Shell.Workbench.Settings;
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen, seconds: 30);
        var model = f.Shell.SetupModel!;
        var window = f.Shell.SetupWindowView!;
        await ToAiAsync(model);
        for (var i = 0; i < 4; i++)
        {
            ShellFixture.Pump();
        }

        // Three ways, No AI chosen, each with the same four facts so they compare.
        Assert.Equal(["No AI", "A coding agent", "The built-in assistant"], model.AiPaths.Select(p => p.Title));
        Assert.True(model.AiPaths[0].IsSelected);
        Assert.All(model.AiPaths, p => Assert.Equal(["Who answers", "What it does", "What leaves this machine", "What you need"], p.Facts.Select(fact => fact.Label)));
        Assert.All(model.AiPaths, p => Assert.All(p.Facts, fact => Assert.False(string.IsNullOrWhiteSpace(fact.Value))));
        Assert.Equal("Nothing.", model.AiPaths[0].Facts[2].Value);
        Assert.Contains("its own provider", model.AiPaths[1].Facts[2].Value, StringComparison.Ordinal);
        Assert.Contains("never sent", model.AiPaths[2].Facts[2].Value, StringComparison.Ordinal);
        var cards = window.FindControl<ItemsControl>("AiPathList")!.GetVisualDescendants().OfType<ToggleButton>().ToList();
        Assert.Equal(["No AI", "A coding agent", "The built-in assistant"], cards.Select(AutomationProperties.GetName));
        Assert.True(cards[0].IsChecked);
        Assert.True(cards[0].IsFocused);
        Assert.All(cards, c => Assert.Contains("Who answers:", AutomationProperties.GetHelpText(c), StringComparison.Ordinal));
        Assert.False(window.FindControl<StackPanel>("AiAgentPanel")!.IsEffectivelyVisible);
        Assert.False(window.FindControl<StackPanel>("AiAssistantPanel")!.IsEffectivelyVisible);
        Snapshot(f, "first-run-setup-ai-none");

        // A coding agent, from the keyboard: the agents, why to run one from the IDE, and its two windows.
        Press(window, Key.Right);
        Assert.Equal(SetupAiPath.Agent, model.AiPath);
        for (var i = 0; i < 4; i++)
        {
            ShellFixture.Pump();
        }

        Assert.True(window.FindControl<StackPanel>("AiAgentPanel")!.IsEffectivelyVisible);
        Assert.Equal(["Claude", "Codex", "Gemini CLI", "GitHub Copilot", "More agents…"], model.AiAgents.Select(a => a.Title));
        Assert.Equal(["Anthropic", "OpenAI", "Google", "GitHub", "pi, OpenCode, goose"], model.AiAgents.Select(a => a.Description));
        Assert.All(model.AiAgents, a => Assert.False(a.IsSelected)); // an agent is chosen, never assumed
        Assert.Equal(5, model.AgentBenefits.Count);
        Assert.Contains("the IDE's tools", model.AgentBenefits[0], StringComparison.Ordinal);
        Assert.Contains("debugging", model.AgentBenefits[0], StringComparison.Ordinal);
        Assert.Contains("preview", model.AgentBenefits[1], StringComparison.Ordinal);
        Assert.Equal(["AI Console", "AI Chat"], model.AgentWindows.Select(w => w.Label));
        var shown = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Why run it from Codalus IDE and not from a terminal", shown);
        Assert.All(model.AgentBenefits, benefit => Assert.Contains(benefit, shown));
        Assert.All(model.AgentWindows, w => Assert.Contains(w.Value, shown));
        Assert.True(model.AiChanged);
        Assert.Equal(new SetupAiChoice(SetupAiPath.Agent), model.AiChoice);
        Snapshot(f, "first-run-setup-ai-agent");

        // Claude. Nothing is written or opened by choosing; the summary says what its button does.
        model.AiAgents.Single(a => a.Id == "claude").SelectCommand.Execute(null);
        Assert.Equal(new SetupAiChoice(SetupAiPath.Agent, Agent: "claude"), model.AiChoice);
        await model.NextAsync();
        Assert.Equal(SetupPage.Finish, model.Page);
        Assert.Equal("Connect Claude…", model.NextTitle);
        Assert.Equal("Claude, a coding agent (connects next)", model.Summary.Single(r => r.Label == "AI").Value);
        Assert.True(model.HasAiNext);
        Assert.Contains("Connect to Claude opens in the main window", model.AiNext, StringComparison.Ordinal);
        Assert.Null(settings.GetLayerValue(AiService.EnabledSetting, SettingScope.User));
        Assert.Null(settings.GetLayerValue(AcpAgents.AgentsSetting, SettingScope.User));
        Assert.False(f.Shell.ConnectAgent.IsOpen);
        Snapshot(f, "first-run-setup-summary-agent");

        // Back keeps the choice; More agents… and no agent at all both lead to the list.
        await model.BackAsync();
        Assert.True(model.AiAgents.Single(a => a.Id == "claude").IsSelected);
        model.AiAgents.Single(a => a.Id == SetupAiChoice.More).SelectCommand.Execute(null);
        await model.NextAsync();
        Assert.Equal("Choose an Agent…", model.NextTitle);
        Assert.Equal("A coding agent (you choose it next)", model.Summary.Single(r => r.Label == "AI").Value);
        await model.BackAsync();
        model.AiAgents.Single(a => a.Id == "claude").SelectCommand.Execute(null);
        await model.NextAsync();

        // The summary's button: the setup closes, Connect to Claude opens in the main window, and the start window waits.
        await model.NextAsync();
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen && f.Shell.ConnectAgent.IsOpen);
        Assert.True(model.WasDone);
        Assert.Equal("Connect to Claude", f.Shell.ConnectAgent.Title);
        Assert.False(f.Shell.IsStartWindowOpen);
        Assert.Null(settings.GetLayerValue(AiService.EnabledSetting, SettingScope.User)); // Connect's own consent and test come first

        // Cancel in Connect: nothing was turned on or written, and the start window follows.
        f.Shell.ConnectAgent.Cancel();
        await ShellFixture.PumpUntil(() => f.Shell.IsStartWindowOpen);
        Assert.Null(settings.GetLayerValue(AiService.EnabledSetting, SettingScope.User));
        Assert.Null(settings.GetLayerValue(AcpAgents.AgentsSetting, SettingScope.User));
        f.Shell.StartWindowModel!.ContinueCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !f.Shell.IsStartWindowOpen);
    }

    [AvaloniaFact]
    public async Task Skipping_or_closing_the_setup_applies_nothing_of_the_AI_step()
    {
        await using var f = ShellFixture.Create(firstRun: true);
        var settings = f.Shell.Workbench.Settings;
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen, seconds: 30);
        var model = f.Shell.SetupModel!;
        await ToAiAsync(model);
        model.AiPaths.Single(p => p.Path == SetupAiPath.Agent).SelectCommand.Execute(null);
        model.AiAgents.Single(a => a.Id == "codex").SelectCommand.Execute(null);
        Assert.True(model.AiChanged);

        // Esc on the step.
        Press(f.Shell.SetupWindowView!, Key.Escape);
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen && f.Shell.IsStartWindowOpen);
        Assert.False(model.WasDone);
        Assert.False(f.Shell.ConnectAgent.IsOpen);
        Assert.False(f.Shell.Palette.IsOpen);
        Assert.Null(settings.GetLayerValue(AiService.EnabledSetting, SettingScope.User));
        f.Shell.StartWindowModel!.ContinueCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !f.Shell.IsStartWindowOpen);

        // From Help, with the built-in assistant chosen: closing the window on the summary applies nothing either.
        await f.RunAsync("help.setUp");
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen);
        model = f.Shell.SetupModel!;
        await ToAiAsync(model);
        Assert.Equal(SetupAiPath.None, model.AiPath);
        model.AiPaths.Single(p => p.Path == SetupAiPath.Assistant).SelectCommand.Execute(null);
        model.AiInlineSuggestions = true;
        await model.NextAsync();
        Assert.Equal("Choose a Provider…", model.NextTitle);
        Assert.Equal("The built-in assistant (you choose the provider next)", model.Summary.Single(r => r.Label == "AI").Value);
        f.Shell.SetupWindowView!.Close();
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen);
        ShellFixture.Pump();
        Assert.False(model.WasDone);
        Assert.False(f.Shell.Palette.IsOpen);
        Assert.Null(settings.GetLayerValue(AiService.EnabledSetting, SettingScope.User));
        Assert.Null(settings.GetLayerValue(AiCompletions.EnabledSetting, SettingScope.User));
    }

    [AvaloniaFact]
    public async Task The_built_in_assistant_turns_on_with_its_provider_and_only_the_suggestions_that_were_ticked()
    {
        await using var server = MockModelServers.OpenAI("Hello.");
        await using var f = ShellFixture.Create(builtInPlugins: ShellFixture.PluginBin("Codalus.Plugins.OpenAI"), firstRun: true);
        var settings = f.Shell.Workbench.Settings;
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen, seconds: 30);
        var model = f.Shell.SetupModel!;
        var window = f.Shell.SetupWindowView!;
        await ToAiAsync(model);

        // The providers the plugins declare, none chosen; the two as-you-type switches, both off.
        model.AiPaths.Single(p => p.Path == SetupAiPath.Assistant).SelectCommand.Execute(null);
        for (var i = 0; i < 4; i++)
        {
            ShellFixture.Pump();
        }

        Assert.True(window.FindControl<StackPanel>("AiAssistantPanel")!.IsEffectivelyVisible);
        Assert.False(window.FindControl<StackPanel>("AiAgentPanel")!.IsEffectivelyVisible);
        Assert.Equal(["ollama", "openai"], model.AiProviders.Select(p => p.Id).Order());
        Assert.Equal("On this machine", model.AiProviders.Single(p => p.Id == "ollama").Description);
        Assert.All(model.AiProviders, p => Assert.False(p.IsSelected));
        Assert.False(model.HasNoProviders);
        Assert.False(window.FindControl<CheckBox>("InlineSuggestionsBox")!.IsChecked);
        Assert.False(window.FindControl<CheckBox>("NextEditBox")!.IsChecked);
        Snapshot(f, "first-run-setup-ai-assistant");

        // The OpenAI-compatible provider, with inline suggestions ticked and next edit left off.
        model.AiProviders.Single(p => p.Id == "openai").SelectCommand.Execute(null);
        window.FindControl<CheckBox>("InlineSuggestionsBox")!.IsChecked = true;
        Assert.Equal(new SetupAiChoice(SetupAiPath.Assistant, Provider: "openai", InlineSuggestions: true), model.AiChoice);
        await model.NextAsync();
        var provider = model.AiProviders.Single(p => p.Id == "openai").Title;
        Assert.Equal($"Set Up {provider}…", model.NextTitle);
        Assert.Equal($"The built-in assistant with {provider}, inline suggestions", model.Summary.Single(r => r.Label == "AI").Value);
        Assert.Contains("credential store", model.AiNext, StringComparison.Ordinal);
        Assert.Null(settings.GetLayerValue(AiService.EnabledSetting, SettingScope.User));

        // The provider's endpoint and key are there (a model server stand-in), so what's left to ask is the model.
        await settings.SetAsync("ai.openai.endpoint", server.BaseUrl + "/v1", SettingScope.User, CancellationToken.None);
        await f.Shell.Secrets.StoreAsync("codalus.openai", "apiKey:openai", MockModelServers.Key, CancellationToken.None);
        await model.NextAsync();
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen && f.Shell.Palette.IsOpen, seconds: 60);
        Assert.True(settings.GetLayerValue(AiService.EnabledSetting, SettingScope.User)!.Value.GetBoolean());
        Assert.Equal("openai", settings.GetString(AiService.ProviderSetting));
        Assert.True(settings.GetLayerValue(AiCompletions.EnabledSetting, SettingScope.User)!.Value.GetBoolean());
        Assert.Null(settings.GetLayerValue(AiNextEdit.EnabledSetting, SettingScope.User));
        Assert.False(f.Shell.IsStartWindowOpen); // it waits for the model
        var row = f.Shell.Palette.Rows[0];
        f.Shell.Palette.Accept(row);
        await ShellFixture.PumpUntil(() => f.Shell.IsStartWindowOpen);
        Assert.False(string.IsNullOrEmpty(settings.GetString(AiService.ModelSetting)));
        Assert.Equal(AiState.Ready, f.Shell.Ai.State);
        f.Shell.StartWindowModel!.ContinueCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !f.Shell.IsStartWindowOpen);

        // Run again from Help: the step shows what is on, and leaving it as it is changes nothing.
        await f.RunAsync("help.setUp");
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen);
        model = f.Shell.SetupModel!;
        await ToAiAsync(model);
        Assert.Equal(SetupAiPath.Assistant, model.AiPath);
        Assert.True(model.AiProviders.Single(p => p.Id == "openai").IsSelected);
        Assert.True(model.AiInlineSuggestions);
        Assert.False(model.AiNextEditSuggestions);
        Assert.False(model.AiChanged);
        await model.NextAsync();
        Assert.Equal("Done", model.NextTitle);
        Assert.False(model.HasAiNext);

        // No AI, chosen and done: AI goes off; the provider and the switch stay for when it's turned on again.
        await model.BackAsync();
        model.AiPaths.Single(p => p.Path == SetupAiPath.None).SelectCommand.Execute(null);
        Assert.True(model.AiChanged);
        await model.NextAsync();
        Assert.Equal("Off", model.Summary.Single(r => r.Label == "AI").Value);
        await model.NextAsync();
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen);
        await ShellFixture.PumpUntil(() => settings.GetLayerValue(AiService.EnabledSetting, SettingScope.User) is { ValueKind: System.Text.Json.JsonValueKind.False });
        Assert.Equal("openai", settings.GetString(AiService.ProviderSetting));
        Assert.True(settings.GetBoolean(AiCompletions.EnabledSetting));
        Assert.False(f.Shell.IsStartWindowOpen); // from Help
    }

    [AvaloniaFact]
    public async Task A_connected_agent_shows_connected_and_choosing_it_again_only_turns_AI_on()
    {
        await using var f = ShellFixture.Create(firstRun: true);
        var settings = f.Shell.Workbench.Settings;
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen, seconds: 30);
        await f.Shell.SetupModel!.FinishAsync(done: false);
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen && f.Shell.IsStartWindowOpen);
        f.Shell.StartWindowModel!.ContinueCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !f.Shell.IsStartWindowOpen);

        // Two agents in the user's settings (one a template's, one written by hand), AI on, the hand-written one chatting.
        await settings.SetMemberAsync(AcpAgents.AgentsSetting, "claude", AcpAgentCatalog.Entry("npx", ["-y", "stand-in"]), SettingScope.User, CancellationToken.None);
        await settings.SetMemberAsync(AcpAgents.AgentsSetting, "mine", AcpAgentCatalog.Entry("mine-acp", []), SettingScope.User, CancellationToken.None);
        await settings.SetAsync(AiService.EnabledSetting, true, SettingScope.User, CancellationToken.None);
        await settings.SetAsync(AcpAgents.ChatAgentSetting, "mine", SettingScope.User, CancellationToken.None);
        await f.RunAsync("help.setUp");
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen);
        var model = f.Shell.SetupModel!;
        await ToAiAsync(model);
        Assert.Equal(new SetupAiChoice(SetupAiPath.Agent, Agent: "mine"), model.AiChoice);
        Assert.False(model.AiChanged);
        Assert.Equal(["Claude", "Codex", "Gemini CLI", "GitHub Copilot", "mine", "More agents…"], model.AiAgents.Select(a => a.Title));
        Assert.Equal("Connected", model.AiAgents.Single(a => a.Id == "claude").Description);
        Assert.Equal("Connected", model.AiAgents.Single(a => a.Id == "mine").Description);
        Assert.True(model.AiAgents.Single(a => a.Id == "mine").IsSelected);
        await model.NextAsync();
        Assert.Equal("mine, connected", model.Summary.Single(r => r.Label == "AI").Value);
        Assert.Equal("Done", model.NextTitle);

        // Claude, connected already: Done makes it the chat's agent; no Connect.
        await model.BackAsync();
        model.AiAgents.Single(a => a.Id == "claude").SelectCommand.Execute(null);
        await model.NextAsync();
        Assert.Equal("Claude, connected", model.Summary.Single(r => r.Label == "AI").Value);
        Assert.Equal("Done", model.NextTitle);
        Assert.False(model.HasAiNext);
        await model.NextAsync();
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen);
        await ShellFixture.PumpUntil(() => settings.GetString(AcpAgents.ChatAgentSetting) == "claude");
        Assert.False(f.Shell.ConnectAgent.IsOpen);
        Assert.True(settings.GetBoolean(AiService.EnabledSetting));
    }

    /// <summary>From the welcome page (a first start) or Appearance to the AI step, saving each step as it is.</summary>
    private static async Task ToAiAsync(SetupViewModel model)
    {
        while (!model.IsAi)
        {
            await model.NextAsync();
        }
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

    /// <summary>Each segment's text and color, line by line.</summary>
    private static List<(string Text, Color Color)> Brushes(IReadOnlyList<ThemePictureCodeLine> code) =>
        [.. code.SelectMany(line => line.Segments).Select(segment => (segment.Text, ((ISolidColorBrush)segment.Brush).Color))];

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
