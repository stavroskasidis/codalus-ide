using Codalus.Platform;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Build;
using Codalus.Workbench.Mobile;
using Codalus.Workbench.Output;
using Codalus.Workbench.Previewer;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Setup;

namespace Codalus.Workbench.Tests;

/// <summary>
/// M44 (ADR 0074, D31): Windows App SDK apps (WinUI 3 and .NET MAUI's Windows target): where they run, the "Windows
/// Machine" target, how a packaged app is registered and started (its identity, the PowerShell command, the framework
/// packages, the activation's output), Developer Mode and the runtime in Check Environment, and WinUI's XAML compiler
/// errors in their files. Pure functions and stand-ins: nothing registers or starts anything, so it runs on every OS.
/// </summary>
public sealed class WindowsAppsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("codalus-winapps-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ProjectInfo Project(string[] frameworks, bool winUI = false, bool maui = false, string? packageType = null, PackageDependencyInfo[]? packages = null) =>
        new("/src/App/App.csproj", "App", ProjectStates.Loaded, null, frameworks, "WinExe", [], [], [], true)
        {
            UsesWinUI = winUI,
            UsesMaui = maui,
            WindowsPackageType = packageType,
            Packages = packages ?? [],
        };

    private const string Manifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
          <Identity Name="Codalus.Samples.WinUIApp" Publisher="CN=Codalus Samples" Version="1.0.0.0" ProcessorArchitecture="x64" />
          <Applications>
            <Application Id="App" Executable="WinUIApp.exe" EntryPoint="Windows.FullTrustApplication" />
          </Applications>
        </Package>
        """;

    [Fact]
    public void Winui_and_maui_windows_apps_are_told_apart_and_run_only_on_windows()
    {
        var winUI = Project(["net11.0-windows10.0.19041.0"], winUI: true, packageType: "None");
        Assert.True(WindowsApps.IsWindowsAppSdk(winUI));
        Assert.True(WindowsApps.IsWindowsAppSdk(Project(["net10.0-android", "net10.0-windows10.0.19041.0"], maui: true)));
        Assert.False(WindowsApps.IsWindowsAppSdk(Project(["net10.0-android"], maui: true)));
        Assert.False(WindowsApps.IsWindowsAppSdk(Project(["net11.0-windows"])));
        Assert.True(WindowsApps.IsPackaged("MSIX"));
        Assert.True(WindowsApps.IsPackaged("msix"));
        Assert.False(WindowsApps.IsPackaged("None"));
        Assert.False(WindowsApps.IsPackaged("Sparse"));
        Assert.False(WindowsApps.IsPackaged(null));

        // WinUI builds only on Windows too (its XAML compiler is a Windows program); WPF builds everywhere.
        Assert.True(WindowsDesktopProjects.RunsOnlyOnWindows(winUI));
        Assert.True(WindowsDesktopProjects.BuildsOnlyOnWindows(winUI));
        Assert.Equal("WinUI 3", WindowsDesktopProjects.Kind(winUI));
        Assert.False(WindowsDesktopProjects.BuildsOnlyOnWindows(Project(["net11.0-windows"]) with { UsesWpf = true }));

        // Launch profiles named as MAUI's and WinUI's templates name a packaged run are runnable.
        Assert.True(new LaunchProfile("App (Package)", LaunchProfile.MsixPackage).IsRunnable);
    }

    [Fact]
    public void The_windows_target_is_windows_machine_on_windows_and_first_when_nothing_is_chosen()
    {
        var app = Project(["net10.0-android", "net10.0-windows10.0.19041.0"], maui: true);
        Assert.True(MobileTargets.RunsOn(MobilePlatform.Windows, PlatformKind.Windows));
        Assert.False(MobileTargets.RunsOn(MobilePlatform.Windows, PlatformKind.Linux));
        Assert.False(MobileTargets.RunsOn(MobilePlatform.Windows, PlatformKind.MacOS));

        // Nothing chosen: Windows first on Windows (as other IDEs start); Android elsewhere. A choice wins.
        Assert.Equal("net10.0-windows10.0.19041.0", MobileTargets.RunFramework(app, null, PlatformKind.Windows));
        Assert.Equal("net10.0-android", MobileTargets.RunFramework(app, "net10.0-android", PlatformKind.Windows));
        Assert.Equal("net10.0-android", MobileTargets.RunFramework(app, null, PlatformKind.Linux));
        Assert.Equal("net10.0-android", MobileTargets.RunFramework(app, "net10.0-windows10.0.19041.0", PlatformKind.MacOS));

        // The workload: maui-windows, where Windows builds it.
        Assert.Equal(["maui-android", "maui-windows"], MobileTargets.MissingWorkloads(app, [], PlatformKind.Windows));
        Assert.Equal(["maui-android"], MobileTargets.MissingWorkloads(app, [], PlatformKind.Linux));

        Assert.Equal("Windows Machine", MobileTargets.WindowsMachine.Label);
        Assert.Equal((MobilePlatform.Windows, MobileDeviceKind.ThisPc, true), (MobileTargets.WindowsMachine.Platform, MobileTargets.WindowsMachine.Kind, MobileTargets.WindowsMachine.IsUsable));
    }

    [Fact]
    public async Task The_device_list_has_this_windows_machine_on_windows_only()
    {
        static Task<(int, string)> NoTools(string program, IReadOnlyList<string> arguments, string? directory, CancellationToken ct) => Task.FromResult((1, string.Empty));
        foreach (var (platform, listed) in new[] { (PlatformKind.Windows, true), (PlatformKind.Linux, false), (PlatformKind.MacOS, false) })
        {
            var toolchain = new AndroidToolchain(platform, _ => null, _dir, _ => false);
            var service = new MobileService(platform, toolchain, NoTools, (_, _, _) => false);
            var devices = await service.RefreshAsync(TestContext.Current.CancellationToken);
            Assert.Equal(listed, devices.Any(d => d.Id == MobileTargets.WindowsMachineId));
            Assert.Equal(listed, service.DeviceFor(MobilePlatform.Windows) is { Name: "Windows Machine" });
        }
    }

    [Fact]
    public void A_packages_identity_gives_its_family_name_and_application_user_model_id()
    {
        // The publisher id: Crockford's base 32 of the SHA-256 of the publisher, as Windows computes it.
        Assert.Equal("8wekyb3d8bbwe", WindowsApps.PublisherId("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"));
        Assert.Equal("b1n3b429t8b64", WindowsApps.PublisherId("CN=Codalus Samples"));

        var identity = WindowsApps.ReadManifest(Manifest);
        Assert.NotNull(identity);
        Assert.Equal(("Codalus.Samples.WinUIApp", "CN=Codalus Samples", "1.0.0.0", "x64"), (identity.Name, identity.Publisher, identity.Version, identity.Architecture));
        Assert.Equal("Codalus.Samples.WinUIApp_b1n3b429t8b64", identity.FamilyName);
        Assert.Equal("Codalus.Samples.WinUIApp_b1n3b429t8b64!App", identity.Aumid);
        Assert.Equal("x64", WindowsApps.Architecture(identity));

        // The fixture's own source manifest reads the same way.
        var fixture = File.ReadAllText(Path.Combine(ContainerToolsTests.RepoRoot, "samples", "WindowsDesktop", "WinUIApp", "Package.appxmanifest"));
        Assert.Equal("Codalus.Samples.WinUIApp_b1n3b429t8b64!App", WindowsApps.ReadManifest(fixture)?.Aumid);

        Assert.Null(WindowsApps.ReadManifest("<Package"));
        Assert.Null(WindowsApps.ReadManifest("<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\" />"));
        Assert.Null(WindowsApps.ReadManifest(Manifest.Replace("<Application Id=\"App\" Executable=\"WinUIApp.exe\" EntryPoint=\"Windows.FullTrustApplication\" />", string.Empty, StringComparison.Ordinal))?.Aumid);
    }

    [Fact]
    public void The_layout_to_register_is_a_deployed_one_then_the_builds_manifest()
    {
        var output = Path.Combine(_dir, "bin", "Debug", "net11.0-windows10.0.19041.0", "win-x64");
        var info = new RunInfo("/src/App/App.csproj", "net11.0-windows10.0.19041.0", "WinExe", Path.Combine(output, "App.dll"), null, null, null, null)
        {
            WindowsPackageType = "MSIX",
            AppxManifest = Path.Combine(output, "AppxManifest.xml"),
        };
        Assert.Null(WindowsApps.LayoutManifest(info, _ => false));
        Assert.Equal(Path.Combine(output, "AppxManifest.xml"), WindowsApps.LayoutManifest(info, p => p == Path.Combine(output, "AppxManifest.xml")));
        Assert.Equal(Path.Combine(output, "AppX", "AppxManifest.xml"), WindowsApps.LayoutManifest(info, _ => true));
        Assert.Equal(Path.Combine(output, "AppxManifest.xml"), WindowsApps.LayoutManifest(info with { AppxManifest = null }, p => !p.Contains("AppX", StringComparison.Ordinal)));
    }

    [Fact]
    public void Registering_passes_the_runtimes_framework_package_and_starting_reads_the_process_id()
    {
        // The restored runtime package's framework MSIX for the architecture (not Main, Singleton or DDLM).
        var root = Path.Combine(_dir, "packages");
        var msix = Path.Combine(root, "microsoft.windowsappsdk.runtime", "1.8.260529003", "tools", "MSIX", "win10-x64");
        Directory.CreateDirectory(msix);
        foreach (var file in new[] { "Microsoft.WindowsAppRuntime.1.8.msix", "Microsoft.WindowsAppRuntime.Main.1.8.msix", "Microsoft.WindowsAppRuntime.Singleton.1.8.msix", "Microsoft.WindowsAppRuntime.DDLM.1.8.msix", "MSIX.inventory" })
        {
            File.WriteAllText(Path.Combine(msix, file), string.Empty);
        }

        var project = Project(["net11.0-windows10.0.19041.0"], winUI: true, packageType: "MSIX", packages:
        [
            new PackageDependencyInfo("net11.0-windows10.0.19041.0", "Microsoft.WindowsAppSDK", "1.8.260529003", ["Microsoft.WindowsAppSDK.Runtime"]),
            new PackageDependencyInfo("net11.0-windows10.0.19041.0", "Microsoft.WindowsAppSDK.Runtime", "1.8.260529003", []),
        ]);
        static IEnumerable<string> List(string folder) => Directory.Exists(folder) ? Directory.EnumerateFiles(folder) : [];
        var dependencies = WindowsApps.FrameworkPackages(project, root, "x64", List);
        Assert.Equal([Path.Combine(msix, "Microsoft.WindowsAppRuntime.1.8.msix")], dependencies);
        Assert.Empty(WindowsApps.FrameworkPackages(project, root, "arm64", List));
        Assert.Equal(Path.Combine("/home/me", ".nuget", "packages"), WindowsApps.PackagesRoot(_ => null, "/home/me"));
        Assert.Equal("/cache", WindowsApps.PackagesRoot(n => n == "NUGET_PACKAGES" ? "/cache" : null, "/home/me"));

        // PowerShell's command: the manifest and the dependencies in single quotes (a quote doubled), failing on errors.
        var arguments = WindowsApps.RegisterArguments(@"C:\src\Bob's App\bin\AppxManifest.xml", [@"C:\p\a.msix", @"C:\p\b.msix"]);
        Assert.Equal(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command"], arguments.Take(5));
        Assert.Equal(@"$ErrorActionPreference = 'Stop'; Add-AppxPackage -Register 'C:\src\Bob''s App\bin\AppxManifest.xml' -ForceApplicationShutdown -ForceUpdateFromAnyVersion -DependencyPath 'C:\p\a.msix','C:\p\b.msix'", arguments[^1]);
        Assert.DoesNotContain("-DependencyPath", WindowsApps.RegisterArguments("m.xml", [])[^1], StringComparison.Ordinal);

        // The companion's activation: its arguments, and the process id it prints.
        Assert.Equal(["--activate-app", "A_b!App"], WindowsApps.ActivateArguments("A_b!App", null));
        Assert.Equal(["--activate-app", "A_b!App", "--arguments", "--verbose"], WindowsApps.ActivateArguments("A_b!App", "--verbose"));
        Assert.Equal(4242, WindowsApps.ParseProcessId("4242\n"));
        Assert.Equal(4242, WindowsApps.ParseProcessId("warming up\r\n4242\r\n"));
        Assert.Null(WindowsApps.ParseProcessId("Windows didn't start A_b!App: Class not registered (0x80040154)."));
        Assert.Null(WindowsApps.ParseProcessId(string.Empty));

        // A failed registration's hint.
        Assert.Contains("Developer Mode", WindowsApps.RegisterFailureHint("Deployment failed with HRESULT: 0x80073CFF, To install this application you need either a Windows developer license"), StringComparison.Ordinal);
        Assert.Contains("Windows App SDK runtime", WindowsApps.RegisterFailureHint("Deployment failed with HRESULT: 0x80073CF3, Package failed updates, dependency or conflict validation."), StringComparison.Ordinal);
        Assert.Null(WindowsApps.RegisterFailureHint("Something else"));
    }

    [Fact]
    public void Check_environment_lists_developer_mode_and_the_runtime_with_their_fixes()
    {
        Assert.Equal(true, WindowsApps.DeveloperMode(1));
        Assert.Equal(false, WindowsApps.DeveloperMode(0));
        Assert.Equal(false, WindowsApps.DeveloperMode(null));
        Assert.Null(WindowsApps.DeveloperMode("yes"));

        var runtimes = WindowsApps.ParseRuntimes("""[{"Name":"Microsoft.WindowsAppRuntime.1.8","Version":"8000.616.304.0","Architecture":9},{"Name":"Microsoft.WindowsAppRuntime.1.6","Version":"6000.519.329.0","Architecture":"X86"},{"Name":"Microsoft.WindowsAppRuntime.Main.1.8","Version":"1.0.0.0"}]""");
        Assert.Equal([new WindowsAppRuntime("1.8", "8000.616.304.0", "x64"), new WindowsAppRuntime("1.6", "6000.519.329.0", "X86")], runtimes);
        Assert.Single(WindowsApps.ParseRuntimes("""{"Name":"Microsoft.WindowsAppRuntime.1.7","Version":"7000.1.2.0","Architecture":12}"""));
        Assert.Empty(WindowsApps.ParseRuntimes(string.Empty));
        Assert.Empty(WindowsApps.ParseRuntimes("not json"));

        PackageDependencyInfo[] sdk18 = [new("net11.0-windows10.0.19041.0", "Microsoft.WindowsAppSDK", "1.8.260529003", [])];
        var unpackaged = Project(["net11.0-windows10.0.19041.0"], winUI: true, packageType: "None", packages: sdk18);
        var packaged = unpackaged with { Path = "/src/B/B.csproj", WindowsPackageType = "MSIX" };
        Assert.Equal("1.8", WindowsApps.RuntimeChannel(unpackaged));

        // Unpackaged only: the runtime, found. Packaged too: Developer Mode, off here, with the Settings page.
        var rows = WindowsApps.ToolRows([unpackaged], developerMode: false, runtimes);
        var runtime = Assert.Single(rows);
        Assert.Equal(("windows-app-runtime:1.8", "Windows App SDK runtime 1.8", ToolState.Found, "8000.616.304.0"), (runtime.Id, runtime.Title, runtime.State, runtime.Version));
        rows = WindowsApps.ToolRows([unpackaged, packaged], developerMode: false, []);
        var developer = Assert.Single(rows, r => r.Id == WindowsApps.DeveloperModeToolId);
        Assert.Equal((ToolState.Missing, "off"), (developer.State, developer.Version));
        Assert.Equal(ToolState.Missing, Assert.Single(rows, r => r.Id == "windows-app-runtime:1.8").State);
        Assert.Equal(ToolState.Found, Assert.Single(WindowsApps.ToolRows([packaged], developerMode: true, runtimes), r => r.Id == WindowsApps.DeveloperModeToolId).State);
        Assert.Empty(WindowsApps.ToolRows([Project(["net11.0-windows"]) with { UsesWpf = true }], developerMode: false, null));

        var settings = WindowsApps.PlanFor(developer, hasWinget: true);
        Assert.Equal((InstallKind.OpenPage, "ms-settings:developers"), (settings?.Kind, settings?.Command));
        var winget = WindowsApps.PlanFor(rows.Single(r => r.Id == "windows-app-runtime:1.8"), hasWinget: true);
        Assert.Equal(InstallKind.Run, winget?.Kind);
        Assert.Contains("--id Microsoft.WindowsAppRuntime.1.8 --exact", winget?.Command, StringComparison.Ordinal);
        Assert.Equal(WindowsApps.RuntimeDownloadPage, WindowsApps.PlanFor(rows.Single(r => r.Id == "windows-app-runtime:1.8"), hasWinget: false)?.Command);
        Assert.Null(WindowsApps.PlanFor(new ToolStatus("git", "Git", true, ToolState.Missing, "x"), hasWinget: true));
    }

    [Fact]
    public void Hot_reload_runs_one_framework_of_a_multi_targeted_app()
    {
        var output = new OutputService();
        var runs = new RunService(null!, output, _ => { });
        var plan = new LaunchPlan("App.exe", string.Empty, "/src/App", new Dictionary<string, string>(), null);
        var watch = runs.HotReloadPlan("/src/App/App.csproj", plan, "--flag", "net10.0-windows10.0.19041.0");
        Assert.Equal("watch run --project \"/src/App/App.csproj\" --no-launch-profile --non-interactive --framework net10.0-windows10.0.19041.0 -- --flag", watch.Arguments);
        Assert.DoesNotContain("--framework", runs.HotReloadPlan("/src/App/App.csproj", plan, null).Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Winui_xaml_compiler_errors_become_squiggles_in_their_files()
    {
        var page = Path.Combine(_dir, "MainPage.xaml");
        var squiggles = XamlBuildDiagnostics.ByFile(
        [
            new BuildDiagnosticInfo(BuildSeverities.Error, "WMC1110", "Invalid binding path 'ViewModel.NoSuchItems' : Property 'NoSuchItems' not found on type 'MainViewModel'.", page, 34, 51, 0, 0, "/src/App/App.csproj", null),
            new BuildDiagnosticInfo(BuildSeverities.Error, "CS0103", "The name 'x' does not exist", Path.Combine(_dir, "MainPage.xaml.cs"), 3, 1, 0, 0, "/src/App/App.csproj", null),
        ]);
        var squiggle = Assert.Single(Assert.Single(squiggles).Value);
        Assert.Equal(("WMC1110", 33, 50), (squiggle.Code, squiggle.Range.Start.Line, squiggle.Range.Start.Character));
        Assert.Equal(page, Assert.Single(squiggles).Key);
    }
}
