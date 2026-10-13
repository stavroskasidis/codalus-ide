using System.Globalization;
using Codalus.Platform;
using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Build;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Notifications;
using Codalus.Workbench.Projects;
using Codalus.Workbench.StatusBar;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>Build, Rebuild and Clean; the startup project, launch profiles and Run Without Debugging (ADR 0006).</summary>
public sealed partial class ShellHost
{
    public const string SaveAllBeforeBuildSetting = "build.saveAllBeforeBuild";
    public const string ExternalConsoleSetting = "run.externalConsole";

    /// <summary>Run Without Debugging's console: a console window on Windows, as a classic IDE; the Output window elsewhere.</summary>
    public static string DefaultRunConsole => OperatingSystem.IsWindows() ? "externalConsole" : "outputWindow";

    /// <summary>Whether Run Without Debugging opens console windows (Windows; <c>run.console</c>, or the older <c>run.externalConsole</c>).</summary>
    private bool RunsInConsoleWindow =>
        Workbench.Settings.GetString(RunConsoleSetting, DefaultRunConsole) == "externalConsole" || Workbench.Settings.GetBoolean(ExternalConsoleSetting, false);

    private const string BuildStatusItem = "codalus.build";
    private const string StartupStatusItem = "codalus.startup";

    public BuildService Builds { get; private set; } = null!;

    public RunService Runs { get; private set; } = null!;

    private void InitializeBuild()
    {
        Builds = new BuildService(Solutions, Workbench.Output);
        Runs = new RunService(Solutions, Workbench.Output, ShellIntegration.OpenUrl);
        Builds.Started += (_, request) => Ui.Post(() => OnBuildStarted(request));
        Builds.Finished += (_, completed) => Ui.Post(() => OnBuildFinished(completed));
        Runs.RunningChanged += (_, _) => Ui.Post(UpdateRunState);
        Runs.StartupChanged += (_, _) => Ui.Post(UpdateRunState);
        Solutions.SolutionChanged += (_, _) => Ui.Post(UpdateRunState);
        Solutions.ProjectChanged += (_, _) => Ui.Post(UpdateRunState);
        UpdateRunState();
    }

    // ------------------------------------------------------------------ build

    /// <summary>
    /// Builds, rebuilds or cleans the solution (<paramref name="project"/> null) or a project. Saves open documents
    /// first (setting <c>build.saveAllBeforeBuild</c>). Returns false when the build failed, was cancelled or didn't run.
    /// </summary>
    public async Task<bool> BuildAsync(string target, string? project)
    {
        if (Solutions.Solution is not { } solution)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.build.openASolutionToBuildIt", "Open a solution to build it."), TimeSpan.FromSeconds(4));
            return false;
        }

        if (!solution.Trusted)
        {
            var notification = Workbench.Notifications.Warning(Localizer.Get("msg.build.buildsRunOnlyInTrustedFolders", "Builds run only in trusted folders."), "Trust Folder");
            if (await notification.Result.ConfigureAwait(true) == "Trust Folder")
            {
                await TrustSolutionAsync().ConfigureAwait(true);
            }

            return false;
        }

