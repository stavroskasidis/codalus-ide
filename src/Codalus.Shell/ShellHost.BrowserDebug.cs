using System.Text.RegularExpressions;
using Codalus.Debugging;
using Codalus.Shell.Infrastructure;
using Codalus.Platform;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Node;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// Debugging in the browser through vscode-js-debug:
/// <list type="bullet">
/// <item>a Blazor WebAssembly app (ADR 0026): its dev server starts, then the page debugs in a Chromium browser
/// through the dev server's debugging proxy; the session and the server end together;</item>
/// <item>an ASP.NET Core app with a page (ADR 0027): a compound session, the server under netcoredbg and the page
/// under js-debug, for a Blazor Web App's WebAssembly components and for scripts;</item>
/// <item>any URL or HTML file (Debug a Web Page): the page alone.</item>
/// </list>
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>Whether Start Debugging debugs an ASP.NET Core app's page with its server (ADR 0027).</summary>
    public const string JavaScriptDebugSetting = "debug.javaScript";

    /// <summary>Whether a Blazor WebAssembly app's dev server runs under <c>dotnet watch</c> while it's debugged (ADR 0040, 2026-10-20).</summary>
    public const string DebugHotReloadSetting = "debug.hotReload";

    private static readonly TimeSpan s_devServerTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How long a server has to log its address before the page opens at the launch profile's.</summary>
    private static readonly TimeSpan s_listeningWait = TimeSpan.FromSeconds(10);

    private RunningProgram? _browserDebugServer;
    private TaskCompletionSource<string>? _serverListening;
    private string? _lastWebPage;

    /// <summary>vscode-js-debug on the on-demand Node.js runtime.</summary>
    public JsDebug JsDebug { get; private set; } = null!;

    private void InitializeBrowserDebugging(string cache)
    {
        JsDebug = new JsDebug(Node, cache);
        Debugger.Ended += (_, _) =>
        {
            // Off the debugger's end, which raises this event: stopping the server ends its process tree, which lists
            // the system's processes, and on the macOS runner that listing once blocked for minutes (ps too), so the
            // session never finished ending and Stop never returned.
            if (Interlocked.Exchange(ref _browserDebugServer, null) is { } server)
            {
                _ = Task.Run(server.Stop);
            }

            Interlocked.Exchange(ref _serverListening, null)?.TrySetCanceled();
        };
    }

    /// <summary>The browser to debug in, with js-debug installed; null (after <paramref name="report"/>) when there is none.</summary>
    private async Task<Browser?> PrepareBrowserAsync(Action<string> report)
    {
        var browser = Browsers.Find(Workbench.Settings.GetString(Browsers.SettingKey, "auto"));
        if (browser is null)
        {
            report("Debugging in the browser needs Chrome, Edge or Chromium. Install one, or set 'debug.browser' to a Chromium browser's path.");
            return null;
        }

        if (NodeRuntime.Platform is null)
        {
            report("Debugging in the browser needs Node.js, which isn't available for this platform.");
            return null;
        }

        DebugLog.AppendLine($"Debugging in {browser.Display} ({browser.Path}) through vscode-js-debug {JsDebug.Version}.");
        try
        {
            await JsDebug.EnsureAsync(new Progress<string>(line => DebugLog.AppendLine(line)), CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or InvalidDataException)
        {
            report($"vscode-js-debug couldn't be installed: {ex.Message}");
            return null;
        }

        return browser;
    }

    private BrowserLaunchArguments BrowserArguments(Browser browser, string url, string webRoot, string? inspectUri)
    {
        var trace = Browsers.TraceFile();
        if (trace is not null)
        {
            DebugLog.AppendLine("vscode-js-debug's log: " + trace);
        }

        return new(browser.LaunchType, url, webRoot, inspectUri, browser.Path, Browsers.Headless ? ["--headless=new"] : null, Trace: trace is null ? null : new JsDebugTrace(trace));
    }

    private Func<CancellationToken, Task<JsDebugAdapter>> StartJsDebug(Browser browser) =>
        ct => JsDebugAdapter.StartAsync(JsDebug.NodePath, JsDebug.ServerScript, browser.LaunchType, ct);

    /// <summary>Where a web project's page is served from: <c>wwwroot</c> when it has one, else the project folder.</summary>
    private static string WebRoot(string project)
    {
        var folder = Path.GetDirectoryName(project)!;
        var wwwroot = Path.Combine(folder, "wwwroot");
        return Directory.Exists(wwwroot) ? wwwroot : folder;
    }

    // ------------------------------------------------------------------ Blazor WebAssembly (ADR 0026)

    private async Task StartBrowserDebuggingAsync(string project, (string Url, string InspectUri) target)
    {
        if (await PrepareBrowserAsync(message => Workbench.Notifications.Error(message)).ConfigureAwait(true) is not { } browser)
        {
            return;
        }

        // Hot Reload while debugging (ADR 0040, 2026-10-20): the components run in the browser's runtime, which takes
        // dotnet watch's updates with the debugging proxy attached, unlike a program netcoredbg runs.
        var hotReload = Workbench.Settings.GetBoolean(DebugHotReloadSetting, true) && HotReloadUnsupportedReason(project) is null;
        var server = await Runs.StartAsync(project, externalConsole: false, CancellationToken.None, hotReload: hotReload, openBrowser: false).ConfigureAwait(true);
        if (hotReload)
        {
            OnHotReloadStarted(server);
        }

        try
        {
            await server.Listening.WaitAsync(s_devServerTimeout).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            server.Stop();
            throw new InvalidOperationException($"The dev server didn't start listening (see Output → Run: {server.Name}).", ex);
        }

        _browserDebugServer = server;
        DebugLog.AppendLine($"> {browser.Display} {target.Url}");
        await Debugger.LaunchBrowserAsync(StartJsDebug(browser), BrowserArguments(browser, target.Url, WebRoot(project), target.InspectUri), CancellationToken.None).ConfigureAwait(true);

        // The dev server stopped on its own: the page has nothing left to debug.
        _ = server.Exited.ContinueWith(_ => Ui.Post(() =>
        {
            if (_browserDebugServer == server)
            {
                _ = Debugger.StopAsync();
            }
        }), TaskScheduler.Default);
    }

    // ------------------------------------------------------------------ server and page together (ADR 0027)

    /// <summary>
    /// Starts <paramref name="plan"/> under netcoredbg, waits for it to listen, then opens <paramref name="page"/> in
    /// the browser in the same session. Without a browser the server debugs alone (and the profile's browser opens as
    /// usual); a WebAssembly app says why its components won't stop.
    /// </summary>
    private async Task StartCompoundDebuggingAsync(string project, LaunchPlan plan, (string Url, string? InspectUri) page)
    {
        var browser = await PrepareBrowserAsync(message =>
        {
            DebugLog.AppendLine(message);
            if (page.InspectUri is not null)
            {
                Workbench.Notifications.Warning(message + " The server debugs alone: breakpoints in WebAssembly components won't bind.");
            }
        }).ConfigureAwait(true);
        if (browser is null)
        {
            _pendingBrowserUrl = plan.BrowserUrl;
            await Debugger.LaunchAsync(plan, CancellationToken.None).ConfigureAwait(true);
            OpenBrowserLater();
            return;
        }

        var listening = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _serverListening = listening;
        await Debugger.LaunchCompoundAsync(plan, CancellationToken.None).ConfigureAwait(true);
        try
        {
            await listening.Task.WaitAsync(s_listeningWait).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            // Its logging may filter the host's messages: the page opens at the launch profile's address anyway.
            DebugLog.AppendLine($"The server didn't log the address it listens on; opening {page.Url}.");
        }
        catch (OperationCanceledException)
        {
            return; // the session ended
        }
        finally
        {
            Interlocked.CompareExchange(ref _serverListening, null, listening);
        }

        if (!Debugger.IsCompound)
        {
            return; // the session ended meanwhile
        }

        DebugLog.AppendLine($"> {browser.Display} {page.Url}");
        try
        {
            await Debugger.AddBrowserAsync(StartJsDebug(browser), BrowserArguments(browser, page.Url, WebRoot(project), page.InspectUri), CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            DebugLog.AppendLine("The page couldn't be debugged: " + ex.Message);
            Workbench.Notifications.Warning(Localizer.Format("msg.browserDebug.thePageCouldntBeDebuggedIn", "The page couldn't be debugged in {0}: {1} It opened without the debugger; the server is still debugged.", browser.Display, ex.Message));
            if (Uri.TryCreate(page.Url, UriKind.Absolute, out var uri))
            {
                OpenExternalUrl(uri);
            }
        }
    }

    /// <summary>"Now listening on: http://localhost:5000" in the server's output: the browser can open.</summary>
    private void OnServerOutput(DebugOutput output)
    {
        if (_serverListening is { } listening && output.Category == "stdout" && ListeningOn().Match(output.Text) is { Success: true } match)
        {
            listening.TrySetResult(match.Groups[1].Value);
        }
    }

    [GeneratedRegex(@"Now listening on: (\S+)")]
    private static partial Regex ListeningOn();

    // ------------------------------------------------------------------ a web page

    /// <summary>
    /// Debug a Web Page: opens a URL (or an HTML file, as a file URL) in the browser under js-debug, with no server.
    /// Without an argument it asks, suggesting the active HTML file or the last page.
    /// </summary>
    public async Task DebugWebPageAsync(string? argument = null)
    {
        if (Debugger.IsActive)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.browserDebug.aDebugSessionIsAlreadyRunning", "A debug session is already running."), TimeSpan.FromSeconds(4));
            return;
        }

        var target = argument;
        if (target is null)
        {
            var suggestion = ActiveEditor?.Model.Path is { } active && IsHtmlFile(active) ? active : _lastWebPage ?? "http://localhost:8080/";
            var answer = await Dialogs.ShowAsync(new DialogRequest("Debug a Web Page", "The page to open in the browser: an http or https URL, or an HTML file.", ["Debug", "Cancel"])
            {
                Input = suggestion,
                Validate = text => WebPageUrl(text) is null ? "Enter an http, https or file URL, or the path of an HTML file." : null,
            }).ConfigureAwait(true);
            if (answer.Button != "Debug" || answer.Input is not { } input)
            {
                return;
            }

            target = input;
        }

        if (WebPageUrl(target) is not { } url)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.browserDebug.isntAUrlOrAnHtml", "'{0}' isn't a URL or an HTML file.", target));
            return;
        }

        _lastWebPage = target;
        var webRoot = url.IsFile ? Path.GetDirectoryName(url.LocalPath)!
            : Solutions.Solution?.Directory is { } solution ? solution
            : ActiveEditor?.Model.Path is { } path && Path.IsPathRooted(path) ? Path.GetDirectoryName(path)! : Environment.CurrentDirectory;
        DebugLog.Clear();
        Layout.ShowToolWindow(DebugConsoleViewModel.ToolWindowId, focus: false);
        if (await PrepareBrowserAsync(message => Workbench.Notifications.Error(message)).ConfigureAwait(true) is not { } browser)
        {
            return;
        }

        DebugLog.AppendLine($"> {browser.Display} {url.AbsoluteUri}");
        try
        {
            await Debugger.LaunchBrowserAsync(StartJsDebug(browser), BrowserArguments(browser, url.AbsoluteUri, webRoot, null), CancellationToken.None).ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.browserDebug.couldNotDebug", "Could not debug {0}: {1}", url.AbsoluteUri, ex.Message));
        }
    }

    private static bool IsHtmlFile(string path) =>
        path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

    /// <summary>An http, https or file URL, or an existing HTML file as a file URL; else null.</summary>
    internal static Uri? WebPageUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        if ((text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file")
        {
            return uri;
        }

        // A path: an HTML file that exists.
        return IsHtmlFile(text) && File.Exists(text) ? new Uri(Path.GetFullPath(text)) : null;
    }
}
