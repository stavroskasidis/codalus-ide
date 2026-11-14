using Codalus.Platform;
using Codalus.Workbench.Setup;

namespace Codalus.Workbench.Tests;

/// <summary>
/// Install .NET's ways on each machine (<see cref="InstallPlans.DotnetOptions"/>): the one that fits installs where the
/// dotnet the user's terminal runs is, the others say when that dotnet won't see what they install. Nothing installs.
/// </summary>
public sealed class DotnetInstallOptionsTests
{
    private static readonly ToolStatus s_missing = new("dotnet", ".NET SDK", true, ToolState.Missing, "Opening .NET projects");

    private static readonly ToolStatus s_runtime = new("dotnet-runtime", ".NET runtime for Codalus IDE", true, ToolState.Missing, "helpers")
    {
        WantedVersion = "11.0.0",
        WantedChannel = "11.0",
    };

    private static DotnetInstallFacts Mac(string? shellRoot, bool arm64 = true) => new(PlatformKind.MacOS, "/Users/u")
    {
        ShellDotnet = shellRoot is null ? null : "/usr/local/bin/dotnet",
        ShellRoot = shellRoot,
        Arm64 = arm64,
    };

    private static string[] Options(IReadOnlyList<InstallPlan> plans) => [.. plans.Select(p => p.Option)];

    [Fact]
    public void On_a_mac_whose_terminal_runs_the_pkgs_dotnet_the_pkg_fits_and_dotnet_install_says_the_terminal_wont_see_it()
    {
        // The maintainer's report: .NET 8-10 from Microsoft's .pkg in /usr/local/share/dotnet, which every shell has on
        // its PATH (/etc/paths.d/dotnet). Install .NET put 11 in ~/.dotnet, which only the IDE then used.
        var plans = InstallPlans.DotnetOptions(s_runtime, Mac("/usr/local/share/dotnet"));
        Assert.Equal(["pkg", "dotnet-install", "page"], Options(plans));
        Assert.Equal([true, false, false], plans.Select(p => p.Recommended));

        var pkg = plans[0];
        Assert.Equal((InstallKind.Terminal, "/usr/local/share/dotnet", null), (pkg.Kind, pkg.AddsToPath, pkg.Note));
        Assert.Equal("pkg=\"$(mktemp -d)/dotnet-sdk.pkg\" && curl -fSL -o \"$pkg\" https://aka.ms/dotnet/11.0/dotnet-sdk-osx-arm64.pkg && sudo installer -pkg \"$pkg\" -target /", pkg.Command);
        Assert.Contains("with the runtime Codalus IDE needs", pkg.Summary, StringComparison.Ordinal);

        var script = plans[1];
        Assert.Equal((InstallKind.Run, "/Users/u/.dotnet"), (script.Kind, script.AddsToPath));
        Assert.Contains("--channel 11.0 --install-dir \"/Users/u/.dotnet\"", script.Command, StringComparison.Ordinal);
        Assert.Equal("Your terminal's dotnet runs from /usr/local/share/dotnet, so it won't see this SDK; only Codalus IDE will use it.", script.Note);
        Assert.Equal((InstallKind.OpenPage, InstallPlans.DotnetDownloadPage), (plans[2].Kind, plans[2].Command));

        // The same machine described the other way round (macOS' folders ignore case).
        Assert.Null(InstallPlans.DotnetOptions(s_runtime, Mac("/USR/local/share/dotnet/"))[0].Note);
    }

    [Fact]
    public void On_a_mac_whose_terminal_runs_a_dotnet_in_the_home_folder_dotnet_install_into_it_fits()
    {
        var plans = InstallPlans.DotnetOptions(s_missing, Mac("/Users/u/dotnet"));
        Assert.Equal(["dotnet-install", "pkg", "page"], Options(plans));
        Assert.True(plans[0].Recommended);
        Assert.Equal("/Users/u/dotnet", plans[0].AddsToPath); // the terminal's own root, not ~/.dotnet
        Assert.Null(plans[0].Note);
        Assert.Equal("Your terminal's dotnet runs from /Users/u/dotnet, so it won't see this SDK; only Codalus IDE will use it.", plans[1].Note);
    }

