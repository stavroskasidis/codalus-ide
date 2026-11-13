using Codalus.Workbench.Debugging;

namespace Codalus.Debugging.Tests;

/// <summary>A session whose adapter can't start leaves the service idle (no netcoredbg needed).</summary>
public sealed class DebugServiceStartTests
{
    [Fact]
    public async Task A_missing_codalus_debugger_fails_the_start_and_says_where_it_goes()
    {
        // The default (M58): the Codalus debugger, whose host isn't installed here.
        var debug = new DebugService(() => null) { CoreClrAdapter = () => null };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => debug.LaunchAsync("app", [], ".", new Dictionary<string, string>(), TestContext.Current.CancellationToken));
        Assert.Contains("The Codalus debugger (codalus-debughost) wasn't found", error.Message, StringComparison.Ordinal);
        Assert.Equal(DebugState.Idle, debug.State);
        Assert.False(debug.IsActive);
    }

    [Fact]
    public async Task A_missing_debugger_fails_the_start_and_the_next_start_fails_the_same_way()
    {
        var debug = new DebugService(() => null) { AdapterSetting = () => DebugService.NetcoredbgAdapter };
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
    public async Task An_x64_program_goes_to_the_debuggers_x64_bundle_when_the_installation_offers_one()
    {
        // M58b (ADR 0094): the osx-x64 bundle, a component. Here the user declines it (the resolver answers null).
        var asked = 0;
        var debug = new DebugService(() => null, resolveX64Adapter: () => null)
        {
            CoreClrAdapter = () => null,
            X64CoreClrAdapter = _ =>
            {
                asked++;
                return Task.FromResult<DebugAdapterStartInfo?>(null);
            },
        };
        var output = new System.Text.StringBuilder();
        debug.Output += (_, o) => output.Append(o.Text);
        var plan = new Workbench.Build.LaunchPlan("dotnet", "exec app.dll", ".", new Dictionary<string, string>(), null) { RunsAsX64 = true };
        Assert.True(debug.WillUseCodalusAdapter(plan));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => debug.LaunchAsync(plan, TestContext.Current.CancellationToken));
        Assert.Contains("The Codalus debugger for x64 programs", error.Message, StringComparison.Ordinal);
        Assert.Contains("Components page", error.Message, StringComparison.Ordinal);
        Assert.True(asked >= 1);
        Assert.StartsWith("Debugging with the Codalus debugger.", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(DebugState.Idle, debug.State);

        // Without one, netcoredbg's x64 build debugs it, and the Debug Console says why.
        debug.X64CoreClrAdapter = null;
        Assert.False(debug.WillUseCodalusAdapter(plan));
        output.Clear();
        var fallback = await Assert.ThrowsAsync<InvalidOperationException>(() => debug.LaunchAsync(plan, TestContext.Current.CancellationToken));
        Assert.Contains("x64 debugger", fallback.Message, StringComparison.Ordinal);
        Assert.Contains("Debugging with netcoredbg: it runs as x64 on Apple silicon", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_x64_program_of_dotnet_core_2_0_or_2_1_goes_to_the_x64_bundle_and_is_told_it_cant_be_held()
    {
        // The x64 bundle holds a program at its start in the startup hook (ADR 0094's amendment), and .NET Core 2.0 and
        // 2.1 run none: it attaches once the program runs, and the Debug Console says so before the start. For a day
        // these two were netcoredbg's, whose x64 build then crashed on every x64 program on macOS 26 (run 43337656697).
        var asked = 0;
        var debug = new DebugService(() => null, resolveX64Adapter: () => null)
        {
            CoreClrAdapter = () => null,
            X64CoreClrAdapter = _ =>
            {
                asked++;
                return Task.FromResult<DebugAdapterStartInfo?>(null);
            },
        };
        var output = new System.Text.StringBuilder();
        debug.Output += (_, o) => output.Append(o.Text);
        var plan = new Workbench.Build.LaunchPlan("dotnet", "exec app.dll", ".", new Dictionary<string, string>(), null) { RunsAsX64 = true, StartsWithoutStartupHooks = true };

        Assert.True(debug.WillUseCodalusAdapter(plan));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => debug.LaunchAsync(plan, TestContext.Current.CancellationToken));

        // The bundle isn't here, so the start fails: what counts is which debugger was asked for, and what the console said.
        Assert.Contains("The Codalus debugger for x64 programs", error.Message, StringComparison.Ordinal);
        Assert.True(asked >= 1);
        Assert.StartsWith("Debugging with the Codalus debugger.", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(".NET Core 2.0 and 2.1 run no startup hooks", output.ToString(), StringComparison.Ordinal);

        // A runtime with hooks, or the same runtime where it runs natively, is told nothing: it is held at its start.
        output.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => debug.LaunchAsync(plan with { StartsWithoutStartupHooks = false }, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("startup hooks", output.ToString(), StringComparison.Ordinal);
        output.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => debug.LaunchAsync(plan with { RunsAsX64 = false }, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("startup hooks", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_32_bit_dotnet_framework_program_goes_to_the_debuggers_32_bit_bundle(bool runs32Bit)
    {
        // M58b (ADR 0094): the win-x86 bundle, a component; a 64-bit program never asks for it. Neither adapter is
        // here, so the start fails either way: what counts is which one was asked for.
        var asked = 0;
        var debug = new DebugService(() => null)
        {
            IsDesktopClrProgram = _ => true,
            Is32BitDesktopClrProgram = _ => runs32Bit,
            DesktopClrAdapter = () => null,
            X86DesktopClrAdapter = _ =>
            {
                asked++;
                return Task.FromResult<DebugAdapterStartInfo?>(null);
            },
        };
        var plan = new Workbench.Build.LaunchPlan(Path.Combine(Path.GetTempPath(), "app.exe"), string.Empty, Path.GetTempPath(), new Dictionary<string, string>(), null);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => debug.LaunchAsync(plan, TestContext.Current.CancellationToken));
        Assert.Contains("The .NET Framework debugger (codalus-debughost) wasn't found", error.Message, StringComparison.Ordinal);
        Assert.Equal(runs32Bit, asked >= 1);
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
