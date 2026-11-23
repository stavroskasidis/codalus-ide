using Codalus.Debugging;
using Codalus.Tests;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Node;

namespace Codalus.Workbench.Tests;

/// <summary>
/// M16c (ADR 0026): JavaScript debugging through vscode-js-debug in a headless Chromium browser, with the session's
/// child targets behind one adapter. Needs a Chromium browser: skipped without one locally, failing in CI.
/// </summary>
public sealed class BrowserDebugTests : IDisposable
{
    /// <summary>The Node.js and js-debug downloads, shared by test runs on this machine.</summary>
    private static string SharedCache => Path.Combine(Path.GetTempPath(), "codalus-test-node-cache");

    private readonly string _site = Path.Combine(Path.GetTempPath(), "codalus-site-" + Guid.NewGuid().ToString("N"));

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
        var console = new System.Text.StringBuilder();
        debug.Output += (_, output) =>
        {
            lock (console)
            {
                console.Append(output.Text);
            }
        };

        // What the session waits for, and js-debug's own log (CODALUS_JSDEBUG_TRACE, which CI sets to the shard's
        // results), for a failure: Step Over once hung here with nothing to say why (macOS CI, run 45077032327).
        var trace = Browsers.TraceFile();
        string Describe()
        {
            lock (console)
            {
                return $"The session: {debug.Describe()}.{(trace is null ? string.Empty : " vscode-js-debug's log: " + trace + ".")} Debug Console:\n{console}";
            }
        }

        debug.Breakpoints.Toggle(script, 2);
        var url = new Uri(Path.Combine(_site, "index.html")).AbsoluteUri;
        await debug.LaunchBrowserAsync(
            ct => JsDebugAdapter.StartAsync(jsDebug.NodePath, jsDebug.ServerScript, browser.LaunchType, ct),
            new BrowserLaunchArguments(browser.LaunchType, url, _site, RuntimeExecutable: browser.Path, RuntimeArgs: ["--headless=new"], Trace: trace is null ? null : new JsDebugTrace(trace)),
            Timeout());
        try
        {
            await WaitAsync(() => debug.State == DebugState.Stopped, () => "The page didn't stop at the breakpoint. " + Describe());
            Assert.Equal("breakpoint", debug.CurrentStop!.Reason);
            Assert.True(debug.Breakpoints.StateOf(debug.Breakpoints.At(script, 2)!) is { Verified: true }, "the breakpoint was reported bound");
            var top = debug.Frames[0];
            Assert.Equal(2, top.Line);
            Assert.Equal(script, top.Source?.Path, StringComparer.OrdinalIgnoreCase);
            Assert.Equal("5", (await debug.EvaluateAsync("a + b", "repl", Timeout())).Result);

            // A step lands on the next line of the same function.
            await AnsweredAsync(debug.StepOverAsync(), "Step Over", Describe);
            await WaitAsync(() => debug.State == DebugState.Stopped && debug.Frames.Count > 0 && debug.Frames[0].Line == 3, () => "The step didn't stop on line 3. " + Describe());
            Assert.Equal("5", (await debug.EvaluateAsync("sum", "repl", Timeout())).Result);

            debug.Breakpoints.Clear();
            await AnsweredAsync(debug.ContinueAsync(), "Continue", Describe);
            await WaitAsync(() => debug.State == DebugState.Running, () => "The page didn't run on. " + Describe());
        }
        finally
        {
            await debug.StopAsync();
        }

