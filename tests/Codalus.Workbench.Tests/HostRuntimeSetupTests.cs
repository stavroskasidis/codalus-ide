using System.Runtime.InteropServices;
using Codalus.Platform;
using Codalus.Plugins.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Languages;
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

    /// <summary>
    /// Starts processes with this machine's environment minus what changes the check's answer: a developer's own
    /// <c>CODALUS_SKIP_RUNTIME_CHECK</c> or <c>DOTNET_ROLL_FORWARD</c> mustn't change what these tests see.
    /// </summary>
    private static readonly LocalProcessLauncher s_cleanLauncher = new()
    {
        Roots = new DotnetRootResolver
        {
            ProcessEnvironment = name => name is HostRuntimeCheck.SkipVariable or RuntimeConfig.RollForwardVariable or RuntimeConfig.RollForwardToPrereleaseVariable or "DOTNET_DISABLE_RUNTIME_VERSIONS"
                ? null
                : Environment.GetEnvironmentVariable(name),
        },
    };

    /// <summary>A stand-in host that says why it can't run on standard error and exits, before any handshake.</summary>
    private static ProcessLaunchInfo ExitsAtStartup(string why) => OperatingSystem.IsWindows()
        ? new ProcessLaunchInfo("cmd.exe", ["/c", $"echo {why} 1>&2 & exit /b 3"])
        : new ProcessLaunchInfo("/bin/sh", ["-c", $"echo '{why}' >&2; exit 3"]);

    /// <summary>
    /// A described machine for <see cref="EnvironmentCheck.ChooseDotnetRoot"/>: .NET roots (folders, with a <c>dotnet</c>
    /// when listed in <see cref="Dotnets"/>), their SDKs and runtimes, the environment and the registered location.
    /// </summary>
    private sealed class Machine(PlatformKind platform, Architecture architecture = Architecture.X64, Architecture? osArchitecture = null)
    {
        public Dictionary<string, List<string>> Sdks { get; } = [];

        public Dictionary<string, List<string>> Runtimes { get; } = [];

        public HashSet<string> Dotnets { get; } = [];

        public Dictionary<string, string> Variables { get; } = [];

        public string? InstallLocation { get; set; }

        private string? Variable(string name) => Variables.GetValueOrDefault(name);

        private bool IsRoot(string folder) => Sdks.ContainsKey(N(folder).TrimEnd('/')) || Runtimes.ContainsKey(N(folder).TrimEnd('/'));

        public EnvironmentCheck Check() => new(platform, Variable, exists: path => Dotnets.Any(r => N(path) == r + "/dotnet" || N(path) == r + "/dotnet.exe"))
        {
            HostsRuntime = Rc2,
            SdkVersions = folder => Sdks.GetValueOrDefault(N(folder).TrimEnd('/')) ?? [],
            InstalledFrameworks = (root, _) => Versions([.. Runtimes.GetValueOrDefault(N(root).TrimEnd('/')) ?? []]),
            DirectoryExists = IsRoot,
            RealPath = path => path,
            Roots = new DotnetRootResolver
            {
                OS = platform switch { PlatformKind.Windows => "windows", PlatformKind.MacOS => "macos", _ => "linux" },
                Architecture = architecture,
                OSArchitecture = osArchitecture ?? architecture,
                ProcessEnvironment = Variable,
                DirectoryExists = IsRoot,
                FileExists = _ => false,
                ReadFirstLine = path => path.StartsWith("/etc/dotnet/install_location", StringComparison.Ordinal) ? InstallLocation : null,
                WindowsRegisteredLocation = _ => null,
            },
        };

        /// <summary>What the shell's UseUserDotnet does with a choice.</summary>
        public void Apply(DotnetRootChoice choice)
        {
            foreach (var (name, value) in choice.Variables)
            {
                Variables[name] = value;
            }
        }
    }

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
            DirectoryExists = folder => N(folder) == "/x",
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
        Assert.Contains("Microsoft.DotNet.SDK.Preview ", InstallPlans.For(row, PlatformKind.Windows, @"C:\Users\u", true, null)!.Command, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_installs_the_winget_package_of_the_wanted_runtime_not_of_the_one_the_ide_runs_on()
    {
        // An IDE on a release candidate offered a release built on GA (11.0.0): the GA SDK, not the preview package that
        // still holds the release candidate. One on GA offered a release on the next preview: the preview package.
        ToolStatus Needs(string version, string channel) =>
            new("dotnet-runtime", ".NET runtime for Codalus IDE", true, ToolState.Missing, "helpers") { WantedVersion = version, WantedChannel = channel };
        Assert.Contains("--id Microsoft.DotNet.SDK.11 ", InstallPlans.For(Needs("11.0.0", "11.0"), PlatformKind.Windows, @"C:\Users\u", true, null)!.Command, StringComparison.Ordinal);
        Assert.Contains("--id Microsoft.DotNet.SDK.Preview ", InstallPlans.For(Needs("12.0.0-preview.1.27101.1", "12.0"), PlatformKind.Windows, @"C:\Users\u", true, null)!.Command, StringComparison.Ordinal);
        Assert.Contains("--id Microsoft.DotNet.SDK.Preview ", InstallPlans.For(Needs(Rc2, "11.0"), PlatformKind.Windows, @"C:\Users\u", true, null)!.Command, StringComparison.Ordinal);
        Assert.Equal("Microsoft.DotNet.SDK.11", InstallPlans.WingetSdk("11.0", "11.0.0"));
        Assert.Equal("Microsoft.DotNet.SDK.11", InstallPlans.WingetSdk("11.0", "12.0.0-preview.1.27101.1"));
        Assert.Equal("Microsoft.DotNet.SDK.Preview", InstallPlans.WingetSdk("12.0", "12.0.0-preview.1.27101.1"));
    }

    /// <summary>
    /// A runtime-only install where the hosts look (a distribution's dotnet-runtime-11.0 in /usr/lib/dotnet, registered
    /// in /etc/dotnet/install_location) keeps its hosts, though the SDK root on the PATH (~/.dotnet, SDK 10) is the one
    /// chosen for SDKs: pointing DOTNET_ROOT at ~/.dotnet would leave the hosts without their runtime.
    /// </summary>
    [Fact]
    public void The_hosts_stay_on_a_root_with_their_runtime_and_move_only_to_one_that_has_it()
    {
        var ubuntu = new Machine(PlatformKind.Linux) { InstallLocation = "/usr/lib/dotnet" };
        ubuntu.Variables["PATH"] = "/home/u/.dotnet:/usr/bin";
        ubuntu.Variables["HOME"] = "/home/u";
        ubuntu.Sdks["/home/u/.dotnet"] = ["10.0.401"];
        ubuntu.Runtimes["/home/u/.dotnet"] = ["10.0.12"];
        ubuntu.Runtimes["/usr/lib/dotnet"] = ["11.0.0"];
        ubuntu.Dotnets.UnionWith(["/home/u/.dotnet", "/usr/lib/dotnet"]);
        var check = ubuntu.Check();
        var choice = check.ChooseDotnetRoot()!;
        Assert.Equal(("/home/u/.dotnet", false), (N(choice.Root), choice.AddToPath));
        Assert.Empty(choice.Variables); // DOTNET_ROOT stays unset: the hosts keep /usr/lib/dotnet
        Assert.Equal((ToolState.Found, "/usr/lib/dotnet"), (check.HostRuntimeStatus().State, N(check.HostRuntimeStatus().Path!)));

        // The Mac's runtime-only package in the default location, the only SDK (10) in ~/.dotnet on the PATH: the same.
        var mac = new Machine(PlatformKind.MacOS, Architecture.Arm64);
        mac.Variables["PATH"] = "/Users/ada/.dotnet:/usr/bin";
        mac.Variables["HOME"] = "/Users/ada";
        mac.Sdks["/Users/ada/.dotnet"] = ["10.0.109"];
        mac.Runtimes["/Users/ada/.dotnet"] = ["10.0.9"];
        mac.Runtimes["/usr/local/share/dotnet"] = ["10.0.9", Rc2];
        mac.Dotnets.UnionWith(["/Users/ada/.dotnet", "/usr/local/share/dotnet"]);
        Assert.Empty(mac.Check().ChooseDotnetRoot()!.Variables);
        Assert.Equal(ToolState.Found, mac.Check().HostRuntimeStatus().State);

        // ~/.dotnet on the PATH with the SDK and the runtime, nothing where the app hosts would look without
        // DOTNET_ROOT (/usr/share/dotnet): the hosts are pointed at ~/.dotnet.
        var linux = new Machine(PlatformKind.Linux);
        linux.Variables["PATH"] = "/home/u/.dotnet:/usr/bin";
        linux.Variables["HOME"] = "/home/u";
        linux.Sdks["/home/u/.dotnet"] = ["11.0.100-rc.2.26504.105"];
        linux.Runtimes["/home/u/.dotnet"] = [Rc2];
        linux.Dotnets.Add("/home/u/.dotnet");
        Assert.Equal(ToolState.Missing, linux.Check().HostRuntimeStatus().State);
        var pointed = linux.Check().ChooseDotnetRoot()!;
        Assert.Equal([new KeyValuePair<string, string>("DOTNET_ROOT", "/home/u/.dotnet")], pointed.Variables.Select(v => new KeyValuePair<string, string>(v.Key, N(v.Value))));
        linux.Apply(pointed);
        Assert.Equal(ToolState.Found, linux.Check().HostRuntimeStatus().State);
    }

    [Fact]
    public void An_architectures_own_dotnet_root_is_pointed_too_since_the_app_hosts_read_it_first()
    {
        // DOTNET_ROOT_ARM64 at /usr/local/share/dotnet (.NET 10 only); ~/.dotnet has the SDK and the runtime.
        var mac = new Machine(PlatformKind.MacOS, Architecture.Arm64);
        mac.Variables["PATH"] = "/usr/bin:/bin";
        mac.Variables["HOME"] = "/Users/ada";
        mac.Variables["DOTNET_ROOT_ARM64"] = "/usr/local/share/dotnet";
        mac.Sdks["/usr/local/share/dotnet"] = ["10.0.109"];
        mac.Runtimes["/usr/local/share/dotnet"] = ["10.0.9"];
        mac.Sdks["/Users/ada/.dotnet"] = ["11.0.100-rc.2.26504.105"];
        mac.Runtimes["/Users/ada/.dotnet"] = [Rc2];
        mac.Dotnets.UnionWith(["/usr/local/share/dotnet", "/Users/ada/.dotnet"]);
        Assert.Equal(ToolState.Missing, mac.Check().HostRuntimeStatus().State);

        var choice = mac.Check().ChooseDotnetRoot()!;
        Assert.True(choice.AddToPath);
        Assert.Equal(
            [("DOTNET_ROOT", "/Users/ada/.dotnet"), ("DOTNET_ROOT_ARM64", "/Users/ada/.dotnet")],
            choice.Variables.Select(v => (v.Key, N(v.Value))));
        mac.Apply(choice);
        Assert.Equal(ToolState.Found, mac.Check().HostRuntimeStatus().State);
        // DOTNET_ROOT alone (what Setup set before) would have left the hosts on DOTNET_ROOT_ARM64's root.
        Assert.Equal([new KeyValuePair<string, string>("DOTNET_ROOT", "/Users/ada/.dotnet")], mac.Check().PointHostsAt("/Users/ada/.dotnet"));
    }

    [Fact]
    public void A_working_architecture_root_is_left_alone_when_the_sdk_root_is_pointed_at()
    {
        // DOTNET_ROOT_ARM64 at /opt/dotnet-11 has the hosts' runtime. Homebrew's dotnet wrapper is first on the PATH (no
        // SDKs next to it), the SDK (10) is in /usr/local/share/dotnet: DOTNET_ROOT moves there, for MSBuildLocator, but
        // DOTNET_ROOT_ARM64 stays: pointing it at a root without the runtime would break hosts that work.
        var mac = new Machine(PlatformKind.MacOS, Architecture.Arm64);
        mac.Variables["PATH"] = "/opt/homebrew/bin:/usr/bin";
        mac.Variables["HOME"] = "/Users/ada";
        mac.Variables["DOTNET_ROOT_ARM64"] = "/opt/dotnet-11";
        mac.Runtimes["/opt/dotnet-11"] = [Rc2];
        mac.Sdks["/usr/local/share/dotnet"] = ["10.0.109"];
        mac.Runtimes["/usr/local/share/dotnet"] = ["10.0.9"];
        mac.Dotnets.UnionWith(["/opt/homebrew/bin", "/usr/local/share/dotnet"]);
        Assert.Equal(ToolState.Found, mac.Check().HostRuntimeStatus().State);

        var choice = mac.Check().ChooseDotnetRoot()!;
        Assert.Equal(("/usr/local/share/dotnet", true), (N(choice.Root), choice.AddToPath));
        Assert.Equal([("DOTNET_ROOT", "/usr/local/share/dotnet")], choice.Variables.Select(v => (v.Key, N(v.Value))));
        mac.Apply(choice);
        Assert.Equal((ToolState.Found, "/opt/dotnet-11"), (mac.Check().HostRuntimeStatus().State, N(mac.Check().HostRuntimeStatus().Path!)));
    }

    /// <summary>
    /// The win-x64 build on Windows on Arm: an x64 process, which can't load the native (Arm64) .NET in
    /// <c>%ProgramFiles%\dotnet</c>, only the x64 one in its <c>x64</c> folder. The native root never counts for the hosts.
    /// </summary>
    [Fact]
    public void The_x64_build_on_windows_on_arm_keeps_its_hosts_off_the_native_dotnet()
    {
        Machine Woa(bool x64Runtime)
        {
            var machine = new Machine(PlatformKind.Windows, Architecture.X64, osArchitecture: Architecture.Arm64);
            machine.Variables["ProgramFiles"] = "C:/pf";
            machine.Variables["LOCALAPPDATA"] = "C:/Users/u/AppData/Local";
            machine.Sdks["C:/pf/dotnet"] = ["11.0.100-rc.2.26504.105"];
            machine.Runtimes["C:/pf/dotnet"] = [Rc2];
            machine.Dotnets.Add("C:/pf/dotnet");
            if (x64Runtime)
            {
                machine.Runtimes["C:/pf/dotnet/x64"] = [Rc2];
            }

            return machine;
        }

        // Nothing set: the native SDK is chosen (DOTNET_ROOT, for MSBuildLocator), and the hosts are pinned to the x64
        // .NET, which DOTNET_ROOT would otherwise hide.
        var fresh = Woa(x64Runtime: true);
        Assert.Equal((ToolState.Found, "C:/pf/dotnet/x64"), (fresh.Check().HostRuntimeStatus().State, N(fresh.Check().HostRuntimeStatus().Path!)));
        var choice = fresh.Check().ChooseDotnetRoot()!;
        Assert.Equal(("C:/pf/dotnet", true), (N(choice.Root), choice.AddToPath));
        Assert.Equal([("DOTNET_ROOT", "C:/pf/dotnet"), ("DOTNET_ROOT_X64", "C:/pf/dotnet/x64")], choice.Variables.Select(v => (v.Key, N(v.Value))));
        fresh.Apply(choice);
        Assert.Equal((ToolState.Found, "C:/pf/dotnet/x64"), (fresh.Check().HostRuntimeStatus().State, N(fresh.Check().HostRuntimeStatus().Path!)));

        // DOTNET_ROOT already at the native SDK: it stays, and the hosts are pinned to the x64 .NET, not "redirected" to
        // the native one (which has the runtime, but not one they can load).
        var set = Woa(x64Runtime: true);
        set.Variables["DOTNET_ROOT"] = "C:/pf/dotnet";
        Assert.Equal(ToolState.Missing, set.Check().HostRuntimeStatus().State);
        var pinned = set.Check().ChooseDotnetRoot()!;
        Assert.Equal([("DOTNET_ROOT", "C:/pf/dotnet"), ("DOTNET_ROOT_X64", "C:/pf/dotnet/x64")], pinned.Variables.Select(v => (v.Key, N(v.Value))));
        set.Apply(pinned);
        Assert.Equal(ToolState.Found, set.Check().HostRuntimeStatus().State);
        // Pinned already (the root the hosts use has the runtime): nothing more.
        Assert.Empty(set.Check().ChooseDotnetRoot()!.Variables);

        // No x64 .NET: the native root's runtime doesn't count, for the hosts nor for an update.
        var native = Woa(x64Runtime: false);
        var check = native.Check();
        Assert.False(check.HasHostRuntime("C:/pf/dotnet"));
        Assert.Equal(ToolState.Missing, check.HostRuntimeStatus().State);
        Assert.Equal(ToolState.Missing, check.RuntimeStatusFor([FrameworkReference.Create(FrameworkReference.NetCoreApp, FrameworkVersion.Parse(Rc2), RollForwardPolicy.Major)]).State);
        Assert.Equal([("DOTNET_ROOT", "C:/pf/dotnet")], check.ChooseDotnetRoot()!.Variables.Select(v => (v.Key, N(v.Value))));
        Assert.True(check.EmulatedX64);

        // Its install is the x64 .NET; a native process's isn't told an architecture.
        var row = check.HostRuntimeStatus();
        Assert.Contains("--exact --architecture x64 --source winget", InstallPlans.For(row, PlatformKind.Windows, @"C:\Users\u", true, null, emulatedX64: true)!.Command, StringComparison.Ordinal);
        Assert.DoesNotContain("--architecture", InstallPlans.For(row, PlatformKind.Windows, @"C:\Users\u", true, null)!.Command, StringComparison.Ordinal);
    }

    [Fact]
    public void After_winget_installs_into_program_files_the_hosts_follow_the_new_root()
    {
        // The only SDK was 10 in %LOCALAPPDATA%\Microsoft\dotnet (dotnet-install.ps1's folder), as DOTNET_ROOT. Install
        // .NET 11.0 runs winget, which installs into Program Files: choosing again moves to it.
        var windows = new Machine(PlatformKind.Windows);
        windows.Variables["LOCALAPPDATA"] = "C:/Users/u/AppData/Local";
        windows.Variables["ProgramFiles"] = "C:/pf";
        windows.Variables["DOTNET_ROOT"] = "C:/Users/u/AppData/Local/Microsoft/dotnet";
        windows.Sdks["C:/Users/u/AppData/Local/Microsoft/dotnet"] = ["10.0.401"];
        windows.Runtimes["C:/Users/u/AppData/Local/Microsoft/dotnet"] = ["10.0.12"];
        windows.Dotnets.Add("C:/Users/u/AppData/Local/Microsoft/dotnet");
        Assert.Empty(windows.Check().ChooseDotnetRoot()!.Variables);
        Assert.Equal(ToolState.Missing, windows.Check().HostRuntimeStatus().State);

        windows.Sdks["C:/pf/dotnet"] = ["11.0.100-rc.2.26504.105"];
        windows.Runtimes["C:/pf/dotnet"] = [Rc2];
        windows.Dotnets.Add("C:/pf/dotnet");
        var choice = windows.Check().ChooseDotnetRoot()!;
        Assert.Equal(("C:/pf/dotnet", true), (N(choice.Root), choice.AddToPath));
        windows.Apply(choice);
        Assert.Equal(ToolState.Found, windows.Check().HostRuntimeStatus().State);
    }

    [Fact]
    public void An_update_counts_only_roots_its_hosts_would_get()
    {
        // ~/.dotnet (SDK and runtime rc.2) is DOTNET_ROOT; /usr/lib/dotnet has the distribution's runtime 11.0.0 and
        // no SDK. A release that needs 11.0.0 would leave its hosts on ~/.dotnet: it isn't installed for it.
        var needs = new[] { FrameworkReference.Create(FrameworkReference.NetCoreApp, FrameworkVersion.Parse("11.0.0"), RollForwardPolicy.Major) };
        var linux = new Machine(PlatformKind.Linux);
        linux.Variables["HOME"] = "/home/u";
        linux.Variables["DOTNET_ROOT"] = "/home/u/.dotnet";
        linux.Sdks["/home/u/.dotnet"] = ["11.0.100-rc.2.26504.105"];
        linux.Runtimes["/home/u/.dotnet"] = [Rc2];
        linux.Runtimes["/usr/lib/dotnet"] = ["11.0.0"];
        linux.Dotnets.UnionWith(["/home/u/.dotnet", "/usr/lib/dotnet"]);
        var status = linux.Check().RuntimeStatusFor(needs);
        Assert.Equal(ToolState.Missing, status.State);
        Assert.Contains("11.0.0 in /usr/lib/dotnet", N(status.Found!), StringComparison.Ordinal); // named, but not counted

        // Where the hosts look without DOTNET_ROOT (registered there), the runtime-only root counts: it stays theirs.
        linux.Variables.Remove("DOTNET_ROOT");
        linux.Variables["PATH"] = "/home/u/.dotnet";
        linux.InstallLocation = "/usr/lib/dotnet";
        Assert.Equal(ToolState.Found, linux.Check().RuntimeStatusFor(needs).State);
        // An SDK root with it counts too (the updated IDE moves its hosts there).
        linux.InstallLocation = null;
        linux.Sdks["/usr/share/dotnet"] = ["11.0.100"];
        linux.Runtimes["/usr/share/dotnet"] = ["11.0.0"];
        linux.Dotnets.Add("/usr/share/dotnet");
        Assert.Equal(ToolState.Found, linux.Check().RuntimeStatusFor(needs).State);
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
            Roots = new DotnetRootResolver { OS = "macos", Architecture = Architecture.Arm64, ProcessEnvironment = environment, ReadFirstLine = _ => null, DirectoryExists = folder => runtimes.ContainsKey(N(folder)) },
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
            Roots = new DotnetRootResolver { OS = "macos", Architecture = Architecture.Arm64, ProcessEnvironment = name => name == "DOTNET_ROOT" ? User : environment(name), ReadFirstLine = _ => null, DirectoryExists = folder => runtimes.ContainsKey(N(folder)) },
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
        var launch = new ProcessLaunchInfo(host, []) { Environment = new Dictionary<string, string> { ["DOTNET_ROOT"] = root, ["DOTNET_ROOT_" + arch] = root }, Launcher = s_cleanLauncher };
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
        manager.HostRuntimeMissing = e =>
        {
            lock (reports)
            {
                reports.Add(e);
            }

            return true; // the shell's notification says it
        };
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
    public async Task A_host_that_exits_at_startup_is_one_error_and_a_retry_activates_only_the_plugins_that_asked()
    {
        // One plugin activates at startup, the other on a command that hasn't run. Their host exits during startup: both
        // fail with it, one error for the group. After the retry, the lazy one waits for its command again.
        WritePlugin("samples.eager", "onStartup");
        WritePlugin("samples.lazy", "onCommand:samples.lazy.run");
        var paths = AppPaths.Resolve(PlatformInfo.Current, _ => null, Path.Combine(_dir, "user-data"));
        paths.EnsureCreated();
        using var workbench = new WorkbenchServices(paths, PlatformInfo.Current, watchFiles: false);
        var launches = 0;
        await using var manager = new PluginManager(workbench, _ =>
        {
            Interlocked.Increment(ref launches);
            return ExitsAtStartup("You must install or update .NET to run this application.") with { Launcher = s_cleanLauncher };
        }, "1.0.0", new SupervisionPolicy { ShutdownRequest = null, ExitNotification = null });
        manager.AddPlugins(PluginDiscovery.Scan([(Path.Combine(_dir, "plugins"), false)], PlatformInfo.Current));
        var eager = manager.Plugins.Single(p => p.Id == "samples.eager");
        var lazy = manager.Plugins.Single(p => p.Id == "samples.lazy");

        await manager.ActivateStartupPluginsAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => lazy.FailedWithItsHost); // the host's crash marks every member
        Assert.Equal((PluginState.Failed, true), (eager.State, eager.FailedWithItsHost));
        Assert.Contains("You must install or update .NET", eager.Error, StringComparison.Ordinal);
        var error = Assert.Single(workbench.Notifications.Items, n => n.Severity == NotificationSeverity.Error);
        Assert.Contains("Plugin host 'default' couldn't start", error.Message, StringComparison.Ordinal);

        await manager.RetryFailedHostsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PluginState.Failed, eager.State); // activated again (and its host failed again)
        Assert.True(Volatile.Read(ref launches) >= 1);
        var log = manager.PluginLog.GetText();
        Assert.Contains("Starting the plugin hosts again for samples.eager; ready again: samples.lazy", log, StringComparison.Ordinal);
        // The lazy plugin was never activated: only the eager one's activations failed with the host (its host's second
        // failure marks the lazy one again, as a member, without activating it).
        Assert.Equal(2, log.Split('\n').Count(l => l.Contains("[samples.eager] failed: its plugin host couldn't start", StringComparison.Ordinal)));
        Assert.DoesNotContain("[samples.lazy] failed: its plugin host couldn't start", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_runtime_report_nobody_shows_is_the_hosts_error()
    {
        // The shell's handler says no (not the IDE's host, say): the plugin host's failure is shown as an error, once.
        WritePlugin("samples.one", "onStartup");
        var root = Path.Combine(_dir, "dotnet");
        Directory.CreateDirectory(Path.Combine(root, "shared", FrameworkReference.NetCoreApp, "10.0.9"));
        File.WriteAllText(Path.Combine(root, "shared", FrameworkReference.NetCoreApp, "10.0.9", FrameworkReference.NetCoreApp + ".deps.json"), "{}");
        var hostFolder = Path.Combine(_dir, "pluginhost");
        WriteConfig(hostFolder, "codalus-pluginhost", Rc2, "Major");
        var host = Path.Combine(hostFolder, OperatingSystem.IsWindows() ? "codalus-pluginhost.exe" : "codalus-pluginhost");
        File.WriteAllText(host, "not a program");
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();
        var launch = new ProcessLaunchInfo(host, []) { Environment = new Dictionary<string, string> { ["DOTNET_ROOT"] = root, ["DOTNET_ROOT_" + arch] = root }, Launcher = s_cleanLauncher };
        var paths = AppPaths.Resolve(PlatformInfo.Current, _ => null, Path.Combine(_dir, "user-data"));
        paths.EnsureCreated();
        using var workbench = new WorkbenchServices(paths, PlatformInfo.Current, watchFiles: false);
        await using var manager = new PluginManager(workbench, _ => launch, "1.0.0", new SupervisionPolicy { ShutdownRequest = null, ExitNotification = null })
        {
            HostRuntimeMissing = _ => false,
        };
        manager.AddPlugins(PluginDiscovery.Scan([(Path.Combine(_dir, "plugins"), false)], PlatformInfo.Current));
        await manager.ActivateStartupPluginsAsync(TestContext.Current.CancellationToken);
        var error = Assert.Single(workbench.Notifications.Items, n => n.Severity == NotificationSeverity.Error);
        Assert.Contains($"needs the .NET runtime Microsoft.NETCore.App {Rc2}", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_language_host_that_exits_at_startup_is_a_start_failure_not_a_crash_and_is_tried_again_on_request()
    {
        var registry = new LanguageServiceRegistry();
        var launches = 0;
        registry.Register(new LanguageServiceDescriptor("standin", "Stand-in", [".standin"], null, () =>
        {
            Interlocked.Increment(ref launches);
            return ExitsAtStartup("You must install or update .NET to run this application.") with { Launcher = s_cleanLauncher };
        }));
        await using var solutions = new Codalus.Workbench.Projects.SolutionService(() => null);
        await using var service = new LanguageService(registry, solutions, new SupervisionPolicy { ShutdownRequest = null, ExitNotification = null });
        var crashes = new List<(string, bool)>();
        var failures = new List<(string LanguageId, Exception Error)>();
        service.HostCrashed += (_, e) => { lock (crashes) { crashes.Add(e); } };
        service.StartFailed += (_, e) => { lock (failures) { failures.Add(e); } };

        Assert.Null(await service.ClientAsync("standin"));
        var failure = Assert.Single(failures);
        Assert.Equal("standin", failure.LanguageId);
        Assert.Contains("You must install or update .NET", Assert.IsType<HostExitedException>(failure.Error).Message, StringComparison.Ordinal);

        // After an install, RestartHostsAsync retries the services that couldn't start.
        await service.RetryFailedAsync();
        Assert.Equal(2, Volatile.Read(ref launches));
        Assert.Equal(2, failures.Count);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Empty(crashes); // never "keeps crashing"
    }

    [Fact]
    public async Task A_language_host_that_ends_its_first_start_cleanly_and_silently_is_reported_too()
    {
        // Exit code 0, nothing on standard error: the loss is a plain JsonRpcConnectionLostException, not a
        // HostExitedException. The crash handler leaves a first start to StartAsync, which must still say it.
        var registry = new LanguageServiceRegistry();
        registry.Register(new LanguageServiceDescriptor("standin", "Stand-in", [".standin"], null, () =>
            (OperatingSystem.IsWindows() ? new ProcessLaunchInfo("cmd.exe", ["/c", "exit /b 0"]) : new ProcessLaunchInfo("/bin/sh", ["-c", "exit 0"])) with { Launcher = s_cleanLauncher }));
        await using var solutions = new Codalus.Workbench.Projects.SolutionService(() => null);
        await using var service = new LanguageService(registry, solutions, new SupervisionPolicy { ShutdownRequest = null, ExitNotification = null });
        var crashes = new List<(string, bool)>();
        var failures = new List<(string LanguageId, Exception Error)>();
        service.HostCrashed += (_, e) => { lock (crashes) { crashes.Add(e); } };
        service.StartFailed += (_, e) => { lock (failures) { failures.Add(e); } };

        Assert.Null(await service.ClientAsync("standin"));
        var failure = Assert.Single(failures);
        Assert.Equal("standin", failure.LanguageId);
        Assert.IsAssignableFrom<JsonRpcConnectionLostException>(failure.Error);
        Assert.IsNotType<HostExitedException>(failure.Error);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Empty(crashes);
    }

    private void WritePlugin(string id, string activation)
    {
        var folder = Path.Combine(_dir, "plugins", id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "plugin.dll"), string.Empty);
        File.WriteAllText(Path.Combine(folder, PluginManifest.FileName), $$"""
            { "id": "{{id}}", "version": "1.0.0", "displayName": "{{id}}", "publisher": "samples", "engines": { "codalus": "^1.0.0" },
              "entry": { "runtime": "dotnet", "assembly": "plugin.dll" }, "activationEvents": [ "{{activation}}" ] }
            """);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
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
