using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Codalus.Debugging;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Node;

namespace Codalus.Shell.Tests;

/// <summary>
/// M16d (ADR 0027): F5 on samples/BlazorHosted debugs the server under netcoredbg and the page in a headless Chromium
/// browser under vscode-js-debug, in one session. Needs Chrome, Edge or Chromium: skipped without one locally,
/// failing in CI.
/// </summary>
public sealed class CompoundDebugTests
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

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [AvaloniaFact]
    public async Task F5_debugs_the_server_and_the_page_in_one_session()
    {
        Assert.SkipWhen(NodeRuntime.Platform is null, "No Node.js build for this platform.");
        Assert.SkipWhen(Browsers.Find() is null && Environment.GetEnvironmentVariable("CI") != "true", "No Chrome, Edge or Chromium is installed.");
        // The server half runs under netcoredbg (a worktree has no .work/netcoredbg: this waited out its 600 s instead).
        Assert.SkipWhen(DebugAdapterLocator.Resolve() is null && Environment.GetEnvironmentVariable("CI") != "true", "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        Environment.SetEnvironmentVariable(ShellHost.NodeCacheVariable, SharedCache);
        Environment.SetEnvironmentVariable(Browsers.HeadlessVariable, "1");
        try
        {
            await using var f = ShellFixture.Create();
            var folder = f.CopySample("BlazorHosted");
            var settings = Path.Combine(folder, "BlazorHosted", "Properties", "launchSettings.json");
            await File.WriteAllTextAsync(settings, (await File.ReadAllTextAsync(settings, TestContext.Current.CancellationToken))
                .Replace("5274", FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal), TestContext.Current.CancellationToken);
            f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
            await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BlazorHosted.slnx"));
            await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);
            f.Shell.Runs.SetStartupProject(Path.Combine(folder, "BlazorHosted", "BlazorHosted.csproj"));

            // The component runs on the server (prerendering) and then in the browser; the script in the browser.
            var counter = Path.Combine(folder, "BlazorHosted.Client", "Pages", "Counter.razor");
            var initialized = Line(counter, "initialized");
            var script = Path.Combine(folder, "BlazorHosted", "wwwroot", "app.js");
            var scriptLine = Line(script, "script");
            f.Shell.Debugger.Breakpoints.Toggle(counter, initialized);
            f.Shell.Debugger.Breakpoints.Toggle(script, scriptLine);

            string Diagnostics() => "Debug Console:\n" + f.Shell.DebugLog.GetText();
            async Task<StopInfo> NextStopAsync(StopInfo? previous, int seconds)
            {
                try
                {
                    await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped && f.Shell.Debugger.CurrentStop is { } stop && !ReferenceEquals(stop, previous)
                        && f.Shell.Debugger.Frames.Count > 0, seconds);
                }
                catch (Xunit.Sdk.XunitException)
                {
                    Assert.Fail("The session didn't stop. " + Diagnostics());
                }

                return f.Shell.Debugger.CurrentStop!;
            }

            string ThreadName(StopInfo stop) => f.Shell.Debugger.Threads.FirstOrDefault(t => t.Id == stop.ThreadId)?.Name ?? "?";

            f.Shell.Run("debug.start");

            // 1. The server prerenders the page: netcoredbg stops in the component.
            var first = await NextStopAsync(null, 600);
            Assert.True(f.Shell.Debugger.IsCompound, Diagnostics());
            Assert.Contains("Counter.OnInitialized", f.Shell.Debugger.Frames[0].Name, StringComparison.Ordinal);
            Assert.DoesNotContain("Browser:", ThreadName(first), StringComparison.Ordinal);
            Assert.Equal("0", (await f.Shell.Debugger.EvaluateAsync("currentCount", "watch", TestContext.Current.CancellationToken)).Result);
            var serverProcess = f.Shell.Debugger.ProcessId;

            // 2. The page: its script, and the component again in WebAssembly, in either order.
            var seen = new List<string>();
            var previous = first;
            for (var i = 0; i < 2; i++)
            {
                await f.Shell.Debugger.ContinueAsync();
                var stop = await NextStopAsync(previous, 180);
                previous = stop;
                Assert.StartsWith("Browser:", ThreadName(stop), StringComparison.Ordinal);
                var top = f.Shell.Debugger.Frames[0];
                if (string.Equals(top.Source?.Path, script, StringComparison.OrdinalIgnoreCase))
                {
                    Assert.Equal(scriptLine, top.Line);
                    Assert.Contains("Counter", (await f.Shell.Debugger.EvaluateAsync("title", "watch", TestContext.Current.CancellationToken)).Result, StringComparison.Ordinal);
                    seen.Add("script");
                }
                else
                {
                    Assert.Contains("Counter.razor", top.Source?.Path ?? top.Name, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(initialized, top.Line);
                    seen.Add("webassembly");
                }
            }

            Assert.Equal(["script", "webassembly"], seen.Order(StringComparer.Ordinal));
            Assert.True(f.Shell.Debugger.Breakpoints.StateOf(f.Shell.Debugger.Breakpoints.At(script, scriptLine)!) is { Verified: true });

            // Stopping ends both: the browser closes and the server exits.
            f.Shell.Debugger.Breakpoints.Clear();
            await f.Shell.Debugger.StopAsync();
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);
            if (serverProcess is { } pid)
            {
                await ShellFixture.PumpUntil(() => !IsRunning(pid), seconds: 30);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(Browsers.HeadlessVariable, null);
        }
    }

    [AvaloniaFact]
    public async Task Debug_a_web_page_opens_an_html_file_in_the_browser_and_stops_in_its_script()
    {
        Assert.SkipWhen(NodeRuntime.Platform is null, "No Node.js build for this platform.");
        Assert.SkipWhen(Browsers.Find() is null && Environment.GetEnvironmentVariable("CI") != "true", "No Chrome, Edge or Chromium is installed.");
        // The server half runs under netcoredbg (a worktree has no .work/netcoredbg: this waited out its 600 s instead).
        Assert.SkipWhen(DebugAdapterLocator.Resolve() is null && Environment.GetEnvironmentVariable("CI") != "true", "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        Environment.SetEnvironmentVariable(ShellHost.NodeCacheVariable, SharedCache);
        Environment.SetEnvironmentVariable(Browsers.HeadlessVariable, "1");
        try
        {
            await using var f = ShellFixture.Create();
            var folder = f.CopySample("WebScripts");
            var page = Path.Combine(folder, "WebScripts", "wwwroot", "index.html");
            var script = Path.Combine(folder, "WebScripts", "wwwroot", "js", "site.js");
            var line = Line(script, "sum");
            f.Shell.Debugger.Breakpoints.Toggle(script, line);

            // No solution: F5 on an open HTML file debugs it as a page (file URL, its folder as the web root).
            await f.Shell.OpenFileAsync(page, preview: false, null);
            f.Shell.Run("debug.start");
            try
            {
                await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped && f.Shell.Debugger.Frames.Count > 0, seconds: 600);
            }
            catch (Xunit.Sdk.XunitException)
            {
                Assert.Fail("The page didn't stop. Debug Console:\n" + f.Shell.DebugLog.GetText());
            }

            var top = f.Shell.Debugger.Frames[0];
            Assert.Equal(script, top.Source?.Path, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(line, top.Line);
            Assert.Equal("5", (await f.Shell.Debugger.EvaluateAsync("a + b", "watch", TestContext.Current.CancellationToken)).Result);
            f.Shell.Debugger.Breakpoints.Clear();
            await f.Shell.Debugger.StopAsync();
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);

            // Any http, https or file URL, or an HTML file's path, is a page; other text isn't.
            Assert.Equal(new Uri(page), ShellHost.WebPageUrl(page));
            Assert.Equal("http://localhost:5310/", ShellHost.WebPageUrl("http://localhost:5310/")?.ToString());
            Assert.Null(ShellHost.WebPageUrl("not a page"));
            Assert.Null(ShellHost.WebPageUrl(Path.Combine(folder, "WebScripts", "Program.cs")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Browsers.HeadlessVariable, null);
        }
    }
}
