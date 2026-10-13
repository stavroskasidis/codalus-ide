using System.Reflection;
using System.Text.Json;
using Codalus.Platform;
using Codalus.Rpc;
using Codalus.Workbench;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Notifications;
using Codalus.Workbench.Plugins;
using Codalus.Workbench.Settings;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Plugins.Tests;

/// <summary>End-to-end: the real codalus-pluginhost process running the HelloWorld sample plugin.</summary>
public sealed class PluginHostIntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(60);
    private readonly string _userData = Path.Combine(Path.GetTempPath(), "codalus-plugins-" + Guid.NewGuid().ToString("N"));
    private WorkbenchServices _workbench = null!;
    private PluginManager _manager = null!;

    internal static string RepoRoot => Metadata("CodalusRepoRoot");

    internal static string Configuration => Metadata("CodalusConfiguration");

    internal static string HostAssembly => Path.Combine(RepoRoot, "src", "Codalus.Plugins.Host", "bin", Configuration, "net10.0", "codalus-pluginhost.dll");

    internal static string HelloWorldDir => Path.Combine(RepoRoot, "samples", "plugins", "HelloWorld", "bin", Configuration, "net10.0");

    private static string Metadata(string key) =>
        typeof(PluginHostIntegrationTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    public ValueTask InitializeAsync()
    {
        Assert.True(File.Exists(HostAssembly), $"Plugin host not built: {HostAssembly}");
        var paths = AppPaths.Resolve(PlatformInfo.Current, _ => null, _userData);
        paths.EnsureCreated();
        _workbench = new WorkbenchServices(paths, PlatformInfo.Current, watchFiles: false);
        _manager = new PluginManager(_workbench, _ => ProcessLaunchInfo.FromPath(HostAssembly), "test",
            new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50), MaxRestarts = 2 });
        _manager.AddPlugins(PluginDiscovery.Scan([(HelloWorldDir, false)], PlatformInfo.Current));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        _workbench.Dispose();
        try
        {
            Directory.Delete(_userData, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private PluginDescription Hello => _manager.Plugins.Single();

    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Contributions_are_applied_without_starting_the_plugin()
    {
        Assert.Equal(PluginState.Ready, Hello.State);
        Assert.Equal("Hello: Say Hello", _workbench.Commands.Find("hello.sayHello")!.DisplayTitle);
        Assert.Contains(_workbench.Menus.GetItems("menuBar/tools"), i => i.Command == "hello.sayHello");
        Assert.NotNull(_workbench.Keybindings.GetBinding("hello.sayHello"));
        Assert.Equal(ToolWindowKind.Tree, _workbench.ToolWindows.Find("hello.view")!.Kind);
        Assert.Equal("Hello", _workbench.Settings.GetString("hello.greeting"));
        Assert.Equal("Hello World", _workbench.Settings.Find("hello.times")!.DisplayCategory);
        Assert.Contains("onCommand:hello.echo", Hello.ActivationEvents);
        Assert.Contains("onView:hello.view", Hello.ActivationEvents);
    }

    [Fact]
    public async Task Registering_a_plugins_settings_does_not_fire_its_setting_change_activation()
    {
        // HelloWorld activates on onSettingChanged:hello.announce; adding the plugin registers that setting.
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Equal(PluginState.Ready, Hello.State);
    }

    [Fact]
    public async Task Executing_a_command_activates_the_plugin_and_round_trips_arguments()
    {
        var result = await _workbench.Commands.ExecuteAsync("hello.echo", [Json("1"), Json("\"two\""), Json("""{"a":[3]}""")], Timeout());

        Assert.Equal(PluginState.Active, Hello.State);
        Assert.Equal(3, result!.Value.GetProperty("count").GetInt32());
        Assert.Equal("two", result.Value.GetProperty("args")[1].GetString());
        Assert.Equal(3, result.Value.GetProperty("args")[2].GetProperty("a")[0].GetInt32());
    }

    [Fact]
    public async Task Plugin_drives_notifications_output_and_status_bar()
    {
        var shown = new TaskCompletionSource<Notification>(TaskCreationOptions.RunContinuationsAsynchronously);
        _workbench.Notifications.Added += (_, n) => shown.TrySetResult(n);
        await _workbench.Settings.SetAsync("hello.greeting", "Ahoy", SettingScope.User, Timeout());

        var run = _workbench.Commands.ExecuteAsync("hello.sayHello", Timeout());
        var notification = await shown.Task.WaitAsync(Timeout());
        Assert.Equal("Ahoy, Codalus!", notification.Message);
        Assert.Equal("Hello World", notification.Source);
        Assert.Equal(["Show Output"], notification.Actions);
        _workbench.Notifications.Choose(notification, "Show Output");

        Assert.Equal(1, (await run)!.Value.GetInt32());
        var channel = _workbench.Output.Channels.Single(c => c.Name == "Hello World: Greetings");
        await WaitUntilAsync(() => channel.GetText().Contains("Ahoy, Codalus! (#1)", StringComparison.Ordinal));
        await WaitUntilAsync(() => _workbench.StatusBar.GetItems(Workbench.StatusBar.StatusBarAlignment.Right).Any(i => i.Text == "Hello: 1" && i.Command == "hello.sayHello"));
    }

    [Fact]
    public async Task Tree_views_load_lazily_and_refresh_on_setting_changes()
    {
        var roots = await _manager.GetTreeChildrenAsync("hello.view", null, Timeout());
        var root = Assert.Single(roots);
        Assert.Equal("Hello #1", root.Label);
        Assert.Equal("collapsed", root.Collapsible);
        Assert.Equal("hello.sayHello", root.Command!.Id);

        var letters = await _manager.GetTreeChildrenAsync("hello.view", root.Handle, Timeout());
        Assert.Equal(["H", "e", "l", "l", "o"], letters.Select(l => l.Label));

        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager.TreeViewRefreshed += (_, e) =>
        {
            if (e.ViewId == "hello.view")
            {
                refreshed.TrySetResult();
            }
        };
        await _workbench.Settings.SetAsync("hello.times", 3, SettingScope.User, Timeout());
        await refreshed.Task.WaitAsync(Timeout());

        Assert.Equal(3, (await _manager.GetTreeChildrenAsync("hello.view", null, Timeout())).Count);
    }

    [Fact]
    public async Task Plugins_can_execute_shell_commands()
    {
        _workbench.Commands.Register(new CommandDescriptor("test.add", "Add", ContributionSource.Shell), (args, _) =>
            Task.FromResult<JsonElement?>(Json((args[0].GetInt32() + args[1].GetInt32()).ToString(System.Globalization.CultureInfo.InvariantCulture))));

        var result = await _workbench.Commands.ExecuteAsync("hello.callShell", [Json("\"test.add\""), Json("2"), Json("40")], Timeout());

        Assert.Equal(42, result!.Value.GetInt32());
    }

    [Fact]
    public async Task Setting_change_activation_event_activates_the_plugin()
    {
        Assert.Equal(PluginState.Ready, Hello.State);
        await _workbench.Settings.SetAsync("hello.announce", true, SettingScope.User, Timeout());
        await WaitUntilAsync(() => Hello.State == PluginState.Active);
    }

    [Fact]
    public async Task A_crashed_host_is_restarted_and_its_plugins_reactivated()
    {
        await _workbench.Commands.ExecuteAsync("hello.echo", Timeout());
        var warnings = new List<string>();
        _workbench.Notifications.Added += (_, n) =>
        {
            lock (warnings)
            {
                warnings.Add(n.Message);
            }
        };

        await Assert.ThrowsAnyAsync<Exception>(() => _workbench.Commands.ExecuteAsync("hello.crashHost", Timeout()));
        await WaitUntilAsync(() => { lock (warnings) { return warnings.Any(w => w.Contains("restarted", StringComparison.Ordinal)); } });

        Assert.Contains(warnings, w => w.Contains("stopped unexpectedly (exit code 42)", StringComparison.Ordinal));
        Assert.Equal(PluginState.Active, Hello.State);
        var result = await _workbench.Commands.ExecuteAsync("hello.echo", [Json("\"again\"")], Timeout());
        Assert.Equal(1, result!.Value.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task A_crash_looping_host_is_stopped_and_its_plugins_fail()
    {
        var restarts = 0;
        _workbench.Notifications.Added += (_, n) =>
        {
            if (n.Message.Contains("restarted", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref restarts);
            }
        };

        // MaxRestarts = 2: the first two crashes are restarted, the third stops the host. Each crash must be
        // observed before the next one, or the next command just fails against the dead connection.
        var crashes = 0;
        while (Hello.State != PluginState.Failed)
        {
            Assert.True(++crashes <= 3, "The host was not stopped after three crashes.");
            await Assert.ThrowsAnyAsync<Exception>(() => _workbench.Commands.ExecuteAsync("hello.crashHost", Timeout()));
            await WaitUntilAsync(() => Volatile.Read(ref restarts) == crashes || Hello.State == PluginState.Failed);
        }

        Assert.Equal(3, crashes);
        Assert.Contains("crashed repeatedly", Hello.Error, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _workbench.Commands.ExecuteAsync("hello.echo", Timeout()));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var started = System.Diagnostics.Stopwatch.StartNew(); // monotonic: immune to the machine sleeping
        while (!condition())
        {
            Assert.True(started.Elapsed < s_timeout, "Timed out.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }
}
