using System.Globalization;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Build;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Projects;
using Codalus.Workbench.StatusBar;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// Hot Reload (M15b, ADR 0022): Start with Hot Reload runs the startup project under <c>dotnet watch</c>; saving a
/// file applies the edit to the running program. The status bar follows the watch's output.
/// </summary>
public sealed partial class ShellHost
{
    public const string HotReloadSetting = "run.hotReload";

    private const string HotReloadStatusItem = "codalus.hotReload";

    /// <summary>The program running with Hot Reload, if any.</summary>
    public RunningProgram? HotReloadProgram => Runs.Running.FirstOrDefault(p => p.HotReload is not null && !p.HasExited);

    private void InitializeHotReload()
    {
        InitializeDebugHotReload();
        Runs.DotnetRoot = Terminals.DotnetRoot;
        Solutions.SolutionChanged += (_, _) => Runs.DotnetRoot = Terminals.DotnetRoot;
        Runs.RunningChanged += (_, _) => Ui.Post(UpdateHotReloadStatus);
    }

    /// <summary>Builds the startup project, then runs it under <c>dotnet watch</c>.</summary>
    public Task StartWithHotReloadAsync(string? argument = null) => StartWithoutDebuggingAsync(argument, hotReload: true);

    /// <summary>
    /// Why Hot Reload can't run <paramref name="project"/> (by default the startup project), or null when it can (M33):
    /// <c>dotnet watch</c> applies changes from .NET 6 on.
    /// </summary>
    public string? HotReloadUnsupportedReason(string? project = null)
    {
        project ??= Runs.StartupProject;
        if (project is null || !Solutions.Projects.TryGetValue(project, out var info))
        {
            return null;
        }

        if (RunsAsPackage(info))
        {
            // dotnet watch starts programs, and a packaged app starts only through its registration (M44).
            return Localizer.Format("msg.windowsApps.noHotReload", "Hot Reload isn't available for {0}: it's a packaged (MSIX) app, which dotnet watch can't start. Set WindowsPackageType to None to use it.", info.Name);
        }

        var framework = Solutions.ActiveFramework(project) ?? info.TargetFrameworks.FirstOrDefault();
        return DotnetFrameworks.HotReloadUnsupportedReason(framework, info.Name);
    }

    /// <summary>How long Apply Code Changes waits for <c>dotnet watch</c> to start watching before it saves anyway.</summary>
    private static readonly TimeSpan s_hotReloadStartWait = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Alt+F10: saves the modified documents, which <c>dotnet watch</c> applies to the running program. While it is still
    /// starting, the save waits until it watches (<see cref="HotReloadSession.Started"/>): a change saved before then
    /// never reaches the app.
    /// </summary>
    public async Task ApplyCodeChangesAsync()
    {
        if (HotReloadProgram is null && DebugEdits.IsActive)
        {
            // Under the Codalus debugger (M55): the editors' text as it is, saved or not.
            await ApplyDebugEditsAsync().ConfigureAwait(true);
            return;
        }

        if (HotReloadProgram is not { } program)
        {
            ShowMessage("Start with Hot Reload first (Debug → Start with Hot Reload).");
            return;
        }

        if (program.HotReload is { Started.IsCompleted: false } session)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.hotReload.theCodeChangesAreSavedOnceItWatches", "Hot Reload is starting: the code changes are saved and applied once it watches for them…"), s_hotReloadStartWait);
            try
            {
                await Task.WhenAny(session.Started, program.Exited).WaitAsync(s_hotReloadStartWait).ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                // Its output didn't say (another SDK's wording): saving is still the trigger.
            }
        }

