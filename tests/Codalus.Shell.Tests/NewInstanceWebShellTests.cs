using System.Text.Json;
using Avalonia.Headless.XUnit;
using Codalus.Debugging;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Node;

namespace Codalus.Shell.Tests;

/// <summary>
/// #547's web step (ADR 0109): while F5 debugs samples/Debugging's Debuggee, Start New Instance adds a web app to the
/// session. A Blazor WebAssembly app's page joins as one more program, with its dev server beside it; a web app debugged
/// with its page (samples/BlazorHosted) joins as one program, its server and its page together. Needs Chrome, Edge or
/// Chromium and netcoredbg: skipped without them locally, failing in CI.
/// </summary>
public sealed class NewInstanceWebShellTests
{
    private static string SharedCache => Path.Combine(Path.GetTempPath(), "codalus-test-node-cache");

    private static int Line(string file, string marker) =>
        Array.FindIndex(File.ReadAllLines(file), l => l.Contains($"// [{marker}]", StringComparison.Ordinal)) + 1;

    private static void SkipWithoutBrowserOrDebugger()
    {
        Assert.SkipWhen(NodeRuntime.Platform is null, "No Node.js build for this platform.");
        Assert.SkipWhen(Browsers.Find() is null && Environment.GetEnvironmentVariable("CI") != "true", "No Chrome, Edge or Chromium is installed.");
        Assert.SkipWhen(DebugAdapterLocator.Resolve() is null && Environment.GetEnvironmentVariable("CI") != "true", "netcoredbg is not installed (eng/get-netcoredbg.sh).");
    }

