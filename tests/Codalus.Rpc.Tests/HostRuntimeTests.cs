namespace Codalus.Rpc.Tests;

/// <summary>
/// M49: the .NET host's framework resolution as a pure component, checked against hostfxr's documented behavior
/// (dotnet/runtime's framework-version-resolution.md and fx_resolver.cpp), and the check before a host starts, on fake
/// .NET roots (folders <c>shared/Microsoft.NETCore.App/&lt;version&gt;</c> with their deps.json). Nothing real is installed.
/// </summary>
public sealed class HostRuntimeTests : IDisposable
{
    private const string Rc2 = "11.0.0-rc.2.26504.105";
    private const string Rc1 = "11.0.0-rc.1.26425.128";

    private readonly string _dir = Directory.CreateTempSubdirectory("codalus-runtime-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static FrameworkVersion V(string text) => FrameworkVersion.Parse(text);

    private static string? Resolve(string requested, RollForwardPolicy policy, bool toPrerelease, params string[] installed) =>
        FrameworkResolver.Resolve(FrameworkReference.Create(FrameworkReference.NetCoreApp, V(requested), policy, toPrerelease), installed.Select(V))?.Text;

    [Theory]
    [InlineData("11.0.0-rc.1.26425.128", "11.0.0-rc.2.26504.105", -1)]
    [InlineData("11.0.0-preview.7.26381.103", "11.0.0-rc.1.26425.128", -1)]
    [InlineData("11.0.0", "11.0.0-rc.2.26504.105", 1)]
    [InlineData("10.0.13", "11.0.0-rc.2.26504.105", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", -1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11", -1)]
    [InlineData("1.0.0-rc.1", "1.0.0", -1)]
    [InlineData("1.0.0+build.5", "1.0.0", 0)]
    public void Versions_order_as_semver(string a, string b, int sign)
    {
        Assert.Equal(sign, Math.Sign(V(a).CompareTo(V(b))));
        Assert.Equal(-sign, Math.Sign(V(b).CompareTo(V(a))));
    }

    [Theory]
    [InlineData("11.0")]
    [InlineData("11.0.0.1")]
    [InlineData("v11.0.0")]
    [InlineData("11.0.0-")]
    [InlineData("11.0.0-rc..1")]
    [InlineData("")]
    public void Folder_names_that_arent_versions_are_skipped(string text) => Assert.False(FrameworkVersion.TryParse(text, out _));

    // The rollForward table of framework-version-resolution.md, on a machine with 2.1.0, 2.1.3, 2.2.1, 3.0.0 and 3.1.2.
    [Theory]
    [InlineData("2.1.0", RollForwardPolicy.Disable, "2.1.0")]
    [InlineData("2.1.1", RollForwardPolicy.Disable, null)]
    [InlineData("2.1.0", RollForwardPolicy.LatestPatch, "2.1.3")]
    [InlineData("2.1.4", RollForwardPolicy.LatestPatch, null)]
    [InlineData("2.0.0", RollForwardPolicy.LatestPatch, null)]
    [InlineData("2.1.0", RollForwardPolicy.Minor, "2.1.3")] // the requested minor is there: its latest patch
    [InlineData("2.0.0", RollForwardPolicy.Minor, "2.1.3")] // the lowest higher minor, then its latest patch
    [InlineData("2.3.0", RollForwardPolicy.Minor, null)] // never another major
    [InlineData("2.1.0", RollForwardPolicy.LatestMinor, "2.2.1")]
    [InlineData("2.3.0", RollForwardPolicy.LatestMinor, null)]
    [InlineData("2.1.0", RollForwardPolicy.Major, "2.1.3")] // the requested major is there: as Minor
    [InlineData("2.3.0", RollForwardPolicy.Major, "3.0.0")] // the lowest higher major and its lowest minor
    [InlineData("1.0.0", RollForwardPolicy.Major, "2.1.3")]
    [InlineData("3.2.0", RollForwardPolicy.Major, null)]
    [InlineData("2.1.0", RollForwardPolicy.LatestMajor, "3.1.2")]
    [InlineData("3.2.0", RollForwardPolicy.LatestMajor, null)]
    public void Roll_forward_policies_pick_what_the_host_picks(string requested, RollForwardPolicy policy, string? expected) =>
        Assert.Equal(expected, Resolve(requested, policy, false, "2.1.0", "2.1.3", "2.2.1", "3.0.0", "3.1.2"));

    [Fact]
    public void A_release_prefers_releases_and_takes_a_prerelease_only_when_no_release_fits()
    {
        // Releases first: 3.0.0 over the closer 2.2.0-preview (Major), as the host's release-only pass finds it.
        Assert.Equal("3.0.0", Resolve("2.1.0", RollForwardPolicy.Major, false, "2.2.0-preview.1", "3.0.0"));
        // No release fits: the pre-release does.
        Assert.Equal("2.2.0-preview.1", Resolve("2.1.0", RollForwardPolicy.Minor, false, "2.2.0-preview.1", "3.0.0"));
        // DOTNET_ROLL_FORWARD_TO_PRERELEASE=1: no preference, so the closest wins.
        Assert.Equal("2.2.0-preview.1", Resolve("2.1.0", RollForwardPolicy.Major, true, "2.2.0-preview.1", "3.0.0"));
        // Disable matches exactly, release or not.
        Assert.Null(Resolve("2.1.0", RollForwardPolicy.Disable, true, "2.1.0-preview.1"));
    }

    [Fact]
    public void A_prerelease_rolls_to_later_prereleases_and_releases_but_not_back_and_doesnt_patch_roll()
    {
        Assert.Equal(Rc2, Resolve(Rc2, RollForwardPolicy.Major, false, Rc1, Rc2, "11.0.0"));
        Assert.Equal("11.0.0-rc.3.1", Resolve(Rc2, RollForwardPolicy.Major, false, Rc1, "11.0.0-rc.3.1", "11.0.2")); // the lowest
        // A release match rolls to its latest patch.
        Assert.Equal("11.0.2", Resolve(Rc2, RollForwardPolicy.Major, false, Rc1, "11.0.0", "11.0.2"));
        Assert.Equal("12.0.0-preview.1.1", Resolve(Rc2, RollForwardPolicy.Major, false, Rc1, "10.0.9", "12.0.0-preview.1.1"));
        Assert.Null(Resolve(Rc2, RollForwardPolicy.Major, false, Rc1, "10.0.9"));
        // LatestMajor: the highest.
        Assert.Equal("12.0.0-preview.1.1", Resolve(Rc2, RollForwardPolicy.LatestMajor, false, "11.0.0", "12.0.0-preview.1.1"));
        // Minor (the default) stays in .NET 11.
        Assert.Null(Resolve(Rc2, RollForwardPolicy.Minor, false, "12.0.0"));
    }

    [Fact]
    public void The_macs_situation_rc2_with_major_is_missing_under_both_roots()
    {
        // 0.4.0-nightly.20261018.224 on the maintainer's Mac: the hosts ask for rc.2 with rollForward Major.
        string[] system = ["8.0.20", "9.0.9", .. Enumerable.Range(0, 10).Select(p => $"10.0.{p}")];
        string[] user = ["10.0.12", Rc1];
        Assert.Null(Resolve(Rc2, RollForwardPolicy.Major, false, system));
        Assert.Null(Resolve(Rc2, RollForwardPolicy.Major, false, user)); // rc.1 < rc.2
        Assert.Equal("8.0.20, 9.0.9, 10.0.0–10.0.9", FrameworkResolver.Describe(system.Select(V)));
        Assert.Equal($"10.0.12, {Rc1}", FrameworkResolver.Describe(user.Select(V)));

        // Under fake roots, as the shell finds them.
        var systemRoot = FakeRoot("system", system);
        var userRoot = FakeRoot("user", user);
        var config = WriteConfig(Path.Combine(_dir, "host"), "codalus-pluginhost", Rc2, "Major");
        var reference = Assert.Single(RuntimeConfig.Read(config, _ => null)!.Frameworks);
        Assert.Equal((RollForwardPolicy.Major, false), (reference.RollForward, reference.PreferRelease));
        foreach (var root in new[] { systemRoot, userRoot })
        {
            var result = HostRuntimeCheck.Check("codalus-pluginhost", RuntimeConfig.Read(config, _ => null)!, root);
            Assert.False(result.IsSatisfied);
            Assert.Contains($"needs the .NET runtime Microsoft.NETCore.App {Rc2} (rollForward Major), which isn't installed in {root}", result.Describe(), StringComparison.Ordinal);
        }

        // Installing rc.2 (or 11.0.0) under the user root fixes it.
        Directory.CreateDirectory(Path.Combine(userRoot, "shared", FrameworkReference.NetCoreApp, Rc2));
        File.WriteAllText(Path.Combine(userRoot, "shared", FrameworkReference.NetCoreApp, Rc2, FrameworkReference.NetCoreApp + ".deps.json"), "{}");
        Assert.Equal(Rc2, HostRuntimeCheck.Check("codalus-pluginhost", RuntimeConfig.Read(config, _ => null)!, userRoot).Frameworks[0].Resolved?.Text);
    }

    [Fact]
    public void Roll_forward_settings_follow_the_hosts_precedence()
    {
        var folder = Path.Combine(_dir, "precedence");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "app.runtimeconfig.json");
        File.WriteAllText(path, """
            {
              // comments and trailing commas, as the host reads them
              "runtimeOptions": {
                "tfm": "net11.0",
                "rollForward": "LatestMinor",
                "frameworks": [
                  { "name": "Microsoft.NETCore.App", "version": "11.0.0" },
                  { "name": "Microsoft.AspNetCore.App", "version": "11.0.0", "rollForward": "latestpatch" },
                ],
              }
            }
            """);
        Func<string, string?> none = _ => null;
        var config = RuntimeConfig.Read(path, none)!;
        Assert.Equal([RollForwardPolicy.LatestMinor, RollForwardPolicy.LatestPatch], config.Frameworks.Select(f => f.RollForward));
        Assert.All(config.Frameworks, f => Assert.True(f.PreferRelease));

        // DOTNET_ROLL_FORWARD wins over both; the muxer's --roll-forward over that.
        Assert.All(RuntimeConfig.Read(path, n => n == "DOTNET_ROLL_FORWARD" ? "Major" : null)!.Frameworks, f => Assert.Equal(RollForwardPolicy.Major, f.RollForward));
        Assert.All(RuntimeConfig.Read(path, n => n == "DOTNET_ROLL_FORWARD" ? "Major" : null, RollForwardPolicy.Disable)!.Frameworks, f => Assert.Equal(RollForwardPolicy.Disable, f.RollForward));
        // To prerelease: a release reference no longer prefers releases.
        Assert.All(RuntimeConfig.Read(path, n => n == "DOTNET_ROLL_FORWARD_TO_PRERELEASE" ? "1" : null)!.Frameworks, f => Assert.False(f.PreferRelease));
        // --fx-version: the first framework at exactly that version.
        Assert.Equal((RollForwardPolicy.Disable, "11.0.3"), (RuntimeConfig.Read(path, none, null, "11.0.3")!.Frameworks[0].RollForward, RuntimeConfig.Read(path, none, null, "11.0.3")!.Frameworks[0].Version.Text));
        // What the host itself would refuse isn't judged.
        Assert.Null(RuntimeConfig.Read(path, n => n == "DOTNET_ROLL_FORWARD" ? "Sideways" : null));

        // No framework: self-contained.
        File.WriteAllText(path, """{ "runtimeOptions": { "includedFrameworks": [ { "name": "Microsoft.NETCore.App", "version": "11.0.0" } ] } }""");
        Assert.False(RuntimeConfig.Read(path, none)!.IsFrameworkDependent);
    }

