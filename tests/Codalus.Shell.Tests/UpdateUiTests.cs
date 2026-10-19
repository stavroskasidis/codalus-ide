using Avalonia.Headless.XUnit;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Updates;

namespace Codalus.Shell.Tests;

/// <summary>Self-update in the shell (M32): the status bar item, the restart, What's New, the settings and the Help menu.</summary>
public class UpdateUiTests
{
    private static readonly UpdateOffer s_offer = new("9.9.0", "stable", false, "## 9.9.0\n\n- Faster everything.", new Uri("https://example.com/v9.9.0"));

    [AvaloniaFact]
    public async Task A_downloaded_update_offers_a_restart_except_while_debugging_or_building()
    {
        await using var f = ShellFixture.Create();
        var backend = new FakeBackend(s_offer);
        f.Shell.UseUpdates(new InstallInfo("9.8.0", InstallKind.SelfUpdating), backend);

        await f.Shell.CheckForUpdatesAsync();
        await ShellFixture.PumpUntil(() => Item(f) is { Visible: true });

        Assert.Equal(("$(refresh) Restart to update (9.9.0)", "update.restart"), (Item(f)!.Text, Item(f)!.Command));
        f.Shell.Workbench.Context.Set("debugActive", true);
        await ShellFixture.PumpUntil(() => Item(f) is null); // hidden
        f.Shell.RestartToUpdate(); // not while debugging
        Assert.Null(backend.Applied);
        f.Shell.Workbench.Context.Set("debugActive", false);
        await ShellFixture.PumpUntil(() => Item(f) is { Visible: true });
    }

    [AvaloniaFact]
    public async Task What_s_new_shows_the_notes_of_the_downloaded_release()
    {
        await using var f = ShellFixture.Create();
        f.Shell.UseUpdates(new InstallInfo("9.8.0", InstallKind.SelfUpdating), new FakeBackend(s_offer));
        Assert.Null(f.Shell.ShowWhatsNew()); // nothing yet: a notification says so

        await f.Shell.CheckForUpdatesAsync();
        await ShellFixture.PumpUntil(() => Item(f) is not null);
        var document = f.Shell.ShowWhatsNew();

        Assert.NotNull(document);
        Assert.Equal("What's New", document.Title);
        Assert.Contains("Faster everything.", document.Source.Text.Text, StringComparison.Ordinal);
        Assert.StartsWith("# What's new in Codalus IDE 9.9.0", document.Source.Text.Text, StringComparison.Ordinal); // no marker comment
    }

    [AvaloniaFact]
    public async Task A_portable_build_is_told_about_a_release_with_a_link_and_a_development_build_never_checks()
    {
        await using var f = ShellFixture.Create();
        f.Shell.UseUpdates(new InstallInfo("9.8.0", InstallKind.Portable), new FakeBackend(s_offer));

        await f.Shell.CheckForUpdatesAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(n => n.Message.Contains("9.9.0 is available", StringComparison.Ordinal)));
        Assert.Equal(["Download Page"], f.Shell.Workbench.Notifications.Items.Last(n => n.Message.Contains("9.9.0", StringComparison.Ordinal)).Actions);
        Assert.Null(Item(f));

