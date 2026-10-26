using Avalonia.Headless.XUnit;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Setup;

namespace Codalus.Shell.Tests;

/// <summary>
/// System Integration (M51, ADR 0084) in the shell: Options' toggles and the first-run setup's choices register and
/// remove what they say, read back from the entries (here the fixture's XDG folders under the user data folder: nothing
/// is registered on the machine); a folder or solution the OS hands over opens in a window with nothing open.
/// </summary>
public sealed class OsIntegrationShellTests
{
    [AvaloniaFact]
    public async Task Options_toggles_register_and_remove_and_show_what_is_there()
    {
        await using var f = ShellFixture.Create();
        var linux = Assert.IsType<LinuxOsIntegration>(f.Shell.OsIntegration);
        var model = new SettingsViewModel(f.Shell.Workbench.Settings, () => { }, f.Shell);

        // Environment → System Integration, both off until chosen.
        var environment = model.Categories.Single(c => c.Name == "Environment");
        var page = environment.Children.Single(c => c.Name == "System Integration");
        Assert.Equal("link", page.Icon);
        model.SelectedCategory = page;
        var items = model.Items.OfType<OsIntegrationSettingViewModel>().ToList();
        Assert.Equal([OsIntegrationFeature.Folders, OsIntegrationFeature.FileTypes], items.Select(i => i.Feature));
        Assert.All(items, i => Assert.Equal(false, i.IsOn));
        Assert.All(items, i => Assert.False(i.IsSetting));
        Assert.All(items, i => Assert.True(i.CanToggle));
        Assert.Equal("Open Folders from the File Manager", items[0].Title);
        Assert.Contains("Turning it off removes it", items[0].Description, StringComparison.Ordinal);

        // On: the entries are written and read back.
        var folders = items[0];
        folders.IsOn = true;
        await ShellFixture.PumpUntil(() => !folders.IsBusy && folders.State.State == OsIntegrationState.Registered);
        Assert.Equal(true, folders.IsOn);
        Assert.True(File.Exists(linux.NautilusScript));
        Assert.Contains(linux.Launcher.Program, folders.Status, StringComparison.Ordinal);
        Assert.Null(folders.Error);

        // Not a setting: settings.json doesn't change, and a search by its key finds it.
        Assert.DoesNotContain("os.integration", await File.ReadAllTextAsync(f.Shell.Paths.SettingsFile, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        model.Search = "os.integration";
        Assert.Equal(2, model.Items.Count);

        // Something removed by hand: undetermined, and clicking removes what's left.
        File.Delete(linux.NemoAction);
        folders.Refresh();
        Assert.Null(folders.IsOn);
        Assert.Equal(OsIntegrationState.Partial, folders.State.State);
        Assert.Contains("Partly there", folders.Status, StringComparison.Ordinal);
        folders.IsOn = false;
        await ShellFixture.PumpUntil(() => !folders.IsBusy && folders.State.State == OsIntegrationState.NotRegistered);
        Assert.Equal(false, folders.IsOn);
        Assert.False(File.Exists(linux.NautilusScript));
        Assert.Null(folders.Status);

        // The file types, on and off.
        var types = items[1];
        types.IsOn = true;
        await ShellFixture.PumpUntil(() => !types.IsBusy && types.State.State == OsIntegrationState.Registered);
        Assert.True(File.Exists(linux.DesktopEntry));
        types.IsOn = false;
        await ShellFixture.PumpUntil(() => !types.IsBusy && types.State.State == OsIntegrationState.NotRegistered);
        Assert.False(File.Exists(linux.DesktopEntry));
    }

    [AvaloniaFact]
    public async Task The_setup_offers_system_integration_off_and_registers_only_what_is_checked()
    {
        await using var f = ShellFixture.Create(firstRun: true);
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen, seconds: 30);
        var model = f.Shell.SetupModel!;
        await model.NextAsync();
        await model.NextAsync();
        await model.NextAsync();
        Assert.Equal(SetupStep.Tools, model.Current);
        Assert.Equal([OsIntegrationFeature.Folders, OsIntegrationFeature.FileTypes], model.Integrations.Select(c => c.Feature));
        Assert.All(model.Integrations, c => Assert.False(c.IsChecked));
        Assert.All(model.Integrations, c => Assert.True(c.IsAvailable));

        // Checked, then Done: that one is registered, the other isn't.
        model.Integrations[1].IsChecked = true;
        await model.NextAsync();
        Assert.True(model.WasDone);
        Assert.Equal(OsIntegrationState.Registered, f.Shell.OsIntegration.GetStatus(OsIntegrationFeature.FileTypes).State);
        Assert.Equal(OsIntegrationState.NotRegistered, f.Shell.OsIntegration.GetStatus(OsIntegrationFeature.Folders).State);
        await ShellFixture.PumpUntil(() => f.Shell.IsStartWindowOpen);
        f.Shell.StartWindowModel!.ContinueCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !f.Shell.IsStartWindowOpen);

        // Again from Help: it shows what's registered, and unchecking it removes it.
        f.Shell.ShowSetup();
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen);
        var again = f.Shell.SetupModel!;
        await again.NextAsync();
        await again.NextAsync();
        await again.NextAsync();
        Assert.True(again.Integrations[1].IsChecked);
        Assert.False(again.Integrations[0].IsChecked);

        // Skip setup doesn't save the step it's on.
        again.Integrations[1].IsChecked = false;
        await again.FinishAsync(done: false);
        Assert.Equal(OsIntegrationState.Registered, f.Shell.OsIntegration.GetStatus(OsIntegrationFeature.FileTypes).State);

        f.Shell.ShowSetup();
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen);
        var third = f.Shell.SetupModel!;
        await third.NextAsync();
        await third.NextAsync();
        await third.NextAsync();
        third.Integrations[1].IsChecked = false;
        await third.BackAsync(); // leaving the step saves it, as Next does
        Assert.Equal(OsIntegrationState.NotRegistered, f.Shell.OsIntegration.GetStatus(OsIntegrationFeature.FileTypes).State);
        await third.FinishAsync(done: false);
    }

    [AvaloniaFact]
    public async Task A_folder_or_solution_from_the_system_opens_in_a_window_with_nothing_open()
    {
        await using var f = ShellFixture.Create();
        var missing = Path.Combine(f.UserData, "Missing.slnx");

        // Nothing is open: this window takes it (here a solution that isn't there, so it says so).
        Assert.True(f.Shell.TakeOpenPath(missing));
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(n => n.Message.Contains("Missing.slnx", StringComparison.Ordinal)));
    }

    [Fact]
    public void Explorers_quoted_drive_root_reads_as_the_root()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows' quoting rules.");
        var root = Path.GetPathRoot(Environment.SystemDirectory)!; // C:\
        Assert.Equal(root, ShellOptions.Parse([root[..^1] + "\""]).Path); // "C:\" arrives as C:"
    }
}
