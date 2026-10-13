using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Node;
using DaedalusStudio.Workbench.Notifications;
using DaedalusStudio.Workbench.Output;
using DaedalusStudio.Workbench.Settings;
using DaedalusStudio.Workbench.Localization;

namespace DaedalusStudio.Shell;

/// <summary>
/// npm (M16d, ADR 0027): the Task Runner Explorer lists the solution's npm scripts and runs them in terminals;
/// Restore npm Packages runs <c>npm install</c>; scripts bound with <c>"-vs-binding"</c> run before and after builds,
/// on Clean and when the solution opens. npm is the user's, else the built-in Node.js's.
/// </summary>
public sealed partial class ShellHost
{
    private string? _npmCheckedSolution;

    public TaskRunnerViewModel TaskRunner { get; private set; } = null!;

    public OutputChannel NpmLog => Workbench.Output.GetOrCreate("daedalus.npm", "npm");

    private void InitializeNpm()
    {
        TaskRunner = new TaskRunnerViewModel(
            NpmRoots,
            () => Solutions.Solution?.Directory,
            (package, script) => RunNpmScriptAsync(package.Path, script.Name),
            package => RestoreNpmAsync(package.Path),
            path => OpenFileAsync(path, preview: false, null));
        Solutions.SolutionChanged += (_, _) => Ui.Post(() => _ = OnSolutionChangedForNpmAsync());
    }

    /// <summary>The folders searched for <c>package.json</c>: the solution's and its projects'.</summary>
    private IReadOnlyList<string> NpmRoots()
    {
        if (Solutions.Solution is not { } solution)
        {
            return [];
        }

        return [solution.Directory, .. solution.Projects.Select(p => Path.GetDirectoryName(p.Path)!)];
    }

