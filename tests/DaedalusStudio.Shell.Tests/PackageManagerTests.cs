using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Shell.Views;

namespace DaedalusStudio.Shell.Tests;

/// <summary>
/// The NuGet Package Manager (ADR 0012) in the headless shell, on a copy of samples/Packages and its local feed: browse,
/// details with the README, install with licence acceptance, Installed with transitive packages and "why", Updates,
/// uninstall, the solution-level manager and Package Sources.
/// </summary>
public sealed class PackageManagerTests
{
    /// <summary>The file's text, or empty while the build host is writing it.</summary>
    private static string Read(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static async Task AnswerAsync(ShellFixture f, string title, string button)
    {
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.Request?.Title == title, seconds: 30);
        f.Shell.Dialogs.Buttons.Single(b => b.Label == button).Command.Execute(null);
    }

    private static async Task<(string Folder, string Plain)> OpenAsync(ShellFixture f)
    {
        var folder = f.CopySample("Packages");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Packages.slnx"));
        await ShellFixture.PumpUntil(
            () => f.Shell.Solutions.Projects.Count == 4 && f.Shell.Solutions.Progress is { Completed: true }
                  && f.Shell.Solutions.RestoreState == "succeeded" && f.Shell.Solutions.Projects.Values.Count(p => p.Restored) >= 3,
            seconds: 180);
        return (folder, Path.Combine(folder, "src", "Plain", "Plain.csproj"));
    }

    [AvaloniaFact]
    public async Task Browse_install_updates_and_uninstall_in_a_project()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1400;
        f.Window.Height = 900;
        var (_, plain) = await OpenAsync(f);

