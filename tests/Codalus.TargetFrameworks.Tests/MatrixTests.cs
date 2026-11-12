using System.Net.Http;
using Codalus.Debugging;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Projects;

namespace Codalus.TargetFrameworks.Tests;

/// <summary>
/// Every target framework from netcoreapp2.0 to net10.0 with the current SDK (M33, ADR 0030): opening and evaluation,
/// the Dependencies node, the property pages, the design-time build's references and C# version, build, run and debug.
/// </summary>
public sealed class MatrixTests(MatrixFixture matrix) : IClassFixture<MatrixFixture>
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(3);

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    [Fact]
    public void Every_project_opens_with_the_current_sdk()
    {
        matrix.Require();
        Assert.Equal(38, matrix.Solutions.Projects.Count);
        Assert.All(matrix.Solutions.Projects.Values, p => Assert.Equal(ProjectStates.Loaded, p.State));
        Assert.All(matrix.Solutions.Projects.Values, p => Assert.DoesNotContain(p.Diagnostics, d => d.Contains("error", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(CompatRoot.Stamp["sdk"].Single(), Path.GetFileName(Path.TrimEndingDirectorySeparator(matrix.Solutions.SdkPath!)));
        Assert.Null(matrix.Solutions.SdkWarning);
        Assert.Equal(["netstandard2.0", "netcoreapp3.1", "net8.0"], matrix.Solutions.Projects[matrix.Project("Multi")].TargetFrameworks);
    }

    [Theory]
    [MemberData(nameof(Matrix.Ids), MemberType = typeof(Matrix))]
    public void Each_framework_evaluates_with_its_framework_and_references(string id)
    {
        matrix.Require();
        var framework = Matrix.Get(id);
        foreach (var name in new[] { framework.App, framework.Lib, framework.Tests })
        {
            var project = matrix.Solutions.Projects[matrix.Project(name)];
            Assert.Equal([framework.Tfm], project.TargetFrameworks);
            Assert.True(project.Restored, $"{name} is not restored.");
        }

        // The Dependencies node: the framework (a package before .NET Core 3.0), the library and the shared ones.
        var lib = matrix.Solutions.Projects[matrix.Project(framework.Lib)];
        var core = Assert.Single(lib.References, r => r.Kind == ReferenceKinds.Framework);
        Assert.Equal("Microsoft.NETCore.App", core.Name);
        Assert.True(core.Resolved);
        if (framework.Runtime.Major < 3)
        {
            Assert.StartsWith(framework.RuntimePrefix, core.Version, StringComparison.Ordinal);
        }

        Assert.Contains(lib.References, r => r is { Kind: ReferenceKinds.Project, Name: "Standard20" });
        Assert.Equal(framework.Runtime.Major >= 3, lib.References.Any(r => r is { Kind: ReferenceKinds.Project, Name: "Standard21" }));
        var entry = matrix.Solutions.Solution!.Projects.Single(p => p.Name == framework.Lib);
        var node = SolutionTree.Project(entry, lib, trusted: true, matrix.Solutions.CompilerInputs.GetValueOrDefault(entry.Path));
        var dependencies = Assert.Single(node.Children, c => c.Kind == ExplorerNodeKind.Dependencies);
        Assert.False(dependencies.IsWarning, "The Dependencies node has a warning.");
        var frameworks = Assert.Single(dependencies.Children, c => c.Name == "Frameworks");
        Assert.Contains(frameworks.Children, c => c.Name == "Microsoft.NETCore.App");
    }

    [Theory]
    [MemberData(nameof(Matrix.Ids), MemberType = typeof(Matrix))]
    public void The_design_time_build_gives_the_language_service_the_frameworks_references_and_csharp_version(string id)
    {
        matrix.Require();
        var framework = Matrix.Get(id);
        var inputs = matrix.Solutions.CompilerInputs[matrix.Project(framework.App)];
        Assert.Equal(CompilerInputStates.Ok, inputs.State);
        Assert.Contains($"/langversion:{framework.CSharp}", inputs.Arguments);
        Assert.Equal(framework.CSharp, DotnetFrameworks.DefaultCSharpVersion(framework.Tfm));

        // Reference assemblies: the Microsoft.NETCore.App package before 3.0, its targeting pack after.
        var pack = framework.Runtime.Major < 3 ? "microsoft.netcore.app/" : "microsoft.netcore.app.ref/";
        Assert.Contains(inputs.Arguments, a => a.Replace('\\', '/').Contains(pack + framework.RuntimePrefix, StringComparison.OrdinalIgnoreCase) && a.EndsWith("System.Runtime.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(Matrix.Ids), MemberType = typeof(Matrix))]
    public async Task The_property_pages_read_the_framework_and_its_csharp_version(string id)
    {
        matrix.Require();
        var framework = Matrix.Get(id);
        var properties = await new ProjectManagementService(matrix.Solutions).GetPropertiesAsync(matrix.Project(framework.App), ["TargetFramework", "LangVersion", "OutputType"], null, Timeout());
        Assert.Equal(framework.Tfm, properties.Properties.Single(p => p.Name == "TargetFramework").Value);
        Assert.Equal(framework.CSharp, properties.Properties.Single(p => p.Name == "LangVersion").Value);
        Assert.Equal("Exe", properties.Properties.Single(p => p.Name == "OutputType").Value);
    }

    [Theory]
    [MemberData(nameof(Matrix.Ids), MemberType = typeof(Matrix))]
    public async Task Each_framework_builds_and_runs_on_its_runtime(string id)
    {
        matrix.RequireBuild();
        var framework = Matrix.Get(id);
        using var runs = new RunService(matrix.Solutions, matrix.Output, _ => { }) { DotnetRoot = CompatRoot.Path };
        var program = await runs.StartAsync(matrix.Project(framework.App), externalConsole: false, Timeout());
        var code = await program.Exited.WaitAsync(Timeout());
        await MatrixFixture.WaitUntilAsync(() => program.Channel!.GetText().Contains("has exited", StringComparison.Ordinal), Timeout());
        var output = program.Channel!.GetText();
        Assert.True(code == 0, $"{framework.App} exited with {code}:\n{output}");
        Assert.Contains($"runtime={framework.RuntimePrefix}", output, StringComparison.Ordinal);
        Assert.Contains($"app={framework.FrameworkName} lib={framework.FrameworkName}", output, StringComparison.Ordinal);
        var multi = framework.Runtime.Major >= 8 ? ".NETCoreApp,Version=v8.0" : framework.Runtime >= new Version(3, 1) ? ".NETCoreApp,Version=v3.1" : ".NETStandard,Version=v2.0";
        Assert.Contains($"multi={multi}", output, StringComparison.Ordinal);
        Assert.Contains($"Hello, {framework.Tfm} from {framework.FrameworkName}", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Web21", "2.1", "http://localhost:5021/")]
    [InlineData("Web31", "3.1", "http://localhost:5031/")]
    public async Task The_aspnet_core_apps_run_and_answer(string name, string runtime, string url)
    {
        matrix.RequireBuild();
        using var runs = new RunService(matrix.Solutions, matrix.Output, _ => { }) { DotnetRoot = CompatRoot.Path };
        var program = await runs.StartAsync(matrix.Project(name), externalConsole: false, Timeout());
        try
        {
            var listening = await program.Listening.WaitAsync(Timeout());
            Assert.StartsWith("http://localhost:", listening, StringComparison.Ordinal);
            using var http = new HttpClient();
            var page = await http.GetStringAsync(new Uri(url), Timeout());
            Assert.StartsWith($"{name} on {runtime}.", page, StringComparison.Ordinal);
            Assert.EndsWith("in Development", page, StringComparison.Ordinal); // the launch profile's environment
        }
        finally
        {
            program.Stop();
        }
    }

    /// <summary>
    /// The Codalus debugger the solution built, started by the matrix's dotnet. The shell's tests point
    /// <c>CODALUS_DEBUGHOST</c> at the same file; this class starts no shell, and without it every framework said the
    /// debugger wasn't found once it became the default (M58b: the matrix isn't part of a pull request's run).
    /// </summary>
    private static DebugAdapterStartInfo? CodalusDebugger()
    {
        var folder = Path.Combine(Codalus.Shell.Tests.ShellFixture.RepoRoot, "src", "Codalus.Debugging.Host", "bin", Codalus.Shell.Tests.ShellFixture.Configuration, Codalus.Tests.RepoBuild.TargetFramework);
        var host = Path.Combine(folder, "codalus-debughost.dll");
        return File.Exists(host) ? new DebugAdapterStartInfo(CompatRoot.Dotnet, [host, CoreClrAdapter.AdapterArgument], folder) : null;
    }

    /// <summary>
    /// The Codalus debugger's x64 bundle on an Apple silicon Mac, built into the checkout (<c>eng/get-codalus-debugger.sh
    /// osx-x64</c>; a download in an installed IDE), or null: the frameworks without an Arm64 runtime (.NET 5 and
    /// earlier) run as x64 there, and without it they'd all be netcoredbg's, under the default too. With the matrix
    /// required (<c>CODALUS_DOTNET_COMPAT</c>, CI's run) it must be there.
    /// </summary>
    private static DebugAdapterStartInfo? X64CodalusDebugger()
    {
        if (!OperatingSystem.IsMacOS() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.Arm64)
        {
            return null;
        }

        // The checkout's bundle or the variable's: the cache folder (a download's) is one that never exists.
        using var components = new DebuggerComponents(Path.Combine(Path.GetTempPath(), "codalus-matrix-no-debugger-components"));
        var debugger = components.Installed(DebuggerComponents.X64OnAppleSilicon);
        Assert.True(debugger is not null || !CompatRoot.Required, "The Codalus debugger's osx-x64 bundle isn't built (eng/get-codalus-debugger.sh osx-x64).");
        return debugger is null ? null : new DebugAdapterStartInfo(debugger, [CoreClrAdapter.AdapterArgument], Path.GetDirectoryName(debugger)) { IsCodalus = true };
    }

    /// <summary>
    /// Under the default, the Codalus debugger (M58, ADR 0094): .NET Core 2.0 to .NET 10 through the runtime's own
    /// mscordbi. On Apple silicon the x64 frameworks go to its x64 bundle, but for .NET Core 2.0 and 2.1, which run no
    /// startup hooks and stay netcoredbg's there (the Debug Console says so).
    /// </summary>
    [Theory]
    [MemberData(nameof(Matrix.Ids), MemberType = typeof(Matrix))]
    public Task Each_framework_debugs_to_a_breakpoint(string id) => DebugsToABreakpointAsync(id, netcoredbg: false);

    /// <summary>Under netcoredbg, the fallback, for as long as it ships.</summary>
    [Theory]
    [MemberData(nameof(Matrix.Ids), MemberType = typeof(Matrix))]
    public Task Each_framework_debugs_to_a_breakpoint_under_netcoredbg(string id) => DebugsToABreakpointAsync(id, netcoredbg: true);

    private async Task DebugsToABreakpointAsync(string id, bool netcoredbg)
    {
        matrix.RequireBuild();
        Assert.SkipWhen(Environment.GetEnvironmentVariable(DebugService.AdapterVariable) is { Length: > 0 }, "CODALUS_DEBUG_ADAPTER overrides the debugger these tests choose.");
        if (netcoredbg)
        {
            Assert.SkipWhen(DebugAdapterLocator.Resolve() is null && !CompatRoot.Required, "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        }
        else
        {
            Assert.True(CodalusDebugger() is not null, "The Codalus debugger (codalus-debughost) isn't built: build the solution.");
        }

        var framework = Matrix.Get(id);
        var project = matrix.Project(framework.App);
        var program = Path.Combine(Path.GetDirectoryName(project)!, "Program.cs");
        var line = Matrix.Line(program, "break");
        using var runs = new RunService(matrix.Solutions, matrix.Output, _ => { }) { DotnetRoot = CompatRoot.Path };
        var (plan, _, _) = await runs.LaunchPlanAsync(project, Timeout());

        await using var debug = new DebugService(DebugAdapterLocator.Resolve)
        {
            AdapterSetting = () => netcoredbg ? DebugService.NetcoredbgAdapter : DebugService.CodalusAdapter,
            CoreClrAdapter = CodalusDebugger,
            X64CoreClrAdapter = !netcoredbg && X64CodalusDebugger() is { } x64 ? _ => Task.FromResult<DebugAdapterStartInfo?>(x64) : null,
        };
        var stopped = new TaskCompletionSource<StopInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new System.Text.StringBuilder();
        debug.Stopped += (_, stop) => stopped.TrySetResult(stop);
        debug.Ended += (_, code) => ended.TrySetResult(code);
        debug.Output += (_, o) =>
        {
            lock (output)
            {
                output.Append(o.Text);
            }
        };
        debug.Breakpoints.Toggle(program, line);
        try
        {
            await debug.LaunchAsync(plan, Timeout());
        }
        catch (InvalidOperationException ex)
        {
            lock (output)
            {
                Assert.Fail($"{framework.Tfm}: {ex.Message}\n> {plan.FileName} {plan.Arguments}\n{string.Join('\n', plan.Environment.Select(e => e.Key + "=" + e.Value))}\n{output}");
            }
        }
        try
        {
            await stopped.Task.WaitAsync(Timeout());
        }
        catch (TimeoutException)
        {
            lock (output)
            {
                Assert.Fail($"{framework.Tfm} did not stop at its breakpoint. State {debug.State}. Output:\n{output}");
            }
        }

        Assert.Equal(line, debug.SelectedFrame?.Line);
        Assert.Contains("\"", (await debug.EvaluateAsync("feature", "watch", Timeout())).Result, StringComparison.Ordinal); // a local of the framework's C# feature
        debug.Breakpoints.Clear();
        await debug.ContinueAsync();
        Assert.Equal(0, await ended.Task.WaitAsync(Timeout()));
        lock (output)
        {
            Assert.Contains($"runtime={framework.RuntimePrefix}", output.ToString(), StringComparison.Ordinal);

            // Which debugger it was: the Codalus debugger under the default, but for an x64 program on Apple silicon of
            // a runtime without startup hooks (or with no x64 bundle built: a local run), which the console explains.
            if (!netcoredbg)
            {
                var hooks = plan.RunsAsX64 && plan.StartsWithoutStartupHooks;
                var expected = hooks ? "Debugging with netcoredbg: it runs on .NET Core 2.0 or 2.1 as x64 on Apple silicon"
                    : plan.RunsAsX64 && debug.X64CoreClrAdapter is null ? "Debugging with netcoredbg: it runs as x64 on Apple silicon"
                    : "Debugging with the Codalus debugger.";
                Assert.Contains(expected, output.ToString(), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Hot_reload_is_offered_from_net6_and_gives_its_reason_before()
    {
        foreach (var framework in Matrix.All)
        {
            var reason = DotnetFrameworks.HotReloadUnsupportedReason(framework.Tfm, framework.App);
            if (framework.Runtime.Major >= 6)
            {
                Assert.Null(reason);
            }
            else
            {
                Assert.Equal($"Hot Reload needs .NET 6 or later: {framework.App} targets {framework.Tfm}.", reason);
            }
        }
    }
}
