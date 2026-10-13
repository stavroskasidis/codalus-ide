using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using DaedalusStudio.Workbench.Settings;

namespace DaedalusStudio.Shell.Tests;

/// <summary>Settings sync (M23b) with a folder and with a Git repository; "another machine" edits the synced file.</summary>
public sealed class SettingsSyncTests
{
    private static async Task OtherMachineAsync(string file, Action<JsonObject> change)
    {
        var document = JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
        change(document);
        await File.WriteAllTextAsync(file, document.ToJsonString());
    }

    [AvaloniaFact]
    public async Task A_folder_syncs_both_ways_and_a_change_on_both_sides_asks()
    {
        await using var f = ShellFixture.Create();
        var shell = f.Shell;
        var settings = shell.Workbench.Settings;
        var folder = Path.Combine(f.UserData, "cloud-drive");
        Directory.CreateDirectory(folder);
        var synced = Path.Combine(folder, SettingsSync.FileName);
        await settings.SetAsync("editor.fontSize", 17, SettingScope.User, CancellationToken.None);
        await settings.SetAsync(ShellHost.SyncIgnoredSetting, JsonDocumentOf("""["editor.tabSize"]"""), SettingScope.User, CancellationToken.None);
        await settings.SetAsync("editor.tabSize", 8, SettingScope.User, CancellationToken.None);

        // Turned on: this machine's settings go to the folder, without the machine-only ones.
        await shell.TurnOnSettingsSyncAsync(folder);
        var text = await File.ReadAllTextAsync(synced, TestContext.Current.CancellationToken);
        Assert.Contains("\"editor.fontSize\": 17", text, StringComparison.Ordinal);
        Assert.DoesNotContain("settingsSync", text, StringComparison.Ordinal);
        Assert.DoesNotContain("editor.tabSize", text, StringComparison.Ordinal);

        // Another machine changed them: they come here; the ignored setting stays.
        await OtherMachineAsync(synced, d =>
        {
            d["settings"]!["editor.fontSize"] = 19;
            d["keybindings"] = new JsonArray(new JsonObject { ["key"] = "ctrl+alt+9", ["command"] = "editor.action.formatDocument" });
            d["snippets"] = new JsonObject { ["csharp.json"] = "{}" };
        });
        await shell.SyncSettingsAsync();
        Assert.Equal(19, settings.GetInt32("editor.fontSize"));
        Assert.Equal(8, settings.GetInt32("editor.tabSize"));
        Assert.Equal(folder, settings.GetString(ShellHost.SyncTargetSetting));
        Assert.Contains(shell.Workbench.Keybindings.UserRules, r => r.Command == "editor.action.formatDocument");
        Assert.True(File.Exists(Path.Combine(shell.Workbench.Paths.Config, "snippets", "csharp.json")));

        // Changed here: it goes there. Nothing new: up to date.
        await settings.SetAsync("editor.fontSize", 21, SettingScope.User, CancellationToken.None);
        await shell.SyncSettingsAsync();
        Assert.Contains("\"editor.fontSize\": 21", await File.ReadAllTextAsync(synced, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        var unchanged = File.GetLastWriteTimeUtc(synced);
        await shell.SyncSettingsAsync();
        Assert.Equal(unchanged, File.GetLastWriteTimeUtc(synced));

        // Changed on both sides: the user chooses; Keep Mine sends this machine's.
        await settings.SetAsync("editor.fontSize", 22, SettingScope.User, CancellationToken.None);
        await OtherMachineAsync(synced, d => d["settings"]!["editor.fontSize"] = 30);
        var sync = shell.SyncSettingsAsync();
        var question = await WaitForNotificationAsync(shell, "changed both here and in");
        shell.Workbench.Notifications.Choose(question, "Keep Mine");
        await sync;
        Assert.Equal(22, settings.GetInt32("editor.fontSize"));
        Assert.Contains("\"editor.fontSize\": 22", await File.ReadAllTextAsync(synced, TestContext.Current.CancellationToken), StringComparison.Ordinal);

        // Again, and Use Synced takes theirs.
        await settings.SetAsync("editor.fontSize", 23, SettingScope.User, CancellationToken.None);
        await OtherMachineAsync(synced, d => d["settings"]!["editor.fontSize"] = 31);
        sync = shell.SyncSettingsAsync();
        shell.Workbench.Notifications.Choose(await WaitForNotificationAsync(shell, "changed both here and in"), "Use Synced");
        await sync;
        Assert.Equal(31, settings.GetInt32("editor.fontSize"));
    }

    [AvaloniaFact]
    public async Task A_git_repository_receives_commits_and_another_clones_changes_come_back()
    {
        await using var f = ShellFixture.Create();
        var shell = f.Shell;
        var settings = shell.Workbench.Settings;
        var remote = Path.Combine(f.UserData, "remote", "settings.git");
        Directory.CreateDirectory(remote);
        await Git(remote, "init", "--quiet", "--bare", "--initial-branch=main");

        await settings.SetAsync("editor.fontSize", 15, SettingScope.User, CancellationToken.None);
        await shell.TurnOnSettingsSyncAsync(remote);
        Assert.Contains("Settings from", await Git(remote, "log", "--format=%s", "main"), StringComparison.Ordinal);

        // Another machine pushes a change through its own clone.
        var other = Path.Combine(f.UserData, "other-machine");
        await Git(f.UserData, "clone", "--quiet", remote, other);
        await OtherMachineAsync(Path.Combine(other, SettingsSync.FileName), d => d["settings"]!["editor.fontSize"] = 18);
        await Git(other, "-c", "user.name=Other", "-c", "user.email=other@example.invalid", "commit", "--quiet", "-am", "From the other machine");
        await Git(other, "push", "--quiet");

        await shell.SyncSettingsAsync();
        Assert.Equal(18, settings.GetInt32("editor.fontSize"));

        await settings.SetAsync("editor.fontSize", 16, SettingScope.User, CancellationToken.None);
        await shell.SyncSettingsAsync();
        await Git(other, "pull", "--quiet");
        Assert.Contains("\"editor.fontSize\": 16", await File.ReadAllTextAsync(Path.Combine(other, SettingsSync.FileName), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(3, (await Git(remote, "log", "--format=%s", "main")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    private static System.Text.Json.JsonElement JsonDocumentOf(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<Workbench.Notifications.Notification> WaitForNotificationAsync(ShellHost shell, string text)
    {
        Workbench.Notifications.Notification? found = null;
        await ShellFixture.PumpUntil(() => (found = shell.Workbench.Notifications.Items.LastOrDefault(n => n.Message.Contains(text, StringComparison.Ordinal))) is not null);
        return found!;
    }

    private static async Task<string> Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
        return output;
    }
}
