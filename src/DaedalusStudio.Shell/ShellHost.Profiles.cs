using System.Text.Json;
using System.Text.Json.Nodes;
using DaedalusStudio.Shell.Docking;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Plugins;
using DaedalusStudio.Workbench.Settings;
using DaedalusStudio.Workbench.StatusBar;
using DaedalusStudio.Workbench.Localization;

namespace DaedalusStudio.Shell;

/// <summary>
/// Profiles (M23b, <see cref="ProfileStore"/>): File → Profiles switches, creates, deletes, exports and imports them.
/// Switching reloads settings, shortcuts and snippets and applies the profile's window layout at once; turned-off
/// plugins differ only after a restart, which it offers. The status bar names a profile other than Default.
/// </summary>
public sealed partial class ShellHost
{
    private ProfileStore? _profiles;

    public ProfileStore Profiles => _profiles ??= new ProfileStore(Workbench.Paths);

    private void InitializeProfiles() => UpdateProfileIndicator();

    private void UpdateProfileIndicator()
    {
        var active = Profiles.Active;
        Workbench.Context.Set("profile", active);
        if (active == ProfileStore.DefaultName)
        {
            Workbench.StatusBar.Remove("daedalus.profile");
            return;
        }

        Workbench.StatusBar.Set(new StatusBarItem("daedalus.profile", "$(account) " + active, StatusBarAlignment.Left, -90, ContributionSource.Shell.Id)
        {
            Tooltip = $"Profile: {active} (switch profiles)",
            AccessibleName = $"Profile {active}",
            Command = "workbench.profiles.switch",
        });
    }

    private string CurrentWindowLayout() => JsonSerializer.Serialize(Layout.Capture(null), ShellJsonContext.Default.LayoutSnapshot);