    [Fact]
    public void On_a_mac_without_dotnet_the_pkg_fits_since_it_puts_dotnet_on_every_shells_path()
    {
        var plans = InstallPlans.DotnetOptions(s_missing, Mac(null, arm64: false));
        Assert.Equal(["pkg", "dotnet-install", "page"], Options(plans));
        Assert.Null(plans[0].Note);
        Assert.Contains("dotnet-sdk-osx-x64.pkg", plans[0].Command, StringComparison.Ordinal);
        Assert.Equal("/Users/u/.dotnet isn't on your terminal's PATH: Codalus IDE will use this SDK, but typing dotnet in a terminal won't find it.", plans[1].Note);

        // Homebrew's dotnet (a wrapper in its Cellar): neither way reaches it, and both say so.
        var brew = InstallPlans.DotnetOptions(s_missing, Mac("/opt/homebrew/Cellar/dotnet/10.0.100/bin"));
        Assert.Equal("pkg", brew[0].Option);
        Assert.All(brew.Take(2), p => Assert.Contains("/opt/homebrew/Cellar/dotnet/10.0.100/bin", p.Note, StringComparison.Ordinal));
    }

    [Fact]
    public void The_pkg_installs_a_pinned_sdk_exactly_and_a_wanted_preview_from_the_channels_previews()
    {
        var pinned = new ToolStatus("dotnet", ".NET SDK", true, ToolState.Mismatch, "global.json") { WantedVersion = "10.0.100" };
        var pkg = InstallPlans.DotnetOptions(pinned, Mac("/usr/local/share/dotnet"))[0];
        Assert.Contains(" https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.100/dotnet-sdk-10.0.100-osx-arm64.pkg ", pkg.Command, StringComparison.Ordinal);
        Assert.StartsWith(".NET SDK 10.0.100 into /usr/local/share/dotnet", pkg.Title, StringComparison.Ordinal);

        var preview = s_runtime with { WantedVersion = "12.0.0-preview.1.27101.1", WantedChannel = "12.0" };
        Assert.Contains(" https://aka.ms/dotnet/12.0/preview/dotnet-sdk-osx-arm64.pkg ", InstallPlans.DotnetOptions(preview, Mac(null))[0].Command, StringComparison.Ordinal);

        // The x64 IDE on Apple silicon: the x64 .NET, which Microsoft's .pkg puts in its own folder.
        var emulated = InstallPlans.DotnetOptions(s_missing, Mac(null) with { EmulatedX64 = true })[0];
        Assert.Contains("dotnet-sdk-osx-x64.pkg", emulated.Command, StringComparison.Ordinal);
        Assert.Equal("/usr/local/share/dotnet/x64", emulated.AddsToPath);
    }

    [Fact]
    public void On_linux_with_the_distributions_dotnet_its_package_fits_when_it_has_the_version()
    {
        var facts = new DotnetInstallFacts(PlatformKind.Linux, "/home/u")
        {
            ShellDotnet = "/usr/bin/dotnet",
            ShellRoot = "/usr/lib/dotnet",
            LinuxDistribution = "ubuntu debian",
            PackageCandidate = "11.0.100-0ubuntu1~24.04.1",
        };
        var plans = InstallPlans.DotnetOptions(s_missing, facts);
        Assert.Equal(["package", "dotnet-install", "page"], Options(plans));
        Assert.Equal((InstallKind.Terminal, "sudo apt-get install -y dotnet-sdk-11.0", "/usr/lib/dotnet", null), (plans[0].Kind, plans[0].Command, plans[0].AddsToPath, plans[0].Note));
        Assert.Contains("11.0.100-0ubuntu1~24.04.1", plans[0].Title, StringComparison.Ordinal);
        Assert.Contains("/usr/lib/dotnet", plans[1].Note, StringComparison.Ordinal);

        // Without the version in the distribution's packages, ~/.dotnet is all there is besides the page, and says so.
        var none = InstallPlans.DotnetOptions(s_missing, facts with { PackageCandidate = null });
        Assert.Equal(["dotnet-install", "page"], Options(none));
        Assert.True(none[0].Recommended);
        Assert.Contains("won't see this SDK", none[0].Note, StringComparison.Ordinal);

        // A pinned version isn't the package's: only dotnet-install installs it exactly.
        var pinned = new ToolStatus("dotnet", ".NET SDK", true, ToolState.Mismatch, "global.json") { WantedVersion = "11.0.101" };
        Assert.Equal(["dotnet-install", "page"], Options(InstallPlans.DotnetOptions(pinned, facts)));
    }

