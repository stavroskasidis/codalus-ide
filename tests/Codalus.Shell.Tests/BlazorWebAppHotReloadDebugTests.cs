using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Codalus.Debugging;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Node;

namespace Codalus.Shell.Tests;

/// <summary>
/// #75, the maintainer's 0.6 request: F5 on a Blazor Web App (samples/BlazorHosted) with Hot Reload while debugging runs
/// the server under <c>dotnet watch</c>, attaches netcoredbg to the app it starts and debugs the page in a headless
/// Chromium browser under vscode-js-debug, in one session. An edit to a WebAssembly component runs in the browser under
/// the debugger; the server refuses it, and says so; an edit that needs a restart restarts the app, and debugging follows
/// it without leaving the old server holding the port (Linux and macOS, when it was stopped at a breakpoint). Needs
/// Chrome, Edge or Chromium and netcoredbg: skipped without them locally, failing in CI.
/// </summary>
public sealed class BlazorWebAppHotReloadDebugTests
{
    private static string SharedCache => Path.Combine(Path.GetTempPath(), "codalus-test-node-cache");

    private static int Line(string file, string marker) =>
        Array.FindIndex(File.ReadAllLines(file), l => l.Contains($"// [{marker}]", StringComparison.Ordinal)) + 1;

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private static void SkipWithoutPrerequisites()
    {
        Assert.SkipWhen(NodeRuntime.Platform is null, "No Node.js build for this platform.");
        Assert.SkipWhen(Browsers.Find() is null && Environment.GetEnvironmentVariable("CI") != "true", "No Chrome, Edge or Chromium is installed.");
        Assert.SkipWhen(DebugAdapterLocator.Resolve() is null && Environment.GetEnvironmentVariable("CI") != "true", "netcoredbg is not installed (eng/get-netcoredbg.sh).");
    }

    /// <summary>The sample on a free port, open, its server the startup project, Hot Reload while debugging on.</summary>
    private static async Task<(ShellFixture F, string Folder, string Counter)> OpenAsync()
    {
        Environment.SetEnvironmentVariable(ShellHost.NodeCacheVariable, SharedCache);
        Environment.SetEnvironmentVariable(Browsers.HeadlessVariable, "1");
        var f = ShellFixture.Create();
        var folder = f.CopySample("BlazorHosted");
        var settings = Path.Combine(folder, "BlazorHosted", "Properties", "launchSettings.json");
        await File.WriteAllTextAsync(settings, (await File.ReadAllTextAsync(settings, TestContext.Current.CancellationToken))
            .Replace("5274", FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal), TestContext.Current.CancellationToken);
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BlazorHosted.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);

