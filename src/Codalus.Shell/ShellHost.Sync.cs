using System.Text.Json;
using System.Text.Json.Nodes;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Git;
using Codalus.Workbench.Settings;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// Settings sync (M23b, <see cref="SettingsSync"/>): the active profile's settings, keyboard shortcuts, snippets and
/// saved layouts, kept in a folder or a Git repository the user chooses (<see cref="SyncTargetSetting"/>). Nothing
/// syncs until the user turns it on. Then it syncs when the IDE starts and a few seconds after a change; a change on
/// both sides asks which to keep. The target is a setting itself, so each profile syncs to its own; the sync settings
/// and <see cref="SyncIgnoredSetting"/> stay on each machine.
/// </summary>
public sealed partial class ShellHost
{
    public const string SyncTargetSetting = "settingsSync.target";
    public const string SyncIgnoredSetting = "settingsSync.ignoredSettings";

    private static readonly TimeSpan s_syncDelay = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private CancellationTokenSource? _syncDebounce;
    private bool _applyingSync;

    private string SyncTarget => Workbench.Settings.GetString(SyncTargetSetting).Trim();

    private SettingsSync CreateSync() => new(Git,
        Path.Combine(Workbench.Paths.Cache, "settings-sync", Profiles.Active),
        Path.Combine(Workbench.Paths.State, $"settings-sync-{Profiles.Active}.json"));

    /// <summary>Syncs after changes (debounced) and once the IDE is interactive.</summary>
    private void InitializeSync()
    {
        Workbench.Settings.Changed += (_, e) =>
        {
            if (!e.Affects(SyncTargetSetting))
            {
                ScheduleSync();
            }
        };
        Workbench.Keybindings.Changed += (_, _) => ScheduleSync();
    }

    private void ScheduleSync()
    {
        if (_disposing || _applyingSync || SyncTarget.Length == 0)
        {
            return;
        }

        var debounce = new CancellationTokenSource();
        Interlocked.Exchange(ref _syncDebounce, debounce)?.Cancel();
        _ = Task.Delay(s_syncDelay, debounce.Token).ContinueWith(
            t =>
            {
                if (!t.IsCanceled)
                {
                    Ui.Post(() => _ = SyncSettingsAsync(quiet: true));
                }
            },
            TaskScheduler.Default);
    }

