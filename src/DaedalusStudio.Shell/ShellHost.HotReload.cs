using System.Globalization;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Projects;
using DaedalusStudio.Workbench.StatusBar;
using DaedalusStudio.Workbench.Localization;

namespace DaedalusStudio.Shell;

/// <summary>
/// Hot Reload (M15b, ADR 0022): Start with Hot Reload runs the startup project under <c>dotnet watch</c>; saving a
/// file applies the edit to the running program. The status bar follows the watch's output.
/// </summary>
public sealed partial class ShellHost
{
    public const string HotReloadSetting = "run.hotReload";

    private const string HotReloadStatusItem = "daedalus.hotReload";

    /// <summary>The program running with Hot Reload, if any.</summary>
    public RunningProgram? HotReloadProgram => Runs.Running.FirstOrDefault(p => p.HotReload is not null && !p.HasExited);

    private void InitializeHotReload()
    {
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

        var framework = Solutions.ActiveFramework(project) ?? info.TargetFrameworks.FirstOrDefault();
        return DotnetFrameworks.HotReloadUnsupportedReason(framework, info.Name);
    }

    /// <summary>Alt+F10: saves the modified documents, which <c>dotnet watch</c> applies to the running program.</summary>
    public async Task ApplyCodeChangesAsync()
    {
        if (HotReloadProgram is null)
        {
            ShowMessage("Start with Hot Reload first (Debug → Start with Hot Reload).");
            return;
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

        session.Changed += (_, e) => Ui.Post(() =>
        {
            UpdateHotReloadStatus();
            if (e.State == HotReloadState.Restarting)
            {
                Workbench.Notifications.Info(Localizer.Format("msg.hotReload.anEditCouldntBeAppliedTo", "An edit couldn't be applied to the running {0}, so it is restarting (a rude edit).", program.Name));
            }
        });
        UpdateHotReloadStatus();
    }

    private void UpdateHotReloadStatus()
    {
        var program = HotReloadProgram;
        Workbench.Context.Set("hotReloadRunning", program is not null);
        if (program?.HotReload is not { } session)
        {
            Workbench.StatusBar.Remove(HotReloadStatusItem);
            return;
        }

        var text = session.State switch
        {
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
