using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// Solution Explorer's Debug ▸ submenu (#547, ADR 0109): Start New Instance and Step Into New Instance start the
/// selected project under the debugger, in the running session when there is one, as the classic IDE does, and Start
/// Without Debugging runs it. With no session the project starts as F5 starts it; otherwise it joins the session as one
/// more program, and Restart brings it back with the others.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>
    /// The project a session began with when Start New Instance began it (Restart starts it again, not the startup
    /// project); null for F5's. Set on the UI thread.
    /// </summary>
    private string? _sessionProject;

    /// <summary>The projects Start New Instance added to the running session, in order, for Restart. The UI thread's.</summary>
    private readonly List<JoinedInstance> _joinedInstances = [];

    /// <summary>A project that joined the session, at <see cref="Index"/> in <see cref="DebugService.Processes"/>.</summary>
    private sealed record JoinedInstance(string Project, int Index);

    /// <summary>The projects Start New Instance added to the running session (tests, the smoke test).</summary>
    internal IReadOnlyList<string> JoinedInstanceProjects => [.. _joinedInstances.Select(i => i.Project)];

    /// <summary>
    /// Debug ▸ Start New Instance (<paramref name="stopAtEntry"/> false) and Step Into New Instance (true): the project
    /// (the argument, or the one selected) under the debugger. With no session it starts as F5 starts it, in a session
    /// others can join; with one, it joins it, when both the session and the project are of a kind that can.
    /// </summary>
    public async Task StartNewInstanceAsync(string? argument, bool stopAtEntry)
    {
        if (CurrentProject(argument) is not { } project)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.newInstance.selectAProject", "Select a project to start."), TimeSpan.FromSeconds(4));
            return;
        }

        if (!Debugger.IsActive && WatchedServerDebugSession is { IsFollowing: true })
        {
            // Between two servers of a Blazor Web App that dotnet watch restarted (#75): the session comes back by itself.
            StatusBar.ShowMessage(Localizer.Get("msg.debug.followingTheRestart", "Debugging goes on once the app has restarted…"), TimeSpan.FromSeconds(4));
            return;
        }

        if (BeginLaunch(project, debug: true) is not { } launch)
        {
            return; // another start is under way (it says so)
        }

        using (launch)
        {
            launch.StopAtEntry = stopAtEntry;
            if (!Debugger.IsActive)
            {
                await StartDebuggingAsync(project, launch).ConfigureAwait(true);
                if (Debugger.IsActive)
                {
                    _sessionProject = project;
                }

                return;
            }

            if (SessionCannotJoinReason() is { } reason)
            {
                Workbench.Notifications.Warning(Localizer.Format("msg.newInstance.sessionCannotJoin", "{0} can't join the running debug session: {1} Stop it to debug {0}, or start {0} without debugging.", Path.GetFileNameWithoutExtension(project), reason));
                return;
            }

            await JoinSessionAsync(project, launch).ConfigureAwait(true);
        }
    }

    /// <summary>Debug ▸ Start Without Debugging in Solution Explorer: the selected project (Ctrl+F5 runs the startup project).</summary>
    public Task StartProjectWithoutDebuggingAsync(string? argument)
    {
        if (CurrentProject(argument) is not { } project)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.newInstance.selectAProject", "Select a project to start."), TimeSpan.FromSeconds(4));
            return Task.CompletedTask;
        }

        return StartWithoutDebuggingAsync(project);
    }

    /// <summary>
    /// Why no program can join the running session, or null when one can: a session of independent programs (F5 of a
    /// project, multiple startup projects, an AppHost's, compose's) takes more. TODO(issue): #547's later steps add
    /// programs to an attach, a remote window's, a web app's server and page, and a page's (ADR 0109).
    /// </summary>
    private string? SessionCannotJoinReason()
    {
        if (Debugger.CanAddLaunch)
        {
            return null;
        }

        if (Debugger.Processes.Count >= Codalus.Debugging.CompoundDebugAdapter.MaxParts)
        {
            return Localizer.Get("msg.newInstance.sessionFull", "it holds as many programs as a session can.");
        }

        if (WatchedServerDebugSession is not null)
        {
            // TODO(issue): dotnet watch restarts the server by ending the session and starting it again (#75).
            return Localizer.Get("msg.newInstance.sessionWatched", "its web app's server runs under dotnet watch, which ends the session to restart it (netcoredbg's Hot Reload while debugging).");
        }

        return Localizer.Get("msg.newInstance.sessionStarting", "it's still starting or ending.");
    }

    /// <summary>
    /// Builds <paramref name="project"/> and adds it to the running session: a program on this machine or in WSL as one
    /// more program, and every other kind (a web app, a device's, a packaged app, a remote window's, an AppHost, a
    /// container profile) through F5's own start for it, which joins the session. When the session ended during the
    /// build, the project starts a session of its own.
    /// </summary>
    private async Task JoinSessionAsync(string project, Launch launch)
    {
        var name = Path.GetFileNameWithoutExtension(project);
        if (ProjectCannotJoinReason(project) is { } kind)
        {
            Workbench.Notifications.Warning(Localizer.Format("msg.newInstance.projectCannotJoin", "{0} can't join the running debug session: {1} Stop the session to debug it, or start it without debugging.", name, kind));
            return;
        }

        if (Remote is null && IsMobileProject(project))
        {
            // A .NET MAUI app on its device: its platform's build deploys it, and the Mono debugger joins the session.
            await JoinAsF5Async(project, () => StartOnDeviceAsync(project, debug: true)).ConfigureAwait(true);
            return;
        }

        if (WarnIfWindowsOnly(project))
        {
            return; // a WinForms, WPF, WinUI or .NET Framework app off Windows
        }

        if (Remote is null && OperatingSystem.IsWindows() && Solutions.Projects.TryGetValue(project, out var packaged) && packaged.UsesWinUI && RunsAsPackage(packaged))
        {
            // A packaged WinUI app: registered and started by Windows, then attached to in the session.
            await JoinAsF5Async(project, () => TryStartPackagedAppAsync(project, debug: true)).ConfigureAwait(true);
            return;
        }

        if (!await BuildBeforeRunAsync(project).ConfigureAwait(true) || launch.IsCanceled)
        {
            if (!launch.IsCanceled && Builds.LastBuild is { Result: { Canceled: false, Errors: > 0 } })
            {
                StatusBar.ShowMessage(Localizer.Format("msg.newInstance.buildFailed", "The build failed, so {0} did not start.", name), TimeSpan.FromSeconds(6));
            }

            return;
        }

        DebugLaunch? debugLaunch;
        string? browserUrl;
        try
        {
            var (info, profiles, error) = await Runs.GetProfilesAsync(project, CancellationToken.None).ConfigureAwait(true);
            var profile = Runs.ResolveProfile(project, profiles);
            if (Remote is { } remote)
            {
                // A remote window's project: the debugger there, through its server.
                await JoinAsF5Async(project, () => StartRemoteAsync(remote, project, info, profile, debug: true)).ConfigureAwait(true);
                return;
            }

            if (IsAspireHost(project))
            {
                // An AppHost: it joins, and so do its projects, as F5's do.
                await JoinAsF5Async(project, () => StartAspireAsync(project, debug: true)).ConfigureAwait(true);
                return;
            }

            if (IsContainerProfile(profile))
            {
                // Its "Container (Dockerfile)" profile: the container's program joins, launched or attached to there.
                await JoinAsF5Async(project, () => StartInContainerAsync(project, info, profile!, debug: true)).ConfigureAwait(true);
                return;
            }

            var plan = await Runs.LaunchPlanAsync(info, profile, CancellationToken.None).ConfigureAwait(true);
            if (error is not null)
            {
                DebugLog.AppendLine($"[{name}] Ignoring the launch settings: {error}");
            }

            if (plan.Wsl is null && await JoinWebAppAsync(project, info, profile, plan, launch).ConfigureAwait(true))
            {
                return; // a web app's page, with its server or its dev server (#547's web step)
            }

            debugLaunch = plan.Wsl is not null
                ? await WslLaunchAsync(name, plan).ConfigureAwait(true)
                : new DebugLaunch(name, plan) { Console = profile?.CommandName == LaunchProfile.RoslynComponent ? DebugConsoleKind.Internal : ConsoleFor(info) };
            if (debugLaunch is null || launch.IsCanceled)
            {
                return;
            }

            debugLaunch = debugLaunch with { StopAtEntry = launch.StopAtEntry, Project = project };
            DebugLog.AppendLine($"[{name}] > {plan.FileName} {plan.Arguments}".TrimEnd());
            browserUrl = plan.BrowserUrl;
        }
        catch (RunNotStartedException)
        {
            return; // said already, with what to do (.NET to install in WSL)
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or IOException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.newInstance.couldNotStart", "Could not start {0}: {1}", name, ex.Message));
            return;
        }

        Layout.ShowToolWindow(DebugConsoleViewModel.ToolWindowId, focus: false);
        try
        {
            if (Debugger.CanAddLaunch)
            {
                var (index, joined) = Debugger.AddLaunch(debugLaunch, CancellationToken.None);
                _joinedInstances.Add(new JoinedInstance(project, index));
                OpenJoinedBrowserLater(browserUrl);
                StatusBar.ShowMessage(Localizer.Format("msg.newInstance.joined", "{0} joined the debug session.", name), TimeSpan.FromSeconds(4));
                _ = joined.ContinueWith(
                    t => Ui.Post(() =>
                    {
                        if (!t.Result)
                        {
                            StatusBar.ShowMessage(Localizer.Format("msg.newInstance.didNotStart", "{0} didn't start: see the Debug Console for why.", name), TimeSpan.FromSeconds(6));
                        }
                    }),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnRanToCompletion,
                    TaskScheduler.Default);
                return;
            }

            if (Debugger.IsActive)
            {
                // Another kind of session started meanwhile.
                Workbench.Notifications.Warning(Localizer.Format("msg.newInstance.sessionCannotJoin", "{0} can't join the running debug session: {1} Stop it to debug {0}, or start {0} without debugging.", name, SessionCannotJoinReason() ?? string.Empty));
                return;
            }

            // The session ended during the build: the project is the next one's first program.
            Debugger.JustMyCode = Workbench.Settings.GetBoolean(JustMyCodeSetting, true);
            Debugger.StepFiltering = Workbench.Settings.GetBoolean(StepFilteringSetting, true);
            await Debugger.LaunchAsync([debugLaunch], CancellationToken.None, joinable: true).ConfigureAwait(true);
            _sessionProject = project;
            OpenJoinedBrowserLater(browserUrl);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or IOException or JsonRpcRemoteException or JsonRpcConnectionLostException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.newInstance.couldNotDebug", "Could not start debugging {0}: {1}", name, ex.Message));
        }
    }

    /// <summary>
    /// A joined web app's browser (its launch profile's <c>launchBrowser</c>), once its address answers: its output may
    /// not reach the Debug Console (a console window of its own, Windows' default), where F5's waits for "Now listening
    /// on" too.
    /// </summary>
    private void OpenJoinedBrowserLater(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var address))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            if (await ServerProbe.WaitUntilListeningAsync(address, TimeSpan.FromMinutes(2), () => Debugger.State != DebugState.Idle, CancellationToken.None).ConfigureAwait(false))
            {
                Ui.Post(() => TryOpenExternalUrl(address));
            }
        });
    }

    /// <summary>
    /// Why <paramref name="project"/> can't join a running session, before it's built, or null: a second AppHost (one
    /// runs at a time: its resources, its dashboard and the IDE's endpoint are its own).
    /// </summary>
    private string? ProjectCannotJoinReason(string project) =>
        IsAspireHost(project) && _aspire is not null
            ? Localizer.Get("msg.newInstance.aspireRunning", "an AppHost runs already, and one runs at a time (its resources, its dashboard and the IDE's endpoint are its own).")
            : null;

    /// <summary>
    /// A kind of project F5 starts its own way (a device, a packaged app, a remote window, an AppHost, a container) joins
    /// the session through that way, whose start adds it to the running session (#547's last step, ADR 0109).
    /// </summary>
    private async Task JoinAsF5Async(string project, Func<Task> start)
    {
        var before = Debugger.Processes.Count;
        Debugger.JustMyCode = Workbench.Settings.GetBoolean(JustMyCodeSetting, true);
        Debugger.StepFiltering = Workbench.Settings.GetBoolean(StepFilteringSetting, true);
        Layout.ShowToolWindow(DebugConsoleViewModel.ToolWindowId, focus: false);
        try
        {
            await start().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or IOException or JsonRpcRemoteException or JsonRpcConnectionLostException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.newInstance.couldNotDebug", "Could not start debugging {0}: {1}", Path.GetFileNameWithoutExtension(project), ex.Message));
            return;
        }

        if (Debugger.IsActive && Debugger.Processes.Count > before)
        {
            _joinedInstances.Add(new JoinedInstance(project, before));
            StatusBar.ShowMessage(Localizer.Format("msg.newInstance.joined", "{0} joined the debug session.", Path.GetFileNameWithoutExtension(project)), TimeSpan.FromSeconds(4));
        }
    }

    /// <summary>
    /// A web app joins the running session (#547's web step, ADR 0109), and true; false when it isn't one whose page is
    /// debugged. A Blazor WebAssembly app's page is one more program (its dev server runs beside it); a web app debugged
    /// with its page joins as one program, its server and its page together, as F5 debugs it. Neither gets the
    /// session's Hot Reload: that stays its first program's (TODO(issue)). When the session ended during the build, the
    /// app starts as F5 starts it.
    /// </summary>
    private async Task<bool> JoinWebAppAsync(string project, RunInfo info, LaunchProfile? profile, LaunchPlan plan, Launch launch)
    {
        var wasm = LaunchPlanner.BrowserDebugTarget(info, profile);
        var page = wasm is null ? LaunchPlanner.PageDebugTarget(profile, Workbench.Settings.GetBoolean(JavaScriptDebugSetting, true)) : null;
        if (wasm is null && page is null)
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(project);
        if (!Debugger.IsActive)
        {
            await StartDebuggingAsync(project, launch).ConfigureAwait(true);
            if (Debugger.IsActive)
            {
                _sessionProject = project;
            }

            return true;
        }

        if (SessionCannotJoinReason() is { } reason)
        {
            Workbench.Notifications.Warning(Localizer.Format("msg.newInstance.sessionCannotJoin", "{0} can't join the running debug session: {1} Stop it to debug {0}, or start {0} without debugging.", name, reason));
            return true;
        }

        Debugger.JustMyCode = Workbench.Settings.GetBoolean(JustMyCodeSetting, true);
        Debugger.StepFiltering = Workbench.Settings.GetBoolean(StepFilteringSetting, true);
        Layout.ShowToolWindow(DebugConsoleViewModel.ToolWindowId, focus: false);
        int? index;
        if (wasm is { } target)
        {
            index = await StartBrowserDebuggingAsync(project, target, joining: true).ConfigureAwait(true);
        }
        else
        {
            if (BlazorProjects.IsWebAppServer(project, Solutions.Projects) && Workbench.Settings.GetBoolean(DebugHotReloadSetting, true))
            {
                DebugLog.AppendLine(Localizer.Format("msg.browserDebug.joinedWithoutHotReload", "{0} joined the session without Hot Reload: edits to it take effect after a restart.", name));
            }

            index = await StartCompoundDebuggingAsync(project, plan, page!.Value, joining: true, stopAtEntry: launch.StopAtEntry).ConfigureAwait(true);
        }

        if (index is { } joined)
        {
            _joinedInstances.Add(new JoinedInstance(project, joined));
            StatusBar.ShowMessage(Localizer.Format("msg.newInstance.joined", "{0} joined the debug session.", name), TimeSpan.FromSeconds(4));
        }

        return true;
    }

    /// <summary>
    /// Restart (Ctrl+Shift+F5) brings back the projects Start New Instance added (#547), after the session's own: each
    /// joins the new session again, built and planned anew, without stopping at its entry.
    /// </summary>
    private async Task RejoinInstancesAsync(IReadOnlyList<string> projects)
    {
        foreach (var project in projects)
        {
            if (!Debugger.IsActive)
            {
                return; // the new session didn't start, or ended
            }

            if (BeginLaunch(project, debug: true) is not { } launch)
            {
                return;
            }

            using (launch)
            {
                await JoinSessionAsync(project, launch).ConfigureAwait(true);
            }
        }
    }

    /// <summary>
    /// A start clears the Debug Console for its session, unless it joins a running one (#547): the console is the
    /// session's, every program's output in it.
    /// </summary>
    private void ClearDebugLogForNewSession()
    {
        if (!Debugger.IsActive)
        {
            DebugLog.Clear();
        }
    }

    /// <summary>The session ended: the next one starts without the programs this one added.</summary>
    private void ForgetJoinedInstances()
    {
        _joinedInstances.Clear();
        _sessionProject = null;
    }

    /// <summary>
    /// The Processes window's Terminate Process and Detach Process (#547): one program ends, or goes on without the
    /// debugger. One that Start New Instance added is left out of Restart then (one that ended by itself comes back).
    /// </summary>
    internal async Task EndDebuggedProcessAsync(int index, bool detach)
    {
        _joinedInstances.RemoveAll(i => i.Index == index);
        try
        {
            await Debugger.EndProcessAsync(index, detach).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Codalus.Debugging.DapException or Codalus.Debugging.DapConnectionLostException)
        {
            Workbench.Notifications.Error(detach
                ? Localizer.Format("msg.newInstance.couldNotDetach", "Could not detach from the program: {0}", ex.Message)
                : Localizer.Format("msg.newInstance.couldNotTerminate", "Could not terminate the program: {0}", ex.Message));
        }
    }
}
