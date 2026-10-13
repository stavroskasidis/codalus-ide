using Codalus.Workbench.Debugging;

namespace Codalus.Debugging.Tests;

/// <summary>A session whose adapter can't start leaves the service idle (no netcoredbg needed).</summary>
public sealed class DebugServiceStartTests
{
    [Fact]
    public async Task A_missing_debugger_fails_the_start_and_the_next_start_fails_the_same_way()
    {
        var debug = new DebugService(() => null);
        var environment = new Dictionary<string, string>();
        var first = await Assert.ThrowsAsync<InvalidOperationException>(() => debug.LaunchAsync("app", [], ".", environment, TestContext.Current.CancellationToken));
        Assert.Contains("netcoredbg", first.Message, StringComparison.Ordinal);
        Assert.Equal(DebugState.Idle, debug.State);
        Assert.False(debug.IsActive);

        // Not "a debug session is already running".
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() => debug.LaunchAsync("app", [], ".", environment, TestContext.Current.CancellationToken));
        Assert.Contains("netcoredbg", second.Message, StringComparison.Ordinal);
        await debug.StopAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_x64_program_needs_the_x64_debugger_and_the_message_says_where_it_goes()
    {
        // M33: on an Arm64 Mac, .NET 5 and earlier run as x64, and so must their debugger.
        var debug = new DebugService(() => null, resolveX64Adapter: () => null);
        var plan = new Workbench.Build.LaunchPlan("dotnet", "exec app.dll", ".", new Dictionary<string, string>(), null) { RunsAsX64 = true };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => debug.LaunchAsync(plan, TestContext.Current.CancellationToken));
        Assert.Contains("x64 debugger", error.Message, StringComparison.Ordinal);
        Assert.Contains("debugger/x64/", error.Message, StringComparison.Ordinal);
        Assert.Contains(DebugAdapterLocator.X64EnvironmentVariable, error.Message, StringComparison.Ordinal);
        Assert.Equal(DebugState.Idle, debug.State);
    }

    [Fact]
    public void Mach_o_headers_tell_an_x64_program_from_an_arm64_one()
    {
        byte[] x64 = [0xCF, 0xFA, 0xED, 0xFE, 0x07, 0x00, 0x00, 0x01];
        byte[] arm64 = [0xCF, 0xFA, 0xED, 0xFE, 0x0C, 0x00, 0x00, 0x01];
        byte[] fat = [0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x00, 0x00, 0x02];
        Assert.True(DebugAdapterLocator.IsMachOX64(x64));
        Assert.False(DebugAdapterLocator.IsMachOX64(arm64));
        Assert.False(DebugAdapterLocator.IsMachOX64(fat));
        Assert.False(DebugAdapterLocator.IsMachOX64([0x4D, 0x5A]));

        // Off an Arm64 Mac, nothing runs as x64 under emulation, and the regular debugger debugs everything.
        if (!(OperatingSystem.IsMacOS() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64))
        {
            Assert.False(DebugAdapterLocator.RunsAsX64OnArm64Mac(typeof(object).Assembly.Location));
        }

        if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.Arm64)
        {
            Assert.Equal(DebugAdapterLocator.Resolve(), DebugAdapterLocator.ResolveX64());
        }
    }
}
