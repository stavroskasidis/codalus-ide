using DaedalusStudio.Platform;
using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.Views;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.StatusBar;
using DaedalusStudio.Workbench.Updates;
using DaedalusStudio.Workbench.Localization;

namespace DaedalusStudio.Shell;

/// <summary>
/// Self-update (M32, ADR 0029): the checks start once the window is interactive; a downloaded update shows a
/// "Restart to update" status bar item (not while debugging or building) and is applied when the IDE exits; What's New
/// opens the release notes once after an update.
/// </summary>
public sealed partial class ShellHost
{
    public const string UpdateStatusItem = "update.status";
    private const string WhatsNewFile = "whats-new.md";
    private readonly HttpClient _updateHttp = new() { Timeout = TimeSpan.FromMinutes(10) };
    private string? _notifiedVersion;
    private bool _restartForUpdate;

    public UpdateService SelfUpdate { get; private set; } = null!;

    private void InitializeUpdates()
    {
        var (install, backend) = Infrastructure.Updates.Create(_updateHttp, line => Workbench.Log.AppendLine(line));
        SelfUpdate = new UpdateService(install, backend, UpdateModeSetting, () => UpdateChannel(install));
        SelfUpdate.StateChanged += (_, state) => Ui.Post(() => ShowUpdateState(state));
        SelfUpdate.Failed += (_, ex) => Workbench.Log.AppendLine($"[update] {ex.Message}");
        Workbench.Context.Changed += (_, key) =>
        {
            if (key is "debugActive" or "buildRunning")
            {
                Ui.Post(() => ShowUpdateState(SelfUpdate.State));
            }
        };
        Workbench.Settings.Changed += (_, e) =>
        {
            if (e.Keys.Contains(UpdateService.ChannelSetting) && !e.Registered.Contains(UpdateService.ChannelSetting))
            {
                _ = SwitchUpdateChannelAsync();
            }
        };
        Workbench.Log.AppendLine($"[update] {install.Version}: {install.Kind}, channel {UpdateChannel(install)}, mode {UpdateModeSetting()}");
    }

    /// <summary>Tests: the update UI on a fake backend and install.</summary>
    internal void UseUpdates(InstallInfo install, IUpdateBackend backend)
    {
        _ = SelfUpdate.DisposeAsync().AsTask();
        SelfUpdate = new UpdateService(install, backend, UpdateModeSetting, () => UpdateChannel(install));
        SelfUpdate.StateChanged += (_, state) => Ui.Post(() => ShowUpdateState(state));
    }

    private UpdateMode UpdateModeSetting() => Workbench.Settings.GetString(UpdateService.ModeSetting, "auto") switch
    {
        "notify" => UpdateMode.Notify,
        "off" => UpdateMode.Off,
        _ => UpdateMode.Auto,
    };

    private string UpdateChannel(InstallInfo install) =>
        Workbench.Settings.GetString(UpdateService.ChannelSetting, string.Empty) is { Length: > 0 } channel ? channel : install.DefaultChannel;

    /// <summary>Called once the window is interactive: the first check, and What's New after an update.</summary>
    private void StartUpdates()
    {
        SelfUpdate.Start();
        ShowWhatsNewAfterUpdate();
    }

    /// <summary>Help → Check for Updates.</summary>
    public async Task CheckForUpdatesAsync()
    {
        var state = await SelfUpdate.CheckAsync(userInitiated: true, CancellationToken.None).ConfigureAwait(true);
        switch (state.Status)
        {
            case UpdateStatus.Disabled:
                Workbench.Notifications.Info(Localizer.Format("msg.updates.thisIsADevelopmentBuildIt", "This is a development build ({0}): it doesn't update itself.", ShellInfo.Version));
                break;
            case UpdateStatus.UpToDate:
                Workbench.Notifications.Info(Localizer.Format("msg.updates.daedalusStudioIsUpToDate", "Daedalus Studio {0} is up to date.", ShellInfo.Version));
                break;
            case UpdateStatus.Failed:
                Workbench.Notifications.Warning(Localizer.Format("msg.updates.couldntCheckForUpdates", "Couldn't check for updates: {0}", state.Error));
                break;
        }
    }