    /// <summary>Turns sync on: a folder, or a Git repository's URL (asked for when <paramref name="target"/> is null).</summary>
    public async Task TurnOnSettingsSyncAsync(string? target = null)
    {
        if (target is null)
        {
            var kind = await Palette.ShowAsync(
                [
                    new QuickPickItem("A Folder…") { Detail = "One that another tool syncs, such as a cloud drive", Tag = "folder" },
                    new QuickPickItem("A Git Repository…") { Detail = "Any remote you can push to; a gist's clone URL works", Tag = "git" },
                ], "Sync settings with").ConfigureAwait(true) is { Tag: string picked } ? picked : null;
            target = kind switch
            {
                "folder" => Window is { } window ? await window.PickFolderAsync("Sync Settings with a Folder").ConfigureAwait(true) : null,
                "git" => await Dialogs.InputAsync(Localizer.Get("msg.sync.syncSettingsWithAGitRepository", "Sync Settings with a Git Repository"), Localizer.Get("msg.sync.theRepositorysUrl", "The repository's URL"), string.Empty,
                    url => SettingsSync.IsGitTarget(url.Trim()) ? null : "A Git URL: https://…, ssh://…, git@host:path or ….git").ConfigureAwait(true),
                _ => null,
            };
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        await Workbench.Settings.SetAsync(SyncTargetSetting, JsonSerializer.SerializeToElement(target.Trim(), ShellJsonContext.Default.String), SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        await SyncSettingsAsync(quiet: false).ConfigureAwait(true);
    }

    public async Task TurnOffSettingsSyncAsync()
    {
        await Workbench.Settings.SetAsync(SyncTargetSetting, (JsonElement?)null, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        Workbench.Notifications.Info(Localizer.Get("msg.sync.settingsSyncIsOffWhatWas", "Settings sync is off. What was synced stays where it is."));
    }

    /// <summary>
    /// The first-run setup's Your settings step (M46): turns sync on with <paramref name="target"/> and syncs at once. What
    /// is there wins over this machine's settings without asking (the setup says so: the point is to bring a profile).
    /// Returns what happened, for the setup to show.
    /// </summary>
    internal async Task<string> TurnOnSettingsSyncFromSetupAsync(string target)
    {
        target = target.Trim();
        await Workbench.Settings.SetAsync(SyncTargetSetting, JsonSerializer.SerializeToElement(target, ShellJsonContext.Default.String), SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        var (outcome, error) = await SyncCoreAsync(quiet: true, preferSynced: true).ConfigureAwait(true);
        return outcome switch
        {
            SyncOutcome.Pulled or SyncOutcome.Conflict => Localizer.Format("msg.sync.setupPulled", "Settings sync is on: your settings from {0} are applied here.", target),
            SyncOutcome.Pushed => Localizer.Format("msg.sync.setupPushed", "Settings sync is on: {0} had no settings yet, so this machine's are there now.", target),
            SyncOutcome.UpToDate => Localizer.Format("msg.sync.setupUpToDate", "Settings sync is on with {0}, and up to date.", target),
            _ => Localizer.Format("msg.sync.setupFailed", "Settings sync is on, but syncing with {0} failed: {1}", target, error ?? string.Empty),
        };
    }

    /// <summary>Syncs now. <paramref name="quiet"/> (automatic syncs) reports only what needs the user.</summary>
    public Task SyncSettingsAsync(bool quiet = false) => SyncCoreAsync(quiet, preferSynced: false);

    /// <summary>
    /// A sync; <paramref name="preferSynced"/> takes the target's settings on a conflict instead of asking. Returns the
    /// outcome (null when it didn't run or failed) and the error.
    /// </summary>
    private async Task<(SyncOutcome? Outcome, string? Error)> SyncCoreAsync(bool quiet, bool preferSynced)
    {
        var target = SyncTarget;
        if (target.Length == 0)
        {
            if (!quiet)
            {
                Workbench.Notifications.Info(Localizer.Get("msg.sync.settingsSyncIsOffTurnIt", "Settings sync is off. Turn it on with Settings Sync: Turn On."));
            }

            return (null, null);
        }

        if (!await _syncGate.WaitAsync(0).ConfigureAwait(true))
        {
            ScheduleSync(); // one is running: sync again after it
            return (null, null);
        }

        try
        {
            var sync = CreateSync();
            var result = await sync.SyncAsync(target, LocalSyncDocument(), CancellationToken.None).ConfigureAwait(true);
            Workbench.Log.AppendLine($"[sync] {target}: {result.Outcome}.");
            switch (result.Outcome)
            {
                case SyncOutcome.UpToDate or SyncOutcome.Pushed:
                    if (!quiet)
                    {
                        StatusBar.ShowMessage(result.Outcome == SyncOutcome.Pushed ? "Settings synced." : "Settings are up to date.", TimeSpan.FromSeconds(4));
                    }

                    break;
                case SyncOutcome.Pulled:
                    await ApplySyncedAsync(result.Remote!).ConfigureAwait(true);
                    await sync.ResolveAsync(target, LocalSyncDocument(), push: false, CancellationToken.None).ConfigureAwait(true);
                    StatusBar.ShowMessage(Localizer.Get("msg.sync.settingsUpdatedFromSync", "Settings updated from sync."), TimeSpan.FromSeconds(4));
                    break;
                case SyncOutcome.Conflict when preferSynced:
                    await ApplySyncedAsync(result.Remote!).ConfigureAwait(true);
                    await sync.ResolveAsync(target, LocalSyncDocument(), push: false, CancellationToken.None).ConfigureAwait(true);
                    break;
                case SyncOutcome.Conflict:
                    var choice = await Workbench.Notifications.Warning(
                        Localizer.Format("msg.sync.settingsChangedBothHereAndIn", "Settings changed both here and in {0} since the last sync. Which do you keep?", target), "Use Synced", "Keep Mine").Result.ConfigureAwait(true);
                    if (choice == "Use Synced")
                    {
                        await ApplySyncedAsync(result.Remote!).ConfigureAwait(true);
                        await sync.ResolveAsync(target, LocalSyncDocument(), push: false, CancellationToken.None).ConfigureAwait(true);
                    }
                    else if (choice == "Keep Mine")
                    {
                        await sync.ResolveAsync(target, LocalSyncDocument(), push: true, CancellationToken.None).ConfigureAwait(true);
                    }

                    break;
            }

            return (result.Outcome, null);
        }
        catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException or JsonException)
        {
            Workbench.Log.AppendLine($"[sync] {target}: {ex.Message}");
            if (quiet)
            {
                StatusBar.ShowMessage(Localizer.Get("msg.sync.settingsSyncFailedSeeTheLog", "Settings sync failed (see the log)."), TimeSpan.FromSeconds(6));
            }
            else
            {
                Workbench.Notifications.Error(Localizer.Format("msg.sync.settingsCouldntBeSyncedWith", "Settings couldn't be synced with {0}: {1}", target, ex.Message));
            }

            return (null, ex.Message);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private bool IsSyncIgnored(string key) =>
        key.StartsWith("settingsSync.", StringComparison.Ordinal)
        || (Workbench.Settings.GetValue(SyncIgnoredSetting) is { ValueKind: JsonValueKind.Array } ignored
            && ignored.EnumerateArray().Any(i => i.ValueKind == JsonValueKind.String && i.GetString() == key));

    /// <summary>The synced document of this machine's settings: the shape of an export, with snippets, without machine-only parts.</summary>
    internal string LocalSyncDocument()
    {
        var settings = new JsonObject();
        if (ReadNode(Paths.SettingsFile) is JsonObject all)
        {
            foreach (var (key, value) in all)
            {
                if (!IsSyncIgnored(key))
                {
                    settings[key] = value?.DeepClone();
                }
            }
        }

        var snippets = new JsonObject();
        if (Directory.Exists(UserSnippetsDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(UserSnippetsDirectory, "*.json").Order(StringComparer.Ordinal))
            {
                snippets[Path.GetFileName(file)] = File.ReadAllText(file);
            }
        }

        return new JsonObject
        {
            ["codalusSettings"] = 1,
            ["settings"] = settings,
            ["keybindings"] = ReadNode(Paths.KeybindingsFile) as JsonArray ?? [],
            ["layouts"] = ReadNode(LayoutsFile) as JsonObject ?? [],
            ["snippets"] = snippets,
        }.ToJsonString(s_readableJson);
    }

    /// <summary>Replaces this machine's settings (but the ignored ones), shortcuts, snippets and layouts with the synced ones.</summary>
    private async Task ApplySyncedAsync(string remote)
    {
        var document = JsonNode.Parse(remote, documentOptions: s_jsonc) as JsonObject ?? throw new JsonException("The synced file isn't a JSON object.");
        _applyingSync = true;
        try
        {
            if (document["settings"] is JsonObject synced)
            {
                var settings = new JsonObject();
                foreach (var (key, value) in synced)
                {
                    if (!IsSyncIgnored(key))
                    {
                        settings[key] = value?.DeepClone();
                    }
                }

                if (ReadNode(Paths.SettingsFile) is JsonObject mine)
                {
                    foreach (var (key, value) in mine.Where(p => IsSyncIgnored(p.Key)))
                    {
                        settings[key] = value?.DeepClone();
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(Paths.SettingsFile)!);
                var temp = Paths.SettingsFile + ".tmp";
                await File.WriteAllTextAsync(temp, settings.ToJsonString(s_readableJson)).ConfigureAwait(true);
                File.Move(temp, Paths.SettingsFile, overwrite: true);
                Workbench.Settings.Reload();
            }

            if (document["keybindings"] is JsonArray keybindings)
            {
                await Workbench.SaveUserKeybindingsAsync(keybindings.ToJsonString(s_readableJson), CancellationToken.None).ConfigureAwait(true);
                Workbench.LoadUserKeybindings();
            }

            if (document["layouts"] is JsonObject layouts)
            {
                SaveLayouts(JsonSerializer.Deserialize(layouts.ToJsonString(), ShellJsonContext.Default.DictionaryStringLayoutSnapshot) ?? []);
            }

            if (document["snippets"] is JsonObject snippets)
            {
                if (Directory.Exists(UserSnippetsDirectory))
                {
                    foreach (var file in Directory.EnumerateFiles(UserSnippetsDirectory, "*.json"))
                    {
                        File.Delete(file);
                    }
                }

                foreach (var (name, text) in snippets)
                {
                    if (text is JsonValue value && value.TryGetValue<string>(out var content) && Path.GetFileName(name) == name && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.CreateDirectory(UserSnippetsDirectory);
                        await File.WriteAllTextAsync(Path.Combine(UserSnippetsDirectory, name), content).ConfigureAwait(true);
                    }
                }

                LoadUserSnippets();
            }
        }
        finally
        {
            _applyingSync = false;
        }
    }
}
