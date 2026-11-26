using Codalus.Platform;
using Codalus.Workbench.Sdks;
using Codalus.Workbench.Setup;

namespace Codalus.Workbench.Tests.Sdks;

/// <summary>
/// The SDK manager's logic (ADR 0112) on described machines and the samples' release metadata: feature bands and
/// updates, what's available, the install, update and removal plans for each kind of folder, and every removal guard.
/// Nothing runs: the plans are only read.
/// </summary>
public sealed class DotnetSdkPlannerTests
{
    private const string Home = "/home/u/.dotnet";
    private static readonly DateOnly s_today = new(2026, 11, 26);
    private static readonly string[] s_channels = ["8.0", "9.0", "10.0", "11.0"];

    private static DotnetSdkFacts Facts(FakeDotnetMachine machine, string inUse = Home, params string[] others)
    {
        var channels = DotnetReleaseParser.ParseIndex(File.ReadAllText(Path.Combine(DotnetReleasesTests.Samples, "releases-index.json")));
        var releases = s_channels.ToDictionary(c => c, c => DotnetReleaseParser.ParseChannel(File.ReadAllText(Path.Combine(DotnetReleasesTests.Samples, c, "releases.json"))));
        return new DotnetSdkFacts(machine, DotnetInstallations.Read(machine, [inUse, .. others], inUse), s_today)
        {
            Channels = channels,
            Releases = releases,
            Install = new DotnetInstallFacts(machine.Platform, machine.Home) { LinuxDistribution = "ubuntu debian", HasWinget = machine.Platform == PlatformKind.Windows },
        };
    }

    [Theory]
    [InlineData("10.0.104", "10.0.1xx", "10.0.100")]
    [InlineData("10.0.401", "10.0.4xx", "10.0.400")]
    [InlineData("11.0.100-rc.2.26504.105", "11.0.1xx", "11.0.100")]
    public void A_version_has_a_feature_band(string sdk, string band, string folder)
    {
        Assert.Equal(band, DotnetSdkPlanner.BandOf(sdk));
        Assert.Equal(folder, DotnetSdkPlanner.BandFolderOf(sdk));
    }

    [Fact]
    public void An_sdk_updates_to_the_newest_of_its_band_and_says_when_that_fixes_security_issues()
    {
        var machine = new FakeDotnetMachine().WithRoot(Home, ["10.0.110", "10.0.400", "11.0.100"], (DotnetFrameworks.NetCore, "10.0.11"), (DotnetFrameworks.NetCore, "11.0.0"));

        var rows = DotnetSdkPlanner.InstalledSdks(Facts(machine)).ToDictionary(r => r.Version);

        Assert.Equal(("10.0.112", true), (rows["10.0.110"].Update, rows["10.0.110"].SecurityUpdate));
        Assert.Equal(("10.0.401", true), (rows["10.0.400"].Update, rows["10.0.400"].SecurityUpdate));
        Assert.Null(rows["11.0.100"].Update);
        Assert.Equal("Until 2028-11-14 (LTS)", rows["10.0.110"].Support);
        Assert.Equal("Until 2028-11-14 (STS)", rows["11.0.100"].Support);
        var runtime = DotnetSdkPlanner.InstalledRuntimes(Facts(machine)).Single(r => r.Runtime.Version == "10.0.11");
        Assert.Equal(("10.0.12", true), (runtime.Update, runtime.SecurityUpdate));
    }

    [Fact]
    public void Available_offers_another_channels_sdk_and_runtimes_with_previews_and_old_channels_only_when_asked()
    {
        var machine = new FakeDotnetMachine().WithRoot(Home, ["11.0.100"], (DotnetFrameworks.NetCore, "11.0.0"), (DotnetFrameworks.AspNetCore, "11.0.0"));
        var facts = Facts(machine);

        var titles = DotnetSdkPlanner.Available(facts, showPreviews: false, showOutOfSupport: false).Select(a => a.Title).ToList();
        Assert.Equal([".NET SDK 10.0.401", ".NET Runtime 10.0.12", "ASP.NET Core Runtime 10.0.12"], titles);

        var all = DotnetSdkPlanner.Available(facts, showPreviews: true, showOutOfSupport: true);
        Assert.Contains(all, a => a is { Title: ".NET SDK 12.0.100-preview.1.27060.1", Preview: true });
        Assert.Contains(all, a => a is { Title: ".NET SDK 9.0.308", OutOfSupport: true });
        Assert.DoesNotContain(all, a => a.Framework == DotnetFrameworks.WindowsDesktop); // Windows only
    }

