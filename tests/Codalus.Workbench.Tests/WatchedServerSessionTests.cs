using System.Net;
using System.Net.Sockets;
using Codalus.Platform;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;

namespace Codalus.Workbench.Tests;

/// <summary>
/// #75: a Blazor Web App's server debugged under <c>dotnet watch</c>. The session follows the server when an edit
/// restarts it, whichever of the restart line and the debugger's end comes first; a Stop ends <c>dotnet watch</c> and
/// any server the debugger held past it; a server left over from a restart is ended. Fed with <c>dotnet watch</c>'s
/// lines, as SDK 10.0.401 and 11 RC2 write them (the spike's logs), and a fake IDE.
/// </summary>
public sealed class WatchedServerSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The IDE side: attaches succeed unless told otherwise, servers are fake process ids.</summary>
    private sealed class FakeHost : IWatchedServerHost
    {
        private readonly Lock _gate = new();
        private readonly Queue<TaskCompletionSource<int?>> _waits = new();

        public List<string> Log { get; } = [];

        public HashSet<int> Alive { get; } = [];

        public bool AttachSucceeds { get; set; } = true;

        public Action? OnRelease { get; set; }

        public bool DebugActive { get; set; }

        public List<WatchedServerChange> Changes { get; } = [];

        public TaskCompletionSource<int?> NextWait()
        {
            lock (_gate)
            {
                var wait = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waits.Enqueue(wait);
                return wait;
            }
        }

        private void Add(string entry)
        {
            lock (_gate)
            {
                Log.Add(entry);
            }
        }

        public string[] Entries()
        {
            lock (_gate)
            {
                return [.. Log];
            }
        }

        public Task<int?> WaitForAppAsync(int? previousPid, CancellationToken cancellationToken)
        {
            Add($"wait {previousPid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}");
            lock (_gate)
            {
                return _waits.TryDequeue(out var wait) ? wait.Task.WaitAsync(cancellationToken) : Task.FromResult<int?>(null);
            }
        }

        public Task<bool> AttachAsync(int processId, CancellationToken cancellationToken)
        {
            Add($"attach {processId}");
            DebugActive = AttachSucceeds;
            return Task.FromResult(AttachSucceeds);
        }

        public Task ReleaseAsync()
        {
            Add("release");
            DebugActive = false;
            OnRelease?.Invoke();
            return Task.CompletedTask;
        }

        public Task StopDebuggingAsync()
        {
            if (DebugActive)
            {
                Add("stop debugging");
                DebugActive = false;
            }

            return Task.CompletedTask;
        }

        public Task StopWatchAsync()
        {
            Add("stop watch");
            return Task.CompletedTask;
        }

        public bool IsAlive(int processId) => Alive.Contains(processId);

        public void Kill(int processId)
        {
            Add($"kill {processId}");
            Alive.Remove(processId);
        }

        public void OnChanged(WatchedServerChange change)
        {
            lock (_gate)
            {
                Changes.Add(change);
            }
        }
    }

    private static async Task UntilAsync(Func<bool> condition, Func<string> describe)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out: " + describe());
            await Task.Delay(10, Ct);
        }
    }

    private static async Task<(WatchedServerSession Session, HotReloadSession HotReload, FakeHost Host, TaskCompletionSource Watch)> StartedAsync(int pid = 100)
    {
        var hotReload = new HotReloadSession("/src/App");
        var host = new FakeHost();
        host.Alive.Add(pid);
        var watch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new WatchedServerSession(hotReload, watch.Task, host, TimeSpan.FromSeconds(5));
        host.NextWait().SetResult(pid);
        Assert.True(await session.StartAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal(WatchedServerState.Attached, session.State);
        Assert.Equal(pid, session.ProcessId);
        return (session, hotReload, host, watch);
    }

    [Fact]
    public async Task A_restart_for_an_edit_detaches_then_follows_the_new_server()
    {
        var (session, hotReload, host, _) = await StartedAsync();
        using var _ = session;
        var next = host.NextWait();
        host.OnRelease = () => session.OnDebugEnded(requested: false); // the detach ends the debug session

        hotReload.OnLine("dotnet watch : File updated: ./Pages/Counter.razor");
        hotReload.OnLine("dotnet watch : Restart is needed to apply the changes.");
        await UntilAsync(() => session.State == WatchedServerState.Following, () => string.Join(", ", host.Entries()));

        // The old server ends on dotnet watch's signal once detached; the new one serves.
        host.Alive.Remove(100);
        host.Alive.Add(200);
        next.SetResult(200);
        await UntilAsync(() => session.State == WatchedServerState.Attached && session.ProcessId == 200, () => string.Join(", ", host.Entries()));
        Assert.Equal(["attach 100", "release", "wait 100", "attach 200"], host.Entries().Where(e => !e.StartsWith("wait -", StringComparison.Ordinal)));
        Assert.Contains(host.Changes, c => c.Kind == WatchedServerChangeKind.Followed && c.ProcessId == 200);
        Assert.DoesNotContain("stop watch", host.Entries());
    }

    [Fact]
    public async Task The_debuggers_end_before_the_restart_line_still_follows_and_never_stops_dotnet_watch()
    {
        // Under load the debugger's end can come first: decided from facts, not from the order.
        var (session, hotReload, host, _) = await StartedAsync();
        using var _ = session;
        var next = host.NextWait();
        session.OnDebugEnded(requested: false);
        await UntilAsync(() => session.State == WatchedServerState.Following, () => string.Join(", ", host.Entries()));
        hotReload.OnLine("dotnet watch : Restart is needed to apply the changes."); // late: ignored
        host.Alive.Remove(100);
        host.Alive.Add(200);
        next.SetResult(200);
        await UntilAsync(() => session.State == WatchedServerState.Attached, () => string.Join(", ", host.Entries()));
        Assert.DoesNotContain("release", host.Entries());
        Assert.DoesNotContain("stop watch", host.Entries());
        Assert.Equal(1, host.Entries().Count(e => e == "attach 200"));
    }

    [Fact]
    public async Task A_stop_stops_dotnet_watch_and_ends_a_server_the_debugger_held_past_it()
    {
        var (session, _, host, _) = await StartedAsync();
        using var _ = session;
        host.DebugActive = false; // Stop ended the debug session
        session.OnDebugEnded(requested: true);
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(["attach 100", "stop watch", "kill 100"], host.Entries().Where(e => !e.StartsWith("wait", StringComparison.Ordinal)));
        Assert.Equal(WatchedServerState.Ended, session.State);
        Assert.Equal(WatchedServerEnd.Stopped, host.Changes.Last().End);
    }

    [Fact]
    public async Task An_unexpected_end_after_which_the_app_exited_ends_the_session_and_says_so()
    {
        var (session, hotReload, host, _) = await StartedAsync();
        using var _ = session;
        var next = host.NextWait();
        session.OnDebugEnded(requested: false);
        await UntilAsync(() => session.State == WatchedServerState.Following, () => string.Join(", ", host.Entries()));
        host.Alive.Remove(100);
        hotReload.OnLine("dotnet watch : [App (net10.0)] Exited with error code 134");
        hotReload.OnLine("dotnet watch : Waiting for a file to change before restarting ...");
        next.SetResult(null); // WatchedServer's wait ends on that line
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(WatchedServerEnd.AppExited, host.Changes.Last().End);
        Assert.Contains("stop watch", host.Entries());
    }

    [Fact]
    public async Task An_unexpected_end_with_no_new_server_in_time_ends_the_session()
    {
        var hotReload = new HotReloadSession();
        var host = new FakeHost();
        host.Alive.Add(100);
        var session = new WatchedServerSession(hotReload, new TaskCompletionSource().Task, host, TimeSpan.FromMilliseconds(300));
        using var _ = session;
        host.NextWait().SetResult(100);
        Assert.True(await session.StartAsync(TimeSpan.FromSeconds(5), Ct));
        host.NextWait(); // never completes
        session.OnDebugEnded(requested: false);
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(WatchedServerEnd.NotRestarted, host.Changes.Last().End);
    }

    [Fact]
    public async Task A_new_server_while_the_last_one_still_runs_ends_the_orphan()
    {
        // SDK 11's agent says the new server's id: the last one, held past dotnet watch's restart, still has the port.
        var (session, hotReload, host, _) = await StartedAsync();
        using var _ = session;
        var next = host.NextWait();
        session.OnDebugEnded(requested: false);
        await UntilAsync(() => session.State == WatchedServerState.Following, () => string.Join(", ", host.Entries()));
        host.Alive.Add(200);
        hotReload.OnLine("[codalus-hot-reload-agent] Loaded into process 200: /src/App/bin/Debug/net10.0/App.dll (/sdk/hotreload/Microsoft.Extensions.DotNetDeltaApplier.dll)");
        Assert.Contains("kill 100", host.Entries());
        Assert.Contains(host.Changes, c => c.Kind == WatchedServerChangeKind.OrphanEnded && c.ProcessId == 100);
        next.SetResult(200);
        await UntilAsync(() => session.State == WatchedServerState.Attached, () => string.Join(", ", host.Entries()));
    }

    [Fact]
    public async Task Dotnet_watch_exiting_ends_the_debug_session_too()
    {
        // Stop Running Programs, or dotnet watch failing.
        var (session, _, host, watch) = await StartedAsync();
        using var _ = session;
        watch.SetResult();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Contains("stop debugging", host.Entries());
        Assert.Equal(WatchedServerEnd.WatchExited, host.Changes.Last().End);
    }

    [Fact]
    public async Task A_start_with_no_server_or_no_attach_leaves_dotnet_watch_to_the_fallback()
    {
        var hotReload = new HotReloadSession();
        var host = new FakeHost();
        using (var session = new WatchedServerSession(hotReload, new TaskCompletionSource().Task, host, TimeSpan.FromSeconds(5)))
        {
            host.NextWait().SetResult(null);
            Assert.False(await session.StartAsync(TimeSpan.FromSeconds(5), Ct));
            Assert.Equal(WatchedServerState.Ended, session.State);
        }

        host.AttachSucceeds = false;
        host.Alive.Add(100);
        using (var session = new WatchedServerSession(hotReload, new TaskCompletionSource().Task, host, TimeSpan.FromSeconds(5)))
        {
            host.NextWait().SetResult(100);
            Assert.False(await session.StartAsync(TimeSpan.FromSeconds(5), Ct));
        }

        Assert.DoesNotContain("stop watch", host.Entries());
        Assert.DoesNotContain(host.Entries(), e => e.StartsWith("kill", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ finding the server

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    [Fact]
    public async Task The_server_is_found_from_the_agents_id_once_it_logs_its_address()
    {
        var hotReload = new HotReloadSession();
        var app = new WatchedApp(hotReload, new TaskCompletionSource().Task, 10, "App", null) { IsAlive = pid => pid == 300, Children = _ => [], NameOf = _ => null };
        var wait = WatchedServer.WaitForAppAsync(app, null, TimeSpan.FromSeconds(10), Ct);
        hotReload.OnLine("[codalus-hot-reload-agent] Loaded into process 300: /src/App/bin/Debug/net10.0/App.dll");
        await Task.Delay(400, Ct);
        Assert.False(wait.IsCompleted); // not serving yet
        hotReload.OnLine("      Now listening on: http://localhost:5274");
        Assert.Equal(300, await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task Without_the_agents_id_or_a_listening_line_the_tree_and_a_probe_find_it()
    {
        // SDK 10's agent line has no id; an app whose logging filters the host's messages never says it listens.
        var port = FreePort();
        var hotReload = new HotReloadSession();
        var tree = new Dictionary<int, int[]> { [10] = [11], [11] = [12, 13] }; // dotnet watch → dotnet run → (msbuild, the app)
        var names = new Dictionary<int, string> { [11] = "dotnet", [12] = "MSBuild", [13] = "BlazorSudoku.Se" }; // Linux's 15 characters
        var app = new WatchedApp(hotReload, new TaskCompletionSource().Task, 10, "BlazorSudoku.Server", new Uri($"http://localhost:{port}/"))
        {
            IsAlive = names.ContainsKey,
            Children = pid => tree.GetValueOrDefault(pid, []),
            NameOf = pid => names.GetValueOrDefault(pid),
        };
        hotReload.OnLine("[codalus-hot-reload-agent] Loaded into process: /src/App/bin/Debug/net10.0/BlazorSudoku.Server.dll");
        var wait = WatchedServer.WaitForAppAsync(app, previousPid: 99, TimeSpan.FromSeconds(10), Ct);
        await Task.Delay(400, Ct);
        Assert.False(wait.IsCompleted);
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Assert.Equal(13, await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task The_probe_reaches_an_app_listening_on_only_one_of_localhosts_addresses()
    {
        // Windows resolves localhost to ::1 first, and a refused connection there takes about two seconds: one
        // attempt (500 ms) that tried the addresses in turn never reached 127.0.0.1.
        var port = FreePort();
        var url = new Uri($"http://localhost:{port}/");
        Assert.False(await WatchedServer.AcceptsAsync(url, Ct));
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Assert.True(await WatchedServer.AcceptsAsync(url, Ct));
        Assert.True(await WatchedServer.AcceptsAsync(new Uri($"http://127.0.0.1:{port}/"), Ct));
        Assert.True(await WatchedServer.AcceptsAsync(new Uri($"http://0.0.0.0:{port}/"), Ct));
    }

    [Fact]
    public void The_apps_process_name_is_its_apphosts()
    {
        // GetFileNameWithoutExtension would make BlazorSudoku.Server "BlazorSudoku" (seen on SDK 10, whose agent doesn't say the id).
        LaunchPlan Plan(string file) => new(file, string.Empty, "/src", new Dictionary<string, string>(), null);
        Assert.Equal("BlazorSudoku.Server", WatchedServer.AppNameOf(Plan("/src/Server/bin/Debug/net10.0/BlazorSudoku.Server")));
        Assert.Equal("BlazorSudoku.Server", WatchedServer.AppNameOf(Plan(@"C:\src\Server\bin\Debug\net10.0\BlazorSudoku.Server.exe".Replace('\\', Path.DirectorySeparatorChar))));
        Assert.Null(WatchedServer.AppNameOf(Plan("/usr/share/dotnet/dotnet")));
        Assert.True(WatchedServer.IsApp("BlazorSudoku.Se", "BlazorSudoku.Server"));
        Assert.True(WatchedServer.IsApp("BlazorSudoku.Server", "BlazorSudoku.Server"));
        Assert.False(WatchedServer.IsApp("BlazorSudoku.Cl", "BlazorSudoku.Server"));
        Assert.False(WatchedServer.IsApp("dotnet", "BlazorSudoku.Server"));
    }

    [Fact]
    public async Task The_tree_search_finds_a_real_grandchild_by_its_name()
    {
        // As dotnet watch → dotnet run → the app, with this OS's process listing and names.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A POSIX shell tree.");
        using var shell = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh", ["-c", "/bin/sh -c 'sleep 30; true' & wait"]) { UseShellExecute = false })!;
        try
        {
            var hotReload = new HotReloadSession();
            hotReload.OnLine("Now listening on: http://localhost:5000");
            var app = new WatchedApp(hotReload, new TaskCompletionSource().Task, shell.Id, "sleep", null);
            var found = await WatchedServer.WaitForAppAsync(app, null, TimeSpan.FromSeconds(10), Ct);
            Assert.NotNull(found);
            Assert.Equal("sleep", WatchedServer.ProcessName(found.Value));
        }
        finally
        {
            ProcessTree.Kill(shell);
        }
    }

    [Fact]
    public async Task The_wait_ends_when_the_app_exits_or_dotnet_watch_does()
    {
        var hotReload = new HotReloadSession();
        var watch = new TaskCompletionSource();
        var app = new WatchedApp(hotReload, watch.Task, 10, "App", null) { Children = _ => [], NameOf = _ => null, IsAlive = _ => false };
        var wait = WatchedServer.WaitForAppAsync(app, 5, TimeSpan.FromSeconds(30), Ct);
        hotReload.OnLine("dotnet watch : Waiting for a file to change before restarting ...");
        Assert.Null(await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct));

        var second = WatchedServer.WaitForAppAsync(app with { HotReload = new HotReloadSession() }, 5, TimeSpan.FromSeconds(30), Ct);
        watch.SetResult();
        Assert.Null(await second.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    // ------------------------------------------------------------------ the choice

    [Fact]
    public void Each_gate_keeps_the_launched_session_with_its_reason()
    {
        Assert.Equal(HotReloadDebugMode.Watched, HotReloadDebugChoice.Decide(true, true, null, "10.0.401", null).Mode);
        Assert.Equal(HotReloadDebugMode.Watched, HotReloadDebugChoice.Decide(true, true, null, "11.0.100-rc.2.26504.105", null).Mode);
        Assert.Equal(HotReloadDebugMode.NotApplicable, HotReloadDebugChoice.Decide(false, true, null, "10.0.401", null).Mode);

        // The setting off is the user's choice: no reason to tell.
        Assert.Equal(new HotReloadDebugDecision(HotReloadDebugMode.Launched), HotReloadDebugChoice.Decide(true, false, null, "10.0.401", null));

        var framework = HotReloadDebugChoice.Decide(true, true, "Hot Reload needs .NET 6 or later.", "10.0.401", null);
        Assert.Equal((HotReloadDebugMode.Launched, "Hot Reload needs .NET 6 or later."), (framework.Mode, framework.Reason));

        foreach (var sdk in new[] { "10.0.100", "10.0.300", "9.0.306", null })
        {
            var old = HotReloadDebugChoice.Decide(true, true, null, sdk, null);
            Assert.Equal(HotReloadDebugMode.Launched, old.Mode);
            Assert.Contains("10.0.400", old.Reason, StringComparison.Ordinal);
        }

        var startup = HotReloadDebugChoice.Decide(true, true, null, "10.0.401", "/src/App/Program.cs");
        Assert.Equal(HotReloadDebugMode.Launched, startup.Mode);
        Assert.Contains("Program.cs", startup.Reason, StringComparison.Ordinal);

        var platform = HotReloadDebugChoice.Decide(true, true, null, "10.0.401", null, "Attaching isn't reliable here yet.");
        Assert.Equal((HotReloadDebugMode.PageOnly, "Attaching isn't reliable here yet."), (platform.Mode, platform.Reason));
        Assert.Empty(HotReloadDebugChoice.UnreliablePlatforms);
    }

    [Fact]
    public void Breakpoints_in_the_servers_startup_code_are_found_in_its_own_folder_only()
    {
        var project = Path.Combine(Path.GetTempPath(), "app", "Server", "Server.csproj");
        var program = Path.Combine(Path.GetTempPath(), "app", "Server", "Program.cs");
        var client = Path.Combine(Path.GetTempPath(), "app", "Client", "Program.cs");
        Assert.Null(HotReloadDebugChoice.StartupBreakpoint(project, [new BreakpointSpec(client, 3)]));
        Assert.Null(HotReloadDebugChoice.StartupBreakpoint(project, [new BreakpointSpec(program, 3) { Enabled = false }]));
        Assert.Equal(program, HotReloadDebugChoice.StartupBreakpoint(project, [new BreakpointSpec(client, 3), new BreakpointSpec(program, 9)]));
    }
}