    /// <summary>The "Restart to update" item: closes the IDE (asking about unsaved files as usual); it starts again updated.</summary>
    public void RestartToUpdate()
    {
        if (SelfUpdate.State.Status != UpdateStatus.Ready)
        {
            return;
        }

        if (Workbench.Context.GetValue("debugActive") is true || Workbench.Context.GetValue("buildRunning") is true)
        {
            Workbench.Notifications.Info(Localizer.Get("msg.updates.theUpdateWaitsUntilDebuggingOr", "The update waits until debugging or the build is over."));
            return;
        }

        _restartForUpdate = true;
        Window?.RequestClose();
    }

    /// <summary>When the IDE exits: a downloaded update is applied (and the IDE restarted when the user asked for it).</summary>
    private void ApplyUpdateOnExit()
    {
        if (SelfUpdate is null)
        {
            return;
        }

        var arguments = _restartForUpdate && Solutions.Solution?.Path is { } solution ? new[] { solution } : [];
        try
        {
            if (SelfUpdate.ApplyOnExit(_restartForUpdate, arguments))
            {
                Workbench.Log.AppendLine($"[update] Applying {SelfUpdate.State.Offer?.Version} after exit{(_restartForUpdate ? ", then restarting" : string.Empty)}.");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Workbench.Log.AppendLine($"[update] Couldn't start the update: {ex.Message}");
        }
    }

    private void ShowUpdateState(UpdateState state)
    {
        var busy = Workbench.Context.GetValue("debugActive") is true || Workbench.Context.GetValue("buildRunning") is true;
        switch (state)
        {
            case { Status: UpdateStatus.Downloading, Offer: { } offer }:
                Workbench.StatusBar.Set(new StatusBarItem(UpdateStatusItem, $"$(arrow-down) Downloading {offer.Version}… {state.Progress}%", StatusBarAlignment.Right, 5, ContributionSource.Shell.Id)
                {
                    Tooltip = $"Daedalus Studio {offer.Version} is downloading in the background. It is applied when you restart.",
                });
                break;
            case { Status: UpdateStatus.Ready, Offer: { } offer }:
                SaveWhatsNew(offer);
                Workbench.StatusBar.Set(new StatusBarItem(UpdateStatusItem, $"$(refresh) Restart to update ({offer.Version})", StatusBarAlignment.Right, 5, ContributionSource.Shell.Id)
                {
                    Command = "update.restart",
                    Tooltip = $"Daedalus Studio {offer.Version} is ready. Restart now, or it is applied when you next close the IDE.",
                    Visible = !busy, // not offered while debugging or building
                });
                break;
            case { Status: UpdateStatus.Available, Offer: { } offer } when _notifiedVersion != offer.Version:
                _notifiedVersion = offer.Version;
                _ = OfferUpdateAsync(offer);
                break;
            case { Status: UpdateStatus.Available or UpdateStatus.Downloading or UpdateStatus.Ready }:
                break;
            default:
                Workbench.StatusBar.Remove(UpdateStatusItem);
                break;
        }
    }

    /// <summary>A release the IDE doesn't download itself: notify mode, or a .deb, .rpm or portable install.</summary>
    private async Task OfferUpdateAsync(UpdateOffer offer)
    {
        const string Download = "Download and Restart";
        const string Notes = "Release Notes";
        var (message, actions) = SelfUpdate.Install.Kind switch
        {
            InstallKind.SelfUpdating => ($"Daedalus Studio {offer.Version} is available.", new[] { Download, Notes }),
            InstallKind.SystemPackage => ($"Daedalus Studio {offer.Version} is available. Update it with your package manager (apt upgrade or dnf upgrade).", [Notes]),
            _ => ($"Daedalus Studio {offer.Version} is available.", ["Download Page"]),
        };
        var choice = await Workbench.Notifications.Info(message, actions).Result.ConfigureAwait(true);
        if (choice == Download)
        {
            var state = await SelfUpdate.DownloadAsync(offer, CancellationToken.None).ConfigureAwait(true);
            if (state.Status == UpdateStatus.Ready)
            {
                RestartToUpdate();
            }
        }
        else if (choice is Notes or "Download Page" && offer.ReleasePage is { } page)
        {
            ShellIntegration.OpenUrl(page);
        }
    }

    /// <summary>update.channel changed: the newest release of the new channel, after a confirmation if it is older.</summary>
    private async Task SwitchUpdateChannelAsync()
    {
        var channel = UpdateChannel(SelfUpdate.Install);
        var state = await SelfUpdate.SwitchChannelAsync(
            channel,
            offer => Dialogs.ConfirmAsync(
                Localizer.Get("msg.updates.switchToStableReleases", "Switch to stable releases?"),
                Localizer.Format("msg.updates.theNewestStableReleaseIsOlder", "The newest stable release, {0}, is older than this build ({1}). Install it? Settings and open solutions are kept.", offer.Version, ShellInfo.Version),
                Localizer.Format("msg.updates.install", "Install {0}", offer.Version)),
            CancellationToken.None).ConfigureAwait(true);
        if (state.Status == UpdateStatus.Failed)
        {
            Workbench.Notifications.Warning(Localizer.Format("msg.updates.couldntSwitchTo", "Couldn't switch to {0}: {1}", channel, state.Error));
        }
    }

    // ------------------------------------------------------------------ What's New

    /// <summary>
    /// Keeps the downloaded release's notes until the updated IDE shows them (once), and for Help → What's New. The
    /// version they belong to is in a file of its own (the Markdown preview would show a marker comment).
    /// </summary>
    private void SaveWhatsNew(UpdateOffer offer)
    {
        var text = $"# What's new in Daedalus Studio {offer.Version}\n\n{offer.Notes ?? "No release notes were published with this version."}\n";
        try
        {
            File.WriteAllText(Path.Combine(Paths.State, WhatsNewFile), text);
            File.WriteAllText(Path.Combine(Paths.State, WhatsNewFile + ".version"), offer.Version);
        }
        catch (IOException ex)
        {
            Workbench.Log.AppendLine($"[update] Couldn't keep the release notes: {ex.Message}");
        }
    }

    private void ShowWhatsNewAfterUpdate()
    {
        var path = Path.Combine(Paths.State, WhatsNewFile);
        var version = Path.Combine(Paths.State, WhatsNewFile + ".version");
        var shown = Path.Combine(Paths.State, WhatsNewFile + ".shown");
        if (File.Exists(path) && File.Exists(version) && File.ReadAllText(version).Trim() == ShellInfo.Version
            && (!File.Exists(shown) || File.ReadAllText(shown).Trim() != ShellInfo.Version))
        {
            File.WriteAllText(shown, ShellInfo.Version);
            ShowWhatsNew();
        }
    }

    /// <summary>Help → What's New: the notes of the last update, in a Markdown preview.</summary>
    public MarkdownPreviewDocument? ShowWhatsNew()
    {
        var path = Path.Combine(Paths.State, WhatsNewFile);
        if (!File.Exists(path))
        {
            Workbench.Notifications.Info(Localizer.Format("msg.updates.noReleaseNotesYetTheyAppear", "No release notes yet: they appear here after the next update. This is Daedalus Studio {0}.", ShellInfo.Version));
            return null;
        }

        var model = CodeDocument.ReadOnly(path, "daedalus-whatsnew:" + ShellInfo.Version, File.ReadAllText(path), "release notes");
        var preview = new MarkdownPreviewDocument(model, link =>
        {
            if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                ShellIntegration.OpenUrl(uri);
            }
        })
        { Title = "What's New" };
        Layout.OpenDocument(preview);
        return preview;
    }
}
