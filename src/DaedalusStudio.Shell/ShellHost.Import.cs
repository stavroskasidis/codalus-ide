using System.Text.Json;
using System.Text.Json.Nodes;
using DaedalusStudio.Workbench.Settings;

namespace DaedalusStudio.Shell;

/// <summary>
/// Tools → Import Settings from VS Code (M23a, <see cref="VsCodeImport"/>): its user settings that are ours, its color
/// customizations, and its key bindings whose commands exist here, after the user agrees. Every skipped key is logged
/// with why.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>Imports from <paramref name="folder"/> (VS Code's user folder; null: the one on this machine).</summary>
    public async Task ImportVsCodeSettingsAsync(string? folder = null, bool confirm = true)
    {
        folder ??= VsCodeImport.UserFolder(Workbench.Platform, Environment.GetEnvironmentVariable);
        if (folder is null)
        {
            Workbench.Notifications.Info("VS Code's user settings weren't found on this machine (VS Code, Insiders or VSCodium).");
            return;
        }

        ImportResult<(string Key, JsonElement Value)> settings;
        ImportResult<JsonElement> keybindings;
        try
        {
            var settingsFile = Path.Combine(folder, "settings.json");
            var keybindingsFile = Path.Combine(folder, "keybindings.json");
            settings = File.Exists(settingsFile) ? VsCodeImport.Settings(await File.ReadAllTextAsync(settingsFile).ConfigureAwait(true), Workbench.Settings.Find) : new([], []);
            keybindings = File.Exists(keybindingsFile) ? VsCodeImport.Keybindings(await File.ReadAllTextAsync(keybindingsFile).ConfigureAwait(true), c => Workbench.Commands.Find(c) is not null) : new([], []);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error($"VS Code's settings couldn't be read ({folder}): {ex.Message}");
            return;
        }

        if (settings.Imported.Count == 0 && keybindings.Imported.Count == 0)
        {
            Workbench.Notifications.Info($"Nothing in {folder} applies to Daedalus Studio.");
            return;
        }

        if (confirm && await Workbench.Notifications.Info(
                $"Import {settings.Imported.Count} settings and {keybindings.Imported.Count} keyboard shortcuts from VS Code ({folder})? They replace yours for the same settings.", "Import").Result.ConfigureAwait(true) != "Import")
        {
            return;
        }

        foreach (var (key, value) in settings.Imported)
        {
            await Workbench.Settings.SetAsync(key, value, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        }

        if (keybindings.Imported.Count > 0)
        {
            var current = File.Exists(Paths.KeybindingsFile) ? await File.ReadAllTextAsync(Paths.KeybindingsFile).ConfigureAwait(true) : "[]";
            var array = JsonNode.Parse(current, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonArray ?? [];
            foreach (var entry in keybindings.Imported)
            {
                array.Add(JsonNode.Parse(entry.GetRawText()));
            }

            await Workbench.SaveUserKeybindingsAsync(array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None).ConfigureAwait(true);
            Workbench.LoadUserKeybindings();
        }

        Workbench.Log.AppendLine($"[import] From {folder}: {settings.Imported.Count} settings, {keybindings.Imported.Count} keyboard shortcuts.");
        foreach (var (name, reason) in settings.Skipped.Concat(keybindings.Skipped))
        {
            Workbench.Log.AppendLine($"[import] Skipped {name}: {reason}.");
        }

        var skipped = settings.Skipped.Count + keybindings.Skipped.Count;
        Workbench.Notifications.Info($"Imported {settings.Imported.Count} settings and {keybindings.Imported.Count} keyboard shortcuts from VS Code."
            + (skipped > 0 ? $" {skipped} didn't apply here (the Daedalus Studio log lists them)." : string.Empty));
    }
}
