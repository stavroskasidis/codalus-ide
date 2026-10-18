using System.Runtime.InteropServices;
using Codalus.Platform;
using Codalus.Plugins.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Notifications;
using Codalus.Workbench.Plugins;
using Codalus.Workbench.Setup;
using Codalus.Workbench.Updates;
using InstallKind = Codalus.Workbench.Setup.InstallKind;

namespace Codalus.Workbench.Tests;

/// <summary>
/// M49: the IDE's own .NET runtime in Check Environment, the .NET root that has it, the plugin hosts that can't start
/// reported once, and an update's runtime from its signed feed. Fake .NET roots only; nothing is installed.
/// </summary>
public sealed class HostRuntimeSetupTests : IDisposable
{
    private const string Rc1 = "11.0.0-rc.1.26425.128";
    private const string Rc2 = "11.0.0-rc.2.26504.105";

    private readonly string _dir = Directory.CreateTempSubdirectory("codalus-hostruntime-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string N(string path) => path.Replace('\\', '/');

    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    private static IReadOnlyList<FrameworkVersion> Versions(params string[] versions) => [.. versions.Select(FrameworkVersion.Parse)];

    [Theory]
    [InlineData("10.0.12", false)]
    [InlineData(Rc1, false)]
    [InlineData(Rc2, true)]
    [InlineData("11.0.0", true)]
    [InlineData("11.0.3", true)]
    [InlineData("12.0.0-preview.1.27101.1", true)]
    public async Task The_ides_runtime_is_a_row_of_its_own_that_installs_the_hosts_channel(string runtime, bool enough)
    {
        // The hosts ask for the release candidate with rollForward Major (M30b): an older .NET alone can't start them,
        // whatever SDKs it has. Their root is DOTNET_ROOT.
        var check = new EnvironmentCheck(PlatformKind.Linux, Env(("PATH", "/x"), ("DOTNET_ROOT", "/x"), ("HOME", "/home/u")), (_, arguments, _, _) => Task.FromResult(string.Join(' ', arguments) switch
        {
            "--list-sdks" => (0, "10.0.401 [/x/sdk]\n"),
            _ => (1, string.Empty),
        }), path => N(path) is "/x/dotnet" or "/x/dotnet.exe")
        {
            HostsRuntime = Rc2,
            InstalledFrameworks = (root, name) => N(root) == "/x" && name == FrameworkReference.NetCoreApp ? Versions("10.0.12", runtime) : [],
        };

        var tools = await check.CheckAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(ToolState.Found, tools.Single(t => t.Id == "dotnet").State); // the SDK row is about SDKs now
        var row = tools.Single(t => t.Id == "dotnet-runtime");
        Assert.True(row.Required);
        Assert.Equal(enough ? ToolState.Found : ToolState.Missing, row.State);
        if (enough)
        {
            Assert.Equal(runtime, row.Version);
            return;
        }

        Assert.Equal(("11.0", Rc2), (row.WantedChannel, row.WantedVersion));
        Assert.Equal($"{FrameworkResolver.Describe(Versions("10.0.12", runtime))} in /x", N(row.Found!));
        Assert.Contains($"need Microsoft.NETCore.App {Rc2} (rollForward Major), which isn't installed in /x", N(row.Detail!), StringComparison.Ordinal);
        var plan = InstallPlans.For(row, PlatformKind.MacOS, "/Users/u", false, null)!;
        Assert.Equal(("dotnet-runtime", InstallKind.Run, "/Users/u/.dotnet"), (plan.ToolId, plan.Kind, plan.AddsToPath));
        Assert.Contains("dotnet-install.sh | bash -s -- --channel 11.0 --install-dir \"/Users/u/.dotnet\"", plan.Command, StringComparison.Ordinal);
        Assert.Contains("with the runtime Codalus IDE needs", plan.Summary, StringComparison.Ordinal);
        Assert.Contains("Microsoft.DotNet.SDK.", InstallPlans.For(row, PlatformKind.Windows, @"C:\Users\u", true, null)!.Command, StringComparison.Ordinal);
    }

    /// <summary>
    /// The maintainer's Mac (0.4.0-nightly.20261018.224): .NET 8, 9 and 10 in /usr/local/share/dotnet, which the hosts use,
    /// and 10.0.12 and 11.0.0-rc.1 in ~/.dotnet; the hosts ask for rc.2 with Major. Missing under both, and the row says
    /// what each has. Once rc.2 is in ~/.dotnet, that root wins over the one on the PATH.
    /// </summary>
    [Fact]
    public void On_the_macs_machine_the_runtime_is_missing_under_both_roots_and_the_root_with_it_wins_once_installed()
    {
        const string System = "/usr/local/share/dotnet";
        const string User = "/Users/ada/.dotnet";
        var runtimes = new Dictionary<string, List<string>>
        {
            [System] = ["8.0.20", "9.0.9", .. Enumerable.Range(0, 10).Select(p => $"10.0.{p}")],
            [User] = ["10.0.12", Rc1],
        };
        var sdks = new Dictionary<string, List<string>> { [System] = ["8.0.414", "9.0.305", "10.0.109"], [User] = ["11.0.100-rc.1.26425.128"] };
        var files = new HashSet<string> { System + "/dotnet", User + "/dotnet" };
        var environment = Env(("PATH", "/usr/local/share/dotnet:/usr/bin:/bin"), ("HOME", "/Users/ada"));
        var check = new EnvironmentCheck(PlatformKind.MacOS, environment, exists: path => files.Contains(N(path)))
        {
            HostsRuntime = Rc2,
            SdkVersions = folder => sdks.GetValueOrDefault(N(folder)) ?? [],
            InstalledFrameworks = (root, _) => Versions([.. runtimes.GetValueOrDefault(N(root)) ?? []]),
            Roots = new DotnetRootResolver { OS = "macos", Architecture = Architecture.Arm64, ProcessEnvironment = environment, ReadFirstLine = _ => null },
        };

        // Neither root has it: the PATH's root stays (as before M49), and the row names both.
        Assert.Equal(System, N(check.FindSdkRoot()!));
        var row = check.HostRuntimeStatus();
        Assert.Equal(ToolState.Missing, row.State);
        Assert.Equal(System, N(row.Path!));
        Assert.Equal($"8.0.20, 9.0.9, 10.0.0–10.0.9 in {System}; 10.0.12, {Rc1} in {User}", N(row.Found!));

        // Install .NET 11 into ~/.dotnet (what Install .NET 11.0 runs): that root has the runtime, so it's the one.
        runtimes[User].Add(Rc2);
        sdks[User].Add("11.0.100-rc.2.26504.105");
        Assert.Equal(User, N(check.FindSdkRoot()!));
        Assert.Equal(ToolState.Found, check.RuntimeStatusFor([FrameworkReference.Create(FrameworkReference.NetCoreApp, FrameworkVersion.Parse(Rc2), RollForwardPolicy.Major)]).State);
        // With DOTNET_ROOT set to it (UseUserDotnet does), the hosts' root has it.
        var after = new EnvironmentCheck(PlatformKind.MacOS, name => name == "DOTNET_ROOT" ? User : environment(name), exists: path => files.Contains(N(path)))
        {
            HostsRuntime = Rc2,
            SdkVersions = check.SdkVersions,
            InstalledFrameworks = check.InstalledFrameworks,
            Roots = new DotnetRootResolver { OS = "macos", Architecture = Architecture.Arm64, ProcessEnvironment = name => name == "DOTNET_ROOT" ? User : environment(name), ReadFirstLine = _ => null },
        };
        Assert.Equal((ToolState.Found, Rc2), (after.HostRuntimeStatus().State, after.HostRuntimeStatus().Version));
    }

    [Fact]
    public void The_ides_requirement_comes_from_its_hosts_runtimeconfig_files()
    {
        WriteConfig(Path.Combine(_dir, "pluginhost"), "codalus-pluginhost", Rc2, "Major");
        WriteConfig(Path.Combine(_dir, "buildhost"), "codalus-buildhost", "10.0.0", "Minor");
        WriteConfig(Path.Combine(_dir, "wpfpreviewer"), "codalus-wpfpreviewer", "8.0.0", "Minor"); // on the app's runtime: not the IDE's
        var reference = Assert.Single(IdeRuntime.Requirements(_dir, _ => null));
        Assert.Equal((FrameworkReference.NetCoreApp, Rc2, RollForwardPolicy.Major), (reference.Name, reference.Version.Text, reference.RollForward));
        Assert.Equal("11.0", IdeRuntime.Channel(reference));
        Assert.Empty(IdeRuntime.Requirements(Path.Combine(_dir, "empty"), _ => null));
    }

    [Fact]
    public async Task Plugin_hosts_whose_runtime_is_missing_are_reported_once_and_their_plugins_retried_after_the_install()
    {
        // Two plugins in one host group; the host is a fake app host whose runtimeconfig asks for a .NET the fake root lacks.
        var root = Path.Combine(_dir, "dotnet");
        Directory.CreateDirectory(Path.Combine(root, "shared", FrameworkReference.NetCoreApp, "10.0.9"));
        File.WriteAllText(Path.Combine(root, "shared", FrameworkReference.NetCoreApp, "10.0.9", FrameworkReference.NetCoreApp + ".deps.json"), "{}");
        var hostFolder = Path.Combine(_dir, "pluginhost");
        WriteConfig(hostFolder, "codalus-pluginhost", Rc2, "Major");
        var host = Path.Combine(hostFolder, OperatingSystem.IsWindows() ? "codalus-pluginhost.exe" : "codalus-pluginhost");
        File.WriteAllText(host, "not a program");
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();
        var launch = new ProcessLaunchInfo(host, []) { Environment = new Dictionary<string, string> { ["DOTNET_ROOT"] = root, ["DOTNET_ROOT_" + arch] = root } };
        foreach (var id in new[] { "samples.one", "samples.two" })
        {
            var folder = Path.Combine(_dir, "plugins", id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "plugin.dll"), string.Empty);
            File.WriteAllText(Path.Combine(folder, PluginManifest.FileName), $$"""
                { "id": "{{id}}", "version": "1.0.0", "displayName": "{{id}}", "publisher": "samples", "engines": { "codalus": "^1.0.0" },
                  "entry": { "runtime": "dotnet", "assembly": "plugin.dll" }, "activationEvents": [ "onStartup" ] }
                """);
        }

        var paths = AppPaths.Resolve(PlatformInfo.Current, _ => null, Path.Combine(_dir, "user-data"));
        paths.EnsureCreated();
        using var workbench = new WorkbenchServices(paths, PlatformInfo.Current, watchFiles: false);
        await using var manager = new PluginManager(workbench, _ => launch, "1.0.0", new SupervisionPolicy { ShutdownRequest = null, ExitNotification = null });
        var reports = new List<HostRuntimeMissingException>();
        manager.HostRuntimeMissing += (_, e) => reports.Add(e);
        manager.AddPlugins(PluginDiscovery.Scan([(Path.Combine(_dir, "plugins"), false)], PlatformInfo.Current));
        Assert.Equal(2, manager.Plugins.Count(p => p.IsUsable));

        await manager.ActivateStartupPluginsAsync(TestContext.Current.CancellationToken);
        await manager.ActivateStartupPluginsAsync(TestContext.Current.CancellationToken); // a second event: still one report

        var report = Assert.Single(reports);
        Assert.Contains($"Microsoft.NETCore.App {Rc2}", report.Message, StringComparison.Ordinal);
        Assert.All(manager.Plugins, p => Assert.Equal((PluginState.Failed, true), (p.State, p.FailedWithItsHost)));
        Assert.All(manager.Plugins, p => Assert.Contains("isn't installed", p.Error, StringComparison.Ordinal));
        Assert.DoesNotContain(workbench.Notifications.Items, n => n.Severity == NotificationSeverity.Error); // no error per plugin

        // The runtime is installed: the plugins are activated again (their host now starts, and this fake one fails to run).
        Directory.CreateDirectory(Path.Combine(root, "shared", FrameworkReference.NetCoreApp, Rc2));
        File.WriteAllText(Path.Combine(root, "shared", FrameworkReference.NetCoreApp, Rc2, FrameworkReference.NetCoreApp + ".deps.json"), "{}");
        await manager.RetryFailedHostsAsync(TestContext.Current.CancellationToken);
        Assert.Single(reports);
        Assert.All(manager.Plugins, p => Assert.DoesNotContain("isn't installed", p.Error ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void An_updates_runtime_comes_from_its_signed_feed()
    {
        const string Feed = """
            { "Assets": [
              { "PackageId": "Codalus", "Version": "0.4.0-nightly.20261018.224", "Type": "Full", "FileName": "a.nupkg", "SHA256": "00", "Size": 1,
                "CodalusFrameworks": [ { "Name": "Microsoft.NETCore.App", "Version": "11.0.0-rc.2.26504.105", "RollForward": "Major" } ] },
              { "PackageId": "Codalus", "Version": "0.4.0-nightly.20261018.224", "Type": "Delta", "FileName": "b.nupkg", "SHA256": "00", "Size": 1 },
              { "PackageId": "Codalus", "Version": "0.3.0", "Type": "Full", "FileName": "c.nupkg", "SHA256": "00", "Size": 1 }
            ] }
            """;
        var reference = Assert.Single(UpdateRuntime.Requirements(Feed, "0.4.0-nightly.20261018.224", _ => null));
        Assert.Equal((Rc2, RollForwardPolicy.Major, false), (reference.Version.Text, reference.RollForward, reference.PreferRelease));
        // The same environment variables apply to the updated hosts.
        Assert.Equal(RollForwardPolicy.LatestMajor, UpdateRuntime.Requirements(Feed, "0.4.0-nightly.20261018.224", n => n == "DOTNET_ROLL_FORWARD" ? "LatestMajor" : null)[0].RollForward);
        // Releases from before M49, and feeds that can't be read, ask for nothing.
        Assert.Empty(UpdateRuntime.Requirements(Feed, "0.3.0", _ => null));
        Assert.Empty(UpdateRuntime.Requirements("not json", "0.3.0", _ => null));
        Assert.Empty(UpdateRuntime.Requirements(null, "0.3.0", _ => null));
    }

    private static void WriteConfig(string folder, string name, string version, string rollForward)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + ".runtimeconfig.json"), $$"""
            { "runtimeOptions": { "tfm": "net11.0", "rollForward": "{{rollForward}}", "framework": { "name": "Microsoft.NETCore.App", "version": "{{version}}" } } }
            """);
    }
}
