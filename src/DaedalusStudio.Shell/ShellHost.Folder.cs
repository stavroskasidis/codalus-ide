using System.Globalization;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Rpc;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Shell;

/// <summary>
/// Folder mode (M26a, ADR 0033): a folder opened without a solution. The build host keeps an in-memory solution of the
/// projects loaded from it; <c>.daedalus/tasks.json</c> and <c>.daedalus/launch.json</c> hold its build tasks and launch
/// configurations, which Build, Start and Start Without Debugging use.
/// </summary>
public sealed partial class ShellHost
{
    private string? _launchConfiguration;

    /// <summary>A folder is open without a solution.</summary>
    public bool IsFolderMode => Solutions.Solution?.IsFolder == true;

    private Workbench.Output.OutputChannel TasksLog => Workbench.Output.GetOrCreate("daedalus.tasks", "Tasks");

    /// <summary>
    /// File → Open Folder: the folder becomes the workspace, as it is on disk, even when it has a solution file. Asks for
    /// one when <paramref name="folder"/> is null, and whether to trust it the first time.
    /// </summary>
    public async Task OpenFolderWorkspaceAsync(string? folder)
    {
        folder ??= Window is { } window ? await window.PickFolderAsync("Open Folder").ConfigureAwait(true) : null;
        if (folder is null)
        {
            return;
        }

        folder = Path.TrimEndingDirectorySeparator(DaedalusStudio.Platform.RealPath.Resolve(Path.GetFullPath(folder)));
        if (!Directory.Exists(folder))
        {
            Recent.Remove(folder);
            Workbench.Notifications.Error($"'{folder}' doesn't exist.");
            return;
        }

        var trusted = Trust.IsTrusted(folder);
        if (!trusted)
        {
            var choice = await Dialogs.ShowAsync(new DialogRequest(
                "Do you trust the authors of this folder?",
                $"Projects you load from a folder run code: MSBuild evaluates their project files and imports, and restore runs NuGet package targets. Its tasks run commands.\n\n{folder}\n\n"
                + "Trust it to load and build its projects and run its tasks. Without trust, you can browse and edit its files.",
                ["Trust and Open", "Open Without Trusting", "Cancel"])).ConfigureAwait(true);
            if (choice.Button is null or "Cancel")
            {
                return;
            }

            trusted = choice.Button == "Trust and Open";
            if (trusted)
            {
                Trust.Trust(folder);
            }
        }

        _structureReported = false;
        _evaluationReported = false;
        _designTimeReported = false;
        if (Solutions.IsOpen)
        {
            await CloseSolutionAsync().ConfigureAwait(true);
        }

        try
        {
            StatusBar.ShowMessage($"Opening {Path.GetFileName(folder)}…");
            var solution = await Solutions.OpenAsync(folder, trusted, Workbench.Settings.GetBoolean(RestoreOnOpenSetting, true), CancellationToken.None).ConfigureAwait(true);
            StatusBar.ClearMessage();
            Recent.Add(folder);
            _launchConfiguration = null;
            Workbench.Settings.SetWorkspaceFile(Path.Combine(folder, ".daedalus", "settings.json"));
            Explorer.SetFolderView(solution.Projects.Length == 0);
            Layout.ShowToolWindow(SolutionExplorerViewModel.ToolWindowId, focus: false);
            await RestoreEditorSessionAsync(folder).ConfigureAwait(true);
            await OpenRepositoryAsync(folder).ConfigureAwait(true);
            UpdateRunState();
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or IOException or System.ComponentModel.Win32Exception)
        {
            StatusBar.ClearMessage();
            ReportError($"Could not open {Path.GetFileName(folder)}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ projects of the folder

    /// <summary>Loads projects into the folder's in-memory solution (with <paramref name="replace"/>, only them).</summary>
    public async Task LoadFolderProjectsAsync(IReadOnlyList<string> projects, bool replace = false)
    {
        if (Solutions.Solution is not { IsFolder: true } solution)
        {
            return;
        }

        var wanted = replace ? projects : [.. solution.Projects.Select(p => p.Path).Concat(projects).Distinct(SolutionService.PathComparer)];
        try
        {
            await ProjectItems.SetFolderProjectsAsync(wanted, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is JsonRpcRemoteException or InvalidOperationException or JsonRpcConnectionLostException)
        {
            Workbench.Notifications.Error(ex.Message);
        }
    }

    /// <summary>Load All Projects: every project file under the folder, as one in-memory solution; shows the solution view.</summary>
    public async Task LoadAllFolderProjectsAsync()
    {
        if (!IsFolderMode)
        {
            return;
        }

        var projects = await ProjectItems.DiscoverProjectsAsync(CancellationToken.None).ConfigureAwait(true);
        if (projects.Count == 0)
        {
            StatusBar.ShowMessage("The folder has no project files.", TimeSpan.FromSeconds(4));
            return;
        }

        await LoadFolderProjectsAsync(projects, replace: true).ConfigureAwait(true);
        Explorer.SetFolderView(false);
    }

    /// <summary>Load Project (the selected project file of the folder view).</summary>
    public Task LoadSelectedFolderProjectAsync() =>
        Explorer.Selected?.Node is { Path: { } path } && SolutionTree.IsProjectFile(path) ? LoadFolderProjectsAsync([path]) : Task.CompletedTask;

    /// <summary>Unload Project from the folder's solution (the project file stays).</summary>
    public async Task UnloadSelectedFolderProjectAsync()
    {
        if (Solutions.Solution is { IsFolder: true } solution && Explorer.Selected?.Node is { Path: { } path } && solution.Projects.Any(p => SolutionService.PathComparer.Equals(p.Path, path)))
        {
            await LoadFolderProjectsAsync([.. solution.Projects.Select(p => p.Path).Where(p => !SolutionService.PathComparer.Equals(p, path))], replace: true).ConfigureAwait(true);
        }
    }

    /// <summary>Open Project: the selected project file on its own (leaving the folder).</summary>
    public Task OpenSelectedProjectAsync() =>
        Explorer.Selected?.Node is { Path: { } path } && SolutionTree.IsProjectFile(path) ? OpenSolutionAsync(path) : Task.CompletedTask;

    // ------------------------------------------------------------------ tasks

    private FolderVariables Variables()
    {
        var editor = ActiveEditor?.Controller?.Editor;
        return new FolderVariables(Solutions.Solution?.Directory ?? Environment.CurrentDirectory, ActiveEditor?.Model.Path, editor?.TextArea.Caret.Line, editor?.SelectedText);
    }

    /// <summary>The workspace's tasks (folder mode, and any solution with a <c>.daedalus/tasks.json</c>).</summary>
    public IReadOnlyList<FolderTask> Tasks()
    {
        if (Solutions.Solution is not { } solution)
        {
            return [];
        }

        var (tasks, error) = FolderConfiguration.ReadTasks(solution.Directory);
        if (error is not null)
        {
            Workbench.Notifications.Warning("tasks.json can't be read: " + error);
        }

        return tasks;
    }

    /// <summary>Run Task: picks one of the workspace's tasks (or runs the one named) with its output in Output → Tasks.</summary>
    public async Task<int?> RunTaskAsync(string? label = null)
    {
        if (!RequireTrustedSolution("Tasks"))
        {
            return null;
        }

        var tasks = Tasks();
        var task = label is not null ? tasks.FirstOrDefault(t => t.Label == label) : null;
        if (task is null && label is null)
        {
            if (tasks.Count == 0)
            {
                var notification = Workbench.Notifications.Info("This workspace has no tasks.", "Configure Tasks");
                if (await notification.Result.ConfigureAwait(true) == "Configure Tasks")
                {
                    await ConfigureTasksAsync().ConfigureAwait(true);
                }

                return null;
            }

            var items = tasks.Select(t => new QuickPickItem(t.Label) { Detail = $"{t.Command} {string.Join(' ', t.Args)}".Trim() + (t.Group is { } group ? $" · {group}" : string.Empty), Tag = t }).ToList();
            task = (await Palette.ShowAsync(items, "Select the task to run").ConfigureAwait(true))?.Tag as FolderTask;
        }

        if (task is null)
        {
            if (label is not null)
            {
                Workbench.Notifications.Warning($"There is no task '{label}' in tasks.json.");
            }

            return null;
        }

        Workbench.Output.Show(TasksLog);
        try
        {
            var exitCode = await FolderConfiguration.RunTaskAsync(task, Variables(), TasksLog, CancellationToken.None).ConfigureAwait(true);
            StatusBar.ShowMessage(exitCode == 0 ? $"The task '{task.Label}' succeeded." : $"The task '{task.Label}' failed (exit code {exitCode.ToString(CultureInfo.InvariantCulture)}).", TimeSpan.FromSeconds(6));
            return exitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Workbench.Notifications.Error($"Could not run the task '{task.Label}': {ex.Message}");
            return null;
        }
    }

    public Task ConfigureTasksAsync() => ConfigureAsync(FolderConfiguration.TasksPath, FolderConfiguration.TasksTemplate);

    public Task ConfigureLaunchAsync() => ConfigureAsync(FolderConfiguration.LaunchPath, FolderConfiguration.LaunchTemplate);

    private async Task ConfigureAsync(Func<string, string> file, string template)
    {
        if (Solutions.Solution is not { } solution)
        {
            StatusBar.ShowMessage("Open a folder or a solution first.", TimeSpan.FromSeconds(4));
            return;
        }

        await OpenTextFileAsync(file(solution.Directory), template).ConfigureAwait(true);
        Explorer.RefreshDisk();
    }

    /// <summary>
    /// Build in folder mode (ADR 0033): the default build task when tasks.json has one (true or false for its outcome),
    /// else null (the loaded projects are built).
    /// </summary>
    private async Task<bool?> FolderBuildTaskAsync(string target)
    {
        if (!IsFolderMode || target != BuildTargets.Build || Tasks().FirstOrDefault(t => t.Group == "build" && t.IsDefault) is not { } task)
        {
            return null;
        }

        return await RunTaskAsync(task.Label).ConfigureAwait(true) == 0;
    }

    // ------------------------------------------------------------------ launch configurations

    /// <summary>The workspace's launch configurations (<c>.daedalus/launch.json</c>).</summary>
    public IReadOnlyList<LaunchConfiguration> LaunchConfigurations() =>
        Solutions.Solution is { } solution ? FolderConfiguration.ReadLaunch(solution.Directory).Configurations : [];

    /// <summary>The launch configuration Start uses in folder mode: the chosen one, else the first.</summary>
    public LaunchConfiguration? CurrentLaunchConfiguration
    {
        get
        {
            if (!IsFolderMode)
            {
                return null;
            }

            var configurations = LaunchConfigurations();
            return configurations.FirstOrDefault(c => c.Name == _launchConfiguration) ?? (configurations.Count > 0 ? configurations[0] : null);
        }
    }

    public void SelectLaunchConfiguration(string name)
    {
        _launchConfiguration = name;
        UpdateRunState();
    }

    /// <summary>
    /// Start (or Start Without Debugging) in folder mode: the launch configuration's pre-launch task, then its project or
    /// program. Returns false when no launch configuration applies (the startup project runs as usual).
    /// </summary>
    private async Task<bool> StartFolderLaunchAsync(bool debug)
    {
        if (CurrentLaunchConfiguration is not { } configuration)
        {
            return false;
        }

        if (!RequireTrustedSolution("Launch configurations"))
        {
            return true;
        }

        var variables = Variables();
        string Expand(string text) => FolderConfiguration.Expand(text, variables);
        if (configuration.PreLaunchTask is { } preLaunch && await RunTaskAsync(preLaunch).ConfigureAwait(true) != 0)
        {
            StatusBar.ShowMessage($"The pre-launch task '{preLaunch}' failed, so '{configuration.Name}' did not start.", TimeSpan.FromSeconds(6));
            return true;
        }

        LaunchPlan plan;
        string key;
        try
        {
            if (configuration.Type == "project")
            {
                key = Path.GetFullPath(Expand(configuration.Project!));
                if (configuration.PreLaunchTask is null && !await BuildAsync(BuildTargets.Build, key).ConfigureAwait(true))
                {
                    return true;
                }

                (plan, _, _) = await Runs.PlanAsync(key, CancellationToken.None).ConfigureAwait(true);
            }
            else
            {
                key = Path.GetFullPath(Expand(configuration.Program!), variables.WorkspaceFolder);
                plan = key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? new LaunchPlan(Runs.DotnetRoot is { } root ? Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet") : "dotnet", Quote(key), Path.GetDirectoryName(key)!, new Dictionary<string, string>(), null)
                    : new LaunchPlan(key, string.Empty, Path.GetDirectoryName(key)!, new Dictionary<string, string>(), null);
            }

            var environment = new Dictionary<string, string>(plan.Environment, StringComparer.Ordinal);
            foreach (var (name, value) in configuration.Env)
            {
                environment[name] = Expand(value);
            }

            var arguments = string.Join(' ', configuration.Args.Select(a => Quote(Expand(a))));
            plan = plan with
            {
                Arguments = (plan.Arguments + " " + arguments).Trim(),
                WorkingDirectory = configuration.Cwd is { Length: > 0 } cwd ? Path.GetFullPath(Expand(cwd), variables.WorkspaceFolder) : plan.WorkingDirectory,
                Environment = environment,
            };

            if (debug)
            {
                DebugLog.Clear();
                DebugLog.AppendLine($"> {plan.FileName} {plan.Arguments}   (launch configuration '{configuration.Name}')".TrimEnd());
                Debugger.JustMyCode = Workbench.Settings.GetBoolean(JustMyCodeSetting, true);
                Layout.ShowToolWindow(DebugConsoleViewModel.ToolWindowId, focus: false);
                await Debugger.LaunchAsync(plan, CancellationToken.None).ConfigureAwait(true);
            }
            else
            {
                var program = Runs.StartPlan(key, configuration.Name, plan);
                if (program.Channel is { } channel)
                {
                    Workbench.Output.Show(channel);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or JsonRpcRemoteException or JsonRpcConnectionLostException or IOException)
        {
            Workbench.Notifications.Error($"Could not start '{configuration.Name}': {ex.Message}");
        }

        return true;
    }

    public async Task SelectLaunchConfigurationAsync()
    {
        var configurations = LaunchConfigurations();
        if (configurations.Count == 0)
        {
            var notification = Workbench.Notifications.Info("This workspace has no launch configurations.", "Configure Launch");
            if (await notification.Result.ConfigureAwait(true) == "Configure Launch")
            {
                await ConfigureLaunchAsync().ConfigureAwait(true);
            }

            return;
        }

        var current = CurrentLaunchConfiguration?.Name;
        var items = configurations.Select(c => new QuickPickItem(c.Name) { Detail = c.Type == "project" ? c.Project : c.Program, Tag = c.Name }).ToList();
        var active = Math.Max(0, items.FindIndex(i => (string)i.Tag! == current));
        if (await Palette.ShowAsync(items, "Select the launch configuration", active).ConfigureAwait(true) is { Tag: string picked })
        {
            SelectLaunchConfiguration(picked);
        }
    }

    /// <summary>A path whose folder is the workspace's (the playlists and other <c>.daedalus/</c> state live there).</summary>
    private static string PlaylistAnchor(SolutionInfo solution) => solution.IsFolder ? Path.Combine(solution.Path, solution.Name + ".folder") : solution.Path;

    private static string Quote(string argument) => argument.Length == 0 || argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument;
}
