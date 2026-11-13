using System.Runtime.InteropServices;
using System.Threading.Channels;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Debugging.Remote;

namespace Codalus.Debugging.Tests;

/// <summary>
/// samples/Debugging/RuntimesDebuggee copied to a temp folder and built for each of its frameworks the first time a test
/// asks (only an Apple silicon Mac's tests do: the build restores the reference packs of frameworks out of support).
/// </summary>
public sealed class RuntimesDebuggeeFixture : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-runtimes-" + Guid.NewGuid().ToString("N"));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _output;

    public string Folder => Path.Combine(_root, "RuntimesDebuggee");

    public string Program => Path.Combine(Folder, "Program.cs");

    public string Dll(string framework) => Path.Combine(Folder, "bin", "Debug", framework, "RuntimesDebuggee.dll");

    /// <summary>Builds every framework once; fails the test with the build's output when it didn't build.</summary>
    public async Task BuildAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_output is null)
            {
                Directory.CreateDirectory(Folder);
                var source = Path.Combine(DebuggeeFixture.RepoRoot, "samples", "Debugging", "RuntimesDebuggee");
                foreach (var file in Directory.EnumerateFiles(source))
                {
                    File.Copy(file, Path.Combine(Folder, Path.GetFileName(file)), overwrite: true);
                }

                File.Copy(Path.Combine(DebuggeeFixture.RepoRoot, "samples", "Directory.Build.props"), Path.Combine(_root, "Directory.Build.props"), overwrite: true);
                Codalus.Tests.FixtureSdk.Pin(_root);
                var start = new System.Diagnostics.ProcessStartInfo("dotnet", ["build", Folder, "-nologo", "-v", "quiet"]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                using var process = System.Diagnostics.Process.Start(start)!;
                var printed = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var failed = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                _output = $"exit code {process.ExitCode}\n{await printed}{await failed}";
            }
        }
        finally
        {
            _gate.Release();
        }

        Assert.True(_output.StartsWith("exit code 0\n", StringComparison.Ordinal), "RuntimesDebuggee didn't build:\n" + _output);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// M58b (ADR 0094): real debug sessions under the Codalus debugger's self-contained bundle, as F5 runs them through the
