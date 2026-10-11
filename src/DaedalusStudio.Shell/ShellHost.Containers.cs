using DaedalusStudio.Debugging;
using DaedalusStudio.Platform;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Containers;
using DaedalusStudio.Workbench.Debugging;
using DaedalusStudio.Workbench.Debugging.Remote;
using DaedalusStudio.Workbench.Output;

namespace DaedalusStudio.Shell;

/// <summary>
/// Containers (M17a, ADR 0042): the Containers window, Add Docker Support, running and debugging a project in a
/// container through its "Container (Dockerfile)" launch profile, and compose files (and Visual Studio's <c>.dcproj</c>)
/// up, down and debugged. Everything goes through the user's docker or podman.
/// </summary>
public sealed partial class ShellHost
{
    private string? _debugContainer;

    public ContainersViewModel ContainersWindow { get; private set; } = null!;

    public OutputChannel ContainerLog => Workbench.Output.GetOrCreate("daedalus.containers", "Container Tools");

    /// <summary>The engine of the settings: <c>containers.tool</c>, else <c>debug.containerTool</c>, else docker or podman.</summary>
    public ContainerEngine ContainerEngine =>
        ContainerEngine.Resolve(Workbench.Settings.GetString(ContainerEngine.ToolSetting) is { Length: > 0 } tool ? tool : Workbench.Settings.GetString(RemoteTargets.ContainerToolSetting));

    private void InitializeContainers()
    {
        ContainersWindow = new ContainersViewModel(
            () => ContainerEngine,
            container => AttachToRemoteAsync(new ContainerTarget(container.Name, ContainerEngine.Tool), processName: null),
            container => OpenContainerTerminalAsync(container.Name),
            ShellIntegration.OpenUrl,
            OpenContainerFileAsync);
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
            Workbench.Notifications.Error($"Could not open a terminal in {container}: {ex.Message}");
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
            StatusBar.ShowMessage($"{path} from {container}: a copy; changes aren't written back to the container.", TimeSpan.FromSeconds(6));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error($"Could not open {path} from {container}: {ex.Message}");
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
            Workbench.Notifications.Warning("Select a project to add Docker support to.");
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
        if (File.Exists(dockerfile) && !await Dialogs.ConfirmAsync("Add Docker Support", $"{project.Name} already has a Dockerfile. Replace it?", "Replace", danger: true).ConfigureAwait(true))
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
            Workbench.Notifications.Error($"Could not add Docker support to {project.Name}: {ex.Message}");
            return null;
        }

        await OpenFileAsync(dockerfile).ConfigureAwait(true);
        Workbench.Notifications.Info($"Docker support added to {project.Name}: choose the '{DockerSupport.ContainerProfileName}' launch profile to run or debug it in a container.");
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
            Workbench.Notifications.Warning($"{name} has no Dockerfile: use Add Docker Support first.");
            return false;
        }

        var engine = ContainerEngine;
        Workbench.Output.Show(ContainerLog);
        ContainerLog.Clear();
        var (os, error) = await engine.ServerOsAsync(CancellationToken.None).ConfigureAwait(true);
        if (os is null)
        {
            Workbench.Notifications.Error($"{engine.Name} doesn't answer, so {name} can't run in a container: {error}");
            return false;
        }

        if (!os.Equals("linux", StringComparison.OrdinalIgnoreCase))
        {
            Workbench.Notifications.Error($"{engine.Name} runs {os} containers; {name}'s Dockerfile needs Linux containers.");
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
        StatusBar.ShowMessage($"Building the container image of {name}…");
        var build = await engine.BuildAsync(launch.Dockerfile, launch.Context, launch.Image, launch.Target, line => ContainerLog.AppendLine(line), CancellationToken.None).ConfigureAwait(true);
        StatusBar.ClearMessage();
        if (!build.Succeeded)
        {
            Workbench.Notifications.Error($"The container image of {name} didn't build (see Output → Container Tools).");
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
            // The container is ours: netcoredbg is copied into it without asking.
            var debugger = await RemoteDebuggers.EnsureAsync(target, _ => Task.FromResult(true), new Progress<string>(line => DebugLog.AppendLine(line)), CancellationToken.None).ConfigureAwait(true);
            var map = new SourceMap(SourceFileMap(), SolutionFiles());
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
                        ShellIntegration.OpenUrl(uri);
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
            Workbench.Notifications.Error($"Could not debug {name} in its container: {ex.Message}");
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

    // ------------------------------------------------------------------ compose

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

    private static List<string> ComposeCommand(string file, params IReadOnlyList<string> command)
    {
        List<string> arguments = ["compose"];
        foreach (var each in ComposeFiles(file))
        {
            arguments.AddRange(["-f", each]);
        }

        arguments.AddRange(["-p", ContainerEngine.ComposeProjectName(file), .. command]);
        return arguments;
    }

    /// <summary>Compose Up: builds and starts the compose file's services, detached; output in Container Tools.</summary>
    public async Task<bool> ComposeUpAsync(string? argument)
    {
        if (ComposeFileFor(argument) is not { } file)
        {
            Workbench.Notifications.Warning("There's no compose file here (compose.yaml or docker-compose.yml).");
            return false;
        }

        return await RunComposeAsync(file, "up", ["up", "-d", "--build"]).ConfigureAwait(true);
    }

    public async Task<bool> ComposeDownAsync(string? argument)
    {
        if (ComposeFileFor(argument) is not { } file)
        {
            Workbench.Notifications.Warning("There's no compose file here (compose.yaml or docker-compose.yml).");
            return false;
        }

        return await RunComposeAsync(file, "down", ["down"]).ConfigureAwait(true);
    }

    private async Task<bool> RunComposeAsync(string file, string verb, IReadOnlyList<string> command)
    {
        var engine = ContainerEngine;
        Workbench.Output.Show(ContainerLog);
        var arguments = ComposeCommand(file, command);
        ContainerLog.AppendLine($"> {engine.Name} {string.Join(' ', arguments)}");
        StatusBar.ShowMessage($"Compose {verb}: {Path.GetFileName(file)}…");
        try
        {
            var result = await engine.RunAsync(arguments, CancellationToken.None, line => ContainerLog.AppendLine(line), Path.GetDirectoryName(file)).ConfigureAwait(true);
            StatusBar.ShowMessage(result.Succeeded ? $"Compose {verb} finished." : $"Compose {verb} failed (see Output → Container Tools).", TimeSpan.FromSeconds(6));
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

    /// <summary>
    /// Debug a compose service: Compose Up, then the debugger attaches to the .NET process of the service picked (the
    /// only one, or the .dcproj's <c>DockerServiceName</c>).
    /// </summary>
    public async Task<bool> ComposeDebugAsync(string? argument, string? service = null)
    {
        if (ComposeFileFor(argument) is not { } file || !await ComposeUpAsync(file).ConfigureAwait(true))
        {
            return false;
        }

        if (service is null && argument is not null && argument.EndsWith(".dcproj", StringComparison.OrdinalIgnoreCase))
        {
            service = ComposeServiceName(argument);
        }

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
            Workbench.Notifications.Warning("No service of the compose file is running.");
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

        Workbench.Notifications.Warning($"No .NET process runs in {picked.Name} to debug.");
        return false;
    }

    /// <summary>The .dcproj's <c>DockerServiceName</c> (the service Visual Studio debugs), or null.</summary>
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
}