        f.Shell.OpenPackageManager(plain);
        await ShellFixture.PumpUntil(() => f.Shell.Layout.ActiveDocument is PackageManagerDocument);
        var vm = ((PackageManagerDocument)f.Shell.Layout.ActiveDocument!).ViewModel;
        Assert.Equal("NuGet: Plain", vm.Title);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<PackageManagerView>().Any());
        await ShellFixture.PumpUntil(() => vm.Sources.Contains("local"));

        // Browse the local feed: the installed version shows next to the latest one.
        vm.SelectedSource = "local";
        vm.Search = "Daedalus.Sample";
        await ShellFixture.PumpUntil(() => vm.Rows.Any(r => r is { Id: "Daedalus.Sample.Greeting", InstalledVersion: not null }) && !vm.IsLoading, seconds: 60);
        var greeting = vm.Rows.Single(r => r.Id == "Daedalus.Sample.Greeting");
        Assert.Equal("1.1.0", greeting.Version);
        Assert.Equal("1.0.0", greeting.InstalledVersion);
        Assert.Equal("1.0.0 → 1.1.0", greeting.VersionLine);

        // Details: versions, README, dependencies; Update to 1.1.0.
        vm.Selected = greeting;
        await ShellFixture.PumpUntil(() => vm.Details is { IsLoading: false, Package: not null });
        var details = vm.Details!;
        Assert.Equal(["1.1.0", "1.0.0"], details.Versions.Select(v => v.Version));
        Assert.Contains("installed", details.Versions[1].Label, StringComparison.Ordinal);
        Assert.Contains("loudly", details.Readme, StringComparison.Ordinal);
        Assert.Contains("Daedalus.Sample.Text", details.Dependencies, StringComparison.Ordinal);
        Assert.Equal("Update", details.InstallLabel);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<Avalonia.Controls.ContentControl>().Any(c => c.Name == "ReadmeHost" && c.Content is not null));
        details.InstallCommand.Execute(null);
        await ShellFixture.PumpUntil(() => Read(plain).Contains("Version=\"1.1.0\"", StringComparison.Ordinal));
        await ShellFixture.PumpUntil(() => vm.Details?.InstalledVersion == "1.1.0", seconds: 120);

        // Installed: the top-level package and, after the restore, the transitive one with "why".
        vm.Tab = PackageTab.Installed;
        await ShellFixture.PumpUntil(() => vm.Rows.Any(r => r.Id == "Daedalus.Sample.Text" && r.IsTransitive), seconds: 120);
        Assert.Equal(["Top-level packages", "Daedalus.Sample.Greeting", "Transitive packages", "Daedalus.Sample.Text"], vm.Rows.Select(r => r.Id));
        vm.Selected = vm.Rows.Single(r => r.Id == "Daedalus.Sample.Text");
        await ShellFixture.PumpUntil(() => vm.Details?.Why is not null);
        Assert.Equal("Daedalus.Sample.Greeting/1.1.0 → Daedalus.Sample.Text/1.0.0", vm.Details!.Why);

        // Licence acceptance: declining installs nothing; accepting installs.
        vm.Tab = PackageTab.Browse;
        vm.Search = "Daedalus.Sample.Licensed";
        await ShellFixture.PumpUntil(() => vm.Rows.Count == 1 && vm.Rows[0].Id == "Daedalus.Sample.Licensed" && !vm.IsLoading);
        vm.Selected = vm.Rows[0];
        await ShellFixture.PumpUntil(() => vm.Details is { IsLoading: false, Package: not null, RequireLicenseAcceptance: true });
        vm.Details!.InstallCommand.Execute(null);
        await AnswerAsync(f, "License Acceptance", "I Decline");
        ShellFixture.Pump();
        Assert.DoesNotContain("Licensed", Read(plain), StringComparison.Ordinal);
        vm.Details.InstallCommand.Execute(null);
        await AnswerAsync(f, "License Acceptance", "I Accept");
        await ShellFixture.PumpUntil(() => Read(plain).Contains("Daedalus.Sample.Licensed", StringComparison.Ordinal));

        // Uninstall (confirmed).
        await ShellFixture.PumpUntil(() => vm.Details?.IsInstalled == true, seconds: 120);
        vm.Details!.UninstallCommand.Execute(null);
        await AnswerAsync(f, "Uninstall Daedalus.Sample.Licensed?", "Uninstall");
        await ShellFixture.PumpUntil(() => !Read(plain).Contains("Licensed", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task The_solution_manager_installs_into_chosen_projects_and_updates_central_versions()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1400;
        f.Window.Height = 900;
        var (folder, plain) = await OpenAsync(f);
        var props = Path.Combine(folder, "cpm", "Directory.Packages.props");

        f.Shell.Run("packages.manageSolution");
        await ShellFixture.PumpUntil(() => f.Shell.Layout.ActiveDocument is PackageManagerDocument);
        var vm = ((PackageManagerDocument)f.Shell.Layout.ActiveDocument!).ViewModel;
        Assert.True(vm.IsSolution);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<PackageManagerView>().Any());

        // Updates: Greeting is at 1.0.0 in three projects; the feed has 1.1.0.
        vm.Tab = PackageTab.Updates;
        await ShellFixture.PumpUntil(() => vm.Rows.Any(r => r.Id == "Daedalus.Sample.Greeting"), seconds: 60);
        Assert.Equal("Updates 1", vm.UpdatesLabel);
        var update = vm.Rows.Single(r => r.Id == "Daedalus.Sample.Greeting");
        Assert.Equal(("1.0.0", "1.1.0", true), (update.InstalledVersion, update.LatestVersion, update.IsChecked));
        Assert.Equal("central", update.Note);

        // Install Text into Plain and Central only: Central's version goes into Directory.Packages.props.
        vm.Tab = PackageTab.Browse;
        vm.SelectedSource = "local";
        vm.Search = "Daedalus.Sample.Text";
        await ShellFixture.PumpUntil(() => vm.Rows.Any(r => r.Id == "Daedalus.Sample.Text") && !vm.IsLoading);
        vm.Selected = vm.Rows.Single(r => r.Id == "Daedalus.Sample.Text");
        await ShellFixture.PumpUntil(() => vm.Details is { IsLoading: false, Package: not null, Projects.Count: 4 });
        var details = vm.Details!;
        Assert.False(details.CanInstall);
        details.Projects.Single(p => p.Name == "Plain").IsChecked = true;
        details.Projects.Single(p => p.Name == "Central").IsChecked = true;
        Assert.True(details.CanInstall);
        details.InstallCommand.Execute(null);
        await ShellFixture.PumpUntil(() => Read(props).Contains("Daedalus.Sample.Text", StringComparison.Ordinal)
                                           && Read(plain).Contains("Daedalus.Sample.Text", StringComparison.Ordinal));
        Assert.DoesNotContain("Daedalus.Sample.Text", Read(Path.Combine(folder, "cpm", "Other", "Other.csproj")), StringComparison.Ordinal);

        // Changing a central version asks first: it affects both CPM projects.
        vm.Search = "Daedalus.Sample.Greeting";
        await ShellFixture.PumpUntil(() => vm.Rows.Any(r => r.Id == "Daedalus.Sample.Greeting") && !vm.IsLoading);
        vm.Selected = vm.Rows.Single(r => r.Id == "Daedalus.Sample.Greeting");
        await ShellFixture.PumpUntil(() => vm.Details is { IsLoading: false, Package: not null, Projects.Count: 4 });
        vm.Details!.Projects.Single(p => p.Name == "Central").IsChecked = true;
        vm.Details.InstallCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.Request?.Title == "Change the central version?");
        Assert.Contains("affects 2 project(s): Central, Other", f.Shell.Dialogs.Request!.Message, StringComparison.Ordinal);
        await AnswerAsync(f, "Change the central version?", "Change Central Version");
        await ShellFixture.PumpUntil(() => Read(props).Contains("""Include="Daedalus.Sample.Greeting" Version="1.1.0" """, StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Package_sources_are_listed_edited_and_saved()
    {
        var userConfig = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NuGet", "NuGet.Config")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "NuGet", "NuGet.Config");
        var user = File.Exists(userConfig) ? Read(userConfig) : null;
        await using var f = ShellFixture.Create();
        var (folder, _) = await OpenAsync(f);

        f.Shell.Run("packages.sources");
        await ShellFixture.PumpUntil(() => f.Shell.Layout.ActiveDocument is PackageSourcesDocument);
        var vm = ((PackageSourcesDocument)f.Shell.Layout.ActiveDocument!).ViewModel;
        await ShellFixture.PumpUntil(() => vm.Sources.Count == 2);
        Assert.Equal(["local", "nuget.org"], vm.Sources.Select(s => s.Name));
        Assert.Equal("Daedalus.Sample.*", vm.Sources[0].Patterns);
        Assert.False(vm.IsModified);

        vm.Selected = vm.Sources[1];
        vm.Sources[1].Enabled = false;
        vm.AddCommand.Execute(null);
        vm.Selected!.Name = "team";
        vm.Selected.Url = Path.Combine(folder, "team-feed");
        vm.Selected.Patterns = "Team.*";
        Assert.True(vm.IsModified);
        await vm.SaveAsync();
        Assert.False(vm.IsModified);
        Assert.Equal(["local", "nuget.org", "team"], vm.Sources.Select(s => s.Name));
        Assert.False(vm.Sources[1].Enabled);
        Assert.Equal("Team.*", vm.Sources[2].Patterns);
        var config = Read(Path.Combine(folder, "nuget.config"));
        Assert.Contains("team-feed", config, StringComparison.Ordinal);
        Assert.Contains("""<package pattern="Team.*" />""", config, StringComparison.Ordinal);
        Assert.Equal(user, File.Exists(userConfig) ? Read(userConfig) : null);
    }
}
