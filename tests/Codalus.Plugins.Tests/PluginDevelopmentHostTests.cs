using System.Diagnostics;
using System.Globalization;
using Codalus.Platform;
using Codalus.Rpc;
using Codalus.Workbench;
using Codalus.Workbench.Plugins;

namespace Codalus.Plugins.Tests;

/// <summary>Debug Plugin in Development Instance's plugin host (M22b): its own host, the process id file, no time limit.</summary>
public sealed class PluginDevelopmentHostTests : IAsyncDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codalus-plugin-dev-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_plugin_under_development_runs_in_a_debug_host_that_writes_its_process_id()
    {
        var debugFile = Path.Combine(_root, "host.pid");
        var paths = AppPaths.Resolve(PlatformInfo.Current, _ => null, Path.Combine(_root, "user-data"));
        paths.EnsureCreated();
        using var workbench = new WorkbenchServices(paths, PlatformInfo.Current, watchFiles: false);
        // The host waits a second for a debugger that never comes, then activates the plugin.
        var launch = ProcessLaunchInfo.FromPath(PluginHostIntegrationTests.HostAssembly) with
        {
            Environment = new Dictionary<string, string> { ["CODALUS_PLUGINHOST_DEBUG_WAIT"] = "1" },
        };
        await using var manager = new PluginManager(workbench, _ => launch, "test")
        {
            DebugHostFile = debugFile,
            DevelopmentDirs = [PluginHostIntegrationTests.HelloWorldDir],
        };
        manager.AddPlugins(PluginDiscovery.Scan([(PluginHostIntegrationTests.HelloWorldDir, false)], PlatformInfo.Current));
        var hello = Assert.Single(manager.Plugins);
        Assert.True(hello.IsDebugged);
        Assert.Equal(PluginDescription.DebugGroup, hello.HostGroup);

        await manager.ActivateDebuggedPluginsAsync(Ct);
        Assert.Equal(PluginState.Active, hello.State);
        Assert.Equal(PluginDescription.DebugGroup, Assert.Single(manager.Hosts).Group);
        var pid = int.Parse(File.ReadAllText(debugFile), CultureInfo.InvariantCulture);
        Assert.NotEqual(Environment.ProcessId, pid);
        using var host = Process.GetProcessById(pid); // the plugin host, running
        Assert.False(host.HasExited);

        Assert.Equal(0, (await workbench.Commands.ExecuteAsync("hello.echo", [], Ct))!.Value.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Without_the_debug_file_plugins_from_a_plugin_folder_share_the_usual_host()
    {
        var paths = AppPaths.Resolve(PlatformInfo.Current, _ => null, Path.Combine(_root, "user-data"));
        using var workbench = new WorkbenchServices(paths, PlatformInfo.Current, watchFiles: false);
        await using var manager = new PluginManager(workbench, _ => null, "test") { DevelopmentDirs = [PluginHostIntegrationTests.HelloWorldDir] };
        manager.AddPlugins(PluginDiscovery.Scan([(PluginHostIntegrationTests.HelloWorldDir, false)], PlatformInfo.Current));
        var hello = Assert.Single(manager.Plugins);
        Assert.False(hello.IsDebugged);
        Assert.Equal("default", hello.HostGroup);
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

        return ValueTask.CompletedTask;
    }
}
