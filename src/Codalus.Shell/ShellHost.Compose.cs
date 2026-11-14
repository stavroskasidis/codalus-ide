using Codalus.Debugging;
using Codalus.Projects.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Build;
using Codalus.Workbench.Containers;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Debugging.Remote;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// Compose (M17a; M63, ADR 0102): Compose Up and Down of a compose file (or the classic IDE's <c>.dcproj</c>), and in
/// fast mode (<c>containers.fastMode</c>, on by default) every .NET service running the IDE's Debug build on its
/// Dockerfile's <c>base</c> stage through the IDE's override file (<see cref="ComposeFastMode"/>): Debug Compose debugs
/// them all as programs of one session, a service is restarted, stopped, debugged or detached alone while the others
/// run, and Compose Up again restarts only the services whose build changed. Everything goes through the user's
/// docker or podman, one process per operation.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>The compose app the IDE last ran in fast mode: its .NET services, their builds and their programs in the debug session.</summary>
    private sealed class ComposeRun(string file, string project, ComposeModel model)
    {
        public string File { get; } = file;

        public string Project { get; } = project;

        public ComposeModel Model { get; set; } = model;

        /// <summary>The .NET services fast mode runs, each as the override has it now (<see cref="ComposeFastService.Debug"/>).</summary>
        public List<ComposeFastService> Services { get; set; } = [];

        /// <summary>The build each service run without the debugger started from (Compose Up restarts those that changed).</summary>
        public Dictionary<string, string> RunStamps { get; } = new(StringComparer.Ordinal);

        /// <summary>Each debugged service's index in the debug session's programs (<see cref="DebugService.Processes"/>).</summary>
        public Dictionary<string, int> Programs { get; } = new(StringComparer.Ordinal);

        public ComposeFastService? Service(string name) => Services.Find(s => s.Service.Service == name);
    }

    private ComposeRun? _compose;

    /// <summary>A compose command of the IDE's is under way (it builds and restarts what it needs itself).</summary>
    private int _composeBusy;

    private void InitializeCompose()
    {
        Debugger.Ended += (_, _) => Ui.Post(() => _compose?.Programs.Clear());

        // A code change reaches a service run without the debugger with the build: the service restarts on it.
        Builds.Finished += (_, done) =>
        {
            if (done.Result is { Canceled: false, Errors: 0 })
            {
                Ui.Post(() => _ = RestartChangedComposeServicesAsync());
            }
        };
    }

    /// <summary>
    /// After a build, the fast-mode services run without the debugger whose build changed restart on it (a debugged
    /// one takes the edit through Apply Code Changes, or Restart Compose Service).
    /// </summary>
    private async Task RestartChangedComposeServicesAsync()
    {
        if (_composeBusy > 0 || _compose is not { RunStamps.Count: > 0 } run || WrittenComposeOverride(run) is not { } ideOverride)
        {
            return;
        }

        var stamps = await Task.Run(() => run.Services.Where(s => !s.Debug && run.RunStamps.ContainsKey(s.Service.Service)).ToDictionary(s => s.Service.Service, s => ComposeFastMode.BuildStamp(s.Output), StringComparer.Ordinal)).ConfigureAwait(true);
        var changed = ComposeFastMode.Changed(run.RunStamps, stamps);
        if (changed.Count == 0)
        {
            return;
        }

        foreach (var service in changed)
        {
            run.RunStamps[service] = stamps[service];
        }

        ContainerLog.AppendLine($"Restarting the services whose build changed: {string.Join(", ", changed)}.");
        await RunComposeAsync(run.File, ideOverride, "restart", ["restart", .. changed]).ConfigureAwait(true);
    }

    /// <summary>Marks a compose command of the IDE's under way until it's disposed (on the UI thread).</summary>
    private ComposeBusyScope ComposeBusy() => new(this);

    private readonly struct ComposeBusyScope : IDisposable
    {
        private readonly ShellHost _shell;

        public ComposeBusyScope(ShellHost shell)
        {
            _shell = shell;
            _shell._composeBusy++;
        }

        public void Dispose() => _shell._composeBusy--;
    }

    // ------------------------------------------------------------------ the compose file

    /// <summary>
    /// The compose file of <paramref name="argument"/>: a compose file itself, a <c>.dcproj</c> (its
    /// <c>docker-compose.yml</c>), or with none the selected item's or the solution folder's.
    /// </summary>
    public string? ComposeFileFor(string? argument)
    {
        var path = argument ?? Explorer.Selected?.Node.Path ?? ActiveEditor?.Model.Path;
        if (path is not null && IsComposeFile(path))
        {
            return path;
        }

        var folder = path is not null && path.EndsWith(".dcproj", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(path)
            : Solutions.Solution?.Directory;
        if (path is not null && Solutions.Projects.TryGetValue(path, out var project) && project.IsComposeProject
            && project.Items.Select(i => i.Path).FirstOrDefault(i => IsComposeFile(i) && !Path.GetFileName(i).Contains(".override.", StringComparison.OrdinalIgnoreCase)) is { } item)
        {
            return item;
        }

        return folder is null ? null : s_composeNames.Select(n => Path.Combine(folder, n)).FirstOrDefault(File.Exists);
    }

    private static readonly string[] s_composeNames = ["compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml"];

    public static bool IsComposeFile(string path) =>
        Path.GetFileName(path) is var file
        && (file.StartsWith("compose.", StringComparison.OrdinalIgnoreCase) || file.StartsWith("docker-compose", StringComparison.OrdinalIgnoreCase))
        && (file.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));

    /// <summary>The compose files a compose command uses: the file, then its <c>.override</c> next to it (as compose itself does).</summary>
    private static List<string> ComposeFiles(string file)
    {
        List<string> files = [file];
        var overrideFile = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + ".override" + Path.GetExtension(file));
        if (File.Exists(overrideFile))
        {
            files.Add(overrideFile);
        }

        return files;
    }

    /// <summary>A compose command on the file (with its override, and the IDE's own after them in fast mode).</summary>
    private static List<string> ComposeCommand(string file, string? ideOverride, params IReadOnlyList<string> command)
    {
        List<string> arguments = ["compose"];
        foreach (var each in ComposeFiles(file))
        {
            arguments.AddRange(["-f", each]);
        }

        if (ideOverride is not null)
        {
            arguments.AddRange(["-f", ideOverride]);
        }

        arguments.AddRange(["-p", ContainerEngine.ComposeProjectName(file), .. command]);
        return arguments;
    }

    /// <summary>The IDE's override in the solution's state folder (D55); null without a solution.</summary>
    public string? ComposeOverridePath() =>
        Solutions.Solution is { } solution ? Path.Combine(SolutionStateLayout.Directory(solution.Path, solution.IsFolder), ComposeFastMode.OverrideFileName) : null;

    /// <summary>The .dcproj's <c>DockerServiceName</c> (the service the classic IDE debugs), or null.</summary>
    public static string? ComposeServiceName(string dcproj)
    {
        try
        {
            var document = System.Xml.Linq.XDocument.Load(dcproj);
            return document.Descendants().FirstOrDefault(e => e.Name.LocalName == "DockerServiceName")?.Value.Trim() is { Length: > 0 } name ? name : null;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The <c>DockerServiceName</c> of the <c>.dcproj</c> next to the compose file, if the solution has one.</summary>
    private string? ComposeStartupService(string file, string? argument)
    {
        if (argument is not null && argument.EndsWith(".dcproj", StringComparison.OrdinalIgnoreCase))
        {
            return ComposeServiceName(argument);
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(file));
        return Solutions.Projects.Values.FirstOrDefault(p => p.IsComposeProject && string.Equals(Path.GetDirectoryName(p.Path), folder, StringComparison.OrdinalIgnoreCase)) is { } dcproj
            ? ComposeServiceName(dcproj.Path)
            : null;
    }

    private bool ComposeFastModeOn => Workbench.Settings.GetBoolean(ContainerLaunch.FastModeSetting, true);

    /// <summary>A local path as the engine mounts it (a Linux path for an engine in WSL).</summary>
    private Func<string, string>? ComposeHostPath(ContainerEngine engine) =>
        Path.GetFileNameWithoutExtension(engine.Tool[0]).Equals("wsl", StringComparison.OrdinalIgnoreCase) ? p => WslLaunch.ToLinuxPath(p) : null;

    // ------------------------------------------------------------------ fast mode

    /// <summary>
    /// Plans the compose app's fast-mode run: reads it, finds its .NET services, builds each one's project (as F5 does,
    /// <c>build.beforeRun</c>) and plans its run, debugged where <paramref name="debug"/> says. Null when it couldn't be
    /// read or a build failed (both reported). A run of the same file keeps what the last one knew.
    /// </summary>
    private async Task<ComposeRun?> PlanComposeAsync(string file, string? argument, Func<string, bool> debug)
    {
        var engine = ContainerEngine;
        var project = ContainerEngine.ComposeProjectName(file);
        ComposeModel model;
        try
        {
            model = await engine.ComposeModelAsync(ComposeFiles(file), project, CancellationToken.None).ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            Workbench.Notifications.Error(ex.Message);
            return null;
        }

        var run = _compose is { } previous && string.Equals(previous.File, file, StringComparison.OrdinalIgnoreCase) ? previous : new ComposeRun(file, project, model);
        run.Model = model;
        var projects = Solutions.Projects.Keys.ToList();
        var startup = ComposeStartupService(file, argument);
        var dotnet = await Task.Run(() => ComposeFastMode.DotnetServices(model, file, projects, startup)).ConfigureAwait(true);
        foreach (var skipped in dotnet.Where(s => s.BaseStage is null))
        {
            ContainerLog.AppendLine($"{skipped.Service}: {Path.GetFileName(skipped.Dockerfile)} has no 'base' stage, so it runs as the compose file says.");
        }

        var services = new List<ComposeFastService>();
        foreach (var service in dotnet.Where(s => s.BaseStage is not null).OrderByDescending(s => s.IsStartup))
        {
            if (!await BuildBeforeRunAsync(service.Project).ConfigureAwait(true))
            {
                StatusBar.ShowMessage(Localizer.Format("msg.compose.theBuildOfFailed", "The build of {0} failed, so the compose app didn't start.", Path.GetFileNameWithoutExtension(service.Project)), TimeSpan.FromSeconds(6));
                return null;
            }

            var (info, _, _) = await Runs.GetProfilesAsync(service.Project, CancellationToken.None).ConfigureAwait(true);
            if (info.TargetPath is not { Length: > 0 } target)
            {
                ContainerLog.AppendLine($"{service.Service}: {Path.GetFileName(service.Project)} has no build output to run, so it runs as the compose file says.");
                continue;
            }

            var compose = model.Services.First(s => s.Name == service.Service);
            var debugged = debug(service.Service);
            services.Add(await Task.Run(() => ComposeFastMode.Plan(service, compose, target, debugged)).ConfigureAwait(true));
        }

        run.Services = services;
        return run;
    }

    /// <summary>Writes the IDE's override for <paramref name="run"/>; null when it couldn't be written (reported).</summary>
    private async Task<string?> WriteComposeOverrideAsync(ComposeRun run)
    {
        if (Solutions.Solution is not { } solution)
        {
            return null;
        }

        var text = ComposeFastMode.Override(run.File, run.Project, [.. run.Services], ComposeHostPath(ContainerEngine));
        try
        {
            return await Task.Run(() =>
            {
                var path = Path.Combine(SolutionStateLayout.EnsureDirectory(solution.Path, solution.IsFolder), ComposeFastMode.OverrideFileName);
                SolutionStateLayout.WriteAllTextAtomic(path, text);
                return path;
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.compose.couldNotWriteTheOverride", "Could not write the IDE's compose override: {0}", ex.Message));
            return null;
        }
    }

    // ------------------------------------------------------------------ up and down

    /// <summary>
    /// Compose Up: builds and starts the compose file's services, detached; output in Container Tools. In fast mode its
    /// .NET services run the IDE's Debug build, and once they run, a service whose build changed since the last Compose
    /// Up is restarted (the others are left running).
    /// </summary>
    public async Task<bool> ComposeUpAsync(string? argument)
    {
        using var busy = ComposeBusy();
        if (ComposeFileFor(argument) is not { } file)
        {
            Workbench.Notifications.Warning(Localizer.Get("msg.containers.theresNoComposeFileHereCompose", "There's no compose file here (compose.yaml or docker-compose.yml)."));
            return false;
        }

        if (!ComposeFastModeOn)
        {
            return await RunComposeAsync(file, null, "up", ["up", "-d", "--build"]).ConfigureAwait(true);
        }

        Workbench.Output.Show(ContainerLog);
        var debugged = _compose is { } current && string.Equals(current.File, file, StringComparison.OrdinalIgnoreCase) && Debugger.IsActive ? current.Programs.Keys.ToHashSet(StringComparer.Ordinal) : [];
        if (await PlanComposeAsync(file, argument, debugged.Contains).ConfigureAwait(true) is not { } run)
        {
            return false;
        }

        if (run.Services.Count == 0)
        {
            return await RunComposeAsync(file, null, "up", ["up", "-d", "--build"]).ConfigureAwait(true);
        }

        if (await WriteComposeOverrideAsync(run).ConfigureAwait(true) is not { } ideOverride)
        {
            return false;
        }

        _compose = run;
        if (!await RunComposeAsync(file, ideOverride, "up", ["up", "-d", "--build"]).ConfigureAwait(true))
        {
            return false;
        }

        // Rerun: a service whose build changed restarts on it; one that didn't goes on (compose recreated those whose
        // configuration changed).
        var runServices = run.Services.Where(s => !s.Debug).ToList();
        var stamps = await Task.Run(() => runServices.ToDictionary(s => s.Service.Service, s => ComposeFastMode.BuildStamp(s.Output), StringComparer.Ordinal)).ConfigureAwait(true);
        var changed = ComposeFastMode.Changed(run.RunStamps, stamps);
        run.RunStamps.Clear();
        foreach (var (service, stamp) in stamps)
        {
            run.RunStamps[service] = stamp;
        }

        if (changed.Count > 0)
        {
            ContainerLog.AppendLine($"Restarting the services whose build changed: {string.Join(", ", changed)}.");
            return await RunComposeAsync(file, ideOverride, "restart", ["restart", .. changed]).ConfigureAwait(true);
        }

        return true;
    }

    public async Task<bool> ComposeDownAsync(string? argument)
    {
        if (ComposeFileFor(argument) is not { } file)
        {
            Workbench.Notifications.Warning(Localizer.Get("msg.containers.theresNoComposeFileHereCompose", "There's no compose file here (compose.yaml or docker-compose.yml)."));
            return false;
        }

        if (_compose is { } run && string.Equals(run.File, file, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var index in run.Programs.Values.ToList())
            {
                await EndComposeProgramAsync(index).ConfigureAwait(true);
            }

            _compose = null;
        }

        return await RunComposeAsync(file, null, "down", ["down"]).ConfigureAwait(true);
    }

    private async Task<bool> RunComposeAsync(string file, string? ideOverride, string verb, IReadOnlyList<string> command)
    {
        var engine = ContainerEngine;
        Workbench.Output.Show(ContainerLog);
        var arguments = ComposeCommand(file, ideOverride, command);
        ContainerLog.AppendLine($"> {engine.Name} {string.Join(' ', arguments)}");
        StatusBar.ShowMessage(Localizer.Format("msg.containers.compose", "Compose {0}: {1}…", verb, Path.GetFileName(file)));
        try
        {
            var result = await engine.RunAsync(arguments, CancellationToken.None, line => ContainerLog.AppendLine(line), Path.GetDirectoryName(file)).ConfigureAwait(true);
            StatusBar.ShowMessage(result.Succeeded
                ? Localizer.Format("msg.compose.finished", "Compose {0} finished.", verb)
                : Localizer.Format("msg.compose.failed", "Compose {0} failed (see Output → Container Tools).", verb), TimeSpan.FromSeconds(6));
            _ = ContainersWindow.RefreshAsync();
            return result.Succeeded;
        }
        catch (InvalidOperationException ex)
        {
            StatusBar.ClearMessage();
            Workbench.Notifications.Error(ex.Message);
            return false;
        }
    }

    // ------------------------------------------------------------------ debugging

    /// <summary>
    /// Debug Compose: in fast mode, starts every service and debugs each .NET one as a program of one debug session, the
    /// debugger starting it in its container so breakpoints in its start-up code hit. With fast mode off (or no .NET
    /// service it can run), Compose Up, then the debugger attaches to the .NET process of the service picked (the only
    /// one, or the .dcproj's <c>DockerServiceName</c>).
    /// </summary>
    public async Task<bool> ComposeDebugAsync(string? argument, string? service = null)
    {
        using var busy = ComposeBusy();
        if (ComposeFileFor(argument) is not { } file)
        {
            Workbench.Notifications.Warning(Localizer.Get("msg.containers.theresNoComposeFileHereCompose", "There's no compose file here (compose.yaml or docker-compose.yml)."));
            return false;
        }

        if (Debugger.IsActive)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.debugSessions.aDebugSessionIsAlreadyRunning", "A debug session is already running."), TimeSpan.FromSeconds(4));
            return false;
        }

        if (!ComposeFastModeOn)
        {
            return await ComposeAttachAsync(file, argument, service).ConfigureAwait(true);
        }

        Workbench.Output.Show(ContainerLog);
        if (await PlanComposeAsync(file, argument, name => service is null || name == service).ConfigureAwait(true) is not { } run)
        {
            return false;
        }

        if (!run.Services.Any(s => s.Debug))
        {
            return await ComposeAttachAsync(file, argument, service).ConfigureAwait(true);
        }

        if (await WriteComposeOverrideAsync(run).ConfigureAwait(true) is not { } ideOverride)
        {
            return false;
        }

        _compose = run;
        if (!await RunComposeAsync(file, ideOverride, "up", ["up", "-d", "--build"]).ConfigureAwait(true))
        {
            return false;
        }

        // The services left to run without the debugger restart on a build, as after Compose Up.
        var runServices = run.Services.Where(s => !s.Debug).ToList();
        var stamps = await Task.Run(() => runServices.ToDictionary(s => s.Service.Service, s => ComposeFastMode.BuildStamp(s.Output), StringComparer.Ordinal)).ConfigureAwait(true);
        run.RunStamps.Clear();
        foreach (var (name, stamp) in stamps)
        {
            run.RunStamps[name] = stamp;
        }

        DebugLog.Clear();
        Debugger.JustMyCode = Workbench.Settings.GetBoolean(JustMyCodeSetting, true);
        Debugger.StepFiltering = Workbench.Settings.GetBoolean(StepFilteringSetting, true);
        Layout.ShowToolWindow(DebugConsoleViewModel.ToolWindowId, focus: false);
        var launches = new List<(string Service, DebugLaunch Launch)>();
        foreach (var fast in run.Services.Where(s => s.Debug))
        {
            if (await ComposeLaunchAsync(run, fast, companion: launches.Count == 0).ConfigureAwait(true) is { } launch)
            {
                launches.Add((fast.Service.Service, launch));
            }
        }

        if (launches.Count == 0)
        {
            return false;
        }

        run.Programs.Clear();
        for (var i = 0; i < launches.Count; i++)
        {
            run.Programs[launches[i].Service] = i;
        }

        try
        {
            await Debugger.LaunchAsync([.. launches.Select(l => l.Launch)], CancellationToken.None, compound: true).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException or DapException or System.ComponentModel.Win32Exception)
        {
            run.Programs.Clear();
            Workbench.Notifications.Error(Localizer.Format("msg.compose.couldNotDebug", "Could not debug the compose app: {0}", ex.Message));
            return false;
        }
    }

    /// <summary>
    /// The debug launch of a fast-mode service: the debugger copied into its container (it's the IDE's: no question),
    /// starting <c>dotnet /app/App.dll</c> there with the container's environment, paths mapped back to the solution's
    /// files, and edits applied to it (its build is this machine's). Null when its container doesn't run (reported).
    /// </summary>
    private async Task<DebugLaunch?> ComposeLaunchAsync(ComposeRun run, ComposeFastService fast, bool companion)
    {
        var engine = ContainerEngine;
        var name = fast.Service.Service;
        var containerName = run.Model.Services.FirstOrDefault(s => s.Name == name)?.ContainerName;
        try
        {
            ContainerSummary? container = null;
            for (var attempt = 0; attempt < 40 && container is not { IsRunning: true }; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(500).ConfigureAwait(true);
                }

                container = await engine.ComposeServiceContainerAsync(run.Project, name, containerName, CancellationToken.None).ConfigureAwait(true);
            }

            if (container is not { IsRunning: true })
            {
                throw new InvalidOperationException($"its container isn't running ({container?.Status ?? "not found"}).");
            }

            var target = new ContainerTarget(container.Name, engine.Tool);
            var debugger = await RemoteDebuggers.EnsureAsync(target, Debugger.UsesCodalusAdapter, _ => Task.FromResult(true), new Progress<string>(line => DebugLog.AppendLine($"[{name}] {line}")), CancellationToken.None).ConfigureAwait(true);
            var map = new SourceMap(SourceFileMap(), SolutionFiles());
            if (companion)
            {
                UseRemoteCompanion(RemoteShell.Companion(target, debugger));
            }

            var environment = new Dictionary<string, string>(fast.Environment, StringComparer.Ordinal);
            var arguments = new LaunchArguments("dotnet", [fast.ContainerProgram], ContainerLaunch.AppFolder, environment);
            var plan = new LaunchPlan("dotnet", fast.ContainerProgram, ContainerLaunch.AppFolder, environment, null);
            DebugLog.AppendLine($"[{name}] > dotnet {fast.ContainerProgram} (in the container {container.Name})");
            return new DebugLaunch(name, plan)
            {
                Remote = new RemoteLaunch(RemoteShell.Adapter(target, debugger), arguments, map, target.Display) { LocalBuild = true },
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException or InvalidDataException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.compose.couldNotDebugService", "Could not debug the service {0}: {1}", name, ex.Message));
            return null;
        }
    }

    /// <summary>
    /// Compose Up, then the debugger attaches to the .NET process of the service picked (fast mode off, M17a): the
    /// only one, or the .dcproj's <c>DockerServiceName</c>, else the one the user picks.
    /// </summary>
    private async Task<bool> ComposeAttachAsync(string file, string? argument, string? service)
    {
        if (!await RunComposeAsync(file, null, "up", ["up", "-d", "--build"]).ConfigureAwait(true))
        {
            return false;
        }

        service ??= ComposeStartupService(file, argument);
        var engine = ContainerEngine;
        var project = ContainerEngine.ComposeProjectName(file);
        var containers = (await engine.ContainersAsync(CancellationToken.None, "com.docker.compose.project=" + project).ConfigureAwait(true)).Where(c => c.IsRunning).ToList();
        if (containers.Count == 0)
        {
            // podman-compose labels its containers differently: fall back to the project's name prefix.
            containers = [.. (await engine.ContainersAsync(CancellationToken.None).ConfigureAwait(true)).Where(c => c.IsRunning && c.Name.StartsWith(project, StringComparison.OrdinalIgnoreCase))];
        }

        var picked = service is not null ? containers.FirstOrDefault(c => c.Name.Contains(service, StringComparison.OrdinalIgnoreCase)) : containers.Count == 1 ? containers[0] : null;
        if (picked is null && containers.Count > 0)
        {
            var items = containers.Select(c => new QuickPickItem(c.Name) { Tag = c, Detail = c.Image }).ToList();
            picked = (await Palette.ShowAsync(items, "Debug which service?").ConfigureAwait(true))?.Tag as ContainerSummary;
        }

        if (picked is null)
        {
            Workbench.Notifications.Warning(Localizer.Get("msg.containers.noServiceOfTheComposeFile", "No service of the compose file is running."));
            return false;
        }

        var target = new ContainerTarget(picked.Name, engine.Tool);
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var processes = await RemoteShell.ProcessesAsync(target, CancellationToken.None).ConfigureAwait(true);
            if (processes.FirstOrDefault(p => p.IsDotnet) is { } process)
            {
                return await AttachToRemoteAsync(target, process.Name, process.Id).ConfigureAwait(true);
            }

            await Task.Delay(500).ConfigureAwait(true);
        }

        Workbench.Notifications.Warning(Localizer.Format("msg.containers.noNetProcessRunsInTo", "No .NET process runs in {0} to debug.", picked.Name));
        return false;
    }

    // ------------------------------------------------------------------ one service

    /// <summary>What can be done to one service of the compose app while the others run.</summary>
    public enum ComposeServiceAction
    {
        Restart,
        Stop,
        Debug,
        Detach,
    }

    /// <summary>Whether <paramref name="service"/>'s program runs in the debug session.</summary>
    private bool IsComposeServiceDebugged(ComposeRun run, string service, out int index) =>
        run.Programs.TryGetValue(service, out index) && Debugger.IsActive && index < Debugger.Processes.Count && !Debugger.Processes[index].Ended;

    /// <summary>
    /// Restarts, stops, debugs or detaches one service of the compose app the IDE runs, the others left running:
    /// <list type="bullet">
    /// <item>Restart: a debugged service's project is built and its program starts again in the session; any other
    /// service is restarted (a fast-mode one on its new build).</item>
    /// <item>Stop: its program leaves the session, and the service stops.</item>
    /// <item>Debug: a fast-mode .NET service joins the debug session (one starts if none runs).</item>
    /// <item>Detach: its program leaves the session, and the service goes on running its build without the debugger.</item>
    /// </list>
    /// </summary>
    public async Task<bool> ComposeServiceAsync(ComposeServiceAction action, string? service = null)
    {
        using var busy = ComposeBusy();
        if (_compose is not { } run)
        {
            Workbench.Notifications.Warning(Localizer.Get("msg.compose.noComposeAppRuns", "No compose app runs from Codalus IDE: use Compose Up or Debug Compose first."));
            return false;
        }

        if (service is null)
        {
            var items = run.Model.Services.Select(s => new QuickPickItem(s.Name)
            {
                Tag = s.Name,
                Detail = IsComposeServiceDebugged(run, s.Name, out _) ? Localizer.Get("msg.compose.debugged", "Debugged")
                    : run.Service(s.Name) is not null ? Localizer.Get("msg.compose.fastMode", ".NET, fast mode")
                    : s.Image ?? string.Empty,
            }).ToList();
            if (await Palette.ShowAsync(items, Localizer.Format("msg.compose.whichService", "{0} which service?", action)).ConfigureAwait(true) is not { Tag: string picked })
            {
                return false;
            }

            service = picked;
        }

        if (run.Model.Services.All(s => s.Name != service))
        {
            Workbench.Notifications.Warning(Localizer.Format("msg.compose.noSuchService", "{0} has no service {1}.", Path.GetFileName(run.File), service));
            return false;
        }

        var debugged = IsComposeServiceDebugged(run, service, out var index);
        var fast = run.Service(service);
        switch (action)
        {
            case ComposeServiceAction.Restart when debugged:
                if (!await BuildBeforeRunAsync(fast!.Service.Project).ConfigureAwait(true) || await ComposeLaunchAsync(run, fast, companion: false).ConfigureAwait(true) is not { } again)
                {
                    return false;
                }

                try
                {
                    var (restarted, _) = await Debugger.RestartProgramAsync(index, again, CancellationToken.None).ConfigureAwait(true);
                    run.Programs[service] = restarted;
                    return true;
                }
                catch (InvalidOperationException ex)
                {
                    Workbench.Notifications.Error(ex.Message);
                    return false;
                }

            case ComposeServiceAction.Restart:
                if (fast is not null)
                {
                    if (!await BuildBeforeRunAsync(fast.Service.Project).ConfigureAwait(true))
                    {
                        return false;
                    }

                    if (fast.Debug)
                    {
                        // Its container idles for a debugger that has gone: it runs its build again by itself.
                        return await SetComposeServiceModeAsync(run, fast, debug: false).ConfigureAwait(true);
                    }

                    run.RunStamps[service] = await Task.Run(() => ComposeFastMode.BuildStamp(fast.Output)).ConfigureAwait(true);
                }

                return await RunComposeAsync(run.File, WrittenComposeOverride(run), "restart", ["restart", service]).ConfigureAwait(true);

            case ComposeServiceAction.Stop:
                if (debugged)
                {
                    await EndComposeProgramAsync(index).ConfigureAwait(true);
                    run.Programs.Remove(service);
                }

                return await RunComposeAsync(run.File, WrittenComposeOverride(run), "stop", ["stop", service]).ConfigureAwait(true);

            case ComposeServiceAction.Debug:
                if (debugged)
                {
                    StatusBar.ShowMessage(Localizer.Format("msg.compose.alreadyDebugged", "{0} is debugged already.", service), TimeSpan.FromSeconds(4));
                    return true;
                }

                if (fast is null)
                {
                    Workbench.Notifications.Warning(Localizer.Format("msg.compose.notFastMode", "{0} isn't a .NET service fast mode runs (a project built from a Dockerfile with a 'base' stage), so it can't join the debug session.", service));
                    return false;
                }

                if (Debugger.IsActive && !Debugger.IsCompound)
                {
                    StatusBar.ShowMessage(Localizer.Get("msg.debugSessions.aDebugSessionIsAlreadyRunning", "A debug session is already running."), TimeSpan.FromSeconds(4));
                    return false;
                }

                if (!await BuildBeforeRunAsync(fast.Service.Project).ConfigureAwait(true)
                    || (!fast.Debug && !await SetComposeServiceModeAsync(run, fast, debug: true).ConfigureAwait(true))
                    || await ComposeLaunchAsync(run, run.Service(service)!, companion: !Debugger.IsActive).ConfigureAwait(true) is not { } launch)
                {
                    return false;
                }

                try
                {
                    if (Debugger.IsActive)
                    {
                        var (added, _) = Debugger.AddLaunch(launch, CancellationToken.None);
                        run.Programs[service] = added;
                    }
                    else
                    {
                        run.Programs.Clear();
                        run.Programs[service] = 0;
                        Layout.ShowToolWindow(DebugConsoleViewModel.ToolWindowId, focus: false);
                        await Debugger.LaunchAsync([launch], CancellationToken.None, compound: true).ConfigureAwait(true);
                    }

                    return true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException or DapException or System.ComponentModel.Win32Exception)
                {
                    run.Programs.Remove(service);
                    Workbench.Notifications.Error(Localizer.Format("msg.compose.couldNotDebugService", "Could not debug the service {0}: {1}", service, ex.Message));
                    return false;
                }

            case ComposeServiceAction.Detach:
                if (!debugged || fast is null)
                {
                    StatusBar.ShowMessage(Localizer.Format("msg.compose.notDebugged", "{0} isn't debugged.", service), TimeSpan.FromSeconds(4));
                    return false;
                }

                await EndComposeProgramAsync(index).ConfigureAwait(true);
                run.Programs.Remove(service);
                return await SetComposeServiceModeAsync(run, fast, debug: false).ConfigureAwait(true);

            default:
                return false;
        }
    }

    /// <summary>The override as written last, when fast mode runs the app (compose commands then read it too).</summary>
    private string? WrittenComposeOverride(ComposeRun run) =>
        run.Services.Count > 0 && ComposeOverridePath() is { } path && File.Exists(path) ? path : null;

    /// <summary>
    /// Switches a fast-mode service between idling for the debugger and running its build: the override is written
    /// again and compose recreates that service alone (<c>up -d --no-deps</c>).
    /// </summary>
    private async Task<bool> SetComposeServiceModeAsync(ComposeRun run, ComposeFastService fast, bool debug)
    {
        var index = run.Services.IndexOf(fast);
        run.Services[index] = fast with { Debug = debug };
        if (await WriteComposeOverrideAsync(run).ConfigureAwait(true) is not { } ideOverride)
        {
            return false;
        }

        if (!debug)
        {
            run.RunStamps[fast.Service.Service] = await Task.Run(() => ComposeFastMode.BuildStamp(fast.Output)).ConfigureAwait(true);
        }
        else
        {
            run.RunStamps.Remove(fast.Service.Service);
        }

        return await RunComposeAsync(run.File, ideOverride, "up", ["up", "-d", "--no-deps", fast.Service.Service]).ConfigureAwait(true);
    }

    private async Task EndComposeProgramAsync(int index)
    {
        try
        {
            await Debugger.EndProgramAsync(index).ConfigureAwait(true);
        }
        catch (InvalidOperationException)
        {
            // The session ended meanwhile.
        }
    }

    /// <summary>Opens the IDE's compose override (D55) as a read-only document; says so when fast mode hasn't written one.</summary>
    public async Task ShowComposeOverrideAsync()
    {
        if (ComposeOverridePath() is not { } path || !File.Exists(path))
        {
            StatusBar.ShowMessage(Localizer.Get("msg.compose.noOverrideYet", "There's no compose override yet: Compose Up or Debug Compose in fast mode writes it."), TimeSpan.FromSeconds(6));
            return;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(path).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error(ex.Message);
            return;
        }

        var uri = "codalus-compose-override:///" + Uri.EscapeDataString(path);
        if (Editors.FirstOrDefault(e => e.Model.Uri == uri) is { } open)
        {
            Layout.CloseDocument(open);
        }

        var model = Editor.CodeDocument.ReadOnly(path, uri, text, Localizer.Get("msg.compose.overrideLabel", "the IDE's override"));
        model.LanguageId = Languages.LanguageIdOf(path);
        Layout.OpenDocument(CreateView(model, primary: true), focus: true);
    }
}
