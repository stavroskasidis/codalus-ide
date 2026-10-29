using System.Globalization;
using Codalus.Debugging;
using Codalus.Rpc;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// Hot Reload while debugging a Blazor Web App (#75, ADR 0040): Start Debugging runs the server under <c>dotnet watch</c>
/// (as Start with Hot Reload does), attaches netcoredbg to the app process <c>dotnet watch</c> starts, and opens the page
/// under vscode-js-debug in the same session. Edits to WebAssembly components apply in the browser while both are
/// debugged; the server's runtime refuses edits under the debugger, and an edit that needs a restart restarts the app,
/// which the session follows (<see cref="WatchedServerSession"/>).
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>How long the server under <c>dotnet watch</c> has to build, start and serve.</summary>
    private static readonly TimeSpan s_watchedStartTimeout = TimeSpan.FromMinutes(5);

    /// <summary>How long the session waits for the server <c>dotnet watch</c> starts again after a restart.</summary>
    private static readonly TimeSpan s_watchedFollowTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The breakpoint message of a file the server runs older code of.</summary>
    internal const string StaleServerBreakpoint = "The server runs the code it started with until a restart (Ctrl+Shift+F5); the page still stops here.";

    private WatchedServerSession? _watchedServer;
    private RunningProgram? _watchedProgram;

    /// <summary>The Blazor Web App server debugged under <c>dotnet watch</c>, while one is (tests).</summary>
    public WatchedServerSession? WatchedServerDebugSession => Volatile.Read(ref _watchedServer);

    /// <summary>Whether Start Debugging gives this Blazor Web App's server Hot Reload, and why not (#75).</summary>
    private async Task<HotReloadDebugDecision> HotReloadDebugDecisionAsync(string project)
    {
        if (!BlazorProjects.IsWebAppServer(project, Solutions.Projects))
        {
            return new HotReloadDebugDecision(HotReloadDebugMode.NotApplicable);
        }

        var setting = Workbench.Settings.GetBoolean(DebugHotReloadSetting, true);
        string? sdk = null;
        if (setting)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                sdk = (await ProjectManagement.GetSdkInfoAsync(timeout.Token).ConfigureAwait(true)).ResolvedVersion;
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException or IOException)
            {
                DebugLog.AppendLine("The solution's .NET SDK version couldn't be read: " + ex.Message);
            }
        }

        return HotReloadDebugChoice.Decide(true, setting, HotReloadUnsupportedReason(project), sdk,
            HotReloadDebugChoice.StartupBreakpoint(project, Debugger.Breakpoints.All), HotReloadDebugChoice.CurrentPlatformFallback);
    }

    /// <summary>
    /// F5 on a Blazor Web App with Hot Reload (#75): the server under <c>dotnet watch</c>, the debugger attached to it once
    /// it serves, its page joining. Without a browser the server debugs alone (launched, as before); when the server can't
    /// be found or attached to (or <paramref name="pageOnly"/>), the page debugs alone with Hot Reload.
    /// </summary>
    private async Task StartWatchedServerDebuggingAsync(string project, LaunchPlan plan, (string Url, string? InspectUri) page, bool pageOnly, string? reason)
    {
        var browser = await PrepareBrowserAsync(message =>
        {
            DebugLog.AppendLine(message);
            Workbench.Notifications.Warning(message + " The server debugs alone: breakpoints in WebAssembly components won't bind.");
        }).ConfigureAwait(true);
        if (browser is null)
        {
            _pendingBrowserUrl = plan.BrowserUrl;
            await Debugger.LaunchAsync(plan, CancellationToken.None).ConfigureAwait(true);
            OpenBrowserLater();
            return;
        }

        var name = Path.GetFileNameWithoutExtension(project);
        DebugLog.AppendLine($"Hot Reload while debugging: {name} runs under dotnet watch (Output → Run: {name}); the debugger attaches to it once it serves.");
        var watch = await Runs.StartAsync(project, externalConsole: false, CancellationToken.None, hotReload: true, openBrowser: false, announceProcess: true).ConfigureAwait(true);
        var hotReload = watch.HotReload!;
        Volatile.Write(ref _watchedProgram, watch);
        OnHotReloadStarted(watch);
        if (pageOnly)
        {
            await StartWatchedPageAloneAsync(project, watch, browser, page, reason!).ConfigureAwait(true);
            return;
        }

        var host = new WatchedServerHost(this, project, name, plan, page, browser, watch);
        var session = new WatchedServerSession(hotReload, watch.Exited, host, s_watchedFollowTimeout);
        host.Session = session;
        Interlocked.Exchange(ref _watchedServer, session)?.Dispose();
        hotReload.Refused += () => Ui.Post(() => OnServerRefusedUpdates(session, project, name));
        hotReload.FilesUpdated += files =>
        {
            // Under the Codalus debugger the server takes the edits in place (M55): its breakpoints bind in the new code.
            if (session.State == WatchedServerState.Attached && !DebugEdits.IsActive)
            {
                _ = Debugger.ExcludeFromServerAsync(files, StaleServerBreakpoint);
            }
        };
        hotReload.AppOutput += line =>
        {
            // The debugger didn't launch the server, so its console isn't the Debug Console's: mirrored from Run.
            if (session.State == WatchedServerState.Attached)
            {
                DebugLog.AppendLine(line);
            }
        };

        if (await session.StartAsync(s_watchedStartTimeout, CancellationToken.None).ConfigureAwait(true))
        {
            return;
        }

        Interlocked.CompareExchange(ref _watchedServer, null, session);
        session.Dispose();
        if (watch.HasExited)
        {
            throw new InvalidOperationException($"dotnet watch ended before the server served (see Output → Run: {name}).");
        }

        await StartWatchedPageAloneAsync(project, watch, browser, page,
            Localizer.Format("msg.browserDebug.serverNotAttached", "The debugger couldn't attach to {0}, which dotnet watch started, so only its page is debugged: breakpoints in the server won't bind. Turn off 'Hot Reload while debugging' (debug.hotReload) to debug the server.", name)).ConfigureAwait(true);
    }

    /// <summary>The fallback: the server stays under <c>dotnet watch</c> (Hot Reload) and only its page is debugged.</summary>
    private async Task StartWatchedPageAloneAsync(string project, RunningProgram watch, Browser browser, (string Url, string? InspectUri) page, string reason)
    {
        // The server isn't debugged: it takes updates, and its restarts are Start with Hot Reload's.
        Interlocked.CompareExchange(ref _watchedProgram, null, watch);
        DebugLog.AppendLine(reason);
        Workbench.Notifications.Warning(reason);
        try
        {
            await watch.Listening.WaitAsync(s_watchedStartTimeout).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            if (watch.HasExited)
            {
                throw new InvalidOperationException($"The server didn't start listening (see Output → Run: {watch.Name}).", ex);
            }

            DebugLog.AppendLine($"The server didn't log the address it listens on; opening {page.Url}.");
        }

        _browserDebugServer = watch;
        DebugLog.AppendLine($"> {browser.Display} {page.Url}");
        await Debugger.LaunchBrowserAsync(StartJsDebug(browser), BrowserArguments(browser, page.Url, WebRoot(project), page.InspectUri), CancellationToken.None).ConfigureAwait(true);
        _ = watch.Exited.ContinueWith(_ => Ui.Post(() =>
        {
            if (_browserDebugServer == watch)
            {
                _ = Debugger.StopAsync();
            }
        }), TaskScheduler.Default);
    }

    /// <summary>The page joins the compound session, or opens without the debugger when it can't (the server stays debugged).</summary>
    private async Task AddPageAsync(string project, Browser browser, (string Url, string? InspectUri) page)
    {
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
                TryOpenExternalUrl(uri);
            }
        }
    }

    /// <summary>Opens a page in the default browser; a failure (no opener) goes to the Debug output instead of ending the IDE.</summary>
    private void TryOpenExternalUrl(Uri uri)
    {
        try
        {
            OpenExternalUrl(uri);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            DebugLog.AppendLine($"The browser couldn't be opened at {uri}: {ex.Message}");
        }
    }

    /// <summary>The server's runtime refused an update under the debugger: said once per session, whenever it comes (it can come only once the server runs again).</summary>
    private void OnServerRefusedUpdates(WatchedServerSession session, string project, string name)
    {
        if (!session.IsActive)
        {
            return;
        }

        if (DebugEdits.IsActive)
        {
            // The Codalus debugger applies the server's edits itself (M55); dotnet watch's own copy is the one refused.
            DebugLog.AppendLine(Localizer.Format("msg.debugHotReload.serverTakesEdits",
                "Edits apply to {0}'s server through the Codalus debugger, and to its WebAssembly components in the browser, as you save. Edits that need a restart restart the app.", name));
            return;
        }

        var message = Localizer.Format("msg.browserDebug.serverKeepsItsCode",
            "Edits to {0}'s WebAssembly components apply in the browser as you save. Its server keeps the code it started with until you restart (Ctrl+Shift+F5): the .NET runtime doesn't apply edits to a program the debugger is attached to. Edits that need a restart restart the app.", name);
        if (RunsServerInteractivity(project))
        {
            message += " " + Localizer.Get("msg.browserDebug.serverComponentsKeepTheirCode", "Components that run on the server (InteractiveServer, or InteractiveAuto before WebAssembly loads) keep their old code too.");
        }

        DebugLog.AppendLine(message);
        var restart = Localizer.Get("msg.browserDebug.restart", "Restart");
        _ = Workbench.Notifications.Info(message, restart).Result.ContinueWith(t =>
        {
            if (t.Result == restart)
            {
                Ui.Post(() => _ = RestartDebuggingAsync());
            }
        }, TaskScheduler.Default);
    }

    /// <summary>Whether the server runs components interactively itself (InteractiveServer, InteractiveAuto): its Program.cs registers it.</summary>
    private static bool RunsServerInteractivity(string project)
    {
        try
        {
            var program = Path.Combine(Path.GetDirectoryName(project)!, "Program.cs");
            return File.Exists(program) && File.ReadAllText(program).Contains("AddInteractiveServer", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>What the session reports, shown in the IDE.</summary>
    private void OnWatchedServerChanged(WatchedServerSession session, string name, WatchedServerChange change)
    {
        switch (change.Kind)
        {
            case WatchedServerChangeKind.Following:
                DebugLog.AppendLine($"dotnet watch is restarting {name} for an edit it couldn't apply; the debugger left it and attaches to the next one.");
                StatusBar.ShowMessage(Localizer.Format("msg.browserDebug.restartingWithTheApp", "Restarting with {0}…", name), s_watchedFollowTimeout);
                break;
            case WatchedServerChangeKind.Followed:
                DebugLog.AppendLine($"The debugger attached to {name} again (process {change.ProcessId?.ToString(CultureInfo.InvariantCulture)}).");
                StatusBar.ShowMessage(Localizer.Format("msg.browserDebug.debuggingAgain", "Debugging {0} again.", name), TimeSpan.FromSeconds(4));
                Workbench.Notifications.Info(Localizer.Format("msg.browserDebug.yourEditNeededARestart", "Your edit needed a restart, so {0} restarted; debugging goes on in a new browser window.", name));
                break;
            case WatchedServerChangeKind.OrphanEnded:
                DebugLog.AppendLine($"Ended {name} (process {change.ProcessId?.ToString(CultureInfo.InvariantCulture)}) left over from before the restart: the debugger held it past dotnet watch's restart.");
                break;
            case WatchedServerChangeKind.Ended:
                Interlocked.CompareExchange(ref _watchedServer, null, session);
                switch (change.End)
                {
                    case WatchedServerEnd.AppExited:
                        Workbench.Notifications.Info(Localizer.Format("msg.browserDebug.theAppExited", "{0} exited, so debugging stopped (see Output → Run: {0}).", name));
                        break;
                    case WatchedServerEnd.NotRestarted:
                        Workbench.Notifications.Warning(Localizer.Format("msg.browserDebug.didntRestart", "dotnet watch didn't start {0} again after a restart, so debugging stopped (see Output → Run: {0}).", name));
                        break;
                    case WatchedServerEnd.AttachFailed:
                        Workbench.Notifications.Warning(Localizer.Format("msg.browserDebug.couldntAttachAgain", "The debugger couldn't attach to {0} after it restarted, so debugging stopped.", name));
                        break;
                    case WatchedServerEnd.WatchExited:
                        DebugLog.AppendLine($"dotnet watch ended, so debugging {name} stopped.");
                        break;
                }

                session.Dispose();
                break;
        }
    }

    /// <summary>The IDE's side of a <see cref="WatchedServerSession"/>.</summary>
    private sealed class WatchedServerHost(ShellHost shell, string project, string name, LaunchPlan plan, (string Url, string? InspectUri) page, Browser browser, RunningProgram watch) : IWatchedServerHost
    {
        public WatchedServerSession? Session { get; set; }

        private WatchedApp App => new(
            watch.HotReload!,
            watch.Exited,
            watch.ProcessId,
            WatchedServer.AppNameOf(plan),
            Uri.TryCreate(page.Url, UriKind.Absolute, out var address) ? address : null);

        public Task<int?> WaitForAppAsync(int? previousPid, CancellationToken cancellationToken) =>
            WatchedServer.WaitForAppAsync(App, previousPid, s_watchedStartTimeout, cancellationToken);

        public async Task<bool> AttachAsync(int processId, CancellationToken cancellationToken)
        {
            var attached = false;
            await Ui.RunAsync(async () =>
            {
                if (shell.Debugger.IsActive)
                {
                    return;
                }

                try
                {
                    shell.Debugger.JustMyCode = shell.Workbench.Settings.GetBoolean(JustMyCodeSetting, true);
                    shell.Debugger.StepFiltering = shell.Workbench.Settings.GetBoolean(StepFilteringSetting, true);
                    await shell.Debugger.AttachOwnedCompoundAsync(processId, name, plan, cancellationToken).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or JsonRpcRemoteException or JsonRpcConnectionLostException or IOException)
                {
                    shell.DebugLog.AppendLine($"The debugger couldn't attach to {name} (process {processId.ToString(CultureInfo.InvariantCulture)}): {ex.Message}");
                    return;
                }

                attached = true;
                shell.DebugLog.AppendLine($"The debugger attached to {name} (process {processId.ToString(CultureInfo.InvariantCulture)}), which dotnet watch started: saving a WebAssembly component (or Apply Code Changes) updates it in the browser while you debug. Server code that already ran (its startup) doesn't stop at breakpoints.");
                shell.DebugLog.AppendLine($"The server's output shows here and in Output → Run: {name}.");
                if (shell.Debugger.IsCompound)
                {
                    await shell.AddPageAsync(project, browser, page).ConfigureAwait(true);
                }
            }).ConfigureAwait(false);
            return attached;
        }

        public Task ReleaseAsync() => shell.Debugger.ReleaseOwnedServerAsync();

        public async Task StopDebuggingAsync()
        {
            if (shell.Debugger.IsActive)
            {
                await shell.Debugger.StopAsync().ConfigureAwait(false);
            }
        }

        public Task StopWatchAsync() => watch.StopAsync();

        public bool IsAlive(int processId) => WatchedServer.IsRunning(processId);

        public void Kill(int processId) => WatchedServer.Kill(processId);

        public void OnChanged(WatchedServerChange change)
        {
            if (Session is { } session)
            {
                Ui.Post(() => shell.OnWatchedServerChanged(session, name, change));
            }
        }
    }
}