        Assert.Equal(DebugState.Idle, debug.State);
    }

    [Fact]
    public async Task Behind_the_webassembly_debugging_proxy_the_page_opens_once_the_proxy_has_attached_to_it()
    {
        // The Blazor WebAssembly debugging proxy sets globalThis.dotnetDebugger for the page's next document when the
        // browser reports the page attached, and the .NET runtime pauses for the proxy only where it's set. js-debug
        // opened the page right after attaching, so a slow handshake left the app's document without the flag: the proxy
        // never saw the runtime start and no C# breakpoint bound (BlazorWasmTests and the smoke step under load). The
        // stand-in proxy answers its handshake late every time; the page must still open with the flag, and stop at a
        // breakpoint set while it was blank.
        Assert.SkipWhen(NodeRuntime.Platform is null, "No Node.js build for this platform.");
        var browser = Browsers.Find();
        Assert.SkipWhen(browser is null && Environment.GetEnvironmentVariable("CI") != "true", "No Chrome, Edge or Chromium is installed.");
        Assert.NotNull(browser);

        using var node = new NodeRuntime(SharedCache);
        using var jsDebug = new JsDebug(node, SharedCache);
        await jsDebug.EnsureAsync(null, Timeout());

        Directory.CreateDirectory(_site);
        var script = Path.Combine(_site, "app.js");
        await File.WriteAllTextAsync(script, "var flagged = globalThis.dotnetDebugger === true;\nconsole.log('flagged', flagged);\n", TestContext.Current.CancellationToken);
        await using var site = new MockHttpServer(request => request.Path switch
        {
            "/index.html" => MockHttpResponse.Text("<!DOCTYPE html>\n<html><body><script src=\"app.js\"></script></body></html>\n", contentType: "text/html; charset=utf-8"),
            "/app.js" => MockHttpResponse.Text(File.ReadAllText(script), contentType: "text/javascript; charset=utf-8"),
            _ => MockHttpResponse.NotFound(),
        });
        await using var proxy = new StandInDebuggingProxy(TimeSpan.FromSeconds(2));

        var debug = new DebugService(() => null);
        var console = new System.Text.StringBuilder();
        debug.Output += (_, output) =>
        {
            lock (console)
            {
                console.Append(output.Text);
            }
        };
        debug.Breakpoints.Toggle(script, 2);
        await debug.LaunchBrowserAsync(
            ct => JsDebugAdapter.StartAsync(jsDebug.NodePath, jsDebug.ServerScript, browser.LaunchType, ct),
            new BrowserLaunchArguments(browser.LaunchType, site.BaseUrl + "/index.html", _site, InspectUri: proxy.InspectUri, RuntimeExecutable: browser.Path, RuntimeArgs: ["--headless=new"]),
            Timeout());
        try
        {
            await WaitAsync(() => debug.State == DebugState.Stopped, () =>
            {
                lock (console)
                {
                    return $"The page didn't stop at the breakpoint ({proxy.Handshakes} handshakes, pages served: {string.Join(", ", site.Requests.Select(r => r.Path))}). Debug Console:\n{console}";
                }
            });
            Assert.Equal(2, debug.Frames[0].Line);
            Assert.Equal(script, debug.Frames[0].Source?.Path, StringComparer.OrdinalIgnoreCase);
            Assert.True(proxy.Handshakes > 0, "js-debug got the page's attach event after the handshake");
            Assert.Equal("true", (await debug.EvaluateAsync("flagged", "repl", Timeout())).Result);
        }
        finally
        {
            await debug.StopAsync();
        }
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
        var wasm = new Codalus.Projects.Protocol.RunInfo("/p/App.csproj", "net10.0", "Exe", "/p/bin/App.dll", "dotnet", "exec \"/nuget/blazor-devserver.dll\" --applicationpath /p/bin/App.dll", null, null);
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

    /// <summary>
    /// Waits for a step's or a continue's answer. The session waits for it with no limit of its own (its token ends only
    /// with the session), and one that vscode-js-debug never answered held the test until the 15-minute hang dump, with
    /// node and the browser still running and nothing to say which request it waited for (macOS CI, run 45077032327).
    /// It fails instead, saying what each of js-debug's sessions waits for. TODO(issue): why js-debug didn't answer.
    /// </summary>
    private static async Task AnsweredAsync(Task resume, string what, Func<string> describe)
    {
        try
        {
            await resume.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            // The stop that follows fails it (its connection closes): observed, not left to the finalizer.
            _ = resume.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            Assert.Fail($"{what} got no answer from vscode-js-debug in 90 s. {describe()}");
        }
    }

    private static async Task WaitAsync(Func<bool> condition, Func<string>? describe = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, describe?.Invoke() ?? "Timed out.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }
}
