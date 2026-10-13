using Avalonia.Headless.XUnit;
using DaedalusStudio.Workbench.Plugins;
using DaedalusStudio.Workbench.Settings;

namespace DaedalusStudio.Shell.Tests;

/// <summary>Profiles and turned-off plugins (M23b).</summary>
public sealed class ProfileTests
{
    [AvaloniaFact]
    public async Task Profiles_keep_their_own_settings_shortcuts_snippets_and_layouts_and_switch_back()
    {
        await using var f = ShellFixture.Create();
        var shell = f.Shell;
        var settings = shell.Workbench.Settings;
        await settings.SetAsync("editor.fontSize", 17, SettingScope.User, CancellationToken.None);
        await shell.Workbench.SaveUserKeybindingsAsync("""[ { "key": "ctrl+alt+9", "command": "editor.action.formatDocument" } ]""", CancellationToken.None);
        shell.Workbench.LoadUserKeybindings();
        var snippets = Path.Combine(shell.Workbench.Paths.Config, "snippets");
        Directory.CreateDirectory(snippets);
        await File.WriteAllTextAsync(Path.Combine(snippets, "csharp.json"), "{}", TestContext.Current.CancellationToken);
        await shell.SaveWindowLayoutAsync("Mine");
        Assert.Equal(ProfileStore.DefaultName, shell.Profiles.Active);
        Assert.DoesNotContain(shell.StatusBar.Left, i => i.Item.Id == "daedalus.profile");

        // A new, empty profile: the defaults, named in the status bar.
        await shell.NewProfileAsync("Work", copyActive: false);
        Assert.Equal("Work", shell.Profiles.Active);
        Assert.Equal(13, settings.GetInt32("editor.fontSize"));
        Assert.DoesNotContain(shell.Workbench.Keybindings.UserRules, r => r.Command == "editor.action.formatDocument");
        Assert.False(Directory.Exists(snippets));
        Assert.Empty(shell.SavedLayouts());
        Assert.Equal("Work", shell.Workbench.Context.GetValue("profile"));
        ShellFixture.Pump();
        Assert.Contains(shell.StatusBar.Left, i => i.Item.Id == "daedalus.profile" && i.Text.Contains("Work", StringComparison.Ordinal));
        await settings.SetAsync("editor.fontSize", 20, SettingScope.User, CancellationToken.None);

        // Back to Default: everything as it was; and Work kept its own.
        await shell.SwitchProfileAsync(ProfileStore.DefaultName);
        Assert.Equal(17, settings.GetInt32("editor.fontSize"));
        Assert.Contains(shell.Workbench.Keybindings.UserRules, r => r.Command == "editor.action.formatDocument");
        Assert.True(File.Exists(Path.Combine(snippets, "csharp.json")));
        Assert.Contains("Mine", shell.SavedLayouts().Keys);
        ShellFixture.Pump();
        Assert.DoesNotContain(shell.StatusBar.Left, i => i.Item.Id == "daedalus.profile");
        await shell.SwitchProfileAsync("Work");
        Assert.Equal(20, settings.GetInt32("editor.fontSize"));

        // A copy of the active profile, without switching; names are checked; Default and the active one stay.
        await shell.NewProfileAsync("Copy", copyActive: true, switchTo: false);
        Assert.Contains("\"editor.fontSize\": 20", await File.ReadAllTextAsync(Path.Combine(shell.Profiles.Folder("Copy"), "settings.json"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.NotNull(shell.Profiles.Validate("copy"));
        Assert.NotNull(shell.Profiles.Validate("a/b"));
        Assert.Equal(["Default", "Copy", "Work"], shell.Profiles.Names);
        await shell.DeleteProfileAsync("Work", confirm: false);
        await shell.DeleteProfileAsync("Copy", confirm: false);
        Assert.Equal(["Default", "Work"], shell.Profiles.Names);

        // Exported and imported as a new profile.
        var file = Path.Combine(f.UserData, "work-profile.json");
        await shell.ExportSettingsAsync(file);
        Assert.Contains("\"profile\": \"Work\"", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        await shell.SwitchProfileAsync(ProfileStore.DefaultName);
        await shell.ImportProfileAsync(file, "Imported");
        Assert.Equal("Imported", shell.Profiles.Active);
        Assert.Equal(20, settings.GetInt32("editor.fontSize"));
    }

    [AvaloniaFact]
    public async Task A_turned_off_plugin_is_discovered_but_not_loaded_and_the_plugins_window_turns_it_back_on()
    {
        var userData = Path.Combine(DaedalusStudio.Platform.RealPath.Resolve(Path.GetTempPath()), "daedalus-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(userData, "config"));
        await File.WriteAllTextAsync(Path.Combine(userData, "config", "settings.json"), """{ "plugins.disabled": ["daedalus.welcome"] }""", TestContext.Current.CancellationToken);
        await using var f = ShellFixture.Create(userData);
        f.Shell.OnInteractive();
        ShellFixture.Pump();

        var welcome = f.Shell.Plugins.Plugins.Single(p => p.Id == "daedalus.welcome");
        Assert.Equal(PluginState.Disabled, welcome.State);
        Assert.Null(f.Shell.Workbench.Commands.Find("welcome.show"));
        Assert.True(f.Shell.Plugins.IsTurnedOff("daedalus.welcome"));

        var row = f.Shell.PluginList.Plugins.Single(r => r.Id == "daedalus.welcome");
        Assert.Equal("Turn On", row.ToggleTitle);
        row.ToggleCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !f.Shell.Plugins.IsTurnedOff("daedalus.welcome"));

        // The Plugins window refreshes after the setting is saved.
        await ShellFixture.PumpUntil(() => f.Shell.PluginList.Plugins.Single(r => r.Id == "daedalus.welcome").State.Contains("turned on from the next start", StringComparison.Ordinal));
    }
}