/// debug service. This machine's own bundle debugs a program here (the bundle a remote target gets, with its launcher
/// and its own runtime); on an Apple silicon Mac, the <c>osx-x64</c> bundle debugs programs that run as x64 under
/// Rosetta 2, each started by the x64 <c>dotnet</c> on the x64 runtime of its .NET version, .NET Core 3.1 to the current
/// one (<c>eng/get-dotnet-x64.sh</c>): .NET 5 and earlier have no Arm64 runtime there. The <c>win-x86</c> bundle's session is
/// in <see cref="NetFxDebuggerTests"/>. The bundles are built by <c>eng/get-codalus-debugger.sh</c>: the tests skip
/// without them locally and fail without them in CI.
/// </summary>
public sealed class DebuggerBundleTests(DebuggeeFixture debuggee, RuntimesDebuggeeFixture runtimes) : IClassFixture<DebuggeeFixture>, IClassFixture<RuntimesDebuggeeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool InCi => Environment.GetEnvironmentVariable("CI") == "true";

    private static bool OnAppleSilicon => OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64;

    /// <summary>The x64 <c>dotnet</c> beside the Arm64 one (<c>eng/get-dotnet-x64.sh</c>).</summary>
    private static string X64Dotnet => Path.Combine(DebuggeeFixture.RepoRoot, ".work", "dotnet-x64", "dotnet");

    private static string HostPlatform =>
        (OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" + (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64");

    /// <summary>
    /// With <paramref name="held"/>, through the held start (<c>CODALUS_DEBUG_HELD_START</c>): what the x64 bundle does
    /// under Rosetta 2, where dbgshim's own wait for the runtime crashed, run here on this machine's bundle. The program
    /// waits in the startup hook, which the bundle carries, and the debugger attaches by the runtime's path.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task This_machines_bundle_debugs_a_program_here_to_its_end(bool held)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The bundles for a machine's own platform are Linux's and macOS's (remote targets).");
        var bundle = RemoteDebuggers.CodalusBundle(HostPlatform);
        Assert.True(bundle is not null || !InCi, $"The Codalus debugger's bundle for {HostPlatform} isn't built (eng/get-codalus-debugger.sh).");
        Assert.SkipWhen(bundle is null or { IsArchive: true }, $"The Codalus debugger's bundle for {HostPlatform} isn't built (eng/get-codalus-debugger.sh).");
        var debugger = Path.Combine(bundle!.Path, RemoteDebuggers.CodalusExecutable);

        // Started in its own folder, with the current directory among the places libraries are looked for: an empty
        // entry in LD_LIBRARY_PATH on Linux (what a trailing colon leaves: "export LD_LIBRARY_PATH=/x:$LD_LIBRARY_PATH"
        // with nothing set before), and always on macOS for a library opened by name. The folder holds the bundle's
        // own runtime, and the debugged runtime's debugging libraries were taken from it: "The debugger couldn't attach
        // (ERROR_INSUFFICIENT_BUFFER)" for a .NET 11 program, CORDBG_E_DEBUG_COMPONENT_MISSING for .NET 8 and 10. The
        // adapter leaves that folder as it starts.
        var environment = new Dictionary<string, string> { ["CODALUS_DEBUG_HELD_START"] = held ? "1" : "0" };
        if (OperatingSystem.IsLinux())
        {
            environment["LD_LIBRARY_PATH"] = ":";
        }

        await using var debug = new DebugService(() => null)
        {
            AdapterSetting = () => DebugService.CodalusAdapter,
            CoreClrAdapter = () => new DebugAdapterStartInfo(debugger, [CoreClrAdapter.AdapterArgument], bundle.Path, environment) { IsCodalus = true },
        };
        await SessionAsync(debug, DebuggeeFixture.DotnetHost, debuggee.Program, debuggee.Dll, debuggee.Folder, atFirstStop: async () =>
        {
            // A held start is the one that gives the program the startup hook, and the hook took its wait folder's
            // variable away: the program waited in it. (The session starts no Hot Reload agent, which adds the hook too.)
            var hooks = (await debug.EvaluateAsync("System.Environment.GetEnvironmentVariable(\"DOTNET_STARTUP_HOOKS\")", "watch", Ct)).Result;
            var wait = (await debug.EvaluateAsync("System.Environment.GetEnvironmentVariable(\"CODALUS_DEBUG_WAIT\")", "watch", Ct)).Result;
            Assert.Equal(held, hooks.Contains("Codalus.Debugging.StartupHook.dll", StringComparison.Ordinal));
            Assert.Equal("null", wait);
        });
    }

    /// <summary>
    /// A program whose runtime is loaded but slow to reach the startup hook: an x64 program under Rosetta 2 on a busy
    /// machine took longer than the held start waits for the hook (macOS CI, run 43309447208, when it waited 5 s for every
    /// runtime). The debugger attached to it as a program that runs no hooks, and never let the hook go when it came: the
    /// program waited out the hook's two minutes with the debugger attached. The wait is longer now for a runtime that
    /// runs hooks, but any wait can run out. Here another startup hook, run first, is what takes longer than it.
    /// </summary>
    [Fact]
    public async Task A_program_that_reaches_the_startup_hook_late_is_let_go_when_it_gets_there()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The bundles for a machine's own platform are Linux's and macOS's (remote targets).");
        var bundle = RemoteDebuggers.CodalusBundle(HostPlatform);
        Assert.True(bundle is not null || !InCi, $"The Codalus debugger's bundle for {HostPlatform} isn't built (eng/get-codalus-debugger.sh).");
        Assert.SkipWhen(bundle is null or { IsArchive: true }, $"The Codalus debugger's bundle for {HostPlatform} isn't built (eng/get-codalus-debugger.sh).");
        var debugger = Path.Combine(bundle!.Path, RemoteDebuggers.CodalusExecutable);

        // A startup hook that sleeps, listed before the debugger's own (the held start leaves a list that has its hook),
        // past the longest the debugger waits for it: a .NET 11 runtime runs hooks, so it waits HookTimeoutWithHooks.
        var late = Codalus.Debugging.Host.CoreClr.DbgShimDebugger.HookTimeoutWithHooks + TimeSpan.FromSeconds(3);
        var slow = await SlowStartupHookAsync();
        var program = new Dictionary<string, string>
        {
            ["DOTNET_STARTUP_HOOKS"] = slow + Path.PathSeparator + Path.Combine(bundle.Path, "Codalus.Debugging.StartupHook.dll"),
            ["SLOW_STARTUP_HOOK_SECONDS"] = late.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var environment = new Dictionary<string, string> { ["CODALUS_DEBUG_HELD_START"] = "1" };
        if (OperatingSystem.IsLinux())
        {
            environment["LD_LIBRARY_PATH"] = ":";
        }

        var output = new System.Text.StringBuilder();
        await using var debug = new DebugService(() => null)
        {
            AdapterSetting = () => DebugService.CodalusAdapter,
            CoreClrAdapter = () => new DebugAdapterStartInfo(debugger, [CoreClrAdapter.AdapterArgument], bundle.Path, environment) { IsCodalus = true },
        };
        debug.Output += (_, o) =>
        {
            lock (output)
            {
                output.Append(o.Text);
            }
        };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan firstStop = default;
        await SessionAsync(debug, DebuggeeFixture.DotnetHost, debuggee.Program, debuggee.Dll, debuggee.Folder, environment: program, atFirstStop: () =>
        {
            firstStop = watch.Elapsed;
            return Task.CompletedTask;
        });

        string console;
        lock (output)
        {
            console = output.ToString();
        }

        // The debugger gave up on the hook and attached; the hook came, and the program went on at once: not after the
        // two minutes the hook waits for a debugger that never comes.
        Assert.Contains("hasn't reached the debugger's startup hook", console, StringComparison.Ordinal);
        Assert.DoesNotContain("The debugger didn't attach in time", console, StringComparison.Ordinal);
        Assert.True(firstStop > late - TimeSpan.FromSeconds(1) && firstStop < late + TimeSpan.FromSeconds(60), $"The first stop came after {firstStop.TotalSeconds:0.0} s. The Debug Console:\n{console}");
    }

    /// <summary>
    /// Builds a startup hook that sleeps for <c>SLOW_STARTUP_HOOK_SECONDS</c> before the hooks after it run, in a temp
    /// folder the fixture's own removes, and returns its assembly.
    /// </summary>
    private async Task<string> SlowStartupHookAsync()
    {
        var folder = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(debuggee.Folder))!, "SlowStartupHook");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "SlowStartupHook.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>netstandard2.0</TargetFramework>
                <ImplicitUsings>disable</ImplicitUsings>
                <Nullable>disable</Nullable>
              </PropertyGroup>
            </Project>
            """, Ct);
        await File.WriteAllTextAsync(Path.Combine(folder, "StartupHook.cs"), """
            using System;
            using System.Globalization;
            using System.Threading;

            internal static class StartupHook
            {
                public static void Initialize()
                {
                    var seconds = double.Parse(Environment.GetEnvironmentVariable("SLOW_STARTUP_HOOK_SECONDS") ?? "0", CultureInfo.InvariantCulture);
                    Thread.Sleep(TimeSpan.FromSeconds(seconds));
                }
            }
            """, Ct);
        var start = new System.Diagnostics.ProcessStartInfo(DebuggeeFixture.DotnetHost, ["build", folder, "-nologo", "-v", "quiet"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(start)!;
        var built = process.StandardOutput.ReadToEndAsync(Ct);
        var errors = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        Assert.True(process.ExitCode == 0, $"The slow startup hook didn't build: {await built}{await errors}");
        return Path.Combine(folder, "bin", "Debug", "netstandard2.0", "SlowStartupHook.dll");
    }

    /// <summary>
    /// Each row is a framework of RuntimesDebuggee and the x64 runtime it runs on. .NET Core 3.1 and .NET 5 are what an
    /// Apple silicon Mac runs as x64 for want of an Arm64 runtime; the later ones run as x64 when their dotnet is.
    /// </summary>
    [Theory]
    [InlineData("netcoreapp3.1", "3.1")]
    [InlineData("net5.0", "5.0")]
    [InlineData("net6.0", "6.0")]
    [InlineData("net8.0", "8.0")]
    [InlineData("net10.0", "10.0")]
    [InlineData("net11.0", "11.0")]
    public async Task An_x64_program_on_apple_silicon_is_debugged_by_the_x64_bundle_to_its_end(string framework, string runtime)
    {
        Assert.SkipUnless(OnAppleSilicon, "x64 programs under Rosetta 2 are an Apple silicon Mac's.");
        var cache = Directory.CreateTempSubdirectory("codalus-debugger-components-").FullName;
        try
        {
            var debugger = new DebuggerComponents(cache).Installed(DebuggerComponents.X64OnAppleSilicon);
            var shared = Path.Combine(Path.GetDirectoryName(X64Dotnet)!, "shared", "Microsoft.NETCore.App");
            var installed = Directory.Exists(shared) && Directory.EnumerateDirectories(shared).Any(d => Path.GetFileName(d).StartsWith(runtime + ".", StringComparison.Ordinal));
            Assert.True(debugger is not null || !InCi, "The Codalus debugger's osx-x64 bundle isn't built (eng/get-codalus-debugger.sh osx-x64).");
            Assert.True(installed || !InCi, $"The x64 .NET {runtime} runtime isn't installed under {shared} (eng/get-dotnet-x64.sh).");
            Assert.SkipWhen(debugger is null, "The Codalus debugger's osx-x64 bundle isn't built (eng/get-codalus-debugger.sh osx-x64).");
            Assert.SkipWhen(!installed, $"The x64 .NET {runtime} runtime isn't installed (eng/get-dotnet-x64.sh).");

            // Both are x64 programs: the dotnet that starts the program, and the debugger that debugs it.
            Assert.True(DebugAdapterLocator.RunsAsX64OnArm64Mac(X64Dotnet), $"{X64Dotnet} is an x64 program");
            Assert.True(DebugAdapterLocator.RunsAsX64OnArm64Mac(debugger!), $"{debugger} is an x64 program");
            await runtimes.BuildAsync(Ct);

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
            var process = await SessionAsync(debug, X64Dotnet, runtimes.Program, runtimes.Dll(framework), runtimes.Folder, "runtime=" + runtime);
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
    /// F5 on a debuggee started by <paramref name="dotnet"/> (Debuggee, or RuntimesDebuggee, which has the same lines):
    /// a breakpoint with a hit count (the debugger applies it), values and a property read in the program, a step,
    /// another breakpoint, and the program's end with its output (and <paramref name="printed"/>, when given).
    /// </summary>
    private static async Task<(int Id, bool WasX64)> SessionAsync(DebugService debug, string dotnet, string program, string dll, string folder, string? printed = null, Func<Task>? atFirstStop = null, IReadOnlyDictionary<string, string>? environment = null)
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

        var loop = DebuggeeFixture.Line(program, "loop");
        var print = DebuggeeFixture.Line(program, "print");
        debug.Breakpoints.Set(new BreakpointSpec(program, loop, HitCondition: "3"));
        try
        {
            await debug.LaunchAsync(new LaunchPlan(dotnet, $"exec \"{dll}\"", folder, environment ?? new Dictionary<string, string>(), null), Ct);
        }
        catch (InvalidOperationException ex)
        {
            // With what the Debug Console has: a debugger that ended says nothing else.
            throw new InvalidOperationException($"{ex.Message} The Debug Console:\n{Output()}", ex);
        }

        // The third time round the loop: the hit count is the debugger's own, with no stop for the first two.
        var stop = await NextStopAsync();
        Assert.True(stop.Reason == "breakpoint", $"The first stop is {stop}, at {debug.SelectedFrame?.Name} {debug.SelectedFrame?.Source?.Path}:{debug.SelectedFrame?.Line}. The Debug Console:\n{Output()}");
        Assert.Contains("Debugging with the Codalus debugger.", Output(), StringComparison.Ordinal);
        Assert.Equal(loop, debug.SelectedFrame?.Line);
        Assert.Equal(program, debug.SelectedFrame?.Source?.Path);
        Assert.True(debug.Breakpoints.StateOf(debug.Breakpoints.All.Single())?.Verified);
        Assert.Equal("2", (await debug.EvaluateAsync("i", "watch", Ct)).Result);
        Assert.Equal("3", (await debug.EvaluateAsync("total", "watch", Ct)).Result);

        if (atFirstStop is not null)
        {
            await atFirstStop();
        }

        // A property read in the program (a func-eval), and arithmetic on it.
        Assert.Equal("5", (await debug.EvaluateAsync("numbers.Count", "watch", Ct)).Result);
        Assert.Equal("8", (await debug.EvaluateAsync("numbers.Count + total", "watch", Ct)).Result);
        var id = Assert.NotNull(debug.ProcessId);
        var wasX64 = debug.IsX64ProcessOnArm64Mac(id);
        Assert.Contains(debug.Modules, m => m.Name == Path.GetFileName(dll) && m.IsUserCode == true);

        // A step stays in the loop; then on to the line that prints, and to the end.
        await debug.StepOverAsync();
        Assert.Equal("step", (await NextStopAsync()).Reason);
        debug.Breakpoints.Clear();
        debug.Breakpoints.Set(new BreakpointSpec(program, print));
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
        if (printed is not null)
        {
            Assert.Contains(printed, Output(), StringComparison.Ordinal);
        }

        Assert.Equal(DebugState.Idle, debug.State);
        return (id, wasX64);
    }
}
