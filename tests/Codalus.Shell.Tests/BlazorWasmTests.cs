using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Node;

namespace Codalus.Shell.Tests;

/// <summary>
/// M16c (ADR 0026): F5 on samples/BlazorWasm debugs the app in a headless Chromium browser through vscode-js-debug and
/// the dev server's debugging proxy. Needs Chrome, Edge or Chromium: skipped without one locally, failing in CI.
/// </summary>
public sealed class BlazorWasmTests
{
    /// <summary>The Node.js and js-debug downloads, shared by test runs on this machine (as in WebLanguagesTests).</summary>
    private static string SharedCache => Path.Combine(Path.GetTempPath(), "codalus-test-node-cache");

    private static int Line(string file, string marker) =>
        Array.FindIndex(File.ReadAllLines(file), l => l.Contains($"// [{marker}]", StringComparison.Ordinal)) + 1;

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    [AvaloniaFact]
    public async Task F5_debugs_the_webassembly_app_in_the_browser()
    {
        Assert.SkipWhen(NodeRuntime.Platform is null, "No Node.js build for this platform.");
        Assert.SkipWhen(Browsers.Find() is null && Environment.GetEnvironmentVariable("CI") != "true", "No Chrome, Edge or Chromium is installed.");
        Environment.SetEnvironmentVariable(ShellHost.NodeCacheVariable, SharedCache);
        Environment.SetEnvironmentVariable(Browsers.HeadlessVariable, "1");
        try
        {
            await using var f = ShellFixture.Create();
            var folder = f.CopySample("BlazorWasm");
            var settings = Path.Combine(folder, "BlazorWasm", "Properties", "launchSettings.json");
            await File.WriteAllTextAsync(settings, (await File.ReadAllTextAsync(settings, TestContext.Current.CancellationToken))
                .Replace("5045", FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal), TestContext.Current.CancellationToken);
            f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
            await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BlazorWasm.slnx"));
            await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);

            // A breakpoint in the component's C#, which runs in the browser.
            var counter = Path.Combine(folder, "BlazorWasm", "Pages", "Counter.razor");
            var initialized = Line(counter, "initialized");
            f.Shell.Debugger.Breakpoints.Toggle(counter, initialized);

            await f.RunAsync("debug.start");
            try
            {
                await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped, seconds: 600);
            }
            catch (Xunit.Sdk.XunitException)
            {
                Assert.Fail("The browser session didn't stop at the breakpoint. Debug Console:\n" + f.Shell.DebugLog.GetText()
                    + "\nRun output:\n" + string.Concat(f.Shell.Workbench.Output.Channels.Where(c => c.Name.StartsWith("Run:", StringComparison.Ordinal)).Select(c => c.GetText())));
            }