    [Fact]
    public void Available_offers_a_newer_band_of_an_installed_channel_but_not_its_update()
    {
        var machine = new FakeDotnetMachine().WithRoot(Home, ["10.0.110"], (DotnetFrameworks.NetCore, "10.0.10"));

        var sdks = DotnetSdkPlanner.Available(Facts(machine), false, false).Where(a => a.IsSdk).Select(a => a.Version).ToList();

        Assert.Contains("10.0.401", sdks);
        Assert.DoesNotContain("10.0.112", sdks);
    }

    [Fact]
    public void An_install_into_the_users_folder_is_dotnet_install_of_that_exact_version_with_no_password()
    {
        var machine = new FakeDotnetMachine().WithRoot(Home, ["11.0.100"], (DotnetFrameworks.NetCore, "11.0.0"));

        var sdk = Assert.Single(DotnetSdkPlanner.InstallOptions(Facts(machine), AvailableDotnet.Sdk, "10.0.401"));
        Assert.Equal(InstallKind.Run, sdk.Kind);
        Assert.Equal("curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --version 10.0.401 --install-dir \"/home/u/.dotnet\"", sdk.Command);
        Assert.Equal(["-c", sdk.Command], sdk.Arguments);

        var runtime = Assert.Single(DotnetSdkPlanner.InstallOptions(Facts(machine), DotnetFrameworks.AspNetCore, "10.0.12"));
        Assert.Contains("--version 10.0.12 --runtime aspnetcore", runtime.Command, StringComparison.Ordinal);
    }

