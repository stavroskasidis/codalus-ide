using Codalus.Debugging;
using Codalus.Platform;
using Codalus.Projects.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Build;
using Codalus.Workbench.Containers;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Debugging.Remote;
using Codalus.Workbench.Output;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// Containers (M17a, ADR 0042): the Containers window, Add Docker Support, running and debugging a project in a
/// container through its "Container (Dockerfile)" launch profile, and compose files (and the classic IDE's <c>.dcproj</c>)
/// up, down and debugged. Everything goes through the user's docker or podman.
/// </summary>
public sealed partial class ShellHost
{
    private string? _debugContainer;

    public ContainersViewModel ContainersWindow { get; private set; } = null!;

    public OutputChannel ContainerLog => Workbench.Output.GetOrCreate("codalus.containers", "Container Tools");

    /// <summary>The engine of the settings: <c>containers.tool</c>, else <c>debug.containerTool</c>, else docker or podman.</summary>
    public ContainerEngine ContainerEngine =>
        ContainerEngine.Resolve(Workbench.Settings.GetString(ContainerEngine.ToolSetting) is { Length: > 0 } tool ? tool : Workbench.Settings.GetString(RemoteTargets.ContainerToolSetting));

    private void InitializeContainers()
    {
        ContainersWindow = new ContainersViewModel(
            () => ContainerEngine,
            container => AttachToRemoteAsync(new ContainerTarget(container.Name, ContainerEngine.Tool), processName: null),
            container => OpenContainerTerminalAsync(container.Name),
            uri => OpenExternalUrl(uri),
            OpenContainerFileAsync,
            container => OpenRemoteWindowAsync("container:" + container.Name),
            ShowComposeOverrideAsync);
        Debugger.Ended += (_, _) =>
        {
            if (Interlocked.Exchange(ref _debugContainer, null) is { } container)
            {
                _ = RemoveContainerQuietlyAsync(container);
            }
        };
    }

    public async Task ShowContainersAsync()
    {
        Layout.ShowToolWindow(ContainersViewModel.ToolWindowId, focus: true);
        await ContainersWindow.RefreshAsync().ConfigureAwait(true);
    }

    private async Task RemoveContainerQuietlyAsync(string container)
    {
        try
        {
            await ContainerEngine.RemoveAsync(container, CancellationToken.None).ConfigureAwait(false);
            ContainerLog.AppendLine($"Removed the container {container}.");
        }
        catch (InvalidOperationException ex)
        {
            ContainerLog.AppendLine($"Could not remove the container {container}: {ex.Message}");
        }
    }

    /// <summary>A shell in a running container, in the integrated terminal (<c>exec -it … sh</c>).</summary>
    public Task OpenContainerTerminalAsync(string container)
    {
        var engine = ContainerEngine;
        try
        {
            var (program, arguments) = engine.Command(["exec", "-it", container, "sh", "-c", "command -v bash >/dev/null && exec bash || exec sh"]);
            var session = Terminals.Run($"{container} ({engine.Name})", program, arguments, Solutions.Solution?.Directory ?? Environment.CurrentDirectory, null);
            Layout.ShowToolWindow(TerminalViewModel.ToolWindowId);
            TerminalWindow.Show(session);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.containers.couldNotOpenATerminalIn", "Could not open a terminal in {0}: {1}", container, ex.Message));
        }

        return Task.CompletedTask;
    }

    /// <summary>Copies a file out of a container into the cache and opens it (changes stay local).</summary>
    public async Task OpenContainerFileAsync(string container, string path)
    {
        var local = Path.Combine(Paths.Cache, "containers", container, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            await ContainerEngine.CopyOutAsync(container, path, local, CancellationToken.None).ConfigureAwait(true);
            await OpenFileAsync(local).ConfigureAwait(true);
            StatusBar.ShowMessage(Localizer.Format("msg.containers.fromACopyChangesArentWritten", "{0} from {1}: a copy; changes aren't written back to the container.", path, container), TimeSpan.FromSeconds(6));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.containers.couldNotOpenFrom", "Could not open {0} from {1}: {2}", path, container, ex.Message));
        }
    }

    // ------------------------------------------------------------------ Add Docker Support