    [Fact]
    public void Installed_versions_need_their_deps_json_and_can_be_disabled()
    {
        var root = FakeRoot("deps", "10.0.9", "11.0.0");
        Directory.CreateDirectory(Path.Combine(root, "shared", FrameworkReference.NetCoreApp, "11.0.1")); // a half-removed folder
        Directory.CreateDirectory(Path.Combine(root, "shared", FrameworkReference.NetCoreApp, "not-a-version"));
        Assert.Equal(["10.0.9", "11.0.0"], HostRuntimeCheck.InstalledVersions(root, FrameworkReference.NetCoreApp, _ => null).Select(v => v.Text));
        Assert.Equal(["10.0.9"], HostRuntimeCheck.InstalledVersions(root, FrameworkReference.NetCoreApp, n => n == "DOTNET_DISABLE_RUNTIME_VERSIONS" ? "11.0.0;12.0.0" : null).Select(v => v.Text));
    }

    [Fact]
    public void An_app_host_uses_dotnet_root_then_the_registered_then_the_default_location_and_the_muxer_its_own_folder()
    {
        var resolver = new DotnetRootResolver
        {
            OS = "linux",
            Architecture = System.Runtime.InteropServices.Architecture.Arm64,
            ReadFirstLine = path => path == "/etc/dotnet/install_location" ? "/usr/lib/dotnet" : null,
        };
        Assert.Equal("/a", resolver.ForAppHost(n => n switch { "DOTNET_ROOT_ARM64" => "/a", "DOTNET_ROOT" => "/b", _ => null }));
        Assert.Equal("/b", resolver.ForAppHost(n => n == "DOTNET_ROOT" ? "/b" : null));
        Assert.Equal("/usr/lib/dotnet", resolver.ForAppHost(_ => null));
        Assert.Equal("/usr/share/dotnet", new DotnetRootResolver { OS = "linux", ReadFirstLine = _ => null }.ForAppHost(_ => null));
        Assert.Equal("/usr/local/share/dotnet", new DotnetRootResolver { OS = "macos", Architecture = System.Runtime.InteropServices.Architecture.Arm64, ReadFirstLine = _ => null }.ForAppHost(_ => null));
        Assert.Equal("/opt/dotnet", new DotnetRootResolver { OS = "macos", ReadFirstLine = p => p == "/etc/dotnet/install_location_arm64" ? "/opt/dotnet" : null, Architecture = System.Runtime.InteropServices.Architecture.Arm64 }.ForAppHost(_ => null));

        // The muxer: the dotnet on PATH, its links resolved.
        var bin = Path.Combine(_dir, "bin");
        var real = Path.Combine(_dir, "real-root");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(real);
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        File.WriteAllText(Path.Combine(real, exe), string.Empty);
        var muxer = new DotnetRootResolver
        {
            ProcessEnvironment = n => n == "PATH" ? bin : null,
            FileExists = p => p == Path.Combine(bin, exe) || File.Exists(p),
            RealPath = p => p == Path.Combine(bin, exe) ? Path.Combine(real, exe) : p,
        };
        Assert.Equal(real, muxer.ForMuxer("dotnet"));
        Assert.Equal(real, muxer.ForMuxer(Path.Combine(real, exe)));
        Assert.Null(new DotnetRootResolver { ProcessEnvironment = _ => null }.ForMuxer("dotnet"));
    }