            Assert.Equal("breakpoint", f.Shell.Debugger.CurrentStop!.Reason);
            await ShellFixture.PumpUntil(() => f.Shell.CallStack.Frames.Count > 0);
            Assert.Contains("Counter.OnInitialized", f.Shell.CallStack.Frames[0].Name, StringComparison.Ordinal);
            await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor is { Controller: { } c } && c.CurrentDebugLine() == initialized
                && string.Equals(f.Shell.ActiveEditor.Model.Path, counter, StringComparison.OrdinalIgnoreCase));
            await ShellFixture.PumpUntil(() => f.Shell.Locals.Roots.Any(r => r.Name == "this"));
            Assert.Single(f.Shell.Runs.Running); // the dev server

            // Stopping the session closes the browser and the dev server.
            f.Shell.Debugger.Breakpoints.Clear();
            await f.Shell.Debugger.StopAsync();
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle && f.Shell.Runs.Running.Count == 0, seconds: 60);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Browsers.HeadlessVariable, null);
        }
    }

    [AvaloniaFact]
    public async Task Hot_reload_applies_an_edit_to_the_app_while_it_is_debugged()
    {
        // The maintainer's 0.5 request: Hot Reload worked only without the debugger. The dev server runs under dotnet watch
        // while the page debugs, and the browser's runtime takes the update with the debugging proxy attached (ADR 0040).
        Assert.SkipWhen(NodeRuntime.Platform is null, "No Node.js build for this platform.");
        Assert.SkipWhen(Browsers.Find() is null && Environment.GetEnvironmentVariable("CI") != "true", "No Chrome, Edge or Chromium is installed.");
        Environment.SetEnvironmentVariable(ShellHost.NodeCacheVariable, SharedCache);
        Environment.SetEnvironmentVariable(Browsers.HeadlessVariable, "1");
        try
        {
            await using var f = ShellFixture.Create();
            var folder = f.CopySample("BlazorWasm");
            var settings = Path.Combine(folder, "BlazorWasm", "Properties", "launchSettings.json");
            await File.WriteAllTextAsync(settings, (await File.ReadAllTextAsync(settings, TestContext.Current.CancellationToken))
                .Replace("5045", FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal), TestContext.Current.CancellationToken);
            f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
            await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BlazorWasm.slnx"));
            await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);

            // Stopped once in the component, so the page has loaded and the proxy is attached; then running again.
            var counter = Path.Combine(folder, "BlazorWasm", "Pages", "Counter.razor");
            f.Shell.Debugger.Breakpoints.Toggle(counter, Line(counter, "initialized"));
            await f.RunAsync("debug.start");
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped, seconds: 600,
                describe: () => "Debug Console:\n" + f.Shell.DebugLog.GetText());
            f.Shell.Debugger.Breakpoints.Clear();
            await f.RunAsync("debug.continue");
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Running);
            var server = Assert.Single(f.Shell.Runs.Running);
            Assert.Same(server, f.Shell.HotReloadProgram);
            Assert.Equal(true, f.Shell.Workbench.Context.GetValue("hotReloadRunning")); // Apply Code Changes (Alt+F10) works while debugging

            // The tick, every half second, now adds 1000: the change runs in the browser, under the debugger.
            var editor = await f.Shell.OpenFileAsync(counter, preview: false, null);
            await ShellFixture.PumpUntil(() => editor?.Controller is not null);
            var offset = editor!.Model.Text.Text.IndexOf("ticks++;", StringComparison.Ordinal);
            editor.Model.Text.Replace(offset, "ticks++;".Length, "ticks += 1000;");
            await f.RunAsync("run.applyCodeChanges");
            await ShellFixture.PumpUntil(() => server.HotReload!.Applied >= 1, seconds: 120, describe: () => "Run output:\n" + server.Channel?.GetText());
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            f.Shell.Debugger.Breakpoints.Toggle(counter, Line(counter, "tick"));
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped, seconds: 60);
            var ticks = int.Parse((await f.Shell.Debugger.EvaluateAsync("ticks", "watch")).Result, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(ticks >= 1000, $"ticks is {ticks}: the edit didn't run");

            f.Shell.Debugger.Breakpoints.Clear();
            await f.Shell.Debugger.StopAsync();
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle && f.Shell.Runs.Running.Count == 0, seconds: 60);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Browsers.HeadlessVariable, null);
        }
    }

    [AvaloniaFact]
    public async Task Hot_reload_applies_a_razor_edit_to_the_page_in_the_browser()
    {
        var browser = Browsers.Find();
        Assert.SkipWhen(browser is null && Environment.GetEnvironmentVariable("CI") != "true", "No Chrome, Edge or Chromium is installed.");
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("BlazorWasm");
        var port = FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var settings = Path.Combine(folder, "BlazorWasm", "Properties", "launchSettings.json");
        await File.WriteAllTextAsync(settings, (await File.ReadAllTextAsync(settings, TestContext.Current.CancellationToken)).Replace("5045", port, StringComparison.Ordinal), TestContext.Current.CancellationToken);
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BlazorWasm.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);

        await f.Shell.StartWithHotReloadAsync();
        await ShellFixture.PumpUntil(() => f.Shell.HotReloadProgram is not null, seconds: 180);
        var program = f.Shell.HotReloadProgram!;
        System.Diagnostics.Process? page = null;
        var profile = Path.Combine(f.UserData, "browser-profile");
        try
        {
            await ShellFixture.PumpUntil(() => program.HotReload!.State == Workbench.Build.HotReloadState.Watching
                && program.Channel?.GetText().Contains("Now listening on", StringComparison.Ordinal) == true, seconds: 240);

            // WebAssembly runs the components in the browser: Hot Reload needs the page open there.
            page = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(browser!.Path)
            {
                ArgumentList = { "--headless=new", "--disable-gpu", "--no-first-run", $"--user-data-dir={profile}", $"http://localhost:{port}/counter" },
                UseShellExecute = false,
            });
            await Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); // the page loads the runtime and connects

            var counter = Path.Combine(folder, "BlazorWasm", "Pages", "Counter.razor");
            var editor = await f.Shell.OpenFileAsync(counter, preview: false, null);
            await ShellFixture.PumpUntil(() => editor?.Controller is not null);
            var offset = editor!.Model.Text.Text.IndexOf("<h1>Counter</h1>", StringComparison.Ordinal);
            editor.Model.Text.Replace(offset, "<h1>Counter</h1>".Length, "<h1>Counter, reloaded</h1>");
            await f.RunAsync("run.applyCodeChanges");
            await ShellFixture.PumpUntil(() => program.HotReload!.Applied >= 1, seconds: 120);
        }
        finally
        {
            try
            {
                page?.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            page?.Dispose();
            f.Shell.StopRunning();
        }
    }
}
