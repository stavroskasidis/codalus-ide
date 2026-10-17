using System.Runtime.InteropServices;
using Codalus.Plugins.DotNetTest;

namespace Codalus.Plugins.Tests;

/// <summary>
/// The test hosts of older frameworks (M33, ADR 0030): x64 on an Arm64 Mac before .NET 6, the system's ICU on Linux
/// before .NET 5, and Coverlet's netstandard2.0 collector before .NET 8.
/// </summary>
public sealed class OlderFrameworksTests
{
    private static RunOptions Options(string? architecture = null, Dictionary<string, string>? environment = null) =>
        new(RunMode.Run, environment ?? [], null, architecture, Parallel: true);

    [Theory]
    [InlineData("netcoreapp3.1", "x64")]
    [InlineData("net5.0", "x64")]
    [InlineData("net6.0", null)]
    [InlineData("netstandard2.0", null)]
    public void On_an_arm64_mac_test_hosts_before_net6_run_as_x64(string tfm, string? expected)
    {
        var options = OlderFrameworks.Adjust(Options(), tfm, Architecture.Arm64, macOS: true, linux: false, _ => null, () => null);
        Assert.Equal(expected, string.IsNullOrEmpty(options.Architecture) ? null : options.Architecture);

        // A chosen architecture (testing.architecture) stays, and x64 machines don't change anything.
        Assert.Equal("arm64", OlderFrameworks.Adjust(Options("arm64"), tfm, Architecture.Arm64, macOS: true, linux: false, _ => null, () => null).Architecture);
        Assert.True(string.IsNullOrEmpty(OlderFrameworks.Adjust(Options(), tfm, Architecture.X64, macOS: true, linux: false, _ => null, () => null).Architecture));
    }

    [Fact]
    public void On_linux_test_hosts_before_net5_get_the_systems_icu_unless_it_is_chosen()
    {
        var adjusted = OlderFrameworks.Adjust(Options(), "netcoreapp2.1", Architecture.X64, macOS: false, linux: true, _ => null, () => 74);
        Assert.Equal("74", adjusted.Environment["CLR_ICU_VERSION_OVERRIDE"]);
        Assert.Empty(OlderFrameworks.Adjust(Options(), "net5.0", Architecture.X64, macOS: false, linux: true, _ => null, () => 74).Environment);
        Assert.DoesNotContain("CLR_ICU_VERSION_OVERRIDE", OlderFrameworks.Adjust(Options(environment: new() { ["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1" }), "netcoreapp3.1", Architecture.X64, false, true, _ => null, () => 74).Environment.Keys);
        Assert.Empty(OlderFrameworks.Adjust(Options(), "netcoreapp3.1", Architecture.X64, false, true, n => n == "CLR_ICU_VERSION_OVERRIDE" ? "70" : null, () => 74).Environment);
        Assert.Empty(OlderFrameworks.Adjust(Options(), "netcoreapp3.1", Architecture.X64, false, false, _ => null, () => 74).Environment);
    }

    [Theory]
    [InlineData("netcoreapp2.0", "collector-netstandard2.0")]
    [InlineData("net7.0", "collector-netstandard2.0")]
    [InlineData("net8.0", "collector")]
    [InlineData("net10.0", "collector")]
    [InlineData(null, "collector")]
    public void Coverage_uses_the_collector_that_loads_into_the_test_host(string? tfm, string folder)
    {
        // (Both are bundled by the plugin's build; DotNetTestCoverageTests run them from the plugin's own folder.)
        Assert.Equal(folder, Path.GetFileName(CoverageTools.CollectorFolder(tfm)));
    }

    [Fact]
    public void Test_projects_whose_every_framework_targets_windows_run_only_there()
    {
        // M40a: net*-windows test projects (WinForms, WPF) say "Windows only" off Windows instead of failing to start.
        Assert.True(WindowsFrameworks.IsWindows("net11.0-windows"));
        Assert.True(WindowsFrameworks.IsWindows("net10.0-windows10.0.19041.0"));
        Assert.False(WindowsFrameworks.IsWindows("net11.0"));
        Assert.False(WindowsFrameworks.IsWindows("net10.0-android"));
        Assert.True(WindowsFrameworks.OnlyWindows(["net11.0-windows"], windows: false));
        Assert.False(WindowsFrameworks.OnlyWindows(["net11.0-windows"], windows: true));
        Assert.False(WindowsFrameworks.OnlyWindows(["net11.0", "net11.0-windows"], windows: false));
        Assert.False(WindowsFrameworks.OnlyWindows([], windows: false));
    }

    [Fact]
    public void Debugging_net_framework_tests_runs_the_test_host_as_the_debuggers_architecture()
    {
        // M43b: the desktop-CLR debugger debugs processes of its own bitness; VSTest's default .NET Framework host may be 32-bit.
        var debug = new RunOptions(RunMode.Debug, new Dictionary<string, string>(), null, null, Parallel: true);
        Assert.Equal("x64", OlderFrameworks.DebugNetFramework(debug, "net48", windows: true, Architecture.X64).Architecture);
        Assert.Equal("ARM64", OlderFrameworks.DebugNetFramework(debug, "net481", windows: true, Architecture.Arm64).Architecture);
        Assert.Null(OlderFrameworks.DebugNetFramework(debug, "net8.0", windows: true, Architecture.X64).Architecture);
        Assert.Null(OlderFrameworks.DebugNetFramework(debug, "net48", windows: false, Architecture.X64).Architecture);
        Assert.Null(OlderFrameworks.DebugNetFramework(Options(), "net48", windows: true, Architecture.X64).Architecture);
        Assert.Equal("x86", OlderFrameworks.DebugNetFramework(debug with { Architecture = "x86" }, "net48", windows: true, Architecture.X64).Architecture);
    }

    [Fact]
    public void Net_framework_test_projects_run_only_on_windows()
    {
        // M43a: net4x test hosts run on the desktop CLR (M43b runs them there); off Windows the project says why.
        Assert.True(WindowsFrameworks.IsNetFramework("net48"));
        Assert.True(WindowsFrameworks.IsNetFramework("net472"));
        Assert.False(WindowsFrameworks.IsNetFramework("net8.0"));
        Assert.False(WindowsFrameworks.IsNetFramework("netstandard2.0"));
        Assert.True(WindowsFrameworks.OnlyWindows(["net48"], windows: false));
        Assert.True(WindowsFrameworks.OnlyWindows(["net48", "net11.0-windows"], windows: false));
        Assert.False(WindowsFrameworks.OnlyWindows(["net48"], windows: true));
        Assert.False(WindowsFrameworks.OnlyWindows(["net48", "net8.0"], windows: false));
        Assert.Equal("Tests targets .NET Framework (net48): its tests run only on Windows.", WindowsFrameworks.Reason("Tests", ["net48"]));
        Assert.Equal("Tests targets Windows (net11.0-windows): its tests run only on Windows.", WindowsFrameworks.Reason("Tests", ["net11.0-windows"]));
    }
}
