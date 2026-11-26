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

    /// <summary>
    /// Whether a Blazor WebAssembly app's dev server (ADR 0040, 2026-10-20), and a Blazor Web App's server (#75), run under
    /// <c>dotnet watch</c> while they're debugged.
    /// </summary>
    public const string DebugHotReloadSetting = "debug.hotReload";

    private static readonly TimeSpan s_devServerTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How long a server has to log its address before the page opens at the launch profile's.</summary>
    private static readonly TimeSpan s_listeningWait = TimeSpan.FromSeconds(10);

    private RunningProgram? _browserDebugServer;

    /// <summary>The dev servers of Blazor WebAssembly apps Start New Instance added to the session (#547): stopped with it.</summary>
    private readonly List<RunningProgram> _joinedDevServers = [];

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

            List<RunningProgram> joined;
            lock (_joinedDevServers)
            {
                joined = [.. _joinedDevServers];
                _joinedDevServers.Clear();
            }

            foreach (var devServer in joined)
            {
                _ = Task.Run(devServer.Stop);
            }

            // A Blazor Web App's server under dotnet watch (#75): a Stop ends dotnet watch too; another end follows the
            // server dotnet watch starts next. Decided here, on the debugger's thread, before anything else can start.
            Volatile.Read(ref _watchedServer)?.OnDebugEnded(Debugger.StopRequested);

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

        DebugLog.AppendLine($"Debugging in {browser.Display}{(browser.Version is { } version ? " " + version : "")} ({browser.Path}) through vscode-js-debug {JsDebug.Version}.");
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

        // A profile of its own in the state folder, and the DevTools Protocol on a loopback port besides js-debug's
        // connection (js-debug keeps a port the arguments give), so an agent's browser tools drive the very page being
        // debugged and its breakpoints stop (ADR 0092, 2026-11-05). The port is the first line of DevToolsActivePort.
        var profile = DebugBrowserProfile;
        Directory.CreateDirectory(profile);
        try
        {
            File.Delete(Path.Combine(profile, "DevToolsActivePort"));
        }
        catch (IOException)
        {
            // A browser still running on that profile: the new one opens in it, as before.
        }

        _debugBrowserProfile = profile;
        string[] arguments = ["--user-data-dir=" + profile, "--remote-debugging-port=0", .. Browsers.Headless ? new[] { "--headless=new" } : []];
        return new(browser.LaunchType, url, webRoot, inspectUri, browser.Path, arguments, UserDataDir: false, Trace: trace is null ? null : new JsDebugTrace(trace));
    }

    /// <summary>The profile of the browser pages are debugged in (it keeps the page's sign-ins between sessions).</summary>
    internal string DebugBrowserProfile => Path.Combine(Paths.State, "debug-browser");

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

    /// <param name="joining">
    /// Start New Instance adds the app to the running session (#547): its page is one more program, its dev server runs
    /// without <c>dotnet watch</c> (the session's Hot Reload is its first program's), and its end ends only its page.
    /// </param>
    /// <returns>The page's index in the session's programs, or null when it didn't start.</returns>
    private async Task<int?> StartBrowserDebuggingAsync(string project, (string Url, string InspectUri) target, bool joining = false)
    {
        if (await PrepareBrowserAsync(message => Workbench.Notifications.Error(message)).ConfigureAwait(true) is not { } browser)
        {
            return null;
        }

        // Hot Reload while debugging (ADR 0040, 2026-10-20): the components run in the browser's runtime, which takes
        // dotnet watch's updates with the debugging proxy attached, unlike a program netcoredbg runs.
        var hotReload = !joining && Workbench.Settings.GetBoolean(DebugHotReloadSetting, true) && HotReloadUnsupportedReason(project) is null;
        if (joining && Workbench.Settings.GetBoolean(DebugHotReloadSetting, true))
        {
            // TODO(issue): Hot Reload for a WebAssembly app that joined the session (#547's later steps).
            DebugLog.AppendLine(Localizer.Format("msg.browserDebug.joinedWithoutHotReload", "{0} joined the session without Hot Reload: edits to it take effect after a restart.", Path.GetFileNameWithoutExtension(project)));
        }

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

        DebugLog.AppendLine($"> {browser.Display} {target.Url}");
        if (joining)
        {
            lock (_joinedDevServers)
            {
                _joinedDevServers.Add(server);
            }

            int page;
            try
            {
                page = await Debugger.LaunchBrowserAsync(StartJsDebug(browser), BrowserArguments(browser, target.Url, WebRoot(project), target.InspectUri), CancellationToken.None).ConfigureAwait(true);
            }
            catch
            {
                server.Stop();
                throw;
            }

            // The dev server stopped on its own: its page has nothing left to debug; the page closed: its server stops.
            var done = EndWithProgram(page, server);
            _ = server.Exited.ContinueWith(_ => Ui.Post(() =>
            {
                if (!Volatile.Read(ref done.Value))
                {
                    _ = EndDebuggedProcessAsync(page, detach: false);
                }
            }), TaskScheduler.Default);
            return page;
        }

        _browserDebugServer = server;
        await Debugger.LaunchBrowserAsync(StartJsDebug(browser), BrowserArguments(browser, target.Url, WebRoot(project), target.InspectUri), CancellationToken.None).ConfigureAwait(true);

        // The dev server stopped on its own: the page has nothing left to debug.
        _ = server.Exited.ContinueWith(_ => Ui.Post(() =>
        {
            if (_browserDebugServer == server)
            {
                _ = Debugger.StopAsync();
            }
        }), TaskScheduler.Default);
        return 0;
    }

    /// <summary>
    /// Stops <paramref name="server"/> once the session's program at <paramref name="index"/> has ended (a joined page
    /// closed, #547), or the session did; the box says when it has, so the index is no longer the page's.
    /// </summary>
    private System.Runtime.CompilerServices.StrongBox<bool> EndWithProgram(int index, RunningProgram server)
    {
        var done = new System.Runtime.CompilerServices.StrongBox<bool>();
        void Check(object? sender, EventArgs e)
        {
            var processes = Debugger.Processes;
            if (Debugger.State == DebugState.Idle || (index < processes.Count && processes[index].Ended))
            {
                Debugger.ProcessesChanged -= Check;
                Debugger.StateChanged -= Check;
                Volatile.Write(ref done.Value, true);
                if (!server.HasExited)
                {
                    _ = Task.Run(server.Stop);
                }
            }
        }

        Debugger.ProcessesChanged += Check;
        Debugger.StateChanged += Check;
        return done;
    }

    // ------------------------------------------------------------------ server and page together (ADR 0027)

    /// <summary>
    /// Starts <paramref name="plan"/> under netcoredbg, waits for it to listen, then opens <paramref name="page"/> in
    /// the browser in the same session. Without a browser the server debugs alone (and the profile's browser opens as
    /// usual); a WebAssembly app says why its components won't stop.
    /// </summary>
    /// <param name="joining">
    /// Start New Instance adds the app to the running session (#547): the server and its page are one program of it,
    /// and the page opens once the server's address answers (the session's other programs write to the Debug Console too).
    /// </param>
    /// <param name="stopAtEntry">Step Into New Instance: the server stops at its entry point.</param>
    /// <returns>The app's index in the session's programs.</returns>
    private async Task<int> StartCompoundDebuggingAsync(string project, LaunchPlan plan, (string Url, string? InspectUri) page, bool joining = false, bool stopAtEntry = false)
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
            if (joining)
            {
                var (alone, _) = Debugger.AddLaunch(new DebugLaunch(Path.GetFileNameWithoutExtension(project), plan) { StopAtEntry = stopAtEntry }, CancellationToken.None);
                OpenJoinedBrowserLater(plan.BrowserUrl);
                return alone;
            }

            _pendingBrowserUrl = plan.BrowserUrl;
            await Debugger.LaunchAsync(plan, CancellationToken.None).ConfigureAwait(true);
            OpenBrowserLater();
            return 0;
        }

        if (joining)
        {
            var group = await Debugger.LaunchCompoundAsync(plan, CancellationToken.None, stopAtEntry).ConfigureAwait(true);
            if (Uri.TryCreate(page.Url, UriKind.Absolute, out var address)
                && !await ServerProbe.WaitUntilListeningAsync(address, s_devServerTimeout, () => Debugger.IsActive && !Debugger.Processes[group].Ended, CancellationToken.None).ConfigureAwait(true))
            {
                if (!Debugger.IsActive || Debugger.Processes[group].Ended)
                {
                    return group; // it ended meanwhile
                }

                DebugLog.AppendLine($"The server didn't answer at {page.Url}; opening it anyway.");
            }

            await AddPageAsync(project, browser, page, group).ConfigureAwait(true);
            return group;
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
            return 0; // the session ended
        }
        finally
        {
            Interlocked.CompareExchange(ref _serverListening, null, listening);
        }

        if (!Debugger.IsCompound)
        {
            return 0; // the session ended meanwhile
        }

        await AddPageAsync(project, browser, page).ConfigureAwait(true);
        return 0;
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