    /// <summary>Switches to <paramref name="name"/> (picked when null; the list also offers New Profile).</summary>
    public async Task SwitchProfileAsync(string? name = null)
    {
        if (name is null)
        {
            var active = Profiles.Active;
            var items = Profiles.Names.Select(n => new QuickPickItem(n) { Detail = n == active ? "current" : null, Tag = n })
                .Append(new QuickPickItem("New Profile…") { Detail = "From this profile or empty", Tag = string.Empty }).ToList();
            name = await Palette.ShowAsync(items, "Switch to a profile").ConfigureAwait(true) is { Tag: string picked } ? picked : null;
            if (name?.Length == 0)
            {
                await NewProfileAsync().ConfigureAwait(true);
                return;
            }
        }

        if (name is null || string.Equals(name, Profiles.Active, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var pluginsBefore = Workbench.Settings.GetValue(PluginManager.DisabledSetting).GetRawText();
        string? layout;
        try
        {
            layout = Profiles.Switch(name, CurrentWindowLayout());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.profiles.couldntSwitchToTheProfile", "Couldn't switch to the profile {0}: {1}", name, ex.Message));
            return;
        }

        Workbench.Settings.Reload();
        Workbench.LoadUserKeybindings();
        LoadUserSnippets();
        if (layout is not null && Deserialize(layout) is { } snapshot && Layout.Restore(snapshot))
        {
            Workbench.Log.AppendLine($"[profiles] Applied {name}'s window layout.");
        }

        UpdateProfileIndicator();
        Workbench.Log.AppendLine($"[profiles] Switched to {name}.");
        var pluginsChanged = Workbench.Settings.GetValue(PluginManager.DisabledSetting).GetRawText() != pluginsBefore;
        if (pluginsChanged)
        {
            if (await Workbench.Notifications.Info(Localizer.Format("msg.profiles.profileItsPluginsDifferTheyApply", "Profile: {0}. Its plugins differ; they apply after a restart.", name), "Restart Now").Result.ConfigureAwait(true) == "Restart Now")
            {
                Restart();
            }
        }
        else
        {
            StatusBar.ShowMessage(Localizer.Format("msg.profiles.profile", "Profile: {0}", name), TimeSpan.FromSeconds(4));
        }

        static LayoutSnapshot? Deserialize(string json)
        {
            try
            {
                return JsonSerializer.Deserialize(json, ShellJsonContext.Default.LayoutSnapshot);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>Creates a profile (<paramref name="name"/> and <paramref name="copyActive"/> asked for when null) and switches to it.</summary>
    public async Task NewProfileAsync(string? name = null, bool? copyActive = null, bool switchTo = true)
    {
        name ??= await Dialogs.InputAsync(Localizer.Get("msg.profiles.newProfile", "New Profile"), Localizer.Get("msg.profiles.aNameForTheProfile", "A name for the profile"), string.Empty, n => Profiles.Validate(n)).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        copyActive ??= await Palette.ShowAsync(
            [
                new QuickPickItem($"Copy of {Profiles.Active}") { Detail = "Its settings, shortcuts, snippets and layouts", Tag = true },
                new QuickPickItem("Empty") { Detail = "The defaults", Tag = false },
            ], $"Start {name.Trim()} from").ConfigureAwait(true) is { Tag: bool copy } ? copy : null;
        if (copyActive is null)
        {
            return;
        }

        try
        {
            Profiles.Create(name, copyActive.Value, copyActive.Value ? CurrentWindowLayout() : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.profiles.couldntCreateTheProfile", "Couldn't create the profile: {0}", ex.Message));
            return;
        }

        if (switchTo)
        {
            await SwitchProfileAsync(name.Trim()).ConfigureAwait(true);
        }
    }

    /// <summary>Deletes a profile (<paramref name="name"/>, or picked) other than Default and the active one.</summary>
    public async Task DeleteProfileAsync(string? name = null, bool confirm = true)
    {
        var deletable = Profiles.Names.Where(n => n != ProfileStore.DefaultName && n != Profiles.Active).ToList();
        if (name is null)
        {
            if (deletable.Count == 0)
            {
                Workbench.Notifications.Info(Localizer.Get("msg.profiles.theresNoProfileToDeleteDefault", "There's no profile to delete: Default and the active profile stay."));
                return;
            }

            name = await Palette.ShowAsync([.. deletable.Select(n => new QuickPickItem(n) { Tag = n })], "Delete a profile").ConfigureAwait(true) is { Tag: string picked } ? picked : null;
        }

        if (name is null || !deletable.Contains(name)
            || (confirm && !await Dialogs.ConfirmAsync(Localizer.Get("msg.profiles.deleteProfile", "Delete Profile"), Localizer.Format("msg.profiles.deleteTheProfileWithItsSettings", "Delete the profile {0}, with its settings, shortcuts, snippets and layouts?", name), Localizer.Get("msg.profiles.delete", "Delete"), danger: true).ConfigureAwait(true)))
        {
            return;
        }

        Profiles.Delete(name);
        Workbench.Log.AppendLine($"[profiles] Deleted {name}.");
    }

    /// <summary>
    /// Creates a profile from an Export Settings file (<paramref name="path"/>, or picked), named
    /// <paramref name="name"/> or as the file says, and switches to it.
    /// </summary>
    public async Task ImportProfileAsync(string? path = null, string? name = null, bool switchTo = true)
    {
        path ??= Window is { } window ? await window.PickFileAsync("Import Profile", "Daedalus Studio settings", ["*.json"]).ConfigureAwait(true) : null;
        if (path is null)
        {
            return;
        }

        JsonObject file;
        try
        {
            file = JsonNode.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(true), documentOptions: s_jsonc) as JsonObject
                ?? throw new JsonException("The file isn't a JSON object.");
            if (file["daedalusSettings"] is null)
            {
                throw new JsonException("It isn't a Daedalus Studio settings export (no \"daedalusSettings\").");
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.profiles.theProfileCouldntBeImportedFrom", "The profile couldn't be imported from {0}: {1}", path, ex.Message));
            return;
        }

        var suggested = (file["profile"] as JsonValue)?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(path);
        name ??= await Dialogs.InputAsync(Localizer.Get("msg.profiles.importProfile", "Import Profile"), Localizer.Get("msg.profiles.aNameForTheImportedProfile", "A name for the imported profile"), Profiles.Validate(suggested) is null ? suggested : string.Empty, n => Profiles.Validate(n)).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || Profiles.Validate(name) is { } error)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                Workbench.Notifications.Error(Localizer.Format("msg.profiles.theProfileCouldntBeImported", "The profile couldn't be imported: {0}", Profiles.Validate(name)));
            }

            return;
        }

        name = name.Trim();
        Profiles.Create(name, copyActive: false);
        var folder = Profiles.Folder(name);
        if (file["settings"] is JsonObject settings)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "settings.json"), settings.ToJsonString(s_readableJson)).ConfigureAwait(true);
        }

        if (file["keybindings"] is JsonArray keybindings)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "keybindings.json"), keybindings.ToJsonString(s_readableJson)).ConfigureAwait(true);
        }

        if (file["layouts"] is JsonObject layouts)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "layouts.json"), layouts.ToJsonString(s_readableJson)).ConfigureAwait(true);
        }

        if (file["windowLayout"] is JsonObject windowLayout)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, ProfileStore.WindowLayoutFile), windowLayout.ToJsonString(s_readableJson)).ConfigureAwait(true);
        }

        Workbench.Log.AppendLine($"[profiles] Imported {name} from {path}.");
        if (switchTo)
        {
            await SwitchProfileAsync(name).ConfigureAwait(true);
        }
    }
}
