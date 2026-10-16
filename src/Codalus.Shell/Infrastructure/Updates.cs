using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Codalus.Workbench.Updates;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// The shell's side of self-update (M32, ADR 0029): Velopack downloads and applies packages from our signed feeds.
/// <see cref="Create"/> picks the backend for the kind of install; <see cref="UpdateService"/> (workbench) decides when.
/// </summary>
internal static class Updates
{
    public const string Repository = "stavroskasidis/codalus-ide";

    /// <summary>A local folder with feeds and packages instead of GitHub (the update test script, eng/test-update.sh).</summary>
    public const string SourceVariable = "CODALUS_UPDATE_SOURCE";

    /// <summary>The feed key to trust instead of the project's, for a local folder source only (tests sign with their own).</summary>
    public const string TestKeyVariable = "CODALUS_UPDATE_TEST_KEY";

    /// <summary>A GitHub token for a private repository's releases (D20); otherwise the GitHub CLI's, when it is signed in.</summary>
    public const string TokenVariable = "CODALUS_UPDATE_TOKEN";

    public static string Rid => RuntimeInformation.RuntimeIdentifier;

    public static (InstallInfo Install, IUpdateBackend Backend) Create(HttpClient http, Action<string> log)
    {
        var store = Store(http);
        // Velopack's locator exists once VelopackApp.Run() ran (first thing in Main); a host without it (the headless
        // tests) is simply not an install Velopack made.
        var installed = Velopack.Locators.VelopackLocator.IsCurrentSet && new UpdateManager(new SignedUpdateSource(store)).IsInstalled;
        var systemPackage = OperatingSystem.IsLinux() && AppContext.BaseDirectory.StartsWith("/opt/codalus/", StringComparison.Ordinal);
        var install = new InstallInfo(ShellInfo.Version, InstallInfo.KindOf(ShellInfo.Version, installed, systemPackage));
        if (installed && Velopack.Locators.VelopackLocator.Current.Channel is { Length: > 0 } channel && channel.StartsWith(Rid + "-", StringComparison.Ordinal))
        {
            install = install with { DefaultChannel = channel[(Rid.Length + 1)..] }; // the installed channel, <rid>-<kind>
        }

        if (installed && OperatingSystem.IsMacOS() && Velopack.Locators.VelopackLocator.Current.RootAppDir is { } bundle)
        {
            install = install with { NeedsAdministrator = MacUpdateLocator.NeedsAdministrator(bundle) };
        }

        IUpdateBackend backend = install.Kind == InstallKind.SelfUpdating
            ? new VelopackBackend(store, log, install.NeedsAdministrator)
            : new FeedOnlyBackend(store, ShellInfo.Version);
        return (install, backend);
    }

    /// <summary><c>codalus --update-now</c> (see <see cref="ShellOptions.UpdateNow"/>).</summary>
    public static int UpdateNow(TextWriter output)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var (install, backend) = Create(http, output.WriteLine);
        if (install.Kind != InstallKind.SelfUpdating)
        {
            output.WriteLine($"This build ({install.Version}, {install.Kind}) doesn't update itself.");
            return 4;
        }

