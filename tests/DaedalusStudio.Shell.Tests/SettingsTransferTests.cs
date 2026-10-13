using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using DaedalusStudio.Workbench.Settings;

namespace DaedalusStudio.Shell.Tests;

/// <summary>Tools → Export Settings and Import Settings (M23a): settings, shortcuts and layouts in one file, and back.</summary>
public sealed class SettingsTransferTests
{
    [AvaloniaFact]
    public async Task Exported_settings_shortcuts_and_layouts_import_back_and_unknown_settings_are_skipped()
    {
        await using var f = ShellFixture.Create();
        var settings = f.Shell.Workbench.Settings;
        await settings.SetAsync("editor.fontSize", 17, SettingScope.User, CancellationToken.None);
        await f.Shell.Workbench.SaveUserKeybindingsAsync("""[ { "key": "ctrl+alt+9", "command": "editor.action.formatDocument" } ]""", CancellationToken.None);
        await f.Shell.SaveWindowLayoutAsync("Mine");
        var file = Path.Combine(f.UserData, "exported-settings.json");
        await f.Shell.ExportSettingsAsync(file);

        // Changed meanwhile, then imported: as they were.
        await settings.SetAsync("editor.fontSize", 11, SettingScope.User, CancellationToken.None);
        await f.Shell.Workbench.SaveUserKeybindingsAsync("[]", CancellationToken.None);
        await f.Shell.DeleteWindowLayoutAsync("Mine");

        // A setting another version had: skipped and logged.
        var json = JsonNode.Parse(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken))!.AsObject();
        json["settings"]!["some.futureSetting"] = true;
        await File.WriteAllTextAsync(file, json.ToJsonString(), TestContext.Current.CancellationToken);

        await f.Shell.ImportSettingsAsync(file, ShellHost.SettingsParts.All);
        await ShellFixture.PumpUntil(() => settings.GetInt32("editor.fontSize") == 17);
        Assert.Contains("ctrl+alt+9", await File.ReadAllTextAsync(f.Shell.Paths.KeybindingsFile, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Contains("Mine", f.Shell.SavedLayouts().Keys);
        Assert.Contains("Skipped the setting some.futureSetting", f.Shell.Workbench.Log.GetText(), StringComparison.Ordinal);

        // Only the layouts: the settings stay.
        await settings.SetAsync("editor.fontSize", 12, SettingScope.User, CancellationToken.None);
        await f.Shell.ImportSettingsAsync(file, ShellHost.SettingsParts.Layouts);
        ShellFixture.Pump();
        Assert.Equal(12, settings.GetInt32("editor.fontSize"));

        // Not an export: says so.
        var other = Path.Combine(f.UserData, "other.json");
        await File.WriteAllTextAsync(other, "{ \"editor.fontSize\": 30 }", TestContext.Current.CancellationToken);
        await f.Shell.ImportSettingsAsync(other, ShellHost.SettingsParts.All);
        Assert.Equal(12, settings.GetInt32("editor.fontSize"));
    }
}
