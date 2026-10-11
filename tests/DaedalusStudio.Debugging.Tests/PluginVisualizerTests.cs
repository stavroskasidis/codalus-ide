using System.Reflection;
using DaedalusStudio.Platform;
using DaedalusStudio.Plugins.Protocol;
using DaedalusStudio.Rpc;
using DaedalusStudio.Workbench;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Debugging;
using DaedalusStudio.Workbench.Languages;
using DaedalusStudio.Workbench.Output;
using DaedalusStudio.Workbench.Plugins;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Debugging.Tests;

/// <summary>
/// The proposed visualizer API end to end (M14b, API 0.2.7): the real plugin host runs the DictionaryVisualizer sample,
/// which reads a dictionary in a program paused under netcoredbg.
/// </summary>
public sealed class PluginVisualizerTests(DebuggeeFixture debuggee) : IClassFixture<DebuggeeFixture>, IAsyncLifetime
{
    private readonly string _userData = Path.Combine(Path.GetTempPath(), "daedalus-visualizer-" + Guid.NewGuid().ToString("N"));
    private WorkbenchServices _workbench = null!;
    private SolutionService _solutions = null!;
    private DebugService _debug = null!;
    private WorkbenchProposedApi _proposed = null!;
    private PluginManager _plugins = null!;

    private static string Metadata(string key) =>
        typeof(PluginVisualizerTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    private static string Built(string folder, string project, string file) =>
        Path.Combine(Metadata("DaedalusRepoRoot"), folder, project, "bin", Metadata("DaedalusConfiguration"), "net10.0", file);

    public ValueTask InitializeAsync()
    {
        var paths = AppPaths.Resolve(PlatformInfo.Current, _ => null, _userData);
        paths.EnsureCreated();
        _workbench = new WorkbenchServices(paths, PlatformInfo.Current, watchFiles: false);
        _solutions = new SolutionService(() => null);
        _debug = new DebugService(() => DebuggeeFixture.Adapter);
        _proposed = new WorkbenchProposedApi(_solutions, new BuildService(_solutions, new OutputService()), _debug, new LanguageService(new LanguageServiceRegistry(), _solutions));
        var host = Built("src", "DaedalusStudio.Plugins.Host", "daedalus-pluginhost.dll");
        _plugins = new PluginManager(_workbench, _ => ProcessLaunchInfo.FromPath(host), "test", new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50) })
        {
            ProposedApi = _proposed,
        };
        var plugin = Path.GetDirectoryName(Built(Path.Combine("samples", "plugins"), "DictionaryVisualizer", "DictionaryVisualizer.dll"))!;
        _plugins.AddPlugins(PluginDiscovery.Scan([(plugin, false)], PlatformInfo.Current, allowProposedApi: true));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _plugins.DisposeAsync();
        _proposed.Dispose();
        await _debug.DisposeAsync();
        await _solutions.DisposeAsync();
        _workbench.Dispose();
        try
        {
            Directory.Delete(_userData, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_plugin_visualizer_reads_a_dictionary_by_evaluating_in_the_paused_program()
    {
        Assert.SkipWhen(DebuggeeFixture.Adapter is null, "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        var plugin = Assert.Single(_plugins.Plugins);
        Assert.Equal(PluginState.Ready, plugin.State);

        var depth = Path.Combine(debuggee.Folder, "Depth.cs");
        _debug.Breakpoints.Set(new BreakpointSpec(depth, DebuggeeFixture.Line(depth, "depth-end")));
        await _debug.LaunchAsync(DotnetHost, ["exec", debuggee.Dll, "--depth"], debuggee.Folder, new Dictionary<string, string>(), TestContext.Current.CancellationToken);
        await WaitForAsync(() => _debug.State == DebugState.Stopped && _debug.SelectedFrame is not null);

        // onDebug starts the plugin, which offers its visualizer for Dictionary<…>.
        await _plugins.ActivateByEventAsync("onDebug", TestContext.Current.CancellationToken);
        await WaitForAsync(() => _plugins.DebugVisualizers.All.Count == 1);
        var locals = await _debug.VariablesAsync((await _debug.ScopesAsync(_debug.SelectedFrame!))[0].VariablesReference);
        var ages = Assert.Single(locals, v => v.Name == "ages");
        var visualizer = Assert.Single(_plugins.DebugVisualizers.For(ages.Type));
        Assert.Equal(("daedalus-samples.dictionary-visualizer/dictionary", "Dictionary Visualizer"), (visualizer.Id, visualizer.Title));
        Assert.Empty(_plugins.DebugVisualizers.For("System.Collections.Generic.List<int>"));

        var view = await visualizer.Visualize(new DebugVisualizeParams(visualizer.Id, "ages", ages.Type, ages.Value), TestContext.Current.CancellationToken);
        Assert.Equal(["Key", "Value"], view.Columns!);
        Assert.Equal(["\"Ada\"=36", "\"Grace\"=85"], view.Rows!.Select(r => r[0] + "=" + r[1])); // "Charles" was removed

        // An evaluation error reaches the plugin, and its failure reaches the shell.
        await Assert.ThrowsAsync<JsonRpcRemoteException>(() => visualizer.Visualize(new DebugVisualizeParams(visualizer.Id, "notThere", ages.Type, ages.Value), TestContext.Current.CancellationToken));
        await _debug.StopAsync();
    }

    private static string DotnetHost =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
                .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
                .First(File.Exists);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 2400 && !condition(); i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }
}
