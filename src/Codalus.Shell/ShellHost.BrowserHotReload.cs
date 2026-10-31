using Codalus.Rpc;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// F5 on a Blazor Web App under the Codalus debugger (ADR 0090): the debugger launches the server itself, without
/// <c>dotnet watch</c>, and applies its edits; the IDE is the browser refresh server the SDK's middleware points the
/// pages at, and sends them the edits to code that runs in the browser (<see cref="BrowserRefreshServer"/>). Its
/// startup code stops at breakpoints, and an edit that needs a restart asks, as for other programs. netcoredbg keeps
/// <c>dotnet watch</c> (<c>ShellHost.WatchedServer.cs</c>).
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>
    /// Debugs a Blazor Web App's server and its page with edits going to both, and true; false when this isn't a Blazor
    /// Web App the Codalus debugger runs (<paramref name="plan"/> goes the usual way). An app whose browser can't take
    /// edits (older than .NET 10, or <c>debug.hotReload</c> off) debugs the same way, and its WebAssembly edits say they
    /// need a restart.
    /// </summary>
    private async Task<bool> TryStartBrowserHotReloadDebuggingAsync(string project, LaunchPlan plan, (string Url, string? InspectUri) page)
    {
        if (!Debugger.WillUseCodalusAdapter(plan) || !BlazorProjects.IsWebAppServer(project, Solutions.Projects))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(project);
        var browserProjects = BlazorProjects.BrowserProjectsOf(project, Solutions.Projects);
        var frameworks = BlazorProjects.ClientsOf(project, Solutions.Projects)
            .Select(client => Solutions.ActiveFramework(client) ?? Solutions.Projects[client].TargetFrameworks.FirstOrDefault())
            .ToList();
        string? middleware = null;
        string? reason;
        if (!Workbench.Settings.GetBoolean(DebugHotReloadSetting, true))
        {
            reason = "Hot Reload while debugging (debug.hotReload) is off";
        }
        else
        {
            string? version = null;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var sdk = await ProjectManagement.GetSdkInfoAsync(timeout.Token).ConfigureAwait(true);
                version = sdk.ResolvedVersion;
                middleware = BrowserHotReload.FindMiddleware(sdk.ResolvedSdk);
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException or IOException)
            {
                DebugLog.AppendLine("The solution's .NET SDK version couldn't be read: " + ex.Message);
            }

            reason = BrowserHotReload.UnsupportedReason(frameworks, version, middleware);
        }

        if (reason is not null)
        {
            DebugEdits.UseBrowser(new BrowserHotReload(browserProjects, null, null, reason));
            DebugLog.AppendLine(Localizer.Format("msg.debugHotReload.browserNeedsRestart",
                "Edits to {0}'s server apply while you debug; edits to its WebAssembly components reach the browser after a restart: {1}.", name, reason));
            await StartCompoundDebuggingAsync(project, plan, page).ConfigureAwait(true);
            return true;
        }

        var server = BrowserRefreshServer.Start();
        server.Log += line => Ui.Post(() => DebugLog.AppendLine(line));
        server.LateAnswer += answer => Ui.Post(() => OnLateBrowserAnswer(answer));
        var major = frameworks.Min(BrowserHotReload.FrameworkMajor) ?? 10;
        DebugEdits.UseBrowser(new BrowserHotReload(browserProjects, server, BrowserHotReload.WebAssemblyCapabilities(major)));
        DebugLog.AppendLine(Localizer.Format("msg.debugHotReload.serverAndBrowser",
            "Hot Reload while debugging: edits apply to {0}'s server through the Codalus debugger, and to its WebAssembly components in the browser.", name));
        try
        {
            await StartCompoundDebuggingAsync(project, plan with { Environment = BrowserHotReload.Environment(plan.Environment, middleware!, server.Environment()) }, page).ConfigureAwait(true);
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(true);
            throw;
        }

        return true;
    }

    /// <summary>The status bar's words for edits that applied: to the server, and what the browser made of them (ADR 0090).</summary>
    private string AppliedStatus(HotReloadBrowserOutcome? browser, string time) => browser switch
    {
        null => Localizer.Format("status.debugHotReload.applied", "Hot Reload: applied at {0}", time),
        { Unreachable: true } => Localizer.Format("status.debugHotReload.browserNeedsRestart", "Hot Reload: applied to the server at {0}; the browser needs a restart", time),
        { Errors.Count: > 0 } => Localizer.Format("status.debugHotReload.browserFailed", "Hot Reload: applied to the server at {0}; the browser couldn't apply it", time),
        { Pages: 0 } => Localizer.Format("status.debugHotReload.noPage", "Hot Reload: applied at {0} (no page open)", time),
        { Unanswered: > 0 } => Localizer.Format("status.debugHotReload.sentToBrowser", "Hot Reload: applied to the server at {0}, sent to the browser", time),
        _ => Localizer.Format("status.debugHotReload.serverAndBrowser", "Hot Reload: applied at {0} (server and browser)", time),
    };

    /// <summary>
    /// What an applied edit's handlers and pages said, in the Debug Console; a page that couldn't apply it runs other code
    /// than the server now, so a warning offers Restart (not waited for: Continue doesn't wait on it).
    /// </summary>
    private void ReportApplied(HotReloadOutcome outcome)
    {
        foreach (var error in outcome.HandlerErrors)
        {
            DebugLog.AppendLine(Localizer.Format("msg.debugHotReload.handlerFailed", "Hot Reload: the program's update handlers failed: {0}", error));
        }

        switch (outcome.Browser)
        {
            case { Unreachable: true } when !_debugEditsBrowserSaid:
                _debugEditsBrowserSaid = true;
                DebugLog.AppendLine(Localizer.Format("msg.debugHotReload.browserUnreachable",
                    "Hot Reload: the edit changed code that runs in the browser, which gets it after a restart (Ctrl+Shift+F5): {0}.", DebugEdits.Browser?.Reason ?? string.Empty));
                break;
            case { Errors.Count: > 0 } failed:
                ReportBrowserFailed(failed.Errors);
                break;
        }
    }

    /// <summary>The page's errors in the Debug Console, and a warning that offers Restart (not waited for).</summary>
    private void ReportBrowserFailed(IEnumerable<string> errors)
    {
        foreach (var error in errors)
        {
            DebugLog.AppendLine(Localizer.Format("msg.debugHotReload.browserError", "Hot Reload: the browser couldn't apply the edit: {0}", error));
        }

        var restart = Localizer.Get("action.debugHotReload.restart", "Restart");
        _ = Workbench.Notifications.Warning(
            Localizer.Get("msg.debugHotReload.browserFailed", "The server took the edit, but the browser couldn't apply it, so the two run different code now (see the Debug Console). Restart to run the edit in both."),
            restart).Result.ContinueWith(t =>
        {
            if (t.Result == restart)
            {
                Ui.Post(() => _ = RestartDebuggingAsync());
            }
        }, TaskScheduler.Default);
    }

    /// <summary>A page answered an edit after the apply stopped waiting for it: the status says so, and a failure is reported as one.</summary>
    private void OnLateBrowserAnswer(BrowserLateAnswer answer)
    {
        if (!DebugEdits.IsActive)
        {
            return;
        }

        if (answer.Error is { } error)
        {
            _debugEditsStatus = Localizer.Format("status.debugHotReload.browserFailed", "Hot Reload: applied to the server at {0}; the browser couldn't apply it", _debugEditsTime ?? string.Empty);
            ReportBrowserFailed([error]);
        }
        else
        {
            _debugEditsStatus = Localizer.Format("status.debugHotReload.serverAndBrowser", "Hot Reload: applied at {0} (server and browser)", _debugEditsTime ?? string.Empty);
            DebugLog.AppendLine(Localizer.Get("msg.debugHotReload.browserApplied", "Hot Reload: the browser applied the code changes."));
        }

        UpdateHotReloadStatus();
    }
}
