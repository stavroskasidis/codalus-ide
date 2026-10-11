using System.Runtime.InteropServices;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>
/// M33 (ADR 0030): what a target framework says about its runtime, and the <c>dotnet</c> a program starts on, on each
/// kind of machine (simulated: an Arm64 Mac, Linux with a newer ICU, an x64 machine with a private SDK).
/// </summary>
public sealed class OlderFrameworksTests
{
    [Theory]
    [InlineData("netcoreapp2.0", "2.0")]
    [InlineData("netcoreapp3.1", "3.1")]
    [InlineData("net5.0", "5.0")]
    [InlineData("net8.0-windows", "8.0")]
    [InlineData("NET10.0", "10.0")]
    [InlineData("netstandard2.0", null)]
    [InlineData("net48", null)]
    [InlineData("net472", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Target_frameworks_name_their_dotnet_version(string? tfm, string? expected) =>
        Assert.Equal(expected is null ? null : Version.Parse(expected), DotnetFrameworks.NetCoreVersion(tfm));

    [Theory]
    [InlineData("netcoreapp2.1", "7.3")]
    [InlineData("netstandard2.0", "7.3")]
    [InlineData("netcoreapp3.0", "8.0")]
    [InlineData("netstandard2.1", "8.0")]
    [InlineData("net5.0", "9.0")]
    [InlineData("net6.0", "10.0")]
    [InlineData("net7.0", "11.0")]
    [InlineData("net8.0", "12.0")]
    [InlineData("net9.0", "13.0")]
    [InlineData("net10.0", "14.0")]
    [InlineData("net48", null)]
    public void Each_framework_defaults_to_its_csharp_version(string tfm, string? expected) =>
        Assert.Equal(expected, DotnetFrameworks.DefaultCSharpVersion(tfm));

    [Theory]
    [InlineData("netcoreapp3.1", true)]
    [InlineData("net5.0", true)]
    [InlineData("net6.0", false)]
    [InlineData("net10.0", false)]
    [InlineData("netstandard2.0", false)]
    public void Hot_reload_needs_net6(string tfm, bool unsupported) =>
        Assert.Equal(unsupported, DotnetFrameworks.HotReloadUnsupportedReason(tfm, "App") is not null);

    [Theory]
    [InlineData("netcoreapp3.1", "OSX", true)]
    [InlineData("net5.0", "OSX", true)]
    [InlineData("net6.0", "OSX", false)]
    [InlineData("netcoreapp3.1", "WINDOWS", true)]
    [InlineData("net5.0", "WINDOWS", false)]
    [InlineData("netcoreapp3.1", "LINUX", false)]
    public void Frameworks_without_an_arm64_runtime_run_as_x64(string tfm, string os, bool x64)
    {
        Assert.Equal(x64, DotnetFrameworks.RunsAsX64(tfm, Architecture.Arm64, OSPlatform.Create(os)));
        Assert.False(DotnetFrameworks.RunsAsX64(tfm, Architecture.X64, OSPlatform.Create(os)));
    }

    private static RunInfo Info(string tfm, string command, string arguments = "") =>
        new("/src/App/App.csproj", tfm, "Exe", "/src/App/bin/App.dll", command, arguments, "/src/App", null);

    private static LaunchPlan Plan(RunInfo info, IReadOnlyDictionary<string, string>? environment = null) =>
        new(info.RunCommand!, info.RunArguments ?? string.Empty, "/src/App", environment ?? new Dictionary<string, string>(), null);

    private static RuntimeSelection.Machine Mac(params string[] files) =>
        new(Architecture.Arm64, OSPlatform.OSX, f => files.Contains(f.Replace('\\', '/')), _ => null, _ => null);

    [Fact]
    public void On_an_arm64_mac_dotnet_exec_of_netcoreapp21_runs_on_the_x64_dotnet()
    {
        var info = Info("netcoreapp2.1", "/usr/local/share/dotnet/dotnet", "exec \"/src/App/bin/App.dll\"");
        var plan = RuntimeSelection.Apply(Plan(info), info, "/usr/local/share/dotnet", Mac("/usr/local/share/dotnet/x64/dotnet"));
        Assert.Equal("/usr/local/share/dotnet/x64/dotnet", plan.FileName.Replace('\\', '/'));
        Assert.True(plan.RunsAsX64);
        Assert.Contains(plan.Notes, n => n.Contains("no Arm64 runtime", StringComparison.Ordinal));
    }

    [Fact]
    public void On_an_arm64_mac_the_apphost_of_netcoreapp31_gets_the_x64_root()
    {
        var info = Info("netcoreapp3.1", "/src/App/bin/App");
        var plan = RuntimeSelection.Apply(Plan(info, new Dictionary<string, string> { ["DOTNET_ROOT"] = "/Users/me/.dotnet" }), info, "/Users/me/.dotnet",
            Mac("/Users/me/.dotnet/x64/dotnet"));
        Assert.Equal("/src/App/bin/App", plan.FileName);
        Assert.Equal("/Users/me/.dotnet/x64", plan.Environment["DOTNET_ROOT"].Replace('\\', '/'));
        Assert.Equal("/Users/me/.dotnet/x64", plan.Environment["DOTNET_ROOT_X64"].Replace('\\', '/'));
        Assert.True(plan.RunsAsX64);
    }

    [Fact]
    public void On_an_arm64_mac_the_registered_x64_location_is_found_and_a_missing_one_is_explained()
    {
        var info = Info("net5.0", "/opt/dotnet/dotnet", "exec App.dll");
        var registered = new RuntimeSelection.Machine(Architecture.Arm64, OSPlatform.OSX, f => f.Replace('\\', '/') == "/opt/x64/dotnet",
            f => f == "/etc/dotnet/install_location_x64" ? "/opt/x64\n" : null, _ => null);
        Assert.Equal("/opt/x64/dotnet", RuntimeSelection.Apply(Plan(info), info, "/opt/dotnet", registered).FileName.Replace('\\', '/'));

        var error = Assert.Throws<InvalidOperationException>(() => RuntimeSelection.Apply(Plan(info), info, "/opt/dotnet", Mac()));
        Assert.Contains("Install the x64 .NET 5.0 runtime", error.Message, StringComparison.Ordinal);
        Assert.Contains("Rosetta 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void On_an_arm64_mac_net6_and_later_stay_native()
    {
        var info = Info("net6.0", "/usr/local/share/dotnet/dotnet", "exec App.dll");
        var plan = RuntimeSelection.Apply(Plan(info), info, "/usr/local/share/dotnet", Mac("/usr/local/share/dotnet/x64/dotnet"));
        Assert.Equal("/usr/local/share/dotnet/dotnet", plan.FileName);
        Assert.False(plan.RunsAsX64);
    }

    [Fact]
    public void An_apphost_gets_the_sdks_root_unless_the_variable_is_set()
    {
        var x64 = new RuntimeSelection.Machine(Architecture.X64, OSPlatform.Linux, _ => false, _ => null, _ => null);
        var net8 = Info("net8.0", "/src/App/bin/App");
        Assert.Equal("/home/me/.dotnet", RuntimeSelection.Apply(Plan(net8), net8, "/home/me/.dotnet", x64).Environment["DOTNET_ROOT_X64"]);
        var core31 = Info("netcoreapp3.1", "/src/App/bin/App");
        Assert.Equal("/home/me/.dotnet", RuntimeSelection.Apply(Plan(core31), core31, "/home/me/.dotnet", x64).Environment["DOTNET_ROOT"]);

        var set = x64 with { GetEnvironmentVariable = name => name == "DOTNET_ROOT_X64" ? "/elsewhere" : null };
        Assert.False(RuntimeSelection.Apply(Plan(net8), net8, "/home/me/.dotnet", set).Environment.ContainsKey("DOTNET_ROOT_X64"));

        // dotnet exec, and Executable profiles (another program), are left alone.
        var muxer = Info("net8.0", "/home/me/.dotnet/dotnet", "exec App.dll");
        Assert.Empty(RuntimeSelection.Apply(Plan(muxer), muxer, "/home/me/.dotnet", x64).Environment);
        var other = new LaunchPlan("/usr/bin/env", string.Empty, "/src", new Dictionary<string, string>(), null);
        Assert.Same(other, RuntimeSelection.Apply(other, net8, "/home/me/.dotnet", x64));
    }

    [Fact]
    public void On_linux_netcoreapp_before_5_gets_the_systems_icu_unless_the_launch_chooses()
    {
        var linux = new RuntimeSelection.Machine(Architecture.X64, OSPlatform.Linux, _ => false, _ => null, _ => null, () => 74);
        var core21 = Info("netcoreapp2.1", "/usr/share/dotnet/dotnet", "exec App.dll");
        var plan = RuntimeSelection.Apply(Plan(core21), core21, "/usr/share/dotnet", linux);
        Assert.Equal("74", plan.Environment["CLR_ICU_VERSION_OVERRIDE"]);
        Assert.Contains(plan.Notes, n => n.StartsWith("CLR_ICU_VERSION_OVERRIDE=74", StringComparison.Ordinal));

        var invariant = RuntimeSelection.Apply(Plan(core21, new Dictionary<string, string> { ["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1" }), core21, "/usr/share/dotnet", linux);
        Assert.False(invariant.Environment.ContainsKey("CLR_ICU_VERSION_OVERRIDE"));
        var chosen = RuntimeSelection.Apply(Plan(core21), core21, "/usr/share/dotnet", linux with { GetEnvironmentVariable = n => n == "CLR_ICU_VERSION_OVERRIDE" ? "72" : null });
        Assert.False(chosen.Environment.ContainsKey("CLR_ICU_VERSION_OVERRIDE"));

        var net5 = Info("net5.0", "/usr/share/dotnet/dotnet", "exec App.dll");
        Assert.Empty(RuntimeSelection.Apply(Plan(net5), net5, "/usr/share/dotnet", linux).Environment);
        var windows = linux with { Os = OSPlatform.Windows };
        Assert.False(RuntimeSelection.Apply(Plan(core21), core21, "/usr/share/dotnet", windows).Environment.ContainsKey("CLR_ICU_VERSION_OVERRIDE"));
    }

    [Fact]
    public void On_linux_the_debugger_gets_the_programs_icu_choice_or_the_systems_icu()
    {
        // netcoredbg reads symbols on the debuggee's runtime in its own process (M33): .NET Core 2.x stopped it at start.
        static string? None(string name) => null;
        var launch = new Dictionary<string, string> { ["CLR_ICU_VERSION_OVERRIDE"] = "74", ["ASPNETCORE_ENVIRONMENT"] = "Development" };
        var adapter = Debugging.DebugService.AdapterEnvironment(launch, OSPlatform.Linux, None, () => 70);
        Assert.Equal(new Dictionary<string, string> { ["CLR_ICU_VERSION_OVERRIDE"] = "74" }, adapter);

        var invariant = new Dictionary<string, string> { ["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1" };
        Assert.Equal(invariant, Debugging.DebugService.AdapterEnvironment(invariant, OSPlatform.Linux, None, () => 74));

        // An attach, or a newer program: the system's ICU, unless the shell's environment decides.
        Assert.Equal("74", Debugging.DebugService.AdapterEnvironment(null, OSPlatform.Linux, None, () => 74)["CLR_ICU_VERSION_OVERRIDE"]);
        Assert.Empty(Debugging.DebugService.AdapterEnvironment(null, OSPlatform.Linux, n => n == "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT" ? "1" : null, () => 74));
        Assert.Empty(Debugging.DebugService.AdapterEnvironment(null, OSPlatform.Linux, None, () => null));
        Assert.Empty(Debugging.DebugService.AdapterEnvironment(launch, OSPlatform.Windows, None, () => 74));
    }
}
