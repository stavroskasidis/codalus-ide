using System.Runtime.InteropServices;
using Codalus.Platform;
using Codalus.Workbench.Setup;

namespace Codalus.Workbench.Tests;

/// <summary>Environment setup (M37): what's found with stand-in tools, and the install plans on each OS. Nothing installs.</summary>
public sealed partial class EnvironmentCheckTests : IDisposable
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

        // What a solution's open checks: the SDK alone, without running git, Docker or Podman.
        calls.Clear();
        var alone = await check.CheckDotnetAsync(solution, TestContext.Current.CancellationToken);
        Assert.Equal((ToolState.Mismatch, "9.0.100"), (alone.State, alone.WantedVersion));
        Assert.All(calls, c => Assert.StartsWith("dotnet ", c, StringComparison.Ordinal));
        Assert.Contains("--version 9.0.100", InstallPlans.For(sdk, PlatformKind.Linux, _dir, false, "fedora")!.Command, StringComparison.Ordinal);
        Assert.Contains("Microsoft.DotNet.SDK.9 ", InstallPlans.For(sdk, PlatformKind.Windows, _dir, true, null)!.Command, StringComparison.Ordinal);

        // What every start checks: the required tools, without running Git LFS, Docker or Podman.
        calls.Clear();
        var required = await check.CheckRequiredAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(["dotnet", "dotnet-runtime", "git"], required.Select(t => t.Id));
        Assert.All(required, t => Assert.True(t.Required, t.Id));
        Assert.Equal("2.51.0", required.Single(t => t.Id == "git").Version);
        Assert.All(calls, c => Assert.Matches("^(dotnet|git) --", c));
    }

    [Fact]
    public async Task Podmans_docker_emulation_shows_its_version_line_without_its_notice()
    {
        // podman-docker's docker prints podman's version and a notice: Check Environment's row showed both, cut off.
        const string bin = "/x/bin";
        var check = new EnvironmentCheck(PlatformKind.Linux, Env(("PATH", bin), ("HOME", "/home/u")), (program, arguments, _, _) => Task.FromResult(
            Path.GetFileName(program) == "docker" && arguments is ["--version"]
                ? (0, "podman version 4.9.3\nEmulate Docker CLI using podman. Create /etc/containers/nodocker to quiet msg.\n")
                : (1, string.Empty)), path => path == Path.Combine(bin, "docker"));

        var docker = (await check.CheckAsync(null, TestContext.Current.CancellationToken)).Single(t => t.Id == "docker");
        Assert.Equal((ToolState.Found, "podman version 4.9.3"), (docker.State, docker.Version));
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

    [Fact]
    public async Task A_program_whose_child_keeps_its_output_open_still_returns_once_it_has_ended()
    {
        // A stand-in that prints, then leaves a process holding its standard output, as a tool that forks a daemon
        // (adb's server) can: the end of the output never comes until that process ends. The run must not wait for it.
        var started = DateTime.Now.AddSeconds(-1);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (program, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/d", "/c", "echo hello& start /b ping -n 300 127.0.0.1" })
            : ("/bin/sh", ["-c", "echo hello; sleep 300 & echo \"held by $!\""]);
        var (code, output) = (-2, "");
        try
        {
            // Not in the test's folder: the process left behind runs there until it is removed, and on Windows a process's
            // working folder can't be deleted (Dispose).
            (code, output) = await EnvironmentCheck.RunProcessAsync(program, arguments, AppContext.BaseDirectory, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"Returned after {clock.Elapsed}: it waited for the process holding the output.");
            Assert.Equal(0, code);
            Assert.Contains("hello", output, StringComparison.Ordinal);
        }
        finally
        {
            // The test removes the process it left behind, and waits until it has gone.
            foreach (var held in LeftBehind(output, started))
            {
                using (held)
                {
                    try
                    {
                        held.Kill();
                        held.WaitForExit(TimeSpan.FromSeconds(10));
                    }
                    catch (InvalidOperationException)
                    {
                        // It has ended already.
                    }
                }
            }
        }

        // Unix: the shell prints the background process's id. Windows: cmd can't, so the ping started since the test began
        // whose parent has exited: `start /b` left it behind when its cmd ended. Every ping started since, as this test did,
        // killed the pings of tests running beside it (AcpAgentTests' timeout command ended with exit code -1 in 0.7 s,
        // HostRuntimeTests' stand-in hosts): theirs run under a cmd /c that waits for them.
        static IEnumerable<System.Diagnostics.Process> LeftBehind(string output, DateTime started)
        {
            if (OperatingSystem.IsWindows())
            {
                return System.Diagnostics.Process.GetProcessesByName("PING").Where(p => StartedSince(p, started) && Orphaned(p));
            }

            return System.Text.RegularExpressions.Regex.Match(output, @"held by (\d+)") is { Success: true } held
                && int.TryParse(held.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var id)
                && Running(id) is { } process
                    ? [process]
                    : [];
        }

        static bool StartedSince(System.Diagnostics.Process process, DateTime started)
        {
            try
            {
                return process.StartTime >= started;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        static System.Diagnostics.Process? Running(int id)
        {
            try
            {
                return System.Diagnostics.Process.GetProcessById(id);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        // Its parent has exited: no process runs under the parent's id, or the one that does started after it (a reused id).
        static bool Orphaned(System.Diagnostics.Process process)
        {
            try
            {
                if (NtQueryInformationProcess(process.Handle, 0, out var info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
                {
                    return false;
                }

                using var parent = Running((int)info.InheritedFromUniqueProcessId);
                return parent is null || parent.StartTime > process.StartTime;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    // ProcessBasicInformation (0): the parent's id, which .NET doesn't expose on Windows.
    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint process, int informationClass, out ProcessBasicInformation information, int length, out int returned);
}
