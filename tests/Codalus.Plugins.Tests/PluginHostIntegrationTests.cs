using Codalus.Plugins.Protocol;
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

    internal static string HostAssembly => Path.Combine(RepoRoot, "src", "Codalus.Plugins.Host", "bin", Configuration, Codalus.Tests.RepoBuild.TargetFramework, "codalus-pluginhost.dll");

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
            new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50), MaxRestarts = 2 })
        {
            TreeTimeout = s_timeout, // a host just started on a busy CI runner can take longer than the shell's guard
        };
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
    public async Task Quick_picks_and_input_boxes_round_trip_through_the_host()
    {
        // HelloWorld's hello.ask (API 1.1): a quick pick, then a folder and a file, each with a Browse button. This is the
        // plugin API's own test of InputBoxOptions.Browse through the host (the Templates plugin's went with it, ADR 0082).
        var folder = Path.Combine(_userData, "some folder");
        var file = Path.Combine(folder, "notes.txt");
        var prompts = new ScriptedPrompts(new Queue<string>(["goodbye", folder, file]));
        _manager.Prompts = prompts;

        var result = await _workbench.Commands.ExecuteAsync("hello.ask", Timeout());

        Assert.Equal(["Pick a word", "Folder", "File"], prompts.Asked);
        Assert.Equal(new Dictionary<string, string> { ["Folder"] = "folder", ["File"] = "file" }, prompts.Browse);
        var items = Assert.Single(prompts.Picks);
        Assert.Equal([new QuickPickItemDto("Hello", "hello", "A greeting"), new QuickPickItemDto("Goodbye", "goodbye", null)], items);
        Assert.Equal("goodbye", result!.Value.GetProperty("picked").GetString());
        Assert.Equal(folder, result.Value.GetProperty("folder").GetString());
        Assert.Equal(file, result.Value.GetProperty("file").GetString());

        // Cancelled prompts come back as nulls.
        _manager.Prompts = new CancellingPrompts();
        var cancelled = await _workbench.Commands.ExecuteAsync("hello.ask", Timeout());
        Assert.Equal(JsonValueKind.Null, cancelled!.Value.GetProperty("picked").ValueKind);
        Assert.Equal(JsonValueKind.Null, cancelled.Value.GetProperty("folder").ValueKind);
    }

    private sealed class CancellingPrompts : IPluginPrompts
    {
        public Task<int?> PickAsync(IReadOnlyList<QuickPickItemDto> items, string? placeholder, string source, CancellationToken cancellationToken) => Task.FromResult<int?>(null);

        public Task<string?> InputAsync(string title, string? prompt, string? value, string? browse, string source, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
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
    public async Task A_source_control_provider_shows_its_state_commits_and_gives_originals()
    {
        // ISourceControl (API 1.0): the provider's state reaches the IDE; commit and originals go back to it.
        var folder = Path.Combine(_userData, "scm");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
        File.WriteAllText(Path.Combine(folder, "b.txt"), "b");
        await _workbench.Commands.ExecuteAsync("hello.startScm", [Json(System.Text.Json.JsonSerializer.Serialize(folder))], Timeout());
        await WaitUntilAsync(() => _manager.SourceControls.Any(s => s is { Id: "hello-scm", Groups.Count: 1 }));
        var scm = _manager.SourceControls.Single();
        Assert.Equal(("Hello Source Control", folder, "no commits", "Commit"), (scm.Label, scm.Root, scm.Status, scm.AcceptLabel));
        Assert.Equal(["a.txt", "b.txt"], scm.Groups[0].Resources.Select(r => Path.GetFileName(r.Path)));

        await _manager.AcceptSourceControlAsync("hello-scm", "first", Timeout());
        await WaitUntilAsync(() => _manager.SourceControls.Single().Status == "1 commit(s), last: first");
        Assert.Equal("the original of a.txt\n", await _manager.GetSourceControlOriginalAsync("hello-scm", Path.Combine(folder, "a.txt"), Timeout()));
    }

    [Fact]
    public async Task An_authentication_provider_signs_in_lists_sessions_asks_consent_and_signs_out()
    {
        // IAuthentication (API 1.0). Asking the provider for its sessions activates its plugin (onAuthenticationRequest).
        Assert.Empty(await _manager.GetAuthenticationSessionsAsync("hello-accounts", Timeout()));
        Assert.Contains(_manager.AuthenticationProviders, p => p is { Id: "hello-accounts", Label: "Hello Accounts", PluginId: "codalus-samples.hello" });

        // The plugin signs in with its own provider: no consent needed.
        Assert.Equal("user 1", (await _workbench.Commands.ExecuteAsync("hello.signIn", Timeout()))!.Value.GetString());
        var session = Assert.Single(await _manager.GetAuthenticationSessionsAsync("hello-accounts", Timeout()));
        Assert.Equal(("user 1", "token-1"), (session.Account, session.AccessToken));
        Assert.Equal("user 1", (await _workbench.Commands.ExecuteAsync("hello.signIn", Timeout()))!.Value.GetString()); // the same session again

        // Another plugin asks: the user is asked once; a refusal gives nothing.
        var asked = new List<string>();
        var allow = false;
        _manager.AskConsent = message =>
        {
            asked.Add(message);
            return Task.FromResult(allow);
        };
        Assert.Null(await _manager.GetSessionForAsync(new AuthGetSessionParams("other.plugin", "hello-accounts", ["read"], false), Timeout()));
        allow = true;
        Assert.Equal("user 1", (await _manager.GetSessionForAsync(new AuthGetSessionParams("other.plugin", "hello-accounts", ["read"], false), Timeout()))!.Account);
        Assert.Equal("user 1", (await _manager.GetSessionForAsync(new AuthGetSessionParams("other.plugin", "hello-accounts", ["read"], false), Timeout()))!.Account);
        Assert.Equal(2, asked.Count);
        Assert.Contains("'other.plugin' wants to use your Hello Accounts account (user 1).", asked);

        // A scope the session hasn't: a new sign-in, when asked for.
        Assert.Null(await _manager.GetSessionForAsync(new AuthGetSessionParams("other.plugin", "hello-accounts", ["write"], false), Timeout()));
        Assert.Equal("user 2", (await _manager.GetSessionForAsync(new AuthGetSessionParams("other.plugin", "hello-accounts", ["write"], true), Timeout()))!.Account);

        // Signing out.
        await _manager.SignOutAsync("hello-accounts", session.Id, Timeout());
        Assert.Equal(["user 2"], (await _manager.GetAuthenticationSessionsAsync("hello-accounts", Timeout())).Select(s => s.Account));
    }

    [Fact]
    public async Task A_plugins_file_system_is_mounted_read_listed_and_written_through_the_plugin()
    {
        // IFileSystems (API 1.0): the plugin serves "hello"; the editor's file systems reach it under its root.
        var root = (await _workbench.Commands.ExecuteAsync("hello.mountFiles", Timeout()))!.Value.GetString()!;
        Assert.Equal(Codalus.Plugins.Protocol.FileSystemRoots.RootOf("hello"), root);
        await WaitUntilAsync(() => _manager.FileSystems.Any(f => f.Scheme == "hello" && f.PluginId == "codalus-samples.hello"));

        var readme = Path.Combine(root, "readme.md");
        var fileSystem = Workbench.FileSystem.FileSystems.For(readme);
        Assert.True(fileSystem.IsRemote);
        Assert.StartsWith("# Hello", System.Text.Encoding.UTF8.GetString(await fileSystem.ReadAllBytesAsync(readme, Timeout())), StringComparison.Ordinal);
        Assert.Equal((true, false), ((await fileSystem.StatAsync(readme, Timeout())).Exists, (await fileSystem.StatAsync(readme, Timeout())).IsDirectory));
        Assert.True((await fileSystem.StatAsync(Path.Combine(root, "notes"), Timeout())).IsDirectory);
        Assert.False((await fileSystem.StatAsync(Path.Combine(root, "missing.txt"), Timeout())).Exists);
        var entries = await fileSystem.ReadDirectoryAsync(root, Timeout());
        Assert.Contains(entries, e => e is { Name: "readme.md", IsDirectory: false });
        Assert.Contains(entries, e => e is { Name: "notes", IsDirectory: true });

        // A write is the plugin's: it reads the new content back.
        var todo = Path.Combine(root, "notes", "todo.txt");
        await fileSystem.WriteAllBytesAsync(todo, "feed the cat\n"u8.ToArray(), Timeout());
        Assert.Equal("feed the cat\n", (await _workbench.Commands.ExecuteAsync("hello.readFile", [Json("\"/notes/todo.txt\"")], Timeout()))!.Value.GetString());

        // The provider's errors come back as the file system's.
        await Assert.ThrowsAsync<FileNotFoundException>(() => fileSystem.ReadAllBytesAsync(Path.Combine(root, "missing.txt"), Timeout()));

        // The plugin's host goes (a crash): so does its file system.
        _ = _workbench.Commands.ExecuteAsync("hello.crashHost", Timeout()).ContinueWith(_ => { }, TaskScheduler.Default);
        await WaitUntilAsync(() => _manager.FileSystems.Count == 0);
        Assert.False(Workbench.FileSystem.FileSystems.For(readme).IsRemote);
    }

    [Fact]
    public async Task A_plugin_task_reports_its_progress_and_the_user_can_cancel_it()
    {
        // IWindow.WithProgressAsync (API 1.0): the task is the workbench's while it runs, and Cancel reaches the plugin.
        // Caught (and cancelled) as the progress arrives, not by polling: the count lasts four seconds, which a busy
        // runner's test thread can miss altogether.
        var seen = new TaskCompletionSource<ProgressTask>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs e)
        {
            if (_workbench.Progress.Running.FirstOrDefault(t => t is { Title: "Counting", Percent: >= 10 } && t.Message!.EndsWith("of 10", StringComparison.Ordinal)) is { } counting
                && seen.TrySetResult(counting))
            {
                counting.Cancel();
            }
        }

        _workbench.Progress.Changed += OnChanged;
        var run = _workbench.Commands.ExecuteAsync("hello.count", Timeout());
        var task = await seen.Task.WaitAsync(Timeout());
        _workbench.Progress.Changed -= OnChanged;
        Assert.Equal("Hello World", task.Source);
        Assert.True(task.Cancellable);
        Assert.StartsWith("cancelled at ", (await run)!.Value.GetString(), StringComparison.Ordinal);
        await WaitUntilAsync(() => _workbench.Progress.Running.Count == 0);
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
