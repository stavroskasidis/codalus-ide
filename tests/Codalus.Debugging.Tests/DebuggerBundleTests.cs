using System.Runtime.InteropServices;
using System.Threading.Channels;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Debugging.Remote;

namespace Codalus.Debugging.Tests;

/// <summary>
/// M58b (ADR 0094): real debug sessions under the Codalus debugger's self-contained bundle, as F5 runs them through the
/// debug service. This machine's own bundle debugs a program here (the bundle a remote target gets, with its launcher
/// and its own runtime); on an Apple silicon Mac, the <c>osx-x64</c> bundle debugs a program that runs as x64 under
/// Rosetta 2, started by the x64 <c>dotnet</c> (<c>eng/get-dotnet-x64.sh</c>). The <c>win-x86</c> bundle's session is
/// in <see cref="NetFxDebuggerTests"/>. The bundles are built by <c>eng/get-codalus-debugger.sh</c>: the tests skip
/// without them locally and fail without them in CI.
/// </summary>
public sealed class DebuggerBundleTests(DebuggeeFixture debuggee) : IClassFixture<DebuggeeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool InCi => Environment.GetEnvironmentVariable("CI") == "true";

    private static bool OnAppleSilicon => OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64;

    /// <summary>The x64 <c>dotnet</c> beside the Arm64 one (<c>eng/get-dotnet-x64.sh</c>).</summary>
    private static string X64Dotnet => Path.Combine(DebuggeeFixture.RepoRoot, ".work", "dotnet-x64", "dotnet");

    private static string HostPlatform =>
        (OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" + (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64");

    [Fact]
    public async Task This_machines_bundle_debugs_a_program_here_to_its_end()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The bundles for a machine's own platform are Linux's and macOS's (remote targets).");
        var bundle = RemoteDebuggers.CodalusBundle(HostPlatform);
        Assert.True(bundle is not null || !InCi, $"The Codalus debugger's bundle for {HostPlatform} isn't built (eng/get-codalus-debugger.sh).");
        Assert.SkipWhen(bundle is null or { IsArchive: true }, $"The Codalus debugger's bundle for {HostPlatform} isn't built (eng/get-codalus-debugger.sh).");
        var debugger = Path.Combine(bundle!.Path, RemoteDebuggers.CodalusExecutable);

        await using var debug = new DebugService(() => null)
        {
            AdapterSetting = () => DebugService.CodalusAdapter,
            CoreClrAdapter = () => new DebugAdapterStartInfo(debugger, [CoreClrAdapter.AdapterArgument], bundle.Path) { IsCodalus = true },
        };
        await SessionAsync(debug, DebuggeeFixture.DotnetHost);
    }

    [Fact]
    public async Task An_x64_program_on_apple_silicon_is_debugged_by_the_x64_bundle_to_its_end()
    {
        Assert.SkipUnless(OnAppleSilicon, "x64 programs under Rosetta 2 are an Apple silicon Mac's.");
        var cache = Directory.CreateTempSubdirectory("codalus-debugger-components-").FullName;
        try
        {
            var debugger = new DebuggerComponents(cache).Installed(DebuggerComponents.X64OnAppleSilicon);
            Assert.True(debugger is not null || !InCi, "The Codalus debugger's osx-x64 bundle isn't built (eng/get-codalus-debugger.sh osx-x64).");
            Assert.True(File.Exists(X64Dotnet) || !InCi, $"The x64 .NET runtime isn't installed at {X64Dotnet} (eng/get-dotnet-x64.sh).");
            Assert.SkipWhen(debugger is null, "The Codalus debugger's osx-x64 bundle isn't built (eng/get-codalus-debugger.sh osx-x64).");
            Assert.SkipWhen(!File.Exists(X64Dotnet), "The x64 .NET runtime isn't installed (eng/get-dotnet-x64.sh).");

            // Both are x64 programs: the dotnet that starts the program, and the debugger that debugs it.
            Assert.True(DebugAdapterLocator.RunsAsX64OnArm64Mac(X64Dotnet), $"{X64Dotnet} is an x64 program");
            Assert.True(DebugAdapterLocator.RunsAsX64OnArm64Mac(debugger!), $"{debugger} is an x64 program");

            var (askedX64, askedArm64) = (false, false);
            await using var debug = new DebugService(() => null)
            {
                AdapterSetting = () => DebugService.CodalusAdapter,
                CoreClrAdapter = () =>
                {
                    askedArm64 = true;
                    return DebuggeeFixture.CodalusAdapter();
                },
                X64CoreClrAdapter = _ =>
                {
                    askedX64 = true;
                    return Task.FromResult<DebugAdapterStartInfo?>(new DebugAdapterStartInfo(debugger!, [CoreClrAdapter.AdapterArgument], Path.GetDirectoryName(debugger)) { IsCodalus = true });
                },
            };
            var process = await SessionAsync(debug, X64Dotnet);
            Assert.True(askedX64, "the x64 program went to the x64 bundle");
            Assert.False(askedArm64, "the Arm64 debugger wasn't started for it");
            Assert.True(process.WasX64, "the debugged process ran as x64");
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    /// <summary>
    /// F5 on the Debuggee started by <paramref name="dotnet"/>: a breakpoint with a hit count (the debugger applies it),
    /// values and a property read in the program, a step, another breakpoint, and the program's end with its output.
    /// </summary>
    private async Task<(int Id, bool WasX64)> SessionAsync(DebugService debug, string dotnet)
    {
        var stops = Channel.CreateUnbounded<StopInfo>();
        var ended = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new System.Text.StringBuilder();
        debug.Stopped += (_, stop) => stops.Writer.TryWrite(stop);
        debug.Ended += (_, code) => ended.TrySetResult(code);
        debug.Output += (_, o) =>
        {
            lock (output)
            {
                output.Append(o.Text);
            }
        };
        string Output()
        {
            lock (output)
            {
                return output.ToString();
            }
        }

        async Task<StopInfo> NextStopAsync()
        {
            try
            {
                return await stops.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(120), Ct);
            }
            catch (TimeoutException)
            {
                var breakpoints = string.Join("; ", debug.Breakpoints.All.Select(b => $"{b.Path}:{b.Line} {debug.Breakpoints.StateOf(b)}"));
                throw new TimeoutException($"The program did not stop. {debug.Describe()}. Breakpoints: {breakpoints}. The Debug Console:\n{Output()}");
            }
        }

        var loop = DebuggeeFixture.Line(debuggee.Program, "loop");
        var print = DebuggeeFixture.Line(debuggee.Program, "print");
        debug.Breakpoints.Set(new BreakpointSpec(debuggee.Program, loop, HitCondition: "3"));
        await debug.LaunchAsync(new LaunchPlan(dotnet, $"exec \"{debuggee.Dll}\"", debuggee.Folder, new Dictionary<string, string>(), null), Ct);

        // The third time round the loop: the hit count is the debugger's own, with no stop for the first two.
        var stop = await NextStopAsync();
        Assert.Equal("breakpoint", stop.Reason);
        Assert.Contains("Debugging with the Codalus debugger.", Output(), StringComparison.Ordinal);
        Assert.Equal(loop, debug.SelectedFrame?.Line);
        Assert.Equal(debuggee.Program, debug.SelectedFrame?.Source?.Path);
        Assert.True(debug.Breakpoints.StateOf(debug.Breakpoints.All.Single())?.Verified);
        Assert.Equal("2", (await debug.EvaluateAsync("i", "watch", Ct)).Result);
        Assert.Equal("3", (await debug.EvaluateAsync("total", "watch", Ct)).Result);

        // A property read in the program (a func-eval), and arithmetic on it.
        Assert.Equal("5", (await debug.EvaluateAsync("numbers.Count", "watch", Ct)).Result);
        Assert.Equal("8", (await debug.EvaluateAsync("numbers.Count + total", "watch", Ct)).Result);
        var id = Assert.NotNull(debug.ProcessId);
        var wasX64 = debug.IsX64ProcessOnArm64Mac(id);
        Assert.Contains(debug.Modules, m => m.Name == "Debuggee.dll" && m.IsUserCode == true);

        // A step stays in the loop; then on to the line that prints, and to the end.
        await debug.StepOverAsync();
        Assert.Equal("step", (await NextStopAsync()).Reason);
        debug.Breakpoints.Clear();
        debug.Breakpoints.Set(new BreakpointSpec(debuggee.Program, print));
        await debug.ContinueAsync();
        Assert.Equal("breakpoint", (await NextStopAsync()).Reason);
        Assert.Equal(print, debug.SelectedFrame?.Line);
        Assert.Equal("15", (await debug.EvaluateAsync("total", "watch", Ct)).Result);
        Assert.Equal("\"Ada\"", (await debug.EvaluateAsync("person.Name", "watch", Ct)).Result);

        debug.Breakpoints.Clear();
        await debug.ContinueAsync();
        var code = await ended.Task.WaitAsync(TimeSpan.FromSeconds(120), Ct);
        Assert.True(code == 15, $"The program's exit code was {code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not reported"}, not 15. The Debug Console:\n{Output()}");
        Assert.Contains("total=15 Ada is 36", Output(), StringComparison.Ordinal);
        Assert.Equal(DebugState.Idle, debug.State);
        return (id, wasX64);
    }
}