    [Fact]
    public void A_package_folder_offers_the_distributions_package_and_dotnet_install_for_the_exact_version()
    {
        var machine = new FakeDotnetMachine().WithRoot("/usr/lib/dotnet", ["11.0.100"], (DotnetFrameworks.NetCore, "11.0.0"));

        var options = DotnetSdkPlanner.InstallOptions(Facts(machine, "/usr/lib/dotnet"), AvailableDotnet.Sdk, "10.0.401");

        Assert.Equal(["package", "dotnet-install"], options.Select(o => o.Option));
        Assert.Equal("sudo apt-get install -y dotnet-sdk-10.0", options[0].Command);
        Assert.True(options[0].Recommended);
        Assert.Contains("--install-dir \"/home/u/.dotnet\"", options[1].Command, StringComparison.Ordinal);
        Assert.Contains("/usr/lib/dotnet", options[1].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_dotnet_yet_the_systems_own_way_comes_first_then_dotnet_install()
    {
        var linux = Facts(new FakeDotnetMachine());
        Assert.Null(linux.Target);
        var options = DotnetSdkPlanner.InstallOptions(linux, AvailableDotnet.Sdk, "11.0.100");
        Assert.Equal(["package", "dotnet-install"], options.Select(o => o.Option));
        Assert.Equal("sudo apt-get install -y dotnet-sdk-11.0", options[0].Command);

        var mac = Facts(new FakeDotnetMachine(PlatformKind.MacOS, "/Users/u"), "/Users/u/.dotnet");
        Assert.Equal(["pkg", "dotnet-install"], DotnetSdkPlanner.InstallOptions(mac, AvailableDotnet.Sdk, "11.0.100").Select(o => o.Option));
    }

    [Fact]
    public void Windows_installs_and_removes_with_winget_by_version()
    {
        var machine = new FakeDotnetMachine(PlatformKind.Windows, @"C:\Users\u").WithRoot(@"C:\Program Files\dotnet", ["10.0.110"], (DotnetFrameworks.NetCore, "10.0.10"));
        var facts = Facts(machine, @"C:\Program Files\dotnet");
        Assert.Equal(DotnetRootOwner.WindowsInstaller, facts.Target!.Owner);

        var install = Assert.Single(DotnetSdkPlanner.InstallOptions(facts, AvailableDotnet.Sdk, "11.0.100"));
        Assert.StartsWith("winget install --id Microsoft.DotNet.SDK.11 --exact --version 11.0.100", install.Command, StringComparison.Ordinal);

        var removal = DotnetSdkPlanner.RemoveSdk(facts with { Installations = [facts.Target with { InUse = false }] }, facts.Target with { InUse = false }, "10.0.110");
        Assert.StartsWith("winget uninstall --id Microsoft.DotNet.SDK.10 --exact --version 10.0.110", removal.Command!.Command, StringComparison.Ordinal);
        Assert.Equal("Microsoft.DotNet.SDK.Preview", DotnetSdkPlanner.WingetId(AvailableDotnet.Sdk, "12.0.100-preview.1.27060.1"));
        Assert.Equal("Microsoft.DotNet.DesktopRuntime.10", DotnetSdkPlanner.WingetId(DotnetFrameworks.WindowsDesktop, "10.0.12"));
    }

    [Fact]
    public void Removing_an_old_patch_takes_the_runtimes_only_it_brought_with_their_packs_and_templates()
    {
        var machine = new FakeDotnetMachine().WithRoot(Home, ["10.0.110", "10.0.112", "11.0.100"],
            (DotnetFrameworks.NetCore, "10.0.10"), (DotnetFrameworks.NetCore, "10.0.12"), (DotnetFrameworks.AspNetCore, "10.0.10"), (DotnetFrameworks.AspNetCore, "10.0.12"), (DotnetFrameworks.NetCore, "11.0.0"));
        var facts = Facts(machine);

        var removal = DotnetSdkPlanner.RemoveSdk(facts, facts.Target!, "10.0.110");

        Assert.Null(removal.Refused);
        Assert.Null(removal.Command);
        Assert.Equal(
        [
            Home + "/sdk/10.0.110",
            Home + "/shared/Microsoft.NETCore.App/10.0.10",
            Home + "/packs/Microsoft.NETCore.App.Ref/10.0.10",
            Home + "/packs/Microsoft.NETCore.App.Host.linux-x64/10.0.10",
            Home + "/templates/10.0.10",
            Home + "/shared/Microsoft.AspNetCore.App/10.0.10",
            Home + "/packs/Microsoft.AspNetCore.App.Ref/10.0.10",
        ], removal.Folders);
        Assert.Empty(removal.Kept);
    }

    [Fact]
    public void A_runtime_stays_when_its_the_newest_of_its_channel_another_sdk_brought_it_or_the_ides_hosts_run_on_it()
    {
        var machine = new FakeDotnetMachine().WithRoot(Home, ["10.0.111", "10.0.400", "10.0.112", "11.0.100"],
            (DotnetFrameworks.NetCore, "10.0.11"), (DotnetFrameworks.NetCore, "10.0.12"), (DotnetFrameworks.NetCore, "11.0.0"));
        var facts = Facts(machine);

        // 10.0.400 and 10.0.111 came with 10.0.11: removing one keeps it.
        var shared = DotnetSdkPlanner.RemoveSdk(facts, facts.Target!, "10.0.400");
        Assert.Equal([Home + "/sdk/10.0.400"], shared.Folders);
        Assert.Contains(shared.Kept, k => k.Contains("another SDK here brought it", StringComparison.Ordinal));

        // 10.0.12 is the newest .NET 10 runtime here.
        var newest = DotnetSdkPlanner.RemoveSdk(facts, facts.Target!, "10.0.112");
        Assert.Equal([Home + "/sdk/10.0.112"], newest.Folders);
        Assert.Contains(newest.Kept, k => k.Contains("newest .NET 10.0 runtime here", StringComparison.Ordinal));

        // Both 10.0.11 SDKs go at once, but the IDE's hosts run on 10.0.11 (as described).
        var hosts = facts with { HostsStillRun = (_, removed) => !removed.Contains(new DotnetRuntime(DotnetFrameworks.NetCore, "10.0.11")) };
        var both = DotnetSdkPlanner.RemoveSdks(hosts, hosts.Target!, ["10.0.111", "10.0.400"]);
        Assert.Equal([Home + "/sdk/10.0.111", Home + "/sdk/10.0.400"], both.Folders);
        Assert.Contains(both.Kept, k => k.Contains("helper processes run on it", StringComparison.Ordinal));
    }

    [Fact]
    public void Removing_the_open_solutions_sdk_or_the_last_sdk_of_the_ides_folder_is_refused()
    {
        var machine = new FakeDotnetMachine().WithRoot(Home, ["10.0.112", "11.0.100"], (DotnetFrameworks.NetCore, "10.0.12"), (DotnetFrameworks.NetCore, "11.0.0"));
        var facts = Facts(machine) with { SolutionSdk = Home + "/sdk/10.0.112" };

        Assert.Contains("open solution builds with", DotnetSdkPlanner.RemoveSdk(facts, facts.Target!, "10.0.112").Refused, StringComparison.Ordinal);
        Assert.Contains("last SDK", DotnetSdkPlanner.RemoveSdks(facts with { SolutionSdk = null }, facts.Target!, ["11.0.100", "10.0.112"]).Refused, StringComparison.Ordinal);
        var both = DotnetSdkPlanner.RemoveSdks(facts, facts.Target!, ["11.0.100", "10.0.112"]); // the solution's stays, so the other isn't the last
        Assert.Equal([Home + "/sdk/11.0.100"], both.Folders);
        Assert.Contains(both.Kept, k => k.Contains("open solution builds with", StringComparison.Ordinal));
        Assert.Null(DotnetSdkPlanner.RemoveSdk(facts, facts.Target!, "11.0.100").Refused);
    }

    [Fact]
    public void A_runtime_an_sdk_left_here_runs_on_cant_be_removed_and_one_it_doesnt_keeps_the_sdks_packs()
    {
        var machine = new FakeDotnetMachine().WithRoot(Home, ["10.0.110", "11.0.100"], (DotnetFrameworks.NetCore, "10.0.10"), (DotnetFrameworks.AspNetCore, "10.0.10"), (DotnetFrameworks.NetCore, "11.0.0"));
        var facts = Facts(machine);

        Assert.Contains("runs on the .NET Runtime 10.0.10", DotnetSdkPlanner.RemoveRuntime(facts, facts.Target!, new(DotnetFrameworks.NetCore, "10.0.10")).Refused, StringComparison.Ordinal);
        var aspnet = DotnetSdkPlanner.RemoveRuntime(facts, facts.Target!, new(DotnetFrameworks.AspNetCore, "10.0.10"));
        Assert.Equal([Home + "/shared/Microsoft.AspNetCore.App/10.0.10"], aspnet.Folders); // 10.0.110 brought it: its packs stay
        Assert.Contains("helper processes", DotnetSdkPlanner.RemoveRuntime(facts with { HostsStillRun = (_, _) => false }, facts.Target!, new(DotnetFrameworks.NetCore, "11.0.0")).Refused, StringComparison.Ordinal);
    }

    [Fact]
    public void Folders_others_own_are_changed_by_their_tool()
    {
        var package = new FakeDotnetMachine().WithRoot("/usr/lib/dotnet", ["10.0.110"], (DotnetFrameworks.NetCore, "10.0.10")).WithRoot(Home, ["11.0.100"], (DotnetFrameworks.NetCore, "11.0.0"));
        var facts = Facts(package, Home, "/usr/lib/dotnet");
        var packaged = facts.Installations.Single(i => i.Root == "/usr/lib/dotnet");

        Assert.Equal("sudo apt-get remove -y dotnet-sdk-10.0", DotnetSdkPlanner.RemoveSdk(facts, packaged, "10.0.110", "dotnet-sdk-10.0").Command!.Command);
        Assert.Contains("couldn't be told", DotnetSdkPlanner.RemoveSdk(facts, packaged, "10.0.110").Refused, StringComparison.Ordinal);
        Assert.Equal("sudo apt-get install --only-upgrade -y dotnet-sdk-10.0", Assert.Single(DotnetSdkPlanner.UpdateOptions(facts, packaged, AvailableDotnet.Sdk, "10.0.110", "10.0.112", "dotnet-sdk-10.0")).Command);

        var opt = new FakeDotnetMachine().WithRoot("/opt/dot'net", ["10.0.110", "10.0.112"], (DotnetFrameworks.NetCore, "10.0.12"));
        var optFacts = Facts(opt, "/opt/dot'net");
        Assert.Equal(DotnetRootOwner.Administrator, optFacts.Target!.Owner);
        var sudo = DotnetSdkPlanner.RemoveSdk(optFacts, optFacts.Target, "10.0.110").Command!;
        Assert.Equal((InstallKind.Terminal, "sudo rm -rf '/opt/dot'\\''net/sdk/10.0.110'"), (sudo.Kind, sudo.Command));
        Assert.StartsWith("curl -sSL https://dot.net/v1/dotnet-install.sh | sudo bash", Assert.Single(DotnetSdkPlanner.InstallOptions(optFacts, AvailableDotnet.Sdk, "10.0.401")).Command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dotnet-sdk-10.0: /usr/lib/dotnet/sdk/10.0.110\n", "dotnet-sdk-10.0")]
    [InlineData("dotnet-sdk-10.0, dotnet-sdk-dbg: /usr/lib/dotnet/sdk/10.0.110\n", "dotnet-sdk-10.0")]
    [InlineData("dotnet-sdk-10.0\n", "dotnet-sdk-10.0")]
    [InlineData("error: No package owns /usr/share/dotnet/sdk/10.0.110\n", null)]
    [InlineData("file /usr/lib64/dotnet/sdk/x is not owned by any package\n", null)]
    [InlineData("dpkg-query: no path found matching pattern /x\n", null)]
    public void The_package_that_holds_a_path_is_read_from_the_package_managers_answer(string output, string? package) =>
        Assert.Equal(package, DotnetSdkPlanner.PackageOwner(output));

    [Fact]
    public void Old_patches_are_the_sdks_with_a_newer_one_of_their_band()
    {
        var installation = new DotnetInstallation(Home, DotnetRootOwner.User, true) { Sdks = ["10.0.110", "10.0.111", "10.0.112", "10.0.400", "11.0.100"] };

        Assert.Equal(["10.0.110", "10.0.111"], DotnetSdkPlanner.OldPatches(installation, "10.0"));
        Assert.Empty(DotnetSdkPlanner.OldPatches(installation, "11.0"));
    }

    [Fact]
    public void A_new_bands_sdk_is_offered_the_workloads_an_older_band_of_its_channel_has()
    {
        var machine = new FakeDotnetMachine().WithRoot(Home, ["10.0.112", "10.0.401"])
            .WithFile(Home + "/metadata/workloads/10.0.100/InstalledWorkloads/wasm-tools")
            .WithFile(Home + "/metadata/workloads/10.0.100/InstalledWorkloads/maui")
            .WithFile(Home + "/metadata/workloads/11.0.100/InstalledWorkloads/android");
        var installation = DotnetInstallations.Read(machine, [Home], Home)[0];

        Assert.Equal(["maui", "wasm-tools"], DotnetSdkPlanner.WorkloadsToCarry(machine, installation, "10.0.401"));

        machine.WithFile(Home + "/metadata/workloads/10.0.400/InstalledWorkloads/maui");
        Assert.Empty(DotnetSdkPlanner.WorkloadsToCarry(machine, installation, "10.0.401"));
    }
}
