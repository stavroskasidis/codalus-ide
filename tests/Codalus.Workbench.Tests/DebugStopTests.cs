using System.Globalization;
using System.Reflection;
using Codalus.Debugging;
using Codalus.Workbench.Debugging;

namespace Codalus.Workbench.Tests;

/// <summary>
/// Stop is done when the program it ended has. netcoredbg answered a Stop's disconnect while the Blazor server it had
/// launched still listened, and the next F5's server, started 6 s later on the same port, failed with "address already
/// in use" (Linux CI, the smoke test's blazor.compoundHotReload.netcoredbg, develop's dry run 44952519485). The adapter
/// here is the scripted one (<c>Codalus.LspConformance.Server</c>), which names a real process in its process event and
/// leaves it running on disconnect.
/// </summary>
public sealed class DebugStopTests
{
    private static string Metadata(string key) =>
        typeof(DebugStopTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    private static string ScriptedAdapter => Path.Combine(Metadata("CodalusRepoRoot"), "tests", "Codalus.LspConformance.Server", "bin", Metadata("CodalusConfiguration"), Codalus.Tests.RepoBuild.TargetFramework, "codalus-lsp-conformance.dll");

    /// <summary>The scripted adapter's own executable, which netcoredbg's arguments make a stand-in for netcoredbg.</summary>
    private static string NetcoredbgStandIn => Path.ChangeExtension(ScriptedAdapter, OperatingSystem.IsWindows() ? ".exe" : null);

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token).Token;

    /// <summary>The program's options: it runs until the session ends it, and the process event names <paramref name="program"/>.</summary>
    private static Dictionary<string, string> Options(System.Diagnostics.Process program) =>
        new() { ["keepsRunning"] = "true", ["processId"] = program.Id.ToString(CultureInfo.InvariantCulture) };

    [Fact]
    public async Task Stop_ends_a_launched_program_its_debugger_left_running()
    {
        // netcoredbg on this machine, launching the program with the Debug Console.
        var plan = new Codalus.Workbench.Build.LaunchPlan("dotnet", string.Empty, AppContext.BaseDirectory, new Dictionary<string, string>(), null) { ArgumentList = ["exec", "program.dll"] };
        await StopAsync(program => new DebugLaunch("Server", plan with { Environment = Options(program) }), (program, output) =>
        {
            // Not a wait: Stop returns once the program has ended.
            Assert.True(program.HasExited, "The program still runs after Stop.");
            Assert.Contains($"The program (process {program.Id.ToString(CultureInfo.InvariantCulture)}) was still running 5 s after the debugger ended it", output, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Stop_leaves_a_program_behind_a_path_mapping_to_its_debugger()
    {
        // A WSL or container launch: the process event's id is another system's, which here may be any other process.
        var folder = AppContext.BaseDirectory;
        await StopAsync(program => new DebugLaunch("Server", new Codalus.Workbench.Build.LaunchPlan("program", string.Empty, folder, new Dictionary<string, string>(), null))
        {
            Remote = new RemoteLaunch(new DebugAdapterStartInfo("dotnet", [ScriptedAdapter, "--dap"], folder), new LaunchArguments("program", [], folder, Options(program)), new SourceMap(), "here"),
        }, (program, output) =>
        {
            Assert.False(program.HasExited, "Stop ended a process by another system's process id.");
            Assert.DoesNotContain("was still running", output, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Starts a stand-in program (a process that runs until it's killed, as the server did), debugs it with
    /// <paramref name="launch"/>, presses Stop, and checks what's left with <paramref name="check"/>.
    /// </summary>
    private static async Task StopAsync(Func<System.Diagnostics.Process, DebugLaunch> launch, Action<System.Diagnostics.Process, string> check)
    {
        using var program = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", [ScriptedAdapter, "--program"]) { UseShellExecute = false, RedirectStandardInput = true })!;
        try
        {
            await using var debug = new DebugService(() => NetcoredbgStandIn) { AdapterSetting = () => DebugService.NetcoredbgAdapter };
            var output = new System.Text.StringBuilder();
            debug.Output += (_, o) =>
            {
                lock (output)
                {
                    output.Append(o.Text);
                }
            };

            await debug.LaunchAsync([launch(program)], Timeout());
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (debug.ProcessId != program.Id)
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"The process event didn't name the program ({debug.State}, process {debug.ProcessId}).");
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            await debug.StopAsync();

            Assert.Equal(DebugState.Idle, debug.State);
            lock (output)
            {
                check(program, output.ToString());
            }
        }
        finally
        {
            if (!program.HasExited)
            {
                program.Kill();
            }
        }
    }
}