    private async Task OnSolutionChangedForNpmAsync()
    {
        if (Solutions.Solution is not { Trusted: true } solution)
        {
            return;
        }

        await TaskRunner.RefreshAsync().ConfigureAwait(true);

        // Once per solution: offer to restore, then run the Project Open bindings.
        if (_npmCheckedSolution == solution.Path)
        {
            return;
        }

        _npmCheckedSolution = solution.Path;
        var missing = TaskRunner.Packages.Where(p => p.HasDependencies && !p.IsRestored).ToList();
        if (missing.Count > 0)
        {
            var mode = Workbench.Settings.GetString(Npm.RestoreOnOpenSetting, "ask");
            if (mode == "always")
            {
                await RestoreNpmAsync(null).ConfigureAwait(true);
            }
            else if (mode == "ask")
            {
                var names = string.Join(", ", missing.Select(p => Path.GetRelativePath(solution.Directory, p.Path)));
                var choice = await Workbench.Notifications.Show(NotificationSeverity.Info, $"npm packages aren't installed for {names}.", ["Restore", "Not Now", "Never"]).Result.ConfigureAwait(true);
                if (choice == "Restore")
                {
                    await RestoreNpmAsync(null).ConfigureAwait(true);
                }
                else if (choice == "Never")
                {
                    await Workbench.Settings.SetAsync(Npm.RestoreOnOpenSetting, "never", SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                }
            }
        }

        foreach (var package in TaskRunner.Packages)
        {
            foreach (var script in package.BoundTo(Npm.ProjectOpen))
            {
                await RunNpmScriptAsync(package.Path, script, show: false).ConfigureAwait(true);
            }
        }
    }

    /// <summary>The npm to run, installing the built-in Node.js when it is the one (asked for, or no npm is installed).</summary>
    private async Task<NpmCommand?> NpmAsync()
    {
        if (Workbench.Settings.GetString(Npm.RuntimeSetting, "auto") != "builtIn" && Npm.UserNpm() is { } user)
        {
            return user;
        }

        if (NodeRuntime.Platform is null)
        {
            Workbench.Notifications.Error(Localizer.Get("msg.npm.npmIsntInstalledAndTheBuilt", "npm isn't installed, and the built-in Node.js isn't available for this platform. Install Node.js."));
            return null;
        }

        try
        {
            await Node.EnsureAsync(new Progress<string>(message => StatusBar.ShowMessage(message)), CancellationToken.None).ConfigureAwait(true);
            StatusBar.ClearMessage();
            return Npm.BuiltIn(Node);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or InvalidDataException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.npm.nodeJsCouldntBeInstalled", "Node.js couldn't be installed: {0}", ex.Message));
            return null;
        }
    }

    // ------------------------------------------------------------------ scripts

    public async Task ShowTaskRunnerAsync()
    {
        Layout.ShowToolWindow(TaskRunnerViewModel.ToolWindowId, focus: true);
        await TaskRunner.RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>Runs an npm script in a new terminal (<c>npm run &lt;script&gt;</c> in the package's folder).</summary>
    public async Task RunNpmScriptAsync(string packageJson, string script, bool show = true)
    {
        if (await NpmAsync().ConfigureAwait(true) is not { } npm)
        {
            return;
        }

        try
        {
            var session = Terminals.Run($"npm: {script}", npm.Program, npm.Arguments("run", script), Path.GetDirectoryName(packageJson)!, npm.Environment);
            if (show)
            {
                Layout.ShowToolWindow(TerminalViewModel.ToolWindowId);
            }

            TerminalWindow.Show(session);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.npm.couldNotRunTheNpmScript", "Could not run the npm script '{0}': {1}", script, ex.Message));
        }
    }

    /// <summary>Run npm Script…: picks one of the solution's scripts.</summary>
    public async Task PickNpmScriptAsync()
    {
        await TaskRunner.RefreshAsync().ConfigureAwait(true);
        var root = Solutions.Solution?.Directory;
        var items = TaskRunner.Packages
            .SelectMany(p => p.Scripts.Select(s => new QuickPickItem(s.Name)
            {
                Detail = (root is null ? p.Path : Path.GetRelativePath(root, p.Path)) + " · " + s.Command,
                Tag = (p.Path, s.Name),
            }))
            .ToList();
        if (items.Count == 0)
        {
            Workbench.Notifications.Info(Localizer.Get("msg.npm.theSolutionHasNoNpmScripts", "The solution has no npm scripts."));
            return;
        }

        if (await Palette.ShowAsync(items, "Run an npm script").ConfigureAwait(true) is { Tag: (string path, string name) })
        {
            await RunNpmScriptAsync(path, name).ConfigureAwait(true);
        }
    }

    // ------------------------------------------------------------------ restore

    /// <summary>The package.json selected in Solution Explorer, or the active editor's; null for the whole solution.</summary>
    public string? SelectedPackageJson()
    {
        static bool IsPackage(string? path) => path is not null && Path.GetFileName(path).Equals("package.json", StringComparison.OrdinalIgnoreCase);
        return Explorer.Selected?.Node.Path is { } selected && IsPackage(selected) ? selected
            : ActiveEditor?.Model.Path is { } active && IsPackage(active) ? active
            : null;
    }

    /// <summary>
    /// <c>npm install</c> for <paramref name="packageJson"/>, or for every package of the solution that has
    /// dependencies. Output goes to the npm output; returns whether every install succeeded.
    /// </summary>
    public async Task<bool> RestoreNpmAsync(string? packageJson)
    {
        if (Solutions.Solution is { Trusted: false })
        {
            Workbench.Notifications.Warning(Localizer.Get("msg.npm.npmPackagesAreRestoredOnlyIn", "npm packages are restored only in trusted folders: their install scripts run code."));
            return false;
        }

        var targets = packageJson is not null
            ? [packageJson]
            : (await Task.Run(() => Npm.Find(NpmRoots()).Select(Npm.Read).OfType<NpmPackage>().Where(p => p.HasDependencies).Select(p => p.Path).ToList()).ConfigureAwait(true));
        if (targets.Count == 0 || await NpmAsync().ConfigureAwait(true) is not { } npm)
        {
            return targets.Count == 0;
        }

        Workbench.Output.Show(NpmLog);
        var succeeded = true;
        foreach (var target in targets)
        {
            var folder = Path.GetDirectoryName(target)!;
            StatusBar.ShowMessage(Localizer.Format("msg.npm.restoringNpmPackagesIn", "Restoring npm packages in {0}…", Path.GetFileName(folder)));
            NpmLog.AppendLine($"> npm install ({folder}, {npm.Description})");
            try
            {
                var code = await Npm.RunAsync(npm, ["install"], folder, line => NpmLog.AppendLine(line), CancellationToken.None).ConfigureAwait(true);
                NpmLog.AppendLine(code == 0 ? "npm packages restored." : $"npm install exited with code {code}.");
                succeeded &= code == 0;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                NpmLog.AppendLine("npm install failed: " + ex.Message);
                succeeded = false;
            }
        }

        StatusBar.ShowMessage(succeeded ? "npm packages restored." : "npm packages couldn't be restored: see Output → npm.", TimeSpan.FromSeconds(6));
        await TaskRunner.RefreshAsync().ConfigureAwait(true);
        return succeeded;
    }

    // ------------------------------------------------------------------ bindings

    /// <summary>
    /// Runs the scripts bound to <paramref name="binding"/> in the packages of <paramref name="project"/> (or of the
    /// solution) one after another, waiting for each, with their output in the npm output. Returns false when one fails.
    /// </summary>
    private async Task<bool> RunNpmBindingsAsync(string binding, string? project)
    {
        var roots = project is null ? NpmRoots() : [Path.GetDirectoryName(project)!];
        var packages = await Task.Run(() => Npm.Find(roots).Select(Npm.Read).OfType<NpmPackage>().Where(p => p.BoundTo(binding).Count > 0).ToList()).ConfigureAwait(true);
        if (packages.Count == 0 || await NpmAsync().ConfigureAwait(true) is not { } npm)
        {
            return true;
        }

        foreach (var package in packages)
        {
            foreach (var script in package.BoundTo(binding))
            {
                NpmLog.AppendLine($"> npm run {script} ({TaskRunnerViewModel.Display(binding)}, {package.Folder})");
                Builds.Channel.AppendLine($"npm: running '{script}' ({TaskRunnerViewModel.Display(binding)})");
                try
                {
                    var code = await Npm.RunAsync(npm, ["run", script], package.Folder, line => NpmLog.AppendLine(line), CancellationToken.None).ConfigureAwait(true);
                    if (code != 0)
                    {
                        Builds.Channel.AppendLine($"npm: '{script}' exited with code {code} (see Output → npm).");
                        return false;
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    Builds.Channel.AppendLine($"npm: '{script}' couldn't run: {ex.Message}");
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The bindings around a build: Before Build (failing it stops the build), Clean, and After Build.</summary>
    private Task<bool> BeforeBuildBindingsAsync(string target, string? project) =>
        target == BuildTargets.Clean ? RunNpmBindingsAsync(Npm.Clean, project)
        : target is BuildTargets.Build or BuildTargets.Rebuild ? RunNpmBindingsAsync(Npm.BeforeBuild, project)
        : Task.FromResult(true);

    private Task AfterBuildBindingsAsync(string target, string? project, bool succeeded) =>
        succeeded && target is BuildTargets.Build or BuildTargets.Rebuild ? RunNpmBindingsAsync(Npm.AfterBuild, project) : Task.CompletedTask;
}