    /// <summary>
    /// Add Docker Support: a Dockerfile in the project's folder following the .NET templates, a <c>.dockerignore</c> in
    /// the solution's folder (the build context), and the "Container (Dockerfile)" launch profile.
    /// </summary>
    public async Task<string?> AddDockerSupportAsync(string? argument)
    {
        var projectPath = argument ?? SelectedProjectPath() ?? Runs.StartupProject;
        if (projectPath is null || !Solutions.Projects.TryGetValue(projectPath, out var project) || project.IsComposeProject)
        {
            Workbench.Notifications.Warning(Localizer.Get("msg.containers.selectAProjectToAddDocker", "Select a project to add Docker support to."));
            return null;
        }

        var framework = project.TargetFrameworks.Select(DockerSupport.ImageVersion).FirstOrDefault(v => v is not null) is not null
            ? project.TargetFrameworks.First(f => DockerSupport.ImageVersion(f) is not null)
            : null;
        if (framework is null)
        {
            Workbench.Notifications.Warning($"{project.Name} targets {string.Join(", ", project.TargetFrameworks)}, which has no .NET container image.");
            return null;
        }

        var folder = Path.GetDirectoryName(projectPath)!;
        var dockerfile = Path.Combine(folder, "Dockerfile");
        if (File.Exists(dockerfile) && !await Dialogs.ConfirmAsync(Localizer.Get("msg.containers.addDockerSupport", "Add Docker Support"), Localizer.Format("msg.containers.alreadyHasADockerfileReplaceIt", "{0} already has a Dockerfile. Replace it?", project.Name), Localizer.Get("msg.containers.replace", "Replace"), danger: true).ConfigureAwait(true))
        {
            return null;
        }

        var context = Solutions.Solution?.Directory ?? folder;
        var isWeb = project.References.Any(r => r.Kind == ReferenceKinds.Framework && r.Name.Equals("Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase));
        var references = project.References.Where(r => r.Kind == ReferenceKinds.Project && r.Path is not null).Select(r => r.Path!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            await File.WriteAllTextAsync(dockerfile, DockerSupport.Dockerfile(new DockerProject(projectPath, framework, isWeb, references), context)).ConfigureAwait(true);
            var ignore = Path.Combine(context, ".dockerignore");
            if (!File.Exists(ignore))
            {
                await File.WriteAllTextAsync(ignore, DockerSupport.DockerIgnore).ConfigureAwait(true);
            }

            var settings = Path.Combine(folder, "Properties", "launchSettings.json");
            var existing = File.Exists(settings) ? await File.ReadAllTextAsync(settings).ConfigureAwait(true) : null;
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            await File.WriteAllTextAsync(settings, DockerSupport.AddContainerProfile(existing, isWeb)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.containers.couldNotAddDockerSupportTo", "Could not add Docker support to {0}: {1}", project.Name, ex.Message));
            return null;
        }

        await OpenFileAsync(dockerfile).ConfigureAwait(true);
        Workbench.Notifications.Info(Localizer.Format("msg.containers.dockerSupportAddedToChooseThe", "Docker support added to {0}: choose the '{1}' launch profile to run or debug it in a container.", project.Name, DockerSupport.ContainerProfileName));
        return dockerfile;
    }

    private string? SelectedProjectPath() =>
        Explorer.Selected?.Node.Path is { } path && Solutions.Projects.ContainsKey(path) ? path : null;

    // ------------------------------------------------------------------ run and debug in a container

    /// <summary>Whether <paramref name="profile"/> runs the project in a container.</summary>
    private static bool IsContainerProfile(LaunchProfile? profile) => profile?.CommandName == LaunchProfile.Docker;

    /// <summary>
    /// Runs (or debugs) a built project in a container with its "Container (Dockerfile)" profile. Fast mode: the
    /// Dockerfile's base stage with the build output mounted; the app starts through <c>exec</c>, or under netcoredbg
    /// copied into the container. The container is removed when the app stops.
    /// </summary>
    public async Task<bool> StartInContainerAsync(string projectPath, RunInfo info, LaunchProfile profile, bool debug)
    {
        var name = Path.GetFileNameWithoutExtension(projectPath);
        if (DockerSupport.FindDockerfile(projectPath) is not { } dockerfile)
        {
            Workbench.Notifications.Warning(Localizer.Format("msg.containers.hasNoDockerfileUseAddDocker", "{0} has no Dockerfile: use Add Docker Support first.", name));
            return false;
        }

        var engine = ContainerEngine;
        Workbench.Output.Show(ContainerLog);
        ContainerLog.Clear();
        var (os, error) = await engine.ServerOsAsync(CancellationToken.None).ConfigureAwait(true);
        if (os is null)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.containers.doesntAnswerSoCantRunIn", "{0} doesn't answer, so {1} can't run in a container: {2}", engine.Name, name, error));
            return false;
        }

        if (!os.Equals("linux", StringComparison.OrdinalIgnoreCase))
        {
            Workbench.Notifications.Error(Localizer.Format("msg.containers.runsContainersSDockerfileNeedsLinux", "{0} runs {1} containers; {2}'s Dockerfile needs Linux containers.", engine.Name, os, name));
            return false;
        }

        var environment = Runs.Adjust(LaunchPlanner.Plan(info, profile)).Environment;
        var wsl = Path.GetFileNameWithoutExtension(engine.Tool[0]).Equals("wsl", StringComparison.OrdinalIgnoreCase);
        var launch = ContainerLaunch.Plan(
            info,
            profile,
            dockerfile,
            Solutions.Solution?.Directory,
            Workbench.Settings.GetBoolean(ContainerLaunch.FastModeSetting, true),
            environment,
            wsl ? p => WslLaunch.ToLinuxPath(p) : null);

        ContainerLog.AppendLine($"> {engine.Name} build -f {launch.Dockerfile}{(launch.Target is null ? string.Empty : " --target " + launch.Target)} -t {launch.Image} {launch.Context}");
        StatusBar.ShowMessage(Localizer.Format("msg.containers.buildingTheContainerImageOf", "Building the container image of {0}…", name));
        var build = await engine.BuildAsync(launch.Dockerfile, launch.Context, launch.Image, launch.Target, line => ContainerLog.AppendLine(line), CancellationToken.None).ConfigureAwait(true);
        StatusBar.ClearMessage();
        if (!build.Succeeded)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.containers.theContainerImageOfDidntBuild", "The container image of {0} didn't build (see Output → Container Tools).", name));
            return false;
        }

        try
        {
            await engine.RemoveAsync(launch.Name, CancellationToken.None).ConfigureAwait(true);
            ContainerLog.AppendLine($"> {engine.Name} {string.Join(' ', ContainerEngine.RunArguments(launch.Run))}");
            await engine.RunDetachedAsync(launch.Run, CancellationToken.None).ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            Workbench.Notifications.Error(ex.Message);
            return false;
        }

        _ = ContainersWindow.RefreshAsync();
        if (debug)
        {
            return await DebugInContainerAsync(name, launch, engine).ConfigureAwait(true);
        }

        var (program, arguments) = launch.FastMode ? engine.Command(launch.ExecArguments()) : engine.Command(["logs", "-f", launch.Name]);
        var plan = new LaunchPlan(program, string.Join(' ', arguments), Path.GetDirectoryName(projectPath)!, new Dictionary<string, string>(), launch.BrowserUrl) { ArgumentList = arguments };
        var running = Runs.StartPlan(projectPath, name, plan);
        running.Channel?.AppendLine($"(in the container {launch.Name}{(launch.BrowserUrl is null ? string.Empty : ", " + launch.BrowserUrl)})");
        _ = running.Exited.ContinueWith(_ => RemoveContainerQuietlyAsync(launch.Name), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        if (running.Channel is { } channel)
        {
            Workbench.Output.Show(channel);
        }

        return true;
    }

    private async Task<bool> DebugInContainerAsync(string name, ContainerLaunch launch, ContainerEngine engine)
    {
        var target = new ContainerTarget(launch.Name, engine.Tool);
        DebugLog.Clear();
        Debugger.JustMyCode = Workbench.Settings.GetBoolean(JustMyCodeSetting, true);
        Debugger.StepFiltering = Workbench.Settings.GetBoolean(StepFilteringSetting, true);
        Layout.ShowToolWindow(DebugConsoleViewModel.ToolWindowId, focus: false);
        try
        {
            // The container is ours: the debugger is copied into it without asking.
            var debugger = await RemoteDebuggers.EnsureAsync(target, Debugger.UsesCodalusAdapter, _ => Task.FromResult(true), new Progress<string>(line => DebugLog.AppendLine(line)), CancellationToken.None).ConfigureAwait(true);
            var map = new SourceMap(SourceFileMap(), SolutionFiles());
            UseRemoteCompanion(RemoteShell.Companion(target, debugger));
            _debugContainer = launch.Name;
            if (launch.FastMode)
            {
                var arguments = new LaunchArguments(launch.Program, [.. launch.Arguments], launch.WorkingDirectory, new Dictionary<string, string>(launch.Environment));
                var plan = new LaunchPlan(launch.Program, string.Join(' ', launch.Arguments), launch.WorkingDirectory, launch.Environment, launch.BrowserUrl);
                DebugLog.AppendLine($"[{name}] > {launch.Program} {string.Join(' ', launch.Arguments)} (in the container {launch.Name})");
                _pendingBrowserUrl = launch.BrowserUrl;
                await Debugger.LaunchAsync([new DebugLaunch(name, plan) { Remote = new RemoteLaunch(RemoteShell.Adapter(target, debugger), arguments, map, target.Display) }], CancellationToken.None).ConfigureAwait(true);
                return true;
            }

            // Regular mode: the image runs its own entry point; attach to its .NET process.
            for (var attempt = 0; attempt < 40; attempt++)
            {
                var processes = await RemoteShell.ProcessesAsync(target, CancellationToken.None).ConfigureAwait(true);
                if (processes.FirstOrDefault(p => p.IsDotnet) is { } process)
                {
                    if (launch.BrowserUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    {
                        OpenExternalUrl(uri);
                    }

                    await Debugger.AttachAsync(process.Id, CancellationToken.None, new RemoteAttach(RemoteShell.Adapter(target, debugger), map, target.Display), process.Name).ConfigureAwait(true);
                    return true;
                }

                await Task.Delay(500).ConfigureAwait(true);
            }

            throw new InvalidOperationException($"no .NET process started in {launch.Name}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException or InvalidDataException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.containers.couldNotDebugInItsContainer", "Could not debug {0} in its container: {1}", name, ex.Message));
            if (Interlocked.Exchange(ref _debugContainer, null) is { } container)
            {
                await RemoveContainerQuietlyAsync(container).ConfigureAwait(true);
            }
            else
            {
                await RemoveContainerQuietlyAsync(launch.Name).ConfigureAwait(true);
            }

            return false;
        }
    }
}