        await SaveAllAsync().ConfigureAwait(true);
        StatusBar.ShowMessage(Localizer.Get("msg.hotReload.applyingTheCodeChanges", "Applying the code changes…"), TimeSpan.FromSeconds(3));
    }

    private void OnHotReloadStarted(RunningProgram program)
    {
        if (program.HotReload is not { } session)
        {
            return;
        }

        session.Refused += () => Ui.Post(UpdateHotReloadStatus);
        session.Changed += (_, e) => Ui.Post(() =>
        {
            UpdateHotReloadStatus();
            if (e.State == HotReloadState.Restarting && !ReferenceEquals(program, Volatile.Read(ref _watchedProgram)))
            {
                // (A Blazor Web App debugged under dotnet watch says it when debugging follows the restart, #75.)
                Workbench.Notifications.Info(Localizer.Format("msg.hotReload.anEditCouldntBeAppliedTo", "An edit couldn't be applied to the running {0}, so it is restarting (a rude edit).", program.Name));
            }
        });
        UpdateHotReloadStatus();
    }

    /// <summary>The toolbar's Hot Reload button (deferred from M15b).</summary>
    private void UpdateToolbarHotReload() =>
        Toolbar?.SetHotReload(HotReloadUnsupportedReason(Runs.StartupProject) is null, HotReloadProgram is not null || DebugEdits?.IsActive == true, Workbench.Settings.GetBoolean(HotReloadSetting, false));

    /// <summary>The Hot Reload menu's Hot Reload on Start Without Debugging: turns the <c>run.hotReload</c> setting over.</summary>
    public Task ToggleHotReloadOnStartAsync() =>
        Workbench.Settings.SetAsync(HotReloadSetting, !Workbench.Settings.GetBoolean(HotReloadSetting, false), Codalus.Workbench.Settings.SettingScope.User, CancellationToken.None);

    private void UpdateHotReloadStatus()
    {
        var program = HotReloadProgram;
        var debugEdits = DebugEdits?.IsActive == true;
        Workbench.Context.Set("hotReloadRunning", program is not null || debugEdits);
        UpdateToolbarHotReload();
        if (program?.HotReload is not { } session)
        {
            if (debugEdits && _debugEditsStatus is { } status)
            {
                // Hot Reload while debugging (M55): the last apply's outcome.
                Workbench.StatusBar.Set(new StatusBarItem(HotReloadStatusItem, "$(flame) " + status, StatusBarAlignment.Left, 46, ContributionSource.Shell.Id)
                {
                    Command = "run.applyCodeChanges",
                    Tooltip = Localizer.Get("status.debugHotReload.tooltip", "Apply Code Changes (Alt+F10): the edits apply to the program being debugged. Continue applies them too, and so does a save while it runs."),
                    AccessibleName = status,
                });
                return;
            }

            Workbench.StatusBar.Remove(HotReloadStatusItem);
            return;
        }

        // A Blazor Web App's server under the debugger refuses updates; the browser applies its own (#75), and whether it
        // did isn't reported back: "sent", not "applied".
        var debugged = ReferenceEquals(program, Volatile.Read(ref _watchedProgram));
        var text = session.State switch
        {
            HotReloadState.Applying when debugged => "Hot Reload: sent to the browser",
            HotReloadState.Watching when debugged && (session.LastApplied is not null || session.UpdatesRefused) => "Hot Reload: sent to the browser",
            HotReloadState.NotApplied => "Hot Reload: not applied (see the output)",
            HotReloadState.Starting => "Hot Reload: starting…",
            HotReloadState.Applying => "Hot Reload: applying…",
            HotReloadState.Errors => "Hot Reload: compile errors",
            HotReloadState.Restarting => "Hot Reload: restarting…",
            HotReloadState.Exited => "Hot Reload: the app exited",
            _ when session.LastApplied is { } applied => $"Hot Reload: applied in {applied.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms",
            _ => "Hot Reload: watching",
        };
        Workbench.StatusBar.Set(new StatusBarItem(HotReloadStatusItem, "$(flame) " + text, StatusBarAlignment.Left, 46, ContributionSource.Shell.Id)
        {
            Command = "workbench.view.output",
            Tooltip = (session.Message ?? "Saving a file applies the change to the running app.") + " Click to show the output.",
            AccessibleName = text,
        });
    }
}