        if (Builds.IsBuilding)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.build.aBuildIsAlreadyRunning", "A build is already running."), TimeSpan.FromSeconds(4));
            return false;
        }

        if (Workbench.Settings.GetBoolean(SaveAllBeforeBuildSetting, true))
        {
            await SaveAllAsync().ConfigureAwait(true);
        }

        // Folder mode: the default build task, when tasks.json has one (ADR 0033).
        if (project is null && await FolderBuildTaskAsync(target).ConfigureAwait(true) is { } taskSucceeded)
        {
            return taskSucceeded;
        }

        Workbench.Output.Show(Builds.Channel);
        if (!await BeforeBuildBindingsAsync(target, project).ConfigureAwait(true))
        {
            StatusBar.ShowMessage(Localizer.Get("msg.build.anNpmScriptBoundToThe", "An npm script bound to the build failed, so the build didn't run (see Output → npm)."), TimeSpan.FromSeconds(8));
            return false;
        }

        var result = await Builds.BuildAsync(target, project, CancellationToken.None).ConfigureAwait(true);
        await AfterBuildBindingsAsync(target, project, result.Succeeded).ConfigureAwait(true);
        return result.Succeeded;
    }

    /// <summary>The project a project-level command acts on: the argument, the Solution Explorer's selection, the active document's project, or the startup project.</summary>
    public string? CurrentProject(string? argument = null)
    {
        if (argument is not null)
        {
            return Path.GetFullPath(argument);
        }

        if (Equals(Workbench.Context.GetValue("focusedView"), SolutionExplorerViewModel.ToolWindowId) && Explorer.Selected?.Node.ProjectPath is { } selected)
        {
            return selected;
        }

        if (ActiveEditor?.Model.Path is { } path
            && Solutions.Projects.Values.FirstOrDefault(p => p.Items.Any(i => SolutionService.PathComparer.Equals(i.Path, path))) is { } owner)
        {
            return owner.Path;
        }

        return Explorer.Selected?.Node.ProjectPath ?? Runs.StartupProject;
    }

    public Task<bool> BuildProjectAsync(string target, string? argument = null)
    {
        if (CurrentProject(argument) is not { } project)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.build.selectAProjectToBuild", "Select a project to build."), TimeSpan.FromSeconds(4));
            return Task.FromResult(false);
        }

        return BuildAsync(target, project);
    }

    public void CancelBuild() => Builds.Cancel();

    private void OnBuildStarted(BuildRequest request)
    {
        Workbench.Context.Set("buildRunning", true);
        var what = request.Project is { } project ? Path.GetFileNameWithoutExtension(project) : "solution";
        var verb = request.Target switch { BuildTargets.Rebuild => "Rebuilding", BuildTargets.Clean => "Cleaning", BuildTargets.Pack => "Packing", _ => "Building" };
        Workbench.StatusBar.Set(new StatusBarItem(BuildStatusItem, $"$(refresh) {verb} {what}…", StatusBarAlignment.Left, 45, ContributionSource.Shell.Id)
        {
            Command = "build.cancel",
            Tooltip = "Click to cancel the build (Ctrl+Break)",
        });
    }

    private void OnBuildFinished(BuildCompleted completed)
    {
        Workbench.Context.Set("buildRunning", false);
        var result = completed.Result;
        var verb = completed.Request.Target switch { BuildTargets.Rebuild => "Rebuild", BuildTargets.Clean => "Clean", BuildTargets.Pack => "Pack", _ => "Build" };
        var text = result.Canceled ? $"$(warning) {verb} cancelled"
            : result.Succeeded ? $"$(check) {verb} succeeded"
            : $"$(error) {verb} failed";
        var counts = $"{result.Errors.ToString(CultureInfo.InvariantCulture)} error(s), {result.Warnings.ToString(CultureInfo.InvariantCulture)} warning(s)";
        Workbench.StatusBar.Set(new StatusBarItem(BuildStatusItem, text, StatusBarAlignment.Left, 45, ContributionSource.Shell.Id)
        {
            Command = "workbench.view.output",
            Tooltip = $"{counts}, {TimeSpan.FromMilliseconds(result.ElapsedMilliseconds):mm\\:ss\\.f}. Click to show the build output.",
            AccessibleName = $"{text[(text.IndexOf(' ', StringComparison.Ordinal) + 1)..]}: {counts}",
        });
        if (!result.Succeeded && !result.Canceled && result.Errors > 0)
        {
            Layout.ShowToolWindow(ErrorListViewModel.ToolWindowId, focus: false);
        }
    }

    // ------------------------------------------------------------------ run

    /// <summary>
    /// Builds the startup project, then starts it with its launch profile (Ctrl+F5). With <paramref name="hotReload"/>,
    /// or the <c>run.hotReload</c> setting, it runs under <c>dotnet watch</c> (M15b).
    /// </summary>
    public async Task StartWithoutDebuggingAsync(string? argument = null, bool hotReload = false)
    {
        var hotReloadSetting = Workbench.Settings.GetBoolean(HotReloadSetting, false);
        if (argument is null && !hotReload && !hotReloadSetting && await StartFolderLaunchAsync(debug: false).ConfigureAwait(true))
        {
            return; // a folder's launch configuration (ADR 0033)
        }

        if (argument is null && !hotReload && Runs.MultipleStartup)
        {
            await StartStartupProjectsAsync(debug: false).ConfigureAwait(true);
            return;
        }

        var project = argument is not null ? Path.GetFullPath(argument) : Runs.StartupProject;
        if (project is null)
        {
            Workbench.Notifications.Warning(Solutions.IsOpen
                ? "There is no startup project: the solution has no executable project. Set one with 'Set as Startup Project'."
                : "Open a solution to run it.");
            return;
        }

        // Hot Reload needs .NET 6 (M33): asked for, it says why not; on by the setting, the program runs without it.
        string? withoutHotReload = null;
        if ((hotReload || hotReloadSetting) && HotReloadUnsupportedReason(project) is { } reason)
        {
            if (hotReload)
            {
                Workbench.Notifications.Warning(reason + " Start Without Debugging (Ctrl+F5) runs it without Hot Reload.");
                return;
            }

            hotReloadSetting = false;
            withoutHotReload = reason + " It runs without Hot Reload.";
        }

        hotReload |= hotReloadSetting;

        if (!await BuildBeforeRunAsync(project).ConfigureAwait(true))
        {
            if (Builds.LastBuild is { Result: { Canceled: false, Errors: > 0 } })
            {
                StatusBar.ShowMessage(Localizer.Get("msg.build.theBuildFailedSoTheProgram", "The build failed, so the program was not started."), TimeSpan.FromSeconds(6));
            }

            return;
        }

        _ = OfferDevCertificateAsync(project);
        try
        {
            if (Remote is { } remote)
            {
                // A remote window (M21): the program runs there, in a terminal (TODO(issue): Hot Reload remotely, M21b).
                var (remoteInfo, remoteProfiles, _) = await Runs.GetProfilesAsync(project, CancellationToken.None).ConfigureAwait(true);
                await StartRemoteAsync(remote, project, remoteInfo, Runs.ResolveProfile(project, remoteProfiles), debug: false).ConfigureAwait(true);
                return;
            }

            if (IsAspireHost(project))
            {
                // An Aspire AppHost runs its projects through this IDE (ADR 0043).
                await StartAspireAsync(project, debug: false).ConfigureAwait(true);
                return;
            }

            if (!hotReload)
            {
                var (info, profiles, _) = await Runs.GetProfilesAsync(project, CancellationToken.None).ConfigureAwait(true);
                if (Runs.ResolveProfile(project, profiles) is { } profile && IsContainerProfile(profile))
                {
                    // The "Container (Dockerfile)" profile (ADR 0042).
                    await StartInContainerAsync(project, info, profile, debug: false).ConfigureAwait(true);
                    return;
                }
            }

            var console = Workbench.Settings.GetString(RunConsoleSetting, DefaultRunConsole);
            if (console == "integratedTerminal" && Runs.IsExecutable(project) && !hotReload)
            {
                await RunInTerminalAsync(project).ConfigureAwait(true);
                return;
            }

            var program = await Runs.StartAsync(project, RunsInConsoleWindow, CancellationToken.None, hotReload).ConfigureAwait(true);
            if (withoutHotReload is not null)
            {
                program.Channel?.AppendLine(withoutHotReload);
            }

            OnHotReloadStarted(program);
            if (program.Channel is { } channel)
            {
                Workbench.Output.Show(channel);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or JsonRpcRemoteException or JsonRpcConnectionLostException or IOException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.build.couldNotStart", "Could not start {0}: {1}", Path.GetFileNameWithoutExtension(project), ex.Message));
        }
    }

    public void StopRunning() => Runs.StopAll();

    public async Task SelectStartupProjectAsync()
    {
        if (Solutions.Solution is not { } solution)
        {
            return;
        }

        var current = Runs.StartupProject;
        var items = solution.Projects
            .Select(p => new QuickPickItem(p.Name)
            {
                Tag = p.Path,
                Detail = SolutionService.PathComparer.Equals(p.Path, current) ? "startup project" : Runs.IsExecutable(p.Path) ? null : "not executable",
            })
            .ToList();
        var active = Math.Max(0, items.FindIndex(i => SolutionService.PathComparer.Equals((string)i.Tag!, current)));
        if (await Palette.ShowAsync(items, "Select the startup project", active).ConfigureAwait(true) is { Tag: string picked })
        {
            SetStartupProject(picked);
        }
    }

    public void SetStartupProject(string? project)
    {
        Runs.SetStartupProject(project);
        SaveEditorSession();
    }

    /// <summary>Picks the launch profile of the startup project (or of <paramref name="argument"/>).</summary>
    public async Task SelectLaunchProfileAsync(string? argument = null)
    {
        if ((argument is not null ? Path.GetFullPath(argument) : Runs.StartupProject) is not { } project)
        {
            return;
        }

        IReadOnlyList<LaunchProfile> profiles;
        try
        {
            (_, profiles, var error) = await Runs.GetProfilesAsync(project, CancellationToken.None).ConfigureAwait(true);
            if (error is not null)
            {
                Workbench.Notifications.Warning("The launch settings could not be read: " + error);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException)
        {
            Workbench.Notifications.Error(ex.Message);
            return;
        }

        if (profiles.Count == 0)
        {
            StatusBar.ShowMessage(Localizer.Format("msg.build.hasNoLaunchProfilesPropertiesLaunchsettings", "{0} has no launch profiles (Properties/launchSettings.json).", Path.GetFileNameWithoutExtension(project)), TimeSpan.FromSeconds(6));
            return;
        }

        var current = Runs.ResolveProfile(project, profiles)?.Name;
        var items = profiles
            .Select(p => new QuickPickItem(p.Name)
            {
                Tag = p.Name,
                Detail = p.IsRunnable ? p.CommandLineArgs : $"'{p.CommandName}' profiles can't run in Codalus IDE",
            })
            .ToList();
        var active = Math.Max(0, items.FindIndex(i => (string)i.Tag! == current));
        if (await Palette.ShowAsync(items, $"Select the launch profile of {Path.GetFileNameWithoutExtension(project)}", active).ConfigureAwait(true) is { Tag: string picked })
        {
            Runs.SetProfile(project, picked);
            SaveEditorSession();
        }
    }

    private void UpdateRunState()
    {
        var running = Runs.Running;
        Workbench.Context.Set("programRunning", running.Count > 0);
        var startup = Runs.StartupProject;
        Workbench.Context.Set("hasStartupProject", startup is not null);
        Workbench.Context.Set("hotReloadSupported", HotReloadUnsupportedReason(startup) is null);
        Toolbar?.SetDebugState(Debugger?.State is { } debug && debug != Codalus.Workbench.Debugging.DebugState.Idle, Debugger?.State == Codalus.Workbench.Debugging.DebugState.Stopped, running.Count > 0);
        UpdateToolbarStartup();
        Explorer.SetStartupProject(startup);
        // The startup project and its profile are in the toolbar (M29); the status bar shows a running program.
        if (startup is null || Solutions.Solution is not { Trusted: true } || running.Count == 0)
        {
            Workbench.StatusBar.Remove(StartupStatusItem);
            return;
        }

        var name = Path.GetFileNameWithoutExtension(startup);
        var profile = Runs.ChosenProfile(startup);
        var label = profile is null ? name : $"{name}: {profile}";
        var text = running.Count > 0 ? $"$(stop) {label} (running)" : $"$(play) {label}";
        Workbench.StatusBar.Set(new StatusBarItem(StartupStatusItem, text, StatusBarAlignment.Left, 48, ContributionSource.Shell.Id)
        {
            Command = running.Count > 0 ? "run.stop" : "run.selectLaunchProfile",
            Tooltip = running.Count > 0 ? "Click to stop the program (Shift+F5)" : "Startup project and launch profile (click to change the profile; Ctrl+F5 runs it)",
            AccessibleName = running.Count > 0 ? $"{label} is running" : $"Startup project {label}",
        });
    }
}
