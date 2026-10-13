using System.Text.Json;
using System.Text.Json.Nodes;
using DaedalusStudio.Workbench.Settings;

namespace DaedalusStudio.Shell;

/// <summary>
/// Tools → Import Settings from VS Code (M23a, <see cref="VsCodeImport"/>): its user settings that are ours, its color
/// customizations, and its key bindings whose commands exist here, after the user agrees. Tools → Import a .vssettings
/// File (<see cref="VsSettingsImport"/>): a classic IDE's exported font, colors, editor options and shortcuts. Every
/// skipped key is logged with why.
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

        await ApplyImportAsync(folder, settings, keybindings, mergeColors: false).ConfigureAwait(true);
        var skipped = settings.Skipped.Count + keybindings.Skipped.Count;
        Workbench.Notifications.Info($"Imported {settings.Imported.Count} settings and {keybindings.Imported.Count} keyboard shortcuts from VS Code."
            + (skipped > 0 ? $" {skipped} didn't apply here (the Daedalus Studio log lists them)." : string.Empty));
    }

    /// <summary>Imports a <c>.vssettings</c> file (<paramref name="path"/>, or picked): its font, colors, editor options and shortcuts.</summary>
    public async Task ImportVsSettingsAsync(string? path = null, bool confirm = true)
    {
        path ??= Window is { } window ? await window.PickFileAsync("Import a .vssettings File", "Exported IDE settings", ["*.vssettings"]).ConfigureAwait(true) : null;
        if (path is null)
        {
            return;
        }

        VsSettingsResult result;
        try
        {
            result = VsSettingsImport.Read(await File.ReadAllTextAsync(path).ConfigureAwait(true), Workbench.Settings.Find, c => Workbench.Commands.Find(c) is not null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error($"{path} couldn't be read: {ex.Message}");
            return;
        }

        var (settings, keybindings) = (result.Settings, result.Keybindings);
        if (settings.Imported.Count == 0 && keybindings.Imported.Count == 0)
        {
            Workbench.Notifications.Info($"Nothing in {Path.GetFileName(path)} applies to Daedalus Studio" + (settings.Skipped.Count > 0 ? $": {settings.Skipped[0].Name} {settings.Skipped[0].Reason}." : "."));
            return;
        }

        if (confirm && await Workbench.Notifications.Info(
                $"Import {settings.Imported.Count} settings and {keybindings.Imported.Count} keyboard shortcuts from {Path.GetFileName(path)}? Colors are added to yours; other settings replace yours.", "Import").Result.ConfigureAwait(true) != "Import")
        {
            return;
        }

        await ApplyImportAsync(path, settings, keybindings, mergeColors: true).ConfigureAwait(true);
        var skipped = settings.Skipped.Count + keybindings.Skipped.Count;
        Workbench.Notifications.Info($"Imported {settings.Imported.Count} settings and {keybindings.Imported.Count} keyboard shortcuts from {Path.GetFileName(path)}."
            + (skipped > 0 ? $" {skipped} didn't apply here (the Daedalus Studio log lists them)." : string.Empty));
    }

    /// <summary>
    /// Writes an import's settings (with <paramref name="mergeColors"/>, the two color settings' entries go over the user's
    /// own instead of replacing them) and appends its key bindings to the user's; logs what was skipped.
    /// </summary>
    private async Task ApplyImportAsync(string source, ImportResult<(string Key, JsonElement Value)> settings, ImportResult<JsonElement> keybindings, bool mergeColors)
    {
        foreach (var (key, value) in settings.Imported)
        {
            var merged = value;
            if (mergeColors && key is Theming.ColorCustomizations.TokensKey or Theming.ColorCustomizations.WorkbenchKey
                && Workbench.Settings.GetLayerValue(key, SettingScope.User) is { ValueKind: JsonValueKind.Object } current
                && JsonNode.Parse(current.GetRawText()) is JsonObject mine)
            {
                foreach (var (name, color) in JsonNode.Parse(value.GetRawText())!.AsObject())
                {
                    mine[name] = color?.DeepClone();
                }

                using var document = JsonDocument.Parse(mine.ToJsonString());
                merged = document.RootElement.Clone();
            }

            await Workbench.Settings.SetAsync(key, merged, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        }

        if (keybindings.Imported.Count > 0)
        {
            var current = File.Exists(Paths.KeybindingsFile) ? await File.ReadAllTextAsync(Paths.KeybindingsFile).ConfigureAwait(true) : "[]";
            var array = JsonNode.Parse(current, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonArray ?? [];
            foreach (var entry in keybindings.Imported)
            {
                array.Add(JsonNode.Parse(entry.GetRawText()));
            }

            await Workbench.SaveUserKeybindingsAsync(array.ToJsonString(s_readableJson), CancellationToken.None).ConfigureAwait(true);
            Workbench.LoadUserKeybindings();
        }

        Workbench.Log.AppendLine($"[import] From {source}: {settings.Imported.Count} settings, {keybindings.Imported.Count} keyboard shortcuts.");
        foreach (var (name, reason) in settings.Skipped.Concat(keybindings.Skipped))
        {
            Workbench.Log.AppendLine($"[import] Skipped {name}: {reason}.");
        }
    }
}
