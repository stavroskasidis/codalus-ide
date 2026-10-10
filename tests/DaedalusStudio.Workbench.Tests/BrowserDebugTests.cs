using DaedalusStudio.Debugging;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Debugging;
using DaedalusStudio.Workbench.Node;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>
/// M16c (ADR 0026): JavaScript debugging through vscode-js-debug in a headless Chromium browser, with the session's
/// child targets behind one adapter. Needs a Chromium browser: skipped without one locally, failing in CI.
/// </summary>
public sealed class BrowserDebugTests : IDisposable
{
    /// <summary>The Node.js and js-debug downloads, shared by test runs on this machine.</summary>
    private static string SharedCache => Path.Combine(Path.GetTempPath(), "daedalus-test-node-cache");

    private readonly string _site = Path.Combine(Path.GetTempPath(), "daedalus-site-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_site, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromMinutes(5)).Token).Token;

    [Fact]
    public async Task A_javascript_breakpoint_stops_and_the_page_can_be_inspected_and_stepped()
    {
        Assert.SkipWhen(NodeRuntime.Platform is null, "No Node.js build for this platform.");
        var browser = Browsers.Find();
        Assert.SkipWhen(browser is null && Environment.GetEnvironmentVariable("CI") != "true", "No Chrome, Edge or Chromium is installed.");
        Assert.NotNull(browser);

        using var node = new NodeRuntime(SharedCache);
        using var jsDebug = new JsDebug(node, SharedCache);
        await jsDebug.EnsureAsync(null, Timeout());

        Directory.CreateDirectory(_site);
        var script = Path.Combine(_site, "app.js");
        await File.WriteAllTextAsync(Path.Combine(_site, "index.html"), "<!DOCTYPE html>\n<html><body><h1 id=\"t\">x</h1><script src=\"app.js\"></script></body></html>\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(script, "function add(a, b) {\n  const sum = a + b;\n  return sum;\n}\nsetTimeout(() => {\n  document.getElementById('t').textContent = String(add(2, 3));\n}, 1000);\n", TestContext.Current.CancellationToken);

        var debug = new DebugService(() => null);
        var stops = new List<StopInfo>();
        debug.Stopped += (_, stop) =>
        {
            lock (stops)
            {
                stops.Add(stop);
            }
        };
        debug.Breakpoints.Toggle(script, 2);
        var url = new Uri(Path.Combine(_site, "index.html")).AbsoluteUri;
        await debug.LaunchBrowserAsync(
            ct => JsDebugAdapter.StartAsync(jsDebug.NodePath, jsDebug.ServerScript, browser.LaunchType, ct),
            new BrowserLaunchArguments(browser.LaunchType, url, _site, RuntimeExecutable: browser.Path, RuntimeArgs: ["--headless=new"]),
            Timeout());
        try
        {
            await WaitAsync(() => debug.State == DebugState.Stopped);
            Assert.Equal("breakpoint", debug.CurrentStop!.Reason);
            Assert.True(debug.Breakpoints.StateOf(debug.Breakpoints.At(script, 2)!) is { Verified: true }, "the breakpoint was reported bound");
            var top = debug.Frames[0];
            Assert.Equal(2, top.Line);
            Assert.Equal(script, top.Source?.Path, StringComparer.OrdinalIgnoreCase);
            Assert.Equal("5", (await debug.EvaluateAsync("a + b", "repl", Timeout())).Result);

            // A step lands on the next line of the same function.
            await debug.StepOverAsync();
            await WaitAsync(() => debug.State == DebugState.Stopped && debug.Frames.Count > 0 && debug.Frames[0].Line == 3);
            Assert.Equal("5", (await debug.EvaluateAsync("sum", "repl", Timeout())).Result);

            debug.Breakpoints.Clear();
            await debug.ContinueAsync();
            await WaitAsync(() => debug.State == DebugState.Running);
        }
        finally
        {
            await debug.StopAsync();
        }

        Assert.Equal(DebugState.Idle, debug.State);
    }

    [Fact]
    public void Browsers_are_found_by_kind_or_path_and_webassembly_apps_debug_in_one()
    {
        Assert.Null(Browsers.Find(Path.Combine(_site, "missing-browser.exe")));
        Directory.CreateDirectory(_site);
        var edge = Path.Combine(_site, "msedge.exe");
        File.WriteAllText(edge, string.Empty);
        Assert.Equal(new Browser(Browsers.Edge, edge), Browsers.Find(edge));
        Assert.Equal("pwa-msedge", Browsers.Find(edge)!.LaunchType);

        // Only an app on the WebAssembly dev server, with an inspectUri, debugs in the browser.
        const string inspect = "{wsProtocol}://{url.hostname}:{url.port}/_framework/debug/ws-proxy?browser={browserInspectUri}";
        var wasm = new DaedalusStudio.Projects.Protocol.RunInfo("/p/App.csproj", "net10.0", "Exe", "/p/bin/App.dll", "dotnet", "exec \"/nuget/blazor-devserver.dll\" --applicationpath /p/bin/App.dll", null, null);
        var profile = new LaunchProfile("http", "Project", ApplicationUrl: "http://localhost:5045", LaunchUrl: "counter", InspectUri: inspect);
        Assert.Equal(("http://localhost:5045/counter", inspect), LaunchPlanner.BrowserDebugTarget(wasm, profile));
        Assert.Null(LaunchPlanner.BrowserDebugTarget(wasm with { RunArguments = "exec /p/bin/App.dll" }, profile));
        Assert.Null(LaunchPlanner.BrowserDebugTarget(wasm, profile with { InspectUri = null }));
    }

    [Fact]
    public void A_web_apps_page_debugs_with_its_server_for_webassembly_or_when_javascript_debugging_is_on()
    {
        // M16d (ADR 0027): a Blazor Web App's profile has the debugging proxy's inspectUri: its page always debugs.
        const string inspect = "{wsProtocol}://{url.hostname}:{url.port}/_framework/debug/ws-proxy?browser={browserInspectUri}";
        var hosted = new LaunchProfile("http", "Project", ApplicationUrl: "https://localhost:7174;http://localhost:5274", LaunchUrl: "counter", InspectUri: inspect);
        Assert.Equal(("https://localhost:7174/counter", inspect), LaunchPlanner.PageDebugTarget(hosted, javaScript: false));

        // Other web apps: their page, when the profile opens one and JavaScript debugging is on.
        var web = new LaunchProfile("http", "Project", LaunchBrowser: true, ApplicationUrl: "http://localhost:5000");
        Assert.Equal(("http://localhost:5000/", (string?)null), LaunchPlanner.PageDebugTarget(web, javaScript: true));
        Assert.Null(LaunchPlanner.PageDebugTarget(web, javaScript: false));
        Assert.Null(LaunchPlanner.PageDebugTarget(web with { LaunchBrowser = false }, javaScript: true));
        Assert.Null(LaunchPlanner.PageDebugTarget(new LaunchProfile("console", "Project"), javaScript: true));
        Assert.Null(LaunchPlanner.PageDebugTarget(null, javaScript: true));
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }
}