        // The client comes first in the solution: the server is the default startup project (task #90).
        Assert.Equal(Path.Combine(folder, "BlazorHosted", "BlazorHosted.csproj"), f.Shell.Runs.StartupProject);
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.DebugHotReloadSetting, true, Workbench.Settings.SettingScope.User, CancellationToken.None);
        return (f, folder, Path.Combine(folder, "BlazorHosted.Client", "Pages", "Counter.razor"));
    }

    private static string Diagnostics(ShellFixture f) =>
        $"Debugger: {f.Shell.Debugger.State}; dotnet watch's session: {f.Shell.WatchedServerDebugSession?.State.ToString() ?? "none"}.\n"
        + "Debug Console:\n" + f.Shell.DebugLog.GetText() + "\nRun output:\n" + (f.Shell.HotReloadProgram?.Channel?.GetText() ?? f.Shell.Workbench.Output.Channels.FirstOrDefault(c => c.Name.StartsWith("Run: ", StringComparison.Ordinal))?.GetText());

    /// <summary>
    /// A failure's diagnostics on the standard error at once: on macOS CI the session never followed dotnet watch's
    /// restart, the fixture's disposal then hung, and the hang dump came without the failure (run 40920415026).
    /// </summary>
    private static async Task ReportingAsync(ShellFixture f, Func<Task> test)
    {
        try
        {
            await test();
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[BlazorWebAppHotReloadDebugTests] failed: {ex.Message}\n{Diagnostics(f)}");
            throw;
        }
    }

    private static async Task<StopInfo> NextStopAsync(ShellFixture f, StopInfo? previous, int seconds)
    {
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped && f.Shell.Debugger.CurrentStop is { } stop && !ReferenceEquals(stop, previous)
            && f.Shell.Debugger.Frames.Count > 0, seconds, describe: () => "The session didn't stop. " + Diagnostics(f));
        return f.Shell.Debugger.CurrentStop!;
    }

    private static string ThreadName(ShellFixture f, StopInfo stop) => f.Shell.Debugger.Threads.FirstOrDefault(t => t.Id == stop.ThreadId)?.Name ?? "?";

    private static bool IsRunning(int processId) => WatchedServer.IsRunning(processId);

    private static async Task StopAndCheckAsync(ShellFixture f, int server)
    {
        f.Shell.Debugger.Breakpoints.Clear();
        await f.RunAsync("debug.stop");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle && f.Shell.Runs.Running.Count == 0 && f.Shell.WatchedServerDebugSession is null,
            seconds: 120, describe: () => Diagnostics(f));
        await ShellFixture.PumpUntil(() => !IsRunning(server), seconds: 30, describe: () => $"The server (process {server}) still runs. " + Diagnostics(f));
    }

    [AvaloniaFact]
    public async Task F5_attaches_to_the_server_dotnet_watch_runs_and_a_webassembly_edit_applies_while_debugged()
    {
        SkipWithoutPrerequisites();
        var (f, _, counter) = await OpenAsync();
        await using (f)
        {
            try
            {
                var initialized = Line(counter, "initialized");
                f.Shell.Debugger.Breakpoints.Toggle(counter, initialized);
                await f.RunAsync("debug.start");

                // 1. The server prerenders the page: netcoredbg, attached to the app dotnet watch started, stops there.
                var first = await NextStopAsync(f, null, 600);
                Assert.True(f.Shell.Debugger.IsCompound, Diagnostics(f));
                Assert.False(f.Shell.Debugger.IsAttached); // the session owns the server: Stop ends it, Restart works
                Assert.DoesNotContain("Browser:", ThreadName(f, first), StringComparison.Ordinal);
                Assert.Equal("0", (await f.Shell.Debugger.EvaluateAsync("currentCount", "watch", TestContext.Current.CancellationToken)).Result);
                var watch = f.Shell.HotReloadProgram;
                Assert.NotNull(watch);
                var server = f.Shell.Debugger.ProcessId!.Value;
                Assert.Equal(server, f.Shell.WatchedServerDebugSession?.ProcessId);
                Assert.NotEqual(watch.ProcessId, server); // the app, not dotnet watch
                Assert.Equal(true, f.Shell.Workbench.Context.GetValue("hotReloadRunning")); // Apply Code Changes works while debugging

                // 2. The page: the component again, in WebAssembly.
                await f.Shell.Debugger.ContinueAsync();
                var second = await NextStopAsync(f, first, 300);
                Assert.StartsWith("Browser:", ThreadName(f, second), StringComparison.Ordinal);
                f.Shell.Debugger.Breakpoints.Clear();
                await f.RunAsync("debug.continue");
                await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Running);

                // 3. The tick, every half second, now adds 1000: the browser runs the change under the debugger, while the
                // server's runtime refuses it (a debugger is attached) and keeps running the same process.
                var editor = await f.Shell.OpenFileAsync(counter, preview: false, null);
                await ShellFixture.PumpUntil(() => editor?.Controller is not null);
                var offset = editor!.Model.Text.Text.IndexOf("ticks++;", StringComparison.Ordinal);
                editor.Model.Text.Replace(offset, "ticks++;".Length, "ticks += 1000;");
                await f.RunAsync("run.applyCodeChanges");
                await ShellFixture.PumpUntil(() => watch.HotReload!.UpdatesRefused, seconds: 120, describe: () => Diagnostics(f));
                Assert.Equal(0, watch.HotReload!.Restarts);
                Assert.Equal(server, f.Shell.Debugger.ProcessId);
                // Under the Codalus debugger (M55) the server takes the edit itself; netcoredbg's keeps its code.
                var said = f.Shell.Debugger.UsesCodalusAdapter ? "through the Codalus debugger" : "keeps the code it started with";
                await ShellFixture.PumpUntil(() => f.Shell.DebugLog.GetText().Contains(said, StringComparison.Ordinal), seconds: 30, describe: () => Diagnostics(f));
                Assert.Contains("sent to the browser", f.Shell.Workbench.StatusBar.GetItems(Workbench.StatusBar.StatusBarAlignment.Left).Single(i => i.Id == "codalus.hotReload").Text, StringComparison.Ordinal);

                // The browser applies the update on its own, and dotnet watch doesn't report it (SDK 11 says "applied" only
                // when every client applied it): the tick is looked at until it runs the edit.
                var tickLine = Line(counter, "tick");
                var previous = second;
                var ticks = 0;
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
                while (true)
                {
                    f.Shell.Debugger.Breakpoints.Toggle(counter, tickLine);
                    var tick = await NextStopAsync(f, previous, 120);
                    previous = tick;
                    Assert.StartsWith("Browser:", ThreadName(f, tick), StringComparison.Ordinal);
                    ticks = int.Parse((await f.Shell.Debugger.EvaluateAsync("ticks", "watch", TestContext.Current.CancellationToken)).Result, System.Globalization.CultureInfo.InvariantCulture);
                    if (ticks >= 1000 || DateTime.UtcNow > deadline)
                    {
                        break;
                    }

                    f.Shell.Debugger.Breakpoints.Clear();
                    await f.RunAsync("debug.continue");
                    await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
                }

                Assert.True(ticks >= 1000, $"ticks is {ticks}: the edit didn't run. " + Diagnostics(f));

                // 4. The edited file's breakpoints left the server (it runs older code there); the page binds them.
                var state = f.Shell.Debugger.Breakpoints.StateOf(f.Shell.Debugger.Breakpoints.At(counter, tickLine)!);
                Assert.True(state is { Verified: true } || state?.Message == ShellHost.StaleServerBreakpoint, $"The breakpoint's state: {state}");

                // 5. Stop ends the browser, dotnet watch and the server.
                await StopAndCheckAsync(f, server);
            }
            finally
            {
                Environment.SetEnvironmentVariable(Browsers.HeadlessVariable, null);
            }
        }
    }

    /// <summary>
    /// M55: under the Codalus debugger the server dotnet watch runs takes an edit in place, through the debugger: its
    /// answer changes, without a restart, in the same process (dotnet watch's own copy of the update is refused).
    /// </summary>
    [AvaloniaFact]
    public async Task Under_the_Codalus_debugger_an_edit_applies_to_the_server_in_place()
    {
        SkipWithoutPrerequisites();
        var (f, folder, _) = await OpenAsync();
        await using (f)
        {
            try
            {
                await f.Shell.Workbench.Settings.SetAsync(ShellHost.DebugAdapterSetting, DebugService.CodalusAdapter, Workbench.Settings.SettingScope.User, CancellationToken.None);
                var settings = await File.ReadAllTextAsync(Path.Combine(folder, "BlazorHosted", "Properties", "launchSettings.json"), TestContext.Current.CancellationToken);
                var url = System.Text.RegularExpressions.Regex.Match(settings, "http://localhost:[0-9]+").Value;
                await f.RunAsync("debug.start");
                await ShellFixture.PumpUntil(() => f.Shell.WatchedServerDebugSession?.ProcessId is not null && f.Shell.DebugEdits.IsActive && f.Shell.Debugger.State == DebugState.Running, seconds: 600, describe: () => Diagnostics(f));
                var watch = f.Shell.HotReloadProgram!;
                var server = f.Shell.Debugger.ProcessId!.Value;
                using var http = new HttpClient();
                Assert.Equal("hello from the server", await http.GetStringAsync(url + "/api/greeting", TestContext.Current.CancellationToken));

                var greeting = Path.Combine(folder, "BlazorHosted", "Greeting.cs");
                var editor = await f.Shell.OpenFileAsync(greeting, preview: false, null);
                await ShellFixture.PumpUntil(() => editor?.Controller is not null);
                var offset = editor!.Model.Text.Text.IndexOf("hello from the server", StringComparison.Ordinal);
                editor.Model.Text.Replace(offset, "hello from the server".Length, "edited on the server");
                await f.RunAsync("run.applyCodeChanges");
                var answer = string.Empty;
                await ShellFixture.PumpUntil(() =>
                {
                    answer = http.GetStringAsync(url + "/api/greeting", TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                    return answer == "edited on the server";
                }, seconds: 120, describe: () => $"The server answered '{answer}'. " + Diagnostics(f));
                Assert.Equal(0, watch.HotReload!.Restarts);
                Assert.Equal(server, f.Shell.Debugger.ProcessId);
                Assert.True(IsRunning(server));

                await StopAndCheckAsync(f, server);
            }
            finally
            {
                Environment.SetEnvironmentVariable(Browsers.HeadlessVariable, null);
            }
        }
    }

    [AvaloniaFact]
    public async Task A_rude_edit_while_stopped_on_the_server_restarts_the_app_and_debugging_follows_it()
    {
        // The case that orphans the server without the detach: held at a breakpoint, it can't run its SIGTERM handler,
        // and `dotnet run` waits for it while dotnet watch starts the next server, which can't bind the port.
        SkipWithoutPrerequisites();
        var (f, _, counter) = await OpenAsync();
        await using (f)
        {
            try
            {
                await ReportingAsync(f, () => RudeEditAsync(f, counter));
            }
            finally
            {
                Environment.SetEnvironmentVariable(Browsers.HeadlessVariable, null);
            }
        }
    }

    private static async Task RudeEditAsync(ShellFixture f, string counter)
    {
        var initialized = Line(counter, "initialized");
        f.Shell.Debugger.Breakpoints.Toggle(counter, initialized);
        await f.RunAsync("debug.start");
        var first = await NextStopAsync(f, null, 600);
        Assert.DoesNotContain("Browser:", ThreadName(f, first), StringComparison.Ordinal);
        var watch = f.Shell.HotReloadProgram!;
        var server = f.Shell.Debugger.ProcessId!.Value;

        // A field's type changes: a rude edit, which dotnet watch restarts the app for (--non-interactive).
        var editor = await f.Shell.OpenFileAsync(counter, preview: false, null);
        await ShellFixture.PumpUntil(() => editor?.Controller is not null);
        var offset = editor!.Model.Text.Text.IndexOf("private int ticks;", StringComparison.Ordinal);
        editor.Model.Text.Replace(offset, "private int ticks;".Length, "private long ticks;");
        await f.RunAsync("run.applyCodeChanges");

        await ShellFixture.PumpUntil(() => watch.HotReload!.Restarts >= 1, seconds: 180, describe: () => Diagnostics(f));
        await ShellFixture.PumpUntil(() => !IsRunning(server), seconds: 60, describe: () => $"The old server (process {server}) still runs. " + Diagnostics(f));

        // The new server serves on the same port, and the session attached to it: the breakpoint stops it again
        // as it prerenders the page the session opened again.
        var again = await NextStopAsync(f, first, 300);
        var restarted = f.Shell.Debugger.ProcessId!.Value;
        Assert.NotEqual(server, restarted);
        Assert.Equal(restarted, f.Shell.WatchedServerDebugSession?.ProcessId);
        Assert.True(f.Shell.Debugger.IsCompound);
        Assert.DoesNotContain("Browser:", ThreadName(f, again), StringComparison.Ordinal);
        Assert.Contains("Counter.OnInitialized", f.Shell.Debugger.Frames[0].Name, StringComparison.Ordinal);
        Assert.Same(watch, f.Shell.HotReloadProgram); // the same dotnet watch

        await StopAndCheckAsync(f, restarted);
    }
}