    [Fact]
    public void On_linux_with_dotnet_in_the_home_folder_dotnet_install_fits_and_the_package_says_the_terminal_wont_see_it()
    {
        var facts = new DotnetInstallFacts(PlatformKind.Linux, "/home/u")
        {
            ShellDotnet = "/home/u/.dotnet/dotnet",
            ShellRoot = "/home/u/.dotnet",
            LinuxDistribution = "fedora",
            PackageCandidate = "11.0.100",
        };
        var plans = InstallPlans.DotnetOptions(s_missing, facts);
        Assert.Equal(["dotnet-install", "package", "page"], Options(plans));
        Assert.Equal([true, false, false], plans.Select(p => p.Recommended));
        Assert.Null(plans[0].Note);
        Assert.Equal("sudo dnf install -y dotnet-sdk-11.0", plans[1].Command);
        Assert.Contains("/home/u/.dotnet", plans[1].Note, StringComparison.Ordinal);

        // No dotnet at all: the package puts one on the PATH.
        Assert.Equal(["package", "dotnet-install", "page"], Options(InstallPlans.DotnetOptions(s_missing, facts with { ShellDotnet = null, ShellRoot = null })));
    }

    [Fact]
    public void On_linux_with_the_dotnet_sdk_snap_its_channel_fits_and_says_it_replaces_the_snaps_sdk()
    {
        var facts = new DotnetInstallFacts(PlatformKind.Linux, "/home/u")
        {
            ShellDotnet = "/snap/bin/dotnet",
            ShellRoot = "/snap/dotnet-sdk/current",
            IsSdkSnap = true,
            LinuxDistribution = "ubuntu debian",
            PackageCandidate = "11.0.100-0ubuntu1",
        };
        var plans = InstallPlans.DotnetOptions(s_missing, facts);
        Assert.Equal(["snap", "package", "dotnet-install", "page"], Options(plans));
        Assert.Equal([true, false, false, false], plans.Select(p => p.Recommended));
        Assert.Equal((InstallKind.Terminal, "sudo snap refresh dotnet-sdk --channel=11.0/stable"), (plans[0].Kind, plans[0].Command));
        Assert.Contains("replaces", plans[0].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void On_windows_winget_fits_and_says_when_the_terminals_dotnet_is_elsewhere()
    {
        var facts = new DotnetInstallFacts(PlatformKind.Windows, @"C:\Users\u") { HasWinget = true, WindowsRoot = @"C:\Program Files\dotnet" };
        var plans = InstallPlans.DotnetOptions(s_missing, facts with { ShellRoot = @"c:\program files\dotnet\" });
        Assert.Equal(["winget", "page"], Options(plans));
        Assert.Null(plans[0].Note);
        Assert.StartsWith("winget install --id Microsoft.DotNet.SDK.", plans[0].Command, StringComparison.Ordinal);

        Assert.Contains(@"C:\Users\u\AppData\Local\Microsoft\dotnet", InstallPlans.DotnetOptions(s_missing, facts with { ShellRoot = @"C:\Users\u\AppData\Local\Microsoft\dotnet" })[0].Note, StringComparison.Ordinal);
        Assert.Equal(["page"], Options(InstallPlans.DotnetOptions(s_missing, facts with { HasWinget = false })));
    }

    [Theory]
    [InlineData("dotnet-sdk-11.0:\n  Installed: (none)\n  Candidate: 11.0.100-0ubuntu1~24.04.1\n  Version table:\n", "11.0.100-0ubuntu1~24.04.1")]
    [InlineData("dotnet-sdk-11.0:\n  Installed: (none)\n  Candidate: (none)\n", null)]
    [InlineData("Available Packages\nName         : dotnet-sdk-11.0\nVersion      : 11.0.100\nRelease      : 1.fc43\n", "11.0.100")]
    [InlineData("Information for package dotnet-sdk-11.0:\n---\nRepository     : packages-microsoft-com-prod\nName           : dotnet-sdk-11.0\nVersion        : 11.0.100-1\n", "11.0.100-1")]
    [InlineData("package 'dotnet-sdk-11.0' not found.\n", null)]
    public void A_package_managers_answer_gives_the_version_it_would_install(string output, string? version) =>
        Assert.Equal(version, InstallPlans.PackageCandidate(output));

    [Fact]
    public void Each_package_manager_is_asked_without_a_download()
    {
        Assert.Equal(("apt-cache", "policy dotnet-sdk-11.0"), Query("ubuntu debian"));
        Assert.Equal(("dnf", "-C info dotnet-sdk-11.0"), Query("fedora"));
        Assert.Equal(("zypper", "--non-interactive --no-refresh info dotnet-sdk-11.0"), Query("opensuse-tumbleweed suse opensuse"));
        Assert.Null(InstallPlans.PackageQuery("arch", "dotnet-sdk-11.0"));

        static (string, string)? Query(string distribution) =>
            InstallPlans.PackageQuery(distribution, "dotnet-sdk-11.0") is ({ } program, { } arguments) ? (program, string.Join(' ', arguments)) : null;
    }
}
