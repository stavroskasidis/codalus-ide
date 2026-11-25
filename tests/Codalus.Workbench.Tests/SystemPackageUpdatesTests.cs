using System.ComponentModel;
using Codalus.Workbench.Updates;

namespace Codalus.Workbench.Tests;

/// <summary>How a .deb, .rpm or Arch install gets a release: what the repository it has can give, and the command for the rest.</summary>
public sealed class SystemPackageUpdatesTests : IDisposable
{
    private const string Releases = "https://example.com/releases/download";
    private const string StableUpgradeApt = "sudo apt update && sudo apt install --only-upgrade codalus";
    private const string NightlyVersion = "1.1.0-nightly.20261123.1505";
    private static readonly InstallInfo s_stableInstall = new("1.0.0", InstallKind.SystemPackage);
    private static readonly UpdateOffer s_stable = new("1.1.0", "stable", false, null, null);
    private static readonly UpdateOffer s_nightly = new(NightlyVersion, "nightly", false, null, null);
    private static readonly UpdateOffer s_older = new("1.0.0", "stable", true, null, null);

    private readonly string _root = Directory.CreateTempSubdirectory("codalus-packages-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("stable\n", SystemPackageUpdateKind.Upgrade, StableUpgradeApt)]
    [InlineData("stable\nstable\n", SystemPackageUpdateKind.Upgrade, StableUpgradeApt)] // one per architecture
    [InlineData("nightly\nstable\n", SystemPackageUpdateKind.Upgrade, StableUpgradeApt)]
    [InlineData("nightly\n", SystemPackageUpdateKind.NoRepository, null)]
    [InlineData("", SystemPackageUpdateKind.NoRepository, null)] // the .deb on its own
    public async Task A_stable_release_is_apts_to_upgrade_only_with_the_stable_suite(string suites, SystemPackageUpdateKind kind, string? command)
    {
        var update = await Apt(suites).PlanAsync(s_stableInstall, s_stable, TestContext.Current.CancellationToken);

        Assert.Equal(kind, update.Kind);
        Assert.Equal(command ?? $"cd \"$(mktemp -d)\" && curl -fLO {Releases}/v1.1.0/codalus-1.1.0-linux-x64.deb && sudo apt install ./codalus-1.1.0-linux-x64.deb", update.Command);
    }

    [Theory]
    [InlineData("stable\n")]
    [InlineData("nightly\n")] // the repository's nightly channel moves on with stable releases, so it may not have it either
    public async Task A_nightly_is_installed_from_its_package_whatever_apt_follows(string suites)
    {
        var update = await Apt(suites).PlanAsync(s_stableInstall, s_nightly, TestContext.Current.CancellationToken);

        Assert.Equal(SystemPackageUpdateKind.Nightly, update.Kind);
        Assert.EndsWith($"curl -fLO {Releases}/v{NightlyVersion}/codalus-{NightlyVersion}-linux-x64.deb && sudo apt install ./codalus-{NightlyVersion}-linux-x64.deb", update.Command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_older_release_needs_a_downgrade()
    {
        var nightly = new InstallInfo(NightlyVersion, InstallKind.SystemPackage);

        var apt = await Apt("stable\n").PlanAsync(nightly, s_older, TestContext.Current.CancellationToken);
        Assert.Equal(SystemPackageUpdateKind.Downgrade, apt.Kind);
        Assert.EndsWith("sudo apt install --allow-downgrades ./codalus-1.0.0-linux-x64.deb", apt.Command, StringComparison.Ordinal);

        foreach (var distribution in new[] { "fedora", "opensuse-tumbleweed opensuse suse" })
        {
            var rpm = await Rpm(distribution).PlanAsync(nightly, s_older, TestContext.Current.CancellationToken);
            Assert.Equal(SystemPackageUpdateKind.Downgrade, rpm.Kind);
            Assert.EndsWith("sudo rpm -U --oldpackage ./codalus-1.0.0-linux-x64.rpm", rpm.Command, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("fedora", "etc/yum.repos.d", "sudo dnf upgrade --refresh codalus", "dnf install")]
    [InlineData("almalinux rhel centos fedora", "etc/yum.repos.d", "sudo dnf upgrade --refresh codalus", "dnf install")]
    [InlineData("opensuse-tumbleweed opensuse suse", "etc/zypp/repos.d", "sudo zypper refresh && sudo zypper update codalus", "zypper install")]
    public async Task Dnf_and_zypper_upgrade_a_stable_release_only_with_the_stable_repository_enabled(string distribution, string folder, string upgrade, string install)
    {
        var updates = Rpm(distribution);
        async Task Check(SystemPackageUpdateKind kind, string commandEnd)
        {
            var update = await updates.PlanAsync(s_stableInstall, s_stable, TestContext.Current.CancellationToken);
            Assert.Equal(kind, update.Kind);
            Assert.EndsWith(commandEnd, update.Command, StringComparison.Ordinal);
        }

        var package = $"curl -fLO {Releases}/v1.1.0/codalus-1.1.0-linux-x64.rpm && sudo {install} ./codalus-1.1.0-linux-x64.rpm";
        await Check(SystemPackageUpdateKind.NoRepository, package); // the .rpm on its own

        Repository(folder, "codalus.repo", "[codalus-nightly]\nname=Codalus IDE (nightly)\nenabled=1\n");
        await Check(SystemPackageUpdateKind.NoRepository, package);

        Repository(folder, "codalus.repo", "[codalus-stable]\nname=Codalus IDE (stable)\nenabled=1\ngpgcheck=1\n\n[other]\nenabled=0\n");
        await Check(SystemPackageUpdateKind.Upgrade, upgrade);

        Repository(folder, "codalus.repo", "[codalus-stable]\nname=Codalus IDE (stable)\n"); // enabled by default
        await Check(SystemPackageUpdateKind.Upgrade, upgrade);

        Repository(folder, "codalus.repo", "[codalus-stable]\nenabled = 0\n");
        await Check(SystemPackageUpdateKind.NoRepository, package);
    }

    [Fact]
    public async Task Dnf_5s_override_wins()
    {
        Repository("etc/yum.repos.d", "codalus.repo", "[codalus-stable]\nenabled=1\n");
        Repository("etc/dnf/repos.override.d", "99-config_manager.repo", "[codalus-stable]\nenabled=0\n");

        Assert.Equal(SystemPackageUpdateKind.NoRepository, (await Rpm("fedora").PlanAsync(s_stableInstall, s_stable, TestContext.Current.CancellationToken)).Kind);
    }

    [Theory]
    [InlineData("fedora", "dnf install")]
    [InlineData("opensuse-leap suse opensuse", "zypper install")]
    public async Task A_nightly_is_installed_from_its_rpm(string distribution, string install)
    {
        Repository("etc/yum.repos.d", "codalus.repo", "[codalus-nightly]\nenabled=1\n");

        var update = await Rpm(distribution).PlanAsync(s_stableInstall, s_nightly, TestContext.Current.CancellationToken);

        Assert.Equal(SystemPackageUpdateKind.Nightly, update.Kind);
        Assert.EndsWith($"sudo {install} ./codalus-{NightlyVersion}-linux-x64.rpm", update.Command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Arch_gets_stable_releases_with_the_install_command_and_no_nightly()
    {
        var arch = s_stableInstall with { UpdateCommand = InstallInfo.ArchInstallCommand };
        var updates = new SystemPackageUpdates(Releases, "linux-x64", (program, _, _, _) => throw new InvalidOperationException(program), () => "arch", _root);

        Assert.Equal(new SystemPackageUpdate(SystemPackageUpdateKind.InstallCommand, InstallInfo.ArchInstallCommand), await updates.PlanAsync(arch, s_stable, TestContext.Current.CancellationToken));
        Assert.Equal(new SystemPackageUpdate(SystemPackageUpdateKind.NoArchNightly), await updates.PlanAsync(arch, s_nightly, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null)] // neither tool is installed
    [InlineData("config-files")] // the .deb was removed
    public async Task A_copy_no_package_manager_installed_has_no_command(string? dpkgStatus)
    {
        var updates = new SystemPackageUpdates(Releases, "linux-x64", (program, _, _, _) => (program, dpkgStatus) switch
        {
            ("dpkg-query", { } status) => Task.FromResult((0, status)),
            ("rpm", not null) => Task.FromResult((1, "package codalus is not installed\n")),
            _ => throw new Win32Exception(2),
        }, () => "debian", _root);

        Assert.Equal(new SystemPackageUpdate(SystemPackageUpdateKind.Unknown), await updates.PlanAsync(s_stableInstall, s_stable, TestContext.Current.CancellationToken));
    }

    /// <summary>A machine whose codalus is dpkg's, and whose apt reads <paramref name="suites"/> of the repository.</summary>
    private SystemPackageUpdates Apt(string suites) => new(Releases, "linux-x64", (program, arguments, _, _) => program switch
    {
        "dpkg-query" => Task.FromResult((0, "installed")),
        "apt-get" when arguments[0] == "indextargets" => Task.FromResult((0, suites)),
        _ => throw new Win32Exception(2),
    }, () => "ubuntu debian", _root);

    /// <summary>A machine whose codalus is rpm's; its repositories are the files in the test's folder.</summary>
    private SystemPackageUpdates Rpm(string distribution) => new(Releases, "linux-x64", (program, _, _, _) => program switch
    {
        "rpm" => Task.FromResult((0, "codalus-1.0.0-1.x86_64\n")),
        _ => throw new Win32Exception(2),
    }, () => distribution, _root);

    private void Repository(string folder, string file, string text) =>
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(_root, folder)).FullName, file), text);
}