        var service = new UpdateService(install, backend, () => UpdateMode.Auto, () => install.DefaultChannel);
        var state = service.CheckAsync(userInitiated: true, CancellationToken.None).GetAwaiter().GetResult();
        switch (state.Status)
        {
            case UpdateStatus.Ready when service.ApplyOnExit(restart: false, []):
                output.WriteLine($"Updating {install.Version} to {state.Offer!.Version} ({install.DefaultChannel}) once this process exits.");
                return 0;
            case UpdateStatus.UpToDate:
                output.WriteLine($"{install.Version} is up to date ({install.DefaultChannel}).");
                return 3;
            default:
                output.WriteLine($"The update failed: {state.Error ?? state.Status.ToString()}");
                return 2;
        }
    }

    private static IReleaseStore Store(HttpClient http)
    {
        if (Environment.GetEnvironmentVariable(SourceVariable) is { Length: > 0 } folder && Directory.Exists(folder))
        {
            return new FolderReleaseStore(folder, Environment.GetEnvironmentVariable(TestKeyVariable) is { Length: > 0 } key ? key : FeedSignature.ProjectKey);
        }

        string? token = null;
        var looked = false;
        return new GitHubReleaseStore(http, Repository, FeedSignature.ProjectKey, async cancellationToken =>
        {
            if (!looked)
            {
                token = Environment.GetEnvironmentVariable(TokenVariable) is { Length: > 0 } variable ? variable : await GitHubCliTokenAsync(cancellationToken).ConfigureAwait(false);
                looked = true;
            }

            return token;
        });
    }

    /// <summary>`gh auth token`, when the GitHub CLI is installed and signed in: how the maintainer reads a private repository's releases.</summary>
    private static async Task<string?> GitHubCliTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo("gh", "auth token") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 && output.Trim() is { Length: > 0 } value ? value : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            return null; // no gh: public releases need no token
        }
    }

    /// <summary>Velopack's view of our signed feeds.</summary>
    internal sealed class SignedUpdateSource(IReleaseStore store) : IUpdateSource
    {
        public ReleaseFeed? LastFeed { get; private set; }

        public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            LastFeed = await store.GetFeedAsync(channel, CancellationToken.None).ConfigureAwait(false);
            return LastFeed is { } feed ? VelopackAssetFeed.FromJson(feed.Json) : VelopackAssetFeed.FromJson("""{"Assets":[]}""");
        }

        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default) =>
            store.DownloadAsync(releaseEntry.FileName, localFile, progress, cancelToken);
    }

    /// <summary>An install Velopack made: check, download in the background, apply after the IDE exits.</summary>
    /// <remarks>
    /// The updater runs silently unless the install <paramref name="needsAdministrator"/>: silent, it can't ask for the
    /// password, fails with "Permission denied" and starts the old version again, which offers the same update.
    /// </remarks>
    private sealed class VelopackBackend(IReleaseStore store, Action<string> log, bool needsAdministrator) : IUpdateBackend
    {
        private readonly Dictionary<string, (UpdateManager Manager, UpdateInfo Info)> _offers = new(StringComparer.Ordinal);

        public async Task<UpdateOffer?> CheckAsync(string channel, bool allowDowngrade, CancellationToken cancellationToken)
        {
            var source = new SignedUpdateSource(store);
            var manager = new UpdateManager(source, new UpdateOptions { ExplicitChannel = $"{Rid}-{channel}", AllowVersionDowngrade = allowDowngrade });
            var info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
            {
                return null;
            }

            var target = info.TargetFullRelease;
            var version = target.Version.ToString();
            lock (_offers)
            {
                _offers[version] = (manager, info);
            }

            log($"[update] {version} on {channel}{(info.IsDowngrade ? " (an older version)" : string.Empty)} from {source.LastFeed?.Tag}");
            var notes = Notes(source.LastFeed?.Json, manager.CurrentVersion?.ToString(), version);
            return new UpdateOffer(version, channel, info.IsDowngrade, notes, source.LastFeed?.ReleasePage);
        }

        public Task DownloadAsync(UpdateOffer offer, IProgress<int> progress, CancellationToken cancellationToken) =>
            Offer(offer).Manager.DownloadUpdatesAsync(Offer(offer).Info, progress.Report, cancellationToken);

        public void ApplyAfterExit(UpdateOffer offer, bool restart, IReadOnlyList<string> arguments)
        {
            var (manager, info) = Offer(offer);
            if (needsAdministrator)
            {
                log("[update] The app isn't writable by this user: the updater asks for an administrator's password.");
            }

            manager.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: !needsAdministrator, restart, [.. arguments]);
        }

        private (UpdateManager Manager, UpdateInfo Info) Offer(UpdateOffer offer)
        {
            lock (_offers)
            {
                return _offers.TryGetValue(offer.Version, out var known) ? known : throw new InvalidOperationException($"{offer.Version} wasn't found by a check.");
            }
        }
    }

    /// <summary>The zip, tar.gz, .deb or .rpm: reads the feed to say that a release is out; the user updates it.</summary>
    private sealed class FeedOnlyBackend(IReleaseStore store, string current) : IUpdateBackend
    {
        public async Task<UpdateOffer?> CheckAsync(string channel, bool allowDowngrade, CancellationToken cancellationToken)
        {
            if (await store.GetFeedAsync($"{Rid}-{channel}", cancellationToken).ConfigureAwait(false) is not { } feed)
            {
                return null;
            }

            var newest = Versions(feed.Json).OrderByDescending(v => v, Comparer<string>.Create(ReleaseVersions.Compare)).FirstOrDefault();
            if (newest is null || newest == current || (!allowDowngrade && !ReleaseVersions.IsNewer(newest, current)))
            {
                return null;
            }

            return new UpdateOffer(newest, channel, !ReleaseVersions.IsNewer(newest, current), Notes(feed.Json, current, newest), feed.ReleasePage);
        }

        public Task DownloadAsync(UpdateOffer offer, IProgress<int> progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException("This install doesn't update itself.");

        public void ApplyAfterExit(UpdateOffer offer, bool restart, IReadOnlyList<string> arguments) =>
            throw new NotSupportedException("This install doesn't update itself.");
    }

    private static IEnumerable<string> Versions(string feed)
    {
        using var document = JsonDocument.Parse(feed);
        return [.. document.RootElement.GetProperty("Assets").EnumerateArray()
            .Where(a => a.TryGetProperty("Type", out var type) && type.GetString() == "Full")
            .Select(a => a.GetProperty("Version").GetString()!)];
    }

    /// <summary>The release notes of every version after <paramref name="current"/> up to <paramref name="target"/>, newest first.</summary>
    internal static string? Notes(string? feed, string? current, string target)
    {
        if (feed is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(feed);
        var notes = document.RootElement.GetProperty("Assets").EnumerateArray()
            .Where(a => a.TryGetProperty("Type", out var type) && type.GetString() == "Full")
            .Select(a => (Version: a.GetProperty("Version").GetString()!, Notes: a.TryGetProperty("NotesMarkdown", out var n) ? n.GetString() : null))
            .Where(a => a.Notes is { Length: > 0 } && ReleaseVersions.Compare(a.Version, target) <= 0 && (current is null || ReleaseVersions.IsNewer(a.Version, current)))
            .OrderByDescending(a => a.Version, Comparer<string>.Create(ReleaseVersions.Compare))
            .Select(a => a.Notes!.Trim())
            .ToList();
        return notes.Count == 0 ? null : string.Join("\n\n", notes);
    }
}