    /// <summary>Debuggee and <paramref name="web"/>'s projects in one solution, each sample copied with its port changed to a free one.</summary>
    private static async Task<(string Samples, string Debuggee)> OpenAsync(ShellFixture f, string web, string port, params string[] webProjects)
    {
        var debugging = f.CopySample("Debugging");
        var folder = f.CopySample(web);
        var settings = Path.Combine(folder, webProjects[0], "Properties", "launchSettings.json");
        await File.WriteAllTextAsync(settings, (await File.ReadAllTextAsync(settings, TestContext.Current.CancellationToken))
            .Replace(port, Codalus.Tests.TestPorts.Free().ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal), TestContext.Current.CancellationToken);
        var samples = Path.GetDirectoryName(folder)!;
        var solution = Path.Combine(samples, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution>\n  <Project Path=\"Debugging/Debuggee/Debuggee.csproj\" />\n"
            + string.Concat(webProjects.Select(p => $"  <Project Path=\"{web}/{p}/{p}.csproj\" />\n")) + "</Solution>\n", TestContext.Current.CancellationToken);
        f.Shell.Trust.Trust(samples);
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 + webProjects.Length && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.DebugHotReloadSetting, false, Workbench.Settings.SettingScope.User, CancellationToken.None);
        var debuggee = Path.Combine(debugging, "Debuggee", "Debuggee.csproj");
        f.Shell.SetStartupProject(debuggee);
        return (samples, Path.Combine(debugging, "Debuggee", "Program.cs"));
    }

    private static string Diagnostics(ShellFixture f) => "Debug Console:\n" + f.Shell.DebugLog.GetText() + "\nNotifications:\n"
        + string.Join("\n", f.Shell.Workbench.Notifications.Items.Select(n => $"{n.Severity}: {n.Message}"))
        + "\nPrograms: " + string.Join(", ", f.Shell.Debugger.Processes.Select(p => $"{p.Name} {p.Id} {(p.Ended ? "ended" : "running")}"))
        + $"\nSession: {f.Shell.Debugger.State}, stop {f.Shell.Debugger.CurrentStop?.Reason} on thread {f.Shell.Debugger.CurrentStop?.ThreadId}; {f.Shell.Debugger.Describe()}"
        + "\nThreads: " + string.Join(", ", f.Shell.Debugger.Threads.Select(t => $"{t.Id} {t.Name}"));

    /// <summary>
    /// Awaits <paramref name="task"/> while the UI thread runs, for at most <paramref name="seconds"/>: a step that hangs
    /// says which one and where the session stands (the joined web app's test hung in one, run by run).
    /// </summary>
    private static async Task WithinAsync(ShellFixture f, Task task, string what, int seconds = 120)
    {
        await ShellFixture.PumpUntil(() => task.IsCompleted, seconds, () => what + " didn't return. " + Diagnostics(f));
        await task;
    }

    /// <summary>F5 on Debuggee, stopped at its breakpoint: the session the web app joins.</summary>
    private static async Task StartDebuggeeAsync(ShellFixture f, string program)
    {
        f.Shell.Debugger.Breakpoints.Toggle(program, Line(program, "print"));
        await f.RunAsync("debug.start");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped && f.Shell.Debugger.SelectedFrame?.Source?.Path == program, seconds: 300, describe: () => Diagnostics(f));
        Assert.True(f.Shell.Debugger.CanAddLaunch);
    }

    private static async Task<StopInfo> NextStopAsync(ShellFixture f, StopInfo? previous, int seconds)
    {
        await ShellFixture.PumpUntil(
            () => f.Shell.Debugger.State == DebugState.Stopped && f.Shell.Debugger.CurrentStop is { } stop && !ReferenceEquals(stop, previous) && f.Shell.Debugger.Frames.Count > 0,
            seconds,
            () => Diagnostics(f));
        return f.Shell.Debugger.CurrentStop!;
    }

    [AvaloniaFact]
    public async Task A_blazor_webassembly_app_joins_the_session_and_stops_in_the_browser()
    {
        SkipWithoutBrowserOrDebugger();
        Environment.SetEnvironmentVariable(ShellHost.NodeCacheVariable, SharedCache);
        Environment.SetEnvironmentVariable(Browsers.HeadlessVariable, "1");
        try
        {
            await using var f = ShellFixture.Create();
            var (samples, program) = await OpenAsync(f, "BlazorWasm", "5045", "BlazorWasm");
            var counter = Path.Combine(samples, "BlazorWasm", "BlazorWasm", "Pages", "Counter.razor");
            await StartDebuggeeAsync(f, program);
            var debuggeeStop = f.Shell.Debugger.CurrentStop;

            // The page joins as the session's second program; its dev server runs beside it.
            f.Shell.Debugger.Breakpoints.Toggle(counter, Line(counter, "initialized"));
            var join = f.ExecuteAsync("debug.startNewInstance", JsonSerializer.SerializeToElement(Path.Combine(samples, "BlazorWasm", "BlazorWasm", "BlazorWasm.csproj")));
            await ShellFixture.PumpUntil(() => join.IsCompleted, seconds: 300, describe: () => "Start New Instance didn't return. " + Diagnostics(f));
            await join;
            Assert.Equal(2, f.Shell.Debugger.Processes.Count);
            Assert.Equal("Browser", f.Shell.Debugger.Processes[1].Name);
            Assert.Single(f.Shell.JoinedInstanceProjects);
            Assert.Single(f.Shell.Runs.Running); // the dev server

            // Once Debuggee goes on, the component stops at its breakpoint, in the browser's .NET.
            await WithinAsync(f, f.Shell.Debugger.ContinueAsync(), "Continue");
            var stop = await NextStopAsync(f, debuggeeStop, 600);
            Assert.Equal("breakpoint", stop.Reason);
            Assert.Contains("Counter.OnInitialized", f.Shell.Debugger.Frames[0].Name, StringComparison.Ordinal);
            Assert.StartsWith("Browser: ", f.Shell.Debugger.Threads.First(t => t.Id == stop.ThreadId).Name, StringComparison.Ordinal);

            // Stop ends both programs, the browser and the dev server.
            f.Shell.Debugger.Breakpoints.Clear();
            await WithinAsync(f, f.RunAsync("debug.stop"), "Stop");
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle && f.Shell.Runs.Running.Count == 0, seconds: 60, describe: () => Diagnostics(f));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Browsers.HeadlessVariable, null);
        }
    }

    [AvaloniaFact]
    public async Task A_web_app_with_its_page_joins_the_session_as_one_program()
    {
        SkipWithoutBrowserOrDebugger();
        Environment.SetEnvironmentVariable(ShellHost.NodeCacheVariable, SharedCache);
        Environment.SetEnvironmentVariable(Browsers.HeadlessVariable, "1");
        try
        {
            await using var f = ShellFixture.Create();
            var (samples, program) = await OpenAsync(f, "BlazorHosted", "5274", "BlazorHosted", "BlazorHosted.Client");
            var counter = Path.Combine(samples, "BlazorHosted", "BlazorHosted.Client", "Pages", "Counter.razor");
            var script = Path.Combine(samples, "BlazorHosted", "BlazorHosted", "wwwroot", "app.js");
            await StartDebuggeeAsync(f, program);
            var previous = f.Shell.Debugger.CurrentStop;

            f.Shell.Debugger.Breakpoints.Toggle(counter, Line(counter, "initialized"));
            f.Shell.Debugger.Breakpoints.Toggle(script, Line(script, "script"));
            var join = f.ExecuteAsync("debug.startNewInstance", JsonSerializer.SerializeToElement(Path.Combine(samples, "BlazorHosted", "BlazorHosted", "BlazorHosted.csproj")));
            await ShellFixture.PumpUntil(() => join.IsCompleted, seconds: 300, describe: () => "Start New Instance didn't return. " + Diagnostics(f));
            await join;
            Assert.Equal(["Debuggee", "BlazorHosted"], f.Shell.Debugger.Processes.Select(p => p.Name));
            await WithinAsync(f, f.Shell.Debugger.ContinueAsync(), "Continue on Debuggee");

            // The server prerenders the component, and the page runs its script and the component again: every stop is
            // the web app's, under its name, the page's threads within it.
            var seen = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var stop = await NextStopAsync(f, previous, 600);
                previous = stop;
                var thread = f.Shell.Debugger.Threads.First(t => t.Id == stop.ThreadId).Name;
                Assert.StartsWith("BlazorHosted: ", thread, StringComparison.Ordinal);
                var top = f.Shell.Debugger.Frames[0];
                seen.Add(string.Equals(top.Source?.Path, script, StringComparison.OrdinalIgnoreCase) ? "script"
                    : thread.StartsWith("BlazorHosted: Browser: ", StringComparison.Ordinal) ? "webassembly" : "server");
                await WithinAsync(f, f.Shell.Debugger.ContinueAsync(), $"Continue after the {seen[^1]} stop");
            }

            Assert.Equal(["script", "server", "webassembly"], seen.Order(StringComparer.Ordinal));

            f.Shell.Debugger.Breakpoints.Clear();
            await WithinAsync(f, f.RunAsync("debug.stop"), "Stop");
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60, describe: () => Diagnostics(f));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Browsers.HeadlessVariable, null);
        }
    }
}
