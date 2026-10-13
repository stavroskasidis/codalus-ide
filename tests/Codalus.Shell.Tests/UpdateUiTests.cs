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

    /// <summary>The update item while it is visible (the status bar lists visible items only).</summary>
    private static Workbench.StatusBar.StatusBarItem? Item(ShellFixture f) =>
        f.Shell.Workbench.StatusBar.GetItems(Workbench.StatusBar.StatusBarAlignment.Right).FirstOrDefault(i => i.Id == ShellHost.UpdateStatusItem);

    private sealed class FakeBackend(UpdateOffer offer) : IUpdateBackend
    {
        public int Checks { get; private set; }

        public UpdateOffer? Applied { get; private set; }

        public Task<UpdateOffer?> CheckAsync(string channel, bool allowDowngrade, CancellationToken cancellationToken)
        {
            Checks++;
            return Task.FromResult<UpdateOffer?>(offer);
        }

        public Task DownloadAsync(UpdateOffer offer, IProgress<int> progress, CancellationToken cancellationToken)
        {
            progress.Report(100);
            return Task.CompletedTask;
        }

        public void ApplyAfterExit(UpdateOffer offer, bool restart, IReadOnlyList<string> arguments) => Applied = offer;
    }
}
