using Codalus.Platform;
using Codalus.Workbench.Setup;

namespace Codalus.Workbench.Tests;

/// <summary>Environment setup (M37): what's found with stand-in tools, and the install plans on each OS. Nothing installs.</summary>
public sealed class EnvironmentCheckTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("codalus-env-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public async Task Without_an_sdk_or_git_both_are_missing_and_each_os_has_its_plan()
    {
        var check = new EnvironmentCheck(PlatformKind.Linux, Env(("PATH", "/usr/bin"), ("HOME", "/home/u")), (_, _, _, _) => Task.FromResult((1, "")), _ => false);
        var tools = await check.CheckAsync(null, TestContext.Current.CancellationToken);
        var dotnet = tools.Single(t => t.Id == "dotnet");
        Assert.Equal((ToolState.Missing, true), (dotnet.State, dotnet.Required));
        Assert.Equal(ToolState.Missing, tools.Single(t => t.Id == "git").State);
        Assert.DoesNotContain(tools, t => t.Id == "git-lfs"); // only checked with git
        Assert.DoesNotContain(tools, t => t.Id == "wsl"); // Windows only

        var linux = InstallPlans.For(dotnet, PlatformKind.Linux, "/home/u", hasWinget: false, "ubuntu debian")!;
        Assert.Equal(InstallKind.Run, linux.Kind);
        // The channel of the .NET the IDE runs on (11.0 since M30b).
        Assert.Contains($"dotnet-install.sh | bash -s -- --channel {InstallPlans.HostChannel} --install-dir \"/home/u/.dotnet\"", linux.Command, StringComparison.Ordinal);
        Assert.Equal("/home/u/.dotnet", linux.AddsToPath);

        var windows = InstallPlans.For(dotnet, PlatformKind.Windows, @"C:\Users\u", hasWinget: true, null)!;
        Assert.Equal($"winget install --id {InstallPlans.WingetSdk(InstallPlans.HostChannel, EnvironmentCheck.DefaultHostsRuntime())} --exact --source winget --accept-package-agreements --accept-source-agreements", windows.Command);
        Assert.Equal(InstallKind.OpenPage, InstallPlans.For(dotnet, PlatformKind.Windows, @"C:\Users\u", hasWinget: false, null)!.Kind);

        var git = tools.Single(t => t.Id == "git");
        Assert.Equal("xcode-select --install", InstallPlans.For(git, PlatformKind.MacOS, "/Users/u", false, null)!.Command);
        var linuxGit = InstallPlans.For(git, PlatformKind.Linux, "/home/u", false, "ubuntu debian")!;
        Assert.Equal((InstallKind.Terminal, "sudo apt-get install -y git"), (linuxGit.Kind, linuxGit.Command));
        Assert.Equal(InstallKind.OpenPage, InstallPlans.For(git, PlatformKind.Linux, "/home/u", false, "gentoo")!.Kind);
    }

    [Fact]
    public async Task Found_tools_report_their_versions_and_a_global_json_sdk_that_isnt_installed_is_a_mismatch()
    {
        var bin = Path.Combine(_dir, "bin");
        string[] present = [Path.Combine(bin, "dotnet.exe"), Path.Combine(bin, "git.exe"), Path.Combine(bin, "docker.exe")];
        var calls = new List<string>();
        var check = new EnvironmentCheck(PlatformKind.Windows, Env(("PATH", bin), ("USERPROFILE", _dir)), (program, arguments, directory, _) =>
        {
            calls.Add($"{Path.GetFileNameWithoutExtension(program)} {string.Join(' ', arguments)}{(directory is null ? string.Empty : " in " + Path.GetFileName(directory))}");
            return Task.FromResult(string.Join(' ', arguments) switch
            {
                "--list-sdks" => (0, "8.0.414 [/x/sdk]\n10.0.401 [/x/sdk]\n"),
                "--version" when Path.GetFileNameWithoutExtension(program) == "dotnet" => (1, "A compatible .NET SDK was not found."),
                "--version" when Path.GetFileNameWithoutExtension(program) == "git" => (0, "git version 2.51.0\n"),
                "--version" => (0, "Docker version 28.4.0, build abc\n"),
                "lfs version" => (0, "git-lfs/3.7.0 (GitHub; linux amd64)\n"),
                _ => (1, string.Empty),
            });
        }, present.Contains);

        var tools = await check.CheckAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(("10.0.401", ToolState.Found), (tools.Single(t => t.Id == "dotnet").Version, tools.Single(t => t.Id == "dotnet").State));
        Assert.Equal("2.51.0", tools.Single(t => t.Id == "git").Version);
        Assert.Equal("3.7.0", tools.Single(t => t.Id == "git-lfs").Version);
        Assert.Equal("28.4.0", tools.Single(t => t.Id == "docker").Version);
        Assert.Equal(ToolState.Missing, tools.Single(t => t.Id == "podman").State);

        // A solution whose global.json asks for an SDK dotnet can't resolve.
        var solution = Path.Combine(_dir, "app");
        Directory.CreateDirectory(solution);
        await File.WriteAllTextAsync(Path.Combine(solution, "global.json"), """{ "sdk": { "version": "9.0.100", "rollForward": "latestPatch" } }""", TestContext.Current.CancellationToken);
        var sdk = (await check.CheckAsync(solution, TestContext.Current.CancellationToken)).Single(t => t.Id == "dotnet");
        Assert.Equal((ToolState.Mismatch, "9.0.100"), (sdk.State, sdk.WantedVersion));
        Assert.Contains("dotnet --version in app", calls);
        Assert.Contains("--version 9.0.100", InstallPlans.For(sdk, PlatformKind.Linux, _dir, false, "fedora")!.Command, StringComparison.Ordinal);
        Assert.Contains("Microsoft.DotNet.SDK.9 ", InstallPlans.For(sdk, PlatformKind.Windows, _dir, true, null)!.Command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10.0.12", false)]
    [InlineData("11.0.0-rc.1.26425.128", false)]
    [InlineData("11.0.0-rc.2.26504.105", true)]
    [InlineData("11.0.0", true)]
    [InlineData("11.0.3", true)]
    [InlineData("12.0.0-preview.1.27101.1", true)]
    public async Task Sdks_without_the_hosts_dotnet_are_a_mismatch_that_installs_the_hosts_channel(string runtime, bool enough)
    {
        // M30b: the hosts target the IDE's .NET (here a release candidate) and roll forward, so an older .NET alone
        // can't start them, whatever SDKs it has.
        var bin = Path.Combine(_dir, "bin");
        var check = new EnvironmentCheck(PlatformKind.Linux, Env(("PATH", bin), ("HOME", "/home/u")), (_, arguments, _, _) => Task.FromResult(string.Join(' ', arguments) switch
        {
            "--list-sdks" => (0, $"10.0.401 [/x/sdk]\n"),
            "--list-runtimes" => (0, $"Microsoft.AspNetCore.App 10.0.12 [/x/shared/Microsoft.AspNetCore.App]\nMicrosoft.NETCore.App 10.0.12 [/x/shared/Microsoft.NETCore.App]\nMicrosoft.NETCore.App {runtime} [/x/shared/Microsoft.NETCore.App]\n"),
            _ => (1, string.Empty),
        }), path => path == Path.Combine(bin, "dotnet"))
        {
            HostsRuntime = "11.0.0-rc.2.26504.105",
        };

        var sdk = (await check.CheckAsync(null, TestContext.Current.CancellationToken)).Single(t => t.Id == "dotnet");
        Assert.Equal(enough ? ToolState.Found : ToolState.Mismatch, sdk.State);
        if (!enough)
        {
            Assert.Equal("11.0", sdk.WantedChannel);
            Assert.Contains(".NET 11.0.0-rc.2.26504.105 or later", sdk.Detail, StringComparison.Ordinal);
            Assert.Contains("--channel 11.0 ", InstallPlans.For(sdk, PlatformKind.Linux, "/home/u", false, "ubuntu debian")!.Command, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Versions_order_as_the_host_rolls_forward_and_winget_has_a_release_candidate_as_the_preview_sdk()
    {
        Assert.True(EnvironmentCheck.CompareVersions("11.0.0-rc.1.26425.128", "11.0.0-rc.2.26504.105") < 0);
        Assert.True(EnvironmentCheck.CompareVersions("11.0.0-preview.7.26381.103", "11.0.0-rc.1.26425.128") < 0);
        Assert.True(EnvironmentCheck.CompareVersions("11.0.0", "11.0.0-rc.2.26504.105") > 0);
        Assert.True(EnvironmentCheck.CompareVersions("10.0.13", "11.0.0-rc.2.26504.105") < 0);
        Assert.Equal(0, EnvironmentCheck.CompareVersions("11.0.0-rc.2.26504.105", "11.0.0-rc.2.26504.105"));

        Assert.Equal("Microsoft.DotNet.SDK.10", InstallPlans.WingetSdk("10.0", "11.0.0-rc.2.26504.105"));
        Assert.Equal($"Microsoft.DotNet.SDK.{Environment.Version.Major}", InstallPlans.WingetSdk(InstallPlans.HostChannel, $"{Environment.Version.Major}.0.0"));
        Assert.Equal("Microsoft.DotNet.SDK.Preview", InstallPlans.WingetSdk(InstallPlans.HostChannel, $"{Environment.Version.Major}.0.0-rc.2.26504.105"));
    }

    [Theory]
    [InlineData("ubuntu debian", "sudo apt-get install -y git")]
    [InlineData("fedora", "sudo dnf install -y git")]
    [InlineData("opensuse-tumbleweed suse opensuse", "sudo zypper install -y git")]
    [InlineData("arch", "sudo pacman -S --noconfirm git")]
    [InlineData("alpine", "sudo apk add git")]
    [InlineData("nixos", null)]
    public void Each_distribution_installs_with_its_package_manager(string distribution, string? command) =>
        Assert.Equal(command, InstallPlans.Package(distribution, "git"));

    [Fact]
    public void Os_release_gives_the_distribution_and_what_its_like()
    {
        var file = Path.Combine(_dir, "os-release");
        File.WriteAllText(file, "NAME=\"Ubuntu\"\nID=ubuntu\nID_LIKE=debian\nVERSION_ID=\"24.04\"\n");
        Assert.Equal("ubuntu debian", InstallPlans.LinuxDistribution(file));
        Assert.Null(InstallPlans.LinuxDistribution(Path.Combine(_dir, "none")));
    }
}
