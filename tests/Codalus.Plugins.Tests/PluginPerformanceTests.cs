using Codalus.Platform;
using Codalus.Rpc;
using Codalus.Workbench;
using Codalus.Workbench.Plugins;

namespace Codalus.Plugins.Tests;

/// <summary>Plugin performance attribution (M22d): activation time, commands, host memory, the slow-plugin event.</summary>
public sealed class PluginPerformanceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("codalus-perf-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Activation_commands_and_host_memory_are_measured_and_a_slow_activation_is_reported()
    {
        var paths = AppPaths.Resolve(PlatformInfo.Current, _ => null, Path.Combine(_root, "user-data"));
        paths.EnsureCreated();
        using var workbench = new WorkbenchServices(paths, PlatformInfo.Current, watchFiles: false);
        await using var manager = new PluginManager(workbench, _ => ProcessLaunchInfo.FromPath(PluginHostIntegrationTests.HostAssembly), "test")
        {
            SlowActivation = TimeSpan.Zero, // every activation counts as slow here
        };
        var slow = new List<string>();
        manager.SlowPlugin += (_, plugin) => slow.Add(plugin.Id);
        manager.AddPlugins(PluginDiscovery.Scan([(PluginHostIntegrationTests.HelloWorldDir, false)], PlatformInfo.Current));
        var hello = Assert.Single(manager.Plugins);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        await workbench.Commands.ExecuteAsync("hello.echo", [], timeout.Token);
        await workbench.Commands.ExecuteAsync("hello.echo", [], timeout.Token);

        Assert.NotNull(hello.ActivationTime);
        Assert.Equal(2, hello.Commands.Calls);
        Assert.True(hello.Commands.Slowest > TimeSpan.Zero);
        Assert.True(manager.HostMemory(hello.HostGroup) > 1024 * 1024);
        Assert.Equal([hello.Id], slow);
        Assert.Null(manager.HostMemory("no-such-group"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