    [Fact]
    public async Task A_host_whose_runtime_is_missing_isnt_started_and_says_which_runtime()
    {
        // The fake host: an app host (never run) whose runtimeconfig asks for a .NET the fake root doesn't have.
        var root = FakeRoot("installed", "8.0.20", "9.0.9", "10.0.0", "10.0.5", "10.0.9");
        var hostFolder = Path.Combine(_dir, "pluginhost");
        WriteConfig(hostFolder, "codalus-pluginhost", Rc2, "Major");
        var exe = Path.Combine(hostFolder, OperatingSystem.IsWindows() ? "codalus-pluginhost.exe" : "codalus-pluginhost");
        File.WriteAllText(exe, "never started");
        // DOTNET_ROOT_<ARCH> too: a machine's own one would win over DOTNET_ROOT.
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();
        var launch = new ProcessLaunchInfo(exe, []) { Environment = new Dictionary<string, string> { ["DOTNET_ROOT"] = root, ["DOTNET_ROOT_" + arch] = root } };

        var check = HostRuntimeCheck.Check(launch)!;
        Assert.False(check.IsSatisfied);
        Assert.Equal(root, check.DotnetRoot);

        var logs = new List<string>();
        await using var process = new SupervisedProcess("plugin host", launch);
        process.Log += (_, line) => logs.Add(line);
        var error = await Assert.ThrowsAsync<HostRuntimeMissingException>(() => process.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains($"needs the .NET runtime Microsoft.NETCore.App {Rc2} (rollForward Major), which isn't installed in {root} (found: 8.0.20, 9.0.9, 10.0.0–10.0.9)", error.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<JsonRpcConnectionLostException>(error); // what callers already handle
        Assert.Contains(logs, l => l.Contains("isn't installed", StringComparison.Ordinal)); // the host's Output channel
        Assert.True(process.GaveUp);
        // Later requests say the same, not "not running".
        Assert.IsType<HostRuntimeMissingException>(Assert.ThrowsAny<JsonRpcConnectionLostException>(() => process.Connection));

        // With the runtime there, the check passes (the fake host would start).
        var installed = Path.Combine(root, "shared", FrameworkReference.NetCoreApp, "11.0.0");
        Directory.CreateDirectory(installed);
        File.WriteAllText(Path.Combine(installed, FrameworkReference.NetCoreApp + ".deps.json"), "{}");
        Assert.True(HostRuntimeCheck.Check(launch)!.IsSatisfied);
        // Turned off, or not asked for: no check.
        Assert.Null(HostRuntimeCheck.Check(launch, new DotnetRootResolver { ProcessEnvironment = n => n == HostRuntimeCheck.SkipVariable ? "1" : Environment.GetEnvironmentVariable(n) }));
    }

    [Fact]
    public void A_dll_through_the_muxer_is_checked_under_the_muxers_root_and_sdk_commands_arent()
    {
        var root = FakeRoot("muxer", "10.0.9");
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        File.WriteAllText(Path.Combine(root, exe), string.Empty);
        var dll = Path.Combine(_dir, "app", "codalus-t4host.dll");
        WriteConfig(Path.GetDirectoryName(dll)!, "codalus-t4host", Rc2, "Major");
        var muxer = Path.Combine(root, exe);

        Assert.False(HostRuntimeCheck.Check(ProcessLaunchInfo.FromPath(dll) with { FileName = muxer })!.IsSatisfied);
        // dotnet exec --runtimeconfig <other> app.dll: the named runtimeconfig.
        var other = WriteConfig(Path.Combine(_dir, "other"), "app", "10.0.0", "Minor");
        Assert.True(HostRuntimeCheck.Check(new ProcessLaunchInfo(muxer, ["exec", "--runtimeconfig", other, dll]))!.IsSatisfied);
        // --roll-forward Disable from the command line wins: 10.0.0 isn't there exactly.
        Assert.False(HostRuntimeCheck.Check(new ProcessLaunchInfo(muxer, ["exec", "--runtimeconfig", other, "--roll-forward", "Disable", dll]))!.IsSatisfied);
        Assert.Null(HostRuntimeCheck.Check(new ProcessLaunchInfo(muxer, ["build", "x.csproj"])));
        Assert.Null(HostRuntimeCheck.Check(new ProcessLaunchInfo(muxer, ["exec", "--unknown-option", "x", dll])));
    }

    [Fact]
    public async Task A_host_that_writes_to_stderr_and_exits_at_startup_puts_its_message_in_the_error_and_isnt_restarted()
    {
        // A stand-in host: writes why it can't run to standard error and exits, before any handshake.
        var launch = OperatingSystem.IsWindows()
            ? new ProcessLaunchInfo("cmd.exe", ["/c", "echo You must install or update .NET to run this application. 1>&2 & echo Framework: 'Microsoft.NETCore.App', version '99.0.0' 1>&2 & exit /b 150"])
            : new ProcessLaunchInfo("/bin/sh", ["-c", "echo 'You must install or update .NET to run this application.' >&2; echo \"Framework: 'Microsoft.NETCore.App', version '99.0.0'\" >&2; exit 150"]);
        var crashes = new List<ProcessCrashedEventArgs>();
        var logs = new List<string>();
        await using var process = new SupervisedProcess("fake host", launch, new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(10), ShutdownRequest = null, ExitNotification = null })
        {
            Initialize = (connection, _, ct) => connection.InvokeAsync("initialize", ct),
        };
        process.Crashed += (_, e) => { lock (crashes) { crashes.Add(e); } };
        process.Log += (_, line) => { lock (logs) { logs.Add(line); } };

        var error = await Assert.ThrowsAsync<HostExitedException>(() => process.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains("fake host exited during startup with exit code 150: You must install or update .NET to run this application.", error.Message, StringComparison.Ordinal);
        Assert.Contains("version '99.0.0'", error.Message, StringComparison.Ordinal);
        Assert.Equal((150, true), (error.ExitCode, error.DuringStartup));
        Assert.Equal(2, error.ErrorOutput.Count);

        // One crash, not restarted: an exit during startup would repeat.
        await WaitUntilAsync(() => { lock (crashes) { return crashes.Count > 0; } });
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var crash = Assert.Single(crashes);
        Assert.Equal((false, true, 150), (crash.WillRestart, crash.DuringStartup, crash.ExitCode));
        Assert.IsType<HostExitedException>(crash.Error);
        Assert.True(process.GaveUp);
        lock (logs)
        {
            Assert.Contains(logs, l => l.Contains("You must install", StringComparison.Ordinal)); // the raw line, for the Output channel
            Assert.Contains(logs, l => l.Contains("It isn't started again", StringComparison.Ordinal));
        }

        Assert.IsType<HostExitedException>(Assert.ThrowsAny<JsonRpcConnectionLostException>(() => process.Connection));
    }

    [Fact]
    public async Task A_policy_can_still_restart_after_a_startup_exit()
    {
        var launch = OperatingSystem.IsWindows()
            ? new ProcessLaunchInfo("cmd.exe", ["/c", "echo boom 1>&2 & exit /b 1"])
            : new ProcessLaunchInfo("/bin/sh", ["-c", "echo boom >&2; exit 1"]);
        var crashes = 0;
        await using var process = new SupervisedProcess("flaky host", launch, new SupervisionPolicy { RestartAfterStartupExit = true, MaxRestarts = 2, InitialBackoff = TimeSpan.FromMilliseconds(10), ShutdownRequest = null, ExitNotification = null })
        {
            Initialize = (connection, _, ct) => connection.InvokeAsync("initialize", ct),
        };
        process.Crashed += (_, _) => Interlocked.Increment(ref crashes);
        await Assert.ThrowsAsync<HostExitedException>(() => process.StartAsync(TestContext.Current.CancellationToken));
        await WaitUntilAsync(() => process.GaveUp);
        Assert.Equal(3, Volatile.Read(ref crashes)); // the start and two restarts
    }

    private string FakeRoot(string name, params string[] versions)
    {
        var root = Path.Combine(_dir, name);
        foreach (var version in versions)
        {
            var folder = Path.Combine(root, "shared", FrameworkReference.NetCoreApp, version);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, FrameworkReference.NetCoreApp + ".deps.json"), "{}");
        }

        Directory.CreateDirectory(root);
        return root;
    }

    private static string WriteConfig(string folder, string name, string version, string rollForward)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".runtimeconfig.json");
        File.WriteAllText(path, $$"""
            {
              "runtimeOptions": {
                "tfm": "net11.0",
                "rollForward": "{{rollForward}}",
                "framework": { "name": "Microsoft.NETCore.App", "version": "{{version}}" }
              }
            }
            """);
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }
}