        var development = new FakeBackend(s_offer);
        f.Shell.UseUpdates(new InstallInfo("9.8.0-dev", InstallKind.Development), development);
        await f.Shell.CheckForUpdatesAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(n => n.Message.Contains("development build", StringComparison.Ordinal)));
        Assert.Equal(0, development.Checks);
    }

    [AvaloniaFact]
    public async Task The_settings_are_in_Options_Environment_and_the_commands_in_the_Help_menu()
    {
        await using var f = ShellFixture.Create();

        Assert.Equal("Environment", SettingsViewModel.OptionsGroup("Updates"));
        Assert.Contains(f.Shell.Workbench.Settings.Descriptors, d => d is { Key: UpdateService.ModeSetting, Category: "Updates" });
        Assert.Contains(f.Shell.Workbench.Settings.Descriptors, d => d is { Key: UpdateService.ChannelSetting, Category: "Updates" });
        var help = f.Shell.Workbench.Menus.GetItems(Workbench.Menus.MenuLocations.MenuBar("help")).Select(i => i.Command).ToList();
        Assert.Contains("update.check", help);
        Assert.Contains("update.whatsNew", help);
        Assert.NotNull(f.Shell.Workbench.Commands.Find("update.restart"));
    }

    /// <summary>
    /// M49 (ADR 0080): a release whose signed feed names a runtime that isn't installed isn't downloaded; a warning says
    /// which runtime, and offers to install it or to update anyway.
    /// </summary>
    [AvaloniaFact]
    public async Task A_release_needing_a_runtime_that_isnt_installed_warns_instead_of_downloading()
    {
        await using var f = ShellFixture.Create();
        var requires = Codalus.Rpc.FrameworkReference.Create(Codalus.Rpc.FrameworkReference.NetCoreApp, Codalus.Rpc.FrameworkVersion.Parse("99.0.0"), Codalus.Rpc.RollForwardPolicy.Major);
        var backend = new FakeBackend(s_offer with { Requires = [requires] });
        f.Shell.UseUpdates(new InstallInfo("9.8.0", InstallKind.SelfUpdating), backend);

        static bool IsWarning(Workbench.Notifications.Notification n) => n.Message.Contains("9.9.0 is available, but it needs the .NET 99.0.0 runtime", StringComparison.Ordinal);
        await f.Shell.CheckForUpdatesAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(IsWarning));
        ShellFixture.Pump();
        var warning = Assert.Single(f.Shell.Workbench.Notifications.Items, IsWarning); // once, not once by the check and once by its state
        Assert.Equal(["Install .NET 99.0", "Update Anyway", "Release Notes"], warning.Actions);
        Assert.Equal(UpdateStatus.NeedsRuntime, f.Shell.SelfUpdate.State.Status);
        Assert.Equal(0, backend.Downloads);
        Assert.Null(Item(f)); // no "Restart to update"

        // Asked for again: said again, once more.
        await f.Shell.CheckForUpdatesAsync();
        ShellFixture.Pump();
        Assert.Equal(2, f.Shell.Workbench.Notifications.Items.Count(IsWarning));
    }

    [AvaloniaFact]
    public async Task A_release_whose_runtime_was_installed_elsewhere_is_announced_when_it_becomes_available()
    {
        // A portable install was told 9.9.0 needs a runtime; the user installed it another way (apt, dotnet-install),
        // so the next check finds 9.9.0 available: that is announced, not hidden behind the earlier warning.
        await using var f = ShellFixture.Create();
        var requires = Codalus.Rpc.FrameworkReference.Create(Codalus.Rpc.FrameworkReference.NetCoreApp, Codalus.Rpc.FrameworkVersion.Parse("99.0.0"), Codalus.Rpc.RollForwardPolicy.Major);
        var backend = new FakeBackend(s_offer with { Requires = [requires] });
        f.Shell.UseUpdates(new InstallInfo("9.8.0", InstallKind.Portable), backend);
        await f.Shell.CheckForUpdatesAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(n => n.Message.Contains("needs the .NET 99.0.0 runtime", StringComparison.Ordinal)));

        backend.Offer = s_offer; // installed: nothing missing now
        await f.Shell.CheckForUpdatesAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(n => n.Message == "Codalus IDE 9.9.0 is available."));
    }

    [AvaloniaFact]
    public async Task After_installing_the_runtime_an_older_release_the_user_switched_to_is_fetched_again_as_a_downgrade()
    {
        // update.channel went to stable and the user agreed to 0.3.1 (older than this nightly), which needed a runtime.
        // After Install .NET, the check again is the switch's, with the downgrade allowed; a plain check never offers it.
        await using var f = ShellFixture.Create();
        var older = new UpdateOffer("9.7.0", "stable", IsDowngrade: true, null, null);
        var backend = new FakeBackend(older) { OnlyWithDowngrade = true };
        f.Shell.UseUpdates(new InstallInfo("9.8.0-nightly.20261018.1", InstallKind.SelfUpdating), backend);
        var state = await f.Shell.RecheckAfterRuntimeInstallAsync(older);
        Assert.Equal((UpdateStatus.Ready, "9.7.0"), (state.Status, state.Offer?.Version));
        Assert.Contains(true, backend.Downgrades);
        Assert.Equal(1, backend.Downloads);
    }

    [Fact]
    public void Velopack_reads_a_feed_with_the_runtime_in_it()
    {
        // eng/package.sh adds CodalusFrameworks to the release's entries (M49): Velopack must still read the feed.
        var feed = Velopack.VelopackAssetFeed.FromJson("""
            {"Assets":[{"PackageId":"Codalus","Version":"0.4.0-nightly.20261018.224","Type":"Full","FileName":"Codalus-0.4.0-nightly.20261018.224-full.nupkg","SHA1":"00","SHA256":"00","Size":1,
              "CodalusFrameworks":[{"Name":"Microsoft.NETCore.App","Version":"11.0.0-rc.2.26504.105","RollForward":"Major"}]}]}
            """);
        var asset = Assert.Single(feed.Assets);
        Assert.Equal("0.4.0-nightly.20261018.224", asset.Version.ToString());
        Assert.Single(UpdateRuntime.Requirements("""
            {"Assets":[{"PackageId":"Codalus","Version":"0.4.0-nightly.20261018.224","Type":"Full","FileName":"x.nupkg","SHA256":"00","Size":1,
              "CodalusFrameworks":[{"Name":"Microsoft.NETCore.App","Version":"11.0.0-rc.2.26504.105","RollForward":"Major"}]}]}
            """, asset.Version.ToString(), _ => null));
    }

    /// <summary>The update item while it is visible (the status bar lists visible items only).</summary>
    private static Workbench.StatusBar.StatusBarItem? Item(ShellFixture f) =>
        f.Shell.Workbench.StatusBar.GetItems(Workbench.StatusBar.StatusBarAlignment.Right).FirstOrDefault(i => i.Id == ShellHost.UpdateStatusItem);

    private sealed class FakeBackend(UpdateOffer offer) : IUpdateBackend
    {
        public UpdateOffer Offer { get; set; } = offer;

        /// <summary>The offer is a downgrade that the backend (Velopack) returns only when downgrades are allowed.</summary>
        public bool OnlyWithDowngrade { get; init; }

        public int Checks { get; private set; }

        public List<bool> Downgrades { get; } = [];

        public UpdateOffer? Applied { get; private set; }

        public int Downloads { get; private set; }

        public Task<UpdateOffer?> CheckAsync(string channel, bool allowDowngrade, CancellationToken cancellationToken)
        {
            Checks++;
            Downgrades.Add(allowDowngrade);
            return Task.FromResult<UpdateOffer?>(OnlyWithDowngrade && !allowDowngrade ? null : Offer);
        }

        public Task DownloadAsync(UpdateOffer offer, IProgress<int> progress, CancellationToken cancellationToken)
        {
            Downloads++;
            progress.Report(100);
            return Task.CompletedTask;
        }

        public void ApplyAfterExit(UpdateOffer offer, bool restart, IReadOnlyList<string> arguments) => Applied = offer;
    }
}
