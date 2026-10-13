using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Projects.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>
/// Project properties, launch profiles, the Reference Manager, Configuration Manager, unload/reload and SDKs (ADR 0013)
/// in the headless shell, on a copy of samples/ProjectSystem.
/// </summary>
public sealed class ProjectManagementTests
{
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

    private static async Task<(ShellFixture Fixture, string Folder)> OpenAsync()
    {
        var f = ShellFixture.Create();
        f.Window.Width = 1400;
        f.Window.Height = 900;
        var slnx = f.CopyFixture();
        f.Shell.Trust.Trust(Path.GetDirectoryName(Path.GetDirectoryName(slnx))!);
        await f.Shell.OpenSolutionAsync(slnx);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 3 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 120);
        return (f, Path.GetDirectoryName(slnx)!);
    }

    [AvaloniaFact]
    public async Task The_sdk_panel_shows_the_sdk_in_use_and_each_sdks_support_status()
    {
        var (f, _) = await OpenAsync();
        await using var _ = f;
        var sdks = f.Shell.OpenSdks()!;
        await ShellFixture.PumpUntil(() => sdks.Sdks.Count > 0, seconds: 60);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<SdkView>().Any());

        // M30: the fixture's copy pins the repository's SDK; every SDK says how long it is supported.
        var inUse = Assert.Single(sdks.Sdks, s => s.IsResolved);
        Assert.Equal(Path.GetFileName(Path.TrimEndingDirectorySeparator(f.Shell.Solutions.SdkPath!)), inUse.Version);
        Assert.StartsWith("Supported until ", inUse.Support, StringComparison.Ordinal);
        Assert.All(sdks.Sdks, s => Assert.NotEmpty(s.Support));
        Assert.All(sdks.Sdks.Where(s => s.IsOutOfSupport), s => Assert.StartsWith("Out of support since ", s.Support, StringComparison.Ordinal));
        Assert.False(sdks.HasWarning);
        NavigationDepthTests.Screenshot(f, "m30-sdks");

        // The oldest SDKs (out of support on most machines) for the screenshot review.
        var list = f.Window.GetVisualDescendants().OfType<Avalonia.Controls.ListBox>().Single(l => Avalonia.Automation.AutomationProperties.GetName(l) == "Installed SDKs");
        list.ScrollIntoView(sdks.Sdks.Count - 1);
        ShellFixture.Pump();
        NavigationDepthTests.Screenshot(f, "m30-sdks-oldest");
        await f.Shell.Workbench.Settings.SetAsync(Theming.ThemeService.SettingKey, "dark", Workbench.Settings.SettingScope.User, TestContext.Current.CancellationToken);
        ShellFixture.Pump();
        NavigationDepthTests.Screenshot(f, "m30-sdks-dark");
    }

    [AvaloniaFact]
    public async Task Property_pages_show_provenance_and_write_the_project_file()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var app = Path.Combine(folder, "src", "App", "App.csproj");

        var vm = f.Shell.OpenProjectProperties(app)!;
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<ProjectPropertiesView>().Any());
        await ShellFixture.PumpUntil(() => vm.Sections.Count > 0 && !vm.IsLoading);
        Assert.Equal("Properties: App", vm.Title);
        var items = vm.Sections.SelectMany(s => s.Items).ToDictionary(i => i.Definition.Name);
        Assert.Equal("net10.0", items[ProjectPropertyPages.TargetFrameworksPseudo].Text);
        Assert.Equal("Exe", items["OutputType"].SelectedOption?.Value);
        Assert.StartsWith("Set in App.csproj (line 3)", items["OutputType"].Origin, StringComparison.Ordinal);
        Assert.True(items["OutputType"].IsSetInProject);
        Assert.Equal("enable", items["Nullable"].SelectedOption?.Value);
        Assert.StartsWith("Inherited from Directory.Build.props", items["Nullable"].Origin, StringComparison.Ordinal);
        Assert.False(items["Nullable"].IsSetInProject);

        // Edits: a text property, then reset it.
        items["RootNamespace"].Text = "Acme.App";
        await ShellFixture.PumpUntil(() => Read(app).Contains("<RootNamespace>Acme.App</RootNamespace>", StringComparison.Ordinal));
        await ShellFixture.PumpUntil(() => vm.Sections.SelectMany(s => s.Items).Any(i => i is { Definition.Name: "RootNamespace", IsSetInProject: true }));
        vm.Sections.SelectMany(s => s.Items).Single(i => i.Definition.Name == "RootNamespace").ResetCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !Read(app).Contains("RootNamespace", StringComparison.Ordinal));

        // Two frameworks switch TargetFramework to TargetFrameworks.
        await ShellFixture.PumpUntil(() => vm.Sections.Count > 0 && !vm.IsLoading);
        vm.Sections.SelectMany(s => s.Items).Single(i => i.Definition.Name == ProjectPropertyPages.TargetFrameworksPseudo).Text = "net10.0;net9.0";
        await ShellFixture.PumpUntil(() => Read(app).Contains("<TargetFrameworks>net10.0;net9.0</TargetFrameworks>", StringComparison.Ordinal) && !Read(app).Contains("<TargetFramework>", StringComparison.Ordinal));

        // Build page, Release only: a per-configuration property goes into a conditioned group; others are read only.
        vm.SelectedPage = vm.Pages.Single(p => p.Id == "build");
        vm.Scope = "Release";
        await ShellFixture.PumpUntil(() => vm.Sections.SelectMany(s => s.Items).Any(i => i.Definition.Name == "Optimize") && !vm.IsLoading);
        var build = vm.Sections.SelectMany(s => s.Items).ToDictionary(i => i.Definition.Name);
        Assert.True(build["Optimize"].BoolValue);
        Assert.True(build["Optimize"].CanEdit);
        Assert.False(build["AllowUnsafeBlocks"].CanEdit);
        build["Optimize"].BoolValue = false;
        await ShellFixture.PumpUntil(() => Read(app).Contains("Condition=\"'$(Configuration)' == 'Release'\"", StringComparison.Ordinal) && Read(app).Contains("<Optimize>false</Optimize>", StringComparison.Ordinal));

        // Debug page: launch profiles.
        vm.SelectedPage = vm.Pages.Single(p => p.IsLaunchProfiles);
        var profiles = vm.LaunchProfiles;
        await ShellFixture.PumpUntil(() => !vm.IsLoading);
        profiles.AddCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.Request?.Title == "New Launch Profile");
        f.Shell.Dialogs.InputText = "Smoke";
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => profiles.Selected?.Name == "Smoke");
        profiles.Selected!.CommandLineArgs = "--fast";
        profiles.Selected.EnvironmentVariables = "MODE=test";
        await profiles.SaveAsync();
        var launch = Path.Combine(folder, "src", "App", "Properties", "launchSettings.json");
        Assert.Contains("\"Smoke\"", Read(launch), StringComparison.Ordinal);
        Assert.Contains("\"commandLineArgs\": \"--fast\"", Read(launch), StringComparison.Ordinal);
        Assert.Contains("\"MODE\": \"test\"", Read(launch), StringComparison.Ordinal);
        Assert.Contains(Codalus.Workbench.Build.LaunchSettings.Read(launch, out var launchError), p => p.Name == "Smoke" && p.CommandLineArgs == "--fast");
        Assert.Null(launchError);
    }

    [AvaloniaFact]
    public async Task References_configurations_unload_and_sdks()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var app = Path.Combine(folder, "src", "App", "App.csproj");
        var tests = Path.Combine(folder, "tests", "Core.Tests", "Core.Tests.csproj");

        // Reference Manager: reference App from the tests.
        var references = f.Shell.OpenReferenceManager(tests)!;
        await ShellFixture.PumpUntil(() => references.Projects.Count == 2);
        references.Filter = "Ap";
        Assert.Equal(["App"], references.VisibleProjects.Select(p => p.Name));
        references.VisibleProjects[0].IsChecked = true;
        await references.ApplyAsync();
        Assert.Contains("App.csproj", Read(tests), StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => references.Projects.Single(p => p.Name == "App").WasChecked);

        // Configuration Manager: don't build App in Debug|x64.
        var configurations = f.Shell.OpenConfigurationManager()!;
        await ShellFixture.PumpUntil(() => configurations.Rows.Count == 3);
        configurations.Platform = "x64";
        await ShellFixture.PumpUntil(() => configurations.Rows.Count == 3);
        configurations.Rows.Single(r => r.Name == "App").Build = false;
        await ShellFixture.PumpUntil(() => configurations.Rows.Any(r => r is { Name: "App", Build: false }));
        Assert.Contains("Build", Read(Path.Combine(folder, "ProjectSystem.slnx")), StringComparison.Ordinal);

        // Unload from the Solution Explorer's selection, then reload.
        f.Shell.Explorer.Selected = f.Shell.Explorer.Rows.First(r => r.Node.Path == app);
        Assert.Equal(false, f.Shell.Workbench.Context.GetValue("explorerProjectUnloaded"));
        await f.Shell.UnloadProjectAsync(app);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects[app].State == ProjectStates.Unloaded
                                           && f.Shell.Explorer.Rows.Any(r => r.Node.Path == app && r.IsUnloaded && r.Detail == "(unloaded)"));
        await f.Shell.ReloadProjectAsync(app);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects[app].State == ProjectStates.Loaded);

        // SDKs.
        var sdks = f.Shell.OpenSdks()!;
        await ShellFixture.PumpUntil(() => sdks.Sdks.Count > 0, seconds: 60);
        Assert.Contains(sdks.Sdks, s => s.IsResolved);
        Assert.StartsWith("In use: .NET SDK", sdks.ResolvedText, StringComparison.Ordinal);
    }
}
