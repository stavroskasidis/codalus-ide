using System.Text.Json;
using System.Text.Json.Nodes;
using Codalus.Shell.Docking;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Settings;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// Tools → Export Settings and Import Settings (M23a, a classic IDE's Import and Export Settings): the user's settings,
/// keyboard shortcuts and saved window layouts in one JSON file, <c>{ "codalusSettings": 1, "settings": {…},
/// "keybindings": […], "layouts": {…} }</c>. Importing asks which of the three to take; settings are validated one by one
/// (an unknown or invalid one is skipped and logged), shortcuts replace the user's, layouts are added.
/// </summary>
public sealed partial class ShellHost
{
    private static readonly JsonDocumentOptions s_jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Files people read: indented, and "+" in a shortcut written as "+" (the default encoder escapes it).</summary>
    private static readonly JsonSerializerOptions s_readableJson = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>What an import takes from the file.</summary>
    [Flags]
    public enum SettingsParts
    {
        Settings = 1,
        Keybindings = 2,
        Layouts = 4,
        All = Settings | Keybindings | Layouts,
    }

    public async Task ExportSettingsAsync(string? path = null)
    {
        path ??= Window is { } window ? await window.SaveFileAsync("Export Settings", "codalus-settings.json", "Codalus IDE settings", ["*.json"]).ConfigureAwait(true) : null;
        if (path is null)
        {
            return;
        }

        var export = new JsonObject
        {
            ["codalusSettings"] = 1,
            ["profile"] = Profiles.Active,
            ["settings"] = ReadNode(Paths.SettingsFile) as JsonObject ?? [],
            ["keybindings"] = ReadNode(Paths.KeybindingsFile) as JsonArray ?? [],
            ["layouts"] = ReadNode(LayoutsFile) as JsonObject ?? [],
            ["windowLayout"] = JsonNode.Parse(CurrentWindowLayout()),
        };
        await File.WriteAllTextAsync(path, export.ToJsonString(s_readableJson)).ConfigureAwait(true);
        Workbench.Notifications.Info(Localizer.Format("msg.settingsTransfer.exportedYourSettingsKeyboardShortcutsAnd", "Exported your settings, keyboard shortcuts and window layouts to {0}.", path));
    }

    /// <summary>Imports an export's settings, shortcuts and layouts; returns what it said, or null when cancelled.</summary>
    public async Task<string?> ImportSettingsAsync(string? path = null, SettingsParts? parts = null)
    {
        path ??= Window is { } window ? await window.PickFileAsync("Import Settings", "Codalus IDE settings", ["*.json"]).ConfigureAwait(true) : null;
        if (path is null)
        {
            return null;
        }

        JsonObject file;
        try
        {
            file = JsonNode.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(true), documentOptions: s_jsonc) as JsonObject
                ?? throw new JsonException("The file isn't a JSON object.");
            if (file["codalusSettings"] is null)
            {
                throw new JsonException("It isn't a Codalus IDE settings export (no \"codalusSettings\").");
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Said(Localizer.Format("msg.settingsTransfer.theSettingsCouldntBeImportedFrom", "The settings couldn't be imported from {0}: {1}", path, ex.Message), error: true);
        }

        if (parts is null)
        {
            var items = new[]
            {
                new QuickPickItem("Everything") { Detail = "Settings, keyboard shortcuts and window layouts", Tag = SettingsParts.All },
                new QuickPickItem("Settings") { Tag = SettingsParts.Settings },
                new QuickPickItem("Keyboard shortcuts") { Detail = "They replace yours", Tag = SettingsParts.Keybindings },
                new QuickPickItem("Window layouts") { Tag = SettingsParts.Layouts },
            };
            parts = await Palette.ShowAsync(items, "Import from " + Path.GetFileName(path)).ConfigureAwait(true) is { Tag: SettingsParts picked } ? picked : null;
            if (parts is null)
            {
                return null;
            }
        }

        var imported = new List<string>();
        if (parts.Value.HasFlag(SettingsParts.Settings) && file["settings"] is JsonObject settings)
        {
            var count = 0;
            foreach (var (key, node) in settings)
            {
                using var value = JsonDocument.Parse(node?.ToJsonString() ?? "null");
                if (Workbench.Settings.Find(key) is not { } descriptor || descriptor.Validate(value.RootElement) is { } error)
                {
                    Workbench.Log.AppendLine($"[import] Skipped the setting {key}: {(Workbench.Settings.Find(key) is null ? "not a setting of this version" : "invalid")}.");
                    continue;
                }

                await Workbench.Settings.SetAsync(key, value.RootElement.Clone(), SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                count++;
            }

            imported.Add($"{count} settings");
        }

        if (parts.Value.HasFlag(SettingsParts.Keybindings) && file["keybindings"] is JsonArray keybindings)
        {
            await Workbench.SaveUserKeybindingsAsync(keybindings.ToJsonString(s_readableJson), CancellationToken.None).ConfigureAwait(true);
            Workbench.LoadUserKeybindings();
            imported.Add($"{keybindings.Count} keyboard shortcuts");
        }

        if (parts.Value.HasFlag(SettingsParts.Layouts) && file["layouts"] is JsonObject layouts)
        {
            var saved = SavedLayouts();
            var added = JsonSerializer.Deserialize(layouts.ToJsonString(), ShellJsonContext.Default.DictionaryStringLayoutSnapshot) ?? [];
            foreach (var (name, snapshot) in added)
            {
                saved[name] = snapshot;
            }

            SaveLayouts(saved);
            imported.Add($"{added.Count} window layouts");
        }

        return Said($"Imported {string.Join(", ", imported)} from {Path.GetFileName(path)}.");
    }

    private static JsonNode? ReadNode(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path), documentOptions: s_jsonc) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }
}
