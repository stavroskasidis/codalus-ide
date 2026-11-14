using System.Globalization;
using System.Text;
using System.Text.Json;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai.Ide;
using Codalus.Workbench.Aspire;
using Codalus.Workbench.Containers;

namespace Codalus.Shell;

/// <summary>
/// M60's tools for agents: the shell's side of <see cref="IdeServiceTools"/>. Compose services come from the
/// workspace's compose files through the user's docker or podman, as the Containers window and the Compose commands
/// use them; Aspire resources come from the running AppHost's resource service, as the Aspire window shows them. Every
/// action takes the IDE's own path (the engine's start, stop and restart, Compose Up and Compose Debug, the resource's
/// commands, Attach to Process), so the IDE's windows show the change.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>How long a service action waits for the service's new state.</summary>
    private static readonly TimeSpan s_serviceStateWait = TimeSpan.FromSeconds(30);

    /// <summary>A compose file's service, with its container once one exists.</summary>
    private sealed record ComposeService(string File, string Project, string Name, ContainerSummary? Container);

    /// <summary>The service an agent named: an Aspire resource or a compose service.</summary>
    private sealed record ServiceTarget(AspireResource? Resource, ComposeService? Compose)
    {
        public string Name => Resource?.DisplayName ?? Compose!.Name;
    }

    private async Task<IdeToolResult> ServiceToolAsync(string tool, JsonElement args, string? root, CancellationToken cancellationToken)
    {
        if (tool == "list_services")
        {
            return new(await ServicesTextAsync(root, cancellationToken).ConfigureAwait(true));
        }

        var name = Str(args, "name") is { Length: > 0 } given ? given : throw new IdeToolException("A service's name is needed: list_services lists them.");
        var target = await FindServiceAsync(name, Str(args, "kind"), root, cancellationToken).ConfigureAwait(true);
        return tool switch
        {
            "service_start" => target.Resource is { } resource
                ? await AspireCommandToolAsync(resource, "resource-start", "start", cancellationToken).ConfigureAwait(true)
                : await ComposeActionToolAsync(target.Compose!, "start", root, cancellationToken).ConfigureAwait(true),
            "service_stop" => target.Resource is { } resource
                ? await AspireCommandToolAsync(resource, "resource-stop", "stop", cancellationToken).ConfigureAwait(true)
                : await ComposeActionToolAsync(target.Compose!, "stop", root, cancellationToken).ConfigureAwait(true),
            "service_restart" => target.Resource is { } resource
                ? await AspireCommandToolAsync(resource, "resource-restart", "restart", cancellationToken).ConfigureAwait(true)
                : await ComposeActionToolAsync(target.Compose!, "restart", root, cancellationToken).ConfigureAwait(true),
            "service_debug" => target.Resource is { } resource
                ? await AspireDebugToolAsync(resource).ConfigureAwait(true)
                : await ComposeDebugToolAsync(target.Compose!, root).ConfigureAwait(true),
            "service_logs" => await ServiceLogsToolAsync(target, Math.Clamp(Int(args, "lines") ?? 200, 1, 2000), cancellationToken).ConfigureAwait(true),
            "service_traces" or "service_metrics" => ServiceTelemetryText(target, tool == "service_traces" ? "traces" : "metrics"),
            _ => throw new Codalus.Workbench.Ai.Mcp.McpException($"There is no tool '{tool}'."),
        };
    }

    // ------------------------------------------------------------------ listing

    private async Task<string> ServicesTextAsync(string? root, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var files = await WorkspaceComposeFilesAsync(root, cancellationToken).ConfigureAwait(true);
        var engine = ContainerEngine;
        foreach (var file in files)
        {
            text.Append(CultureInfo.InvariantCulture, $"Compose file {Relative(file, root)} (project {ContainerEngine.ComposeProjectName(file)}, {engine.Name}):").Append('\n');
            var (services, error) = await ComposeServicesAsync(file, cancellationToken).ConfigureAwait(true);
            if (error is not null)
            {
                text.Append("  ").Append(error).Append('\n');
                continue;
            }

            if (services.Count == 0)
            {
                text.Append("  It has no services.\n");
            }

            foreach (var service in services)
            {
                text.Append("- ").Append(ComposeServiceLine(service)).Append('\n');
            }
        }

        if (AspireWindow.AppHost is { } appHost && (AspireWindow.IsRunning || AspireWindow.Resources.Count > 0))
        {
            text.Append(CultureInfo.InvariantCulture, $"Aspire AppHost {appHost} ({(_aspire is { } run ? run.Debug ? "running under the debugger, with its projects" : "running without the debugger" : "it has stopped; its last resources")}):").Append('\n');
            if (AspireWindow.DashboardUrl is { } dashboard && _aspire is not null)
            {
                text.Append("  The dashboard: ").Append(dashboard).Append('\n');
            }

            if (AspireWindow.Resources.Count == 0)
            {
                text.Append("  It is starting: no resources yet.\n");
            }

            foreach (var row in AspireWindow.Resources)
            {
                text.Append("- ").Append(AspireResourceLine(row.Resource)).Append('\n');
            }
        }

        var idle = Solutions.Projects.Values
            .Where(p => p.IsAspireHost && !(_aspire is { } running && string.Equals(running.Project, p.Path, StringComparison.OrdinalIgnoreCase)))
            .Select(p => $"{p.Name} ({Relative(p.Path, root)})")
            .ToList();
        if (idle.Count > 0)
        {
            text.Append("Aspire AppHost projects that aren't running (run_project or debug_start on one starts its resources here): ").AppendJoin(", ", idle).Append('\n');
        }

        return text.Length > 0
            ? text.ToString().TrimEnd()
            : "The workspace has no services: no compose file (compose.yaml, docker-compose.yml, or a .dcproj's) and no Aspire AppHost project.";
    }

    private static string ComposeServiceLine(ComposeService service) =>
        service.Container is not { } container
            ? $"{service.Name}: not created (service_start creates and starts it)"
            : $"{service.Name}: {(container.IsRunning ? "running" : container.State is { Length: > 0 } state ? state : "stopped")}"
                + (container.Status.Length > 0 ? $" ({container.Status})" : "")
                + $", container {container.Name}, image {container.Image}"
                + (container.Ports.Length > 0 ? ", ports " + container.Ports : "");

    private static string AspireResourceLine(AspireResource resource)
    {
        var urls = resource.Urls.Where(u => !u.IsInternal).Select(u => u.Url).ToList();
        var commands = resource.Commands.Where(c => c.IsEnabled && c.Name is "resource-start" or "resource-stop" or "resource-restart").Select(c => c.Name["resource-".Length..]).ToList();
        return $"{resource.DisplayName} ({resource.Type}): {resource.State ?? "state unknown"}"
            + (urls.Count > 0 ? " — " + string.Join(", ", urls) : "")
            + (commands.Count > 0 ? " [can " + string.Join(", ", commands) + "]" : "");
    }

    /// <summary>
    /// The workspace's compose files that agents may read: the one in the solution's folder (compose picks the first of
    /// its names), then each <c>.dcproj</c>'s; a file excluded from AI is left out.
    /// </summary>
    private async Task<List<string>> WorkspaceComposeFilesAsync(string? root, CancellationToken cancellationToken)
    {
        var candidates = new List<string>();
        if (root is not null && s_composeNames.Select(n => Path.Combine(root, n)).FirstOrDefault(File.Exists) is { } top)
        {
            candidates.Add(top);
        }

        foreach (var project in Solutions.Projects.Values.Where(p => p.IsComposeProject))
        {
            if (ComposeFileFor(project.Path) is { } file && !candidates.Contains(file, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(file);
            }
        }

        var files = new List<string>();
        foreach (var file in candidates)
        {
            if (!await IsExcludedAsync(file, root, cancellationToken).ConfigureAwait(true))
            {
                files.Add(file);
            }
        }

        return files;
    }

    /// <summary>
    /// A compose file's services (<c>compose config --services</c>) with their containers (by the compose project's
    /// label, or, for podman-compose, its name prefix), or why they can't be listed.
    /// </summary>
    private async Task<(List<ComposeService> Services, string? Error)> ComposeServicesAsync(string file, CancellationToken cancellationToken)
    {
        var engine = ContainerEngine;
        var project = ContainerEngine.ComposeProjectName(file);
        try
        {
            var (os, error) = await engine.ServerOsAsync(cancellationToken).ConfigureAwait(true);
            if (os is null)
            {
                return ([], $"{engine.Name} doesn't answer: {error} Start Docker or Podman, or choose the engine in the '{ContainerEngine.ToolSetting}' setting.");
            }

            var config = await engine.RunAsync(ComposeCommand(file, null, "config", "--services"), cancellationToken, workingDirectory: Path.GetDirectoryName(file)).ConfigureAwait(true);
            if (!config.Succeeded)
            {
                return ([], $"{engine.Name} compose config failed: {config.Message}");
            }

            var names = config.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var containers = await engine.ContainersAsync(cancellationToken, "com.docker.compose.project=" + project).ConfigureAwait(true);
            if (containers.Count == 0)
            {
                containers = [.. (await engine.ContainersAsync(cancellationToken).ConfigureAwait(true)).Where(c => c.Name.StartsWith(project, StringComparison.OrdinalIgnoreCase))];
            }

            return ([.. names.Select(n => new ComposeService(file, project, n, ContainerOf(containers, project, n)))], null);
        }
        catch (InvalidOperationException ex)
        {
            return ([], ex.Message);
        }
    }

    /// <summary>A service's container: <c>project-service-1</c> (compose v2), <c>project_service_1</c> (podman-compose), or its own <c>container_name</c>.</summary>
    private static ContainerSummary? ContainerOf(IReadOnlyList<ContainerSummary> containers, string project, string service) =>
        containers.FirstOrDefault(c => c.Name.StartsWith($"{project}-{service}-", StringComparison.OrdinalIgnoreCase) || c.Name.StartsWith($"{project}_{service}_", StringComparison.OrdinalIgnoreCase))
        ?? containers.FirstOrDefault(c => c.Name.Equals(service, StringComparison.OrdinalIgnoreCase));

    /// <summary>The service named <paramref name="name"/>: an Aspire resource of the running AppHost first, then a compose service.</summary>
    private async Task<ServiceTarget> FindServiceAsync(string name, string? kind, string? root, CancellationToken cancellationToken)
    {
        if (kind is null or "aspire"
            && AspireWindow.Resources.FirstOrDefault(r => r.Resource.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase) || r.Resource.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } row)
        {
            return new ServiceTarget(row.Resource, null);
        }

        if (kind is null or "compose")
        {
            var errors = new List<string>();
            foreach (var file in await WorkspaceComposeFilesAsync(root, cancellationToken).ConfigureAwait(true))
            {
                var (services, error) = await ComposeServicesAsync(file, cancellationToken).ConfigureAwait(true);
                if (error is not null)
                {
                    errors.Add(error);
                }

                if (services.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || s.Container?.Name.Equals(name, StringComparison.OrdinalIgnoreCase) == true) is { } service)
                {
                    return new ServiceTarget(null, service);
                }
            }

            if (errors.Count > 0)
            {
                throw new IdeToolException($"No Aspire resource is named '{name}', and the compose services couldn't be listed: {string.Join(" ", errors)}");
            }
        }

        throw new IdeToolException($"No service is named '{name}'. list_services lists them.");
    }

    // ------------------------------------------------------------------ actions

    /// <summary>Starts, stops or restarts a compose service as the Containers window does, or Compose Up for one not created.</summary>
    private async Task<IdeToolResult> ComposeActionToolAsync(ComposeService service, string verb, string? root, CancellationToken cancellationToken)
    {
        var engine = ContainerEngine;
        if (service.Container is not { } container)
        {
            if (verb == "stop")
            {
                return IdeToolResult.Error($"{service.Name} isn't created, so there is nothing to stop.");
            }

            RequireTrustedForAgentActions();
            if (!await RunComposeAsync(service.File, null, "up", ["up", "-d", service.Name]).ConfigureAwait(true))
            {
                return IdeToolResult.Error($"Compose up of {service.Name} failed. Its output (Output → Container Tools):\n{ServiceOutputTail(ContainerLog.GetText(), 40)}");
            }
        }
        else
        {
            ContainerLog.AppendLine($"> {engine.Name} {verb} {container.Name} (an AI agent)");
            try
            {
                await (verb switch
                {
                    "start" => engine.StartAsync(container.Name, cancellationToken),
                    "stop" => engine.StopAsync(container.Name, cancellationToken),
                    _ => engine.RestartAsync(container.Name, cancellationToken),
                }).ConfigureAwait(true);
            }
            catch (InvalidOperationException ex)
            {
                ContainerLog.AppendLine(ex.Message);
                _ = ContainersWindow.RefreshAsync();
                return IdeToolResult.Error($"Could not {verb} {service.Name}: {ex.Message}");
            }
        }

        _ = ContainersWindow.RefreshAsync();
        var (services, error) = await ComposeServicesAsync(service.File, cancellationToken).ConfigureAwait(true);
        var now = services.FirstOrDefault(s => s.Name == service.Name);
        return new($"Done ({verb}). " + (now is not null ? ComposeServiceLine(now) : error ?? $"{service.Name} isn't listed any more.") + $"\n(Compose file {Relative(service.File, root)}; the Containers window shows it.)");
    }

    /// <summary>Runs a resource command as the Aspire window's buttons do, then waits for the resource's new state.</summary>
    private async Task<IdeToolResult> AspireCommandToolAsync(AspireResource resource, string command, string verb, CancellationToken cancellationToken)
    {
        var run = _aspire ?? throw new IdeToolException($"The AppHost {AspireWindow.AppHost} has stopped: start it again with run_project or debug_start.");
        var offered = resource.Commands.FirstOrDefault(c => c.Name == command);
        if (offered is null)
        {
            return IdeToolResult.Error($"{resource.DisplayName} has no {verb} command; it offers: {(resource.Commands.Count == 0 ? "none" : string.Join(", ", resource.Commands.Select(c => c.Name)))}.");
        }

        if (!offered.IsEnabled)
        {
            return IdeToolResult.Error($"{resource.DisplayName} can't {verb} now ({resource.State ?? "state unknown"}): the resource service has its {offered.DisplayName} command disabled.");
        }

        var error = await run.Client.ExecuteAsync(command, resource, cancellationToken).ConfigureAwait(true);
        AspireLog.AppendLine($"{resource.DisplayName}: {offered.DisplayName} (an AI agent){(error is null ? "" : ": " + error)}");
        if (error is not null)
        {
            return IdeToolResult.Error($"Could not {verb} {resource.DisplayName}: {error}");
        }

        string? State() => AspireWindow.Resources.FirstOrDefault(r => r.Resource.Name == resource.Name)?.Resource.State;
        var before = resource.State;
        var started = DateTime.UtcNow;
        await WaitUntilAsync(
            () => verb switch
            {
                "stop" => State() is not ("Running" or "Starting" or "Stopping" or "Waiting"),
                "start" => State() is "Running" or "Exited" or "Finished" or "FailedToStart",
                _ => (State() != before && State() is "Running" or "Exited" or "Finished" or "FailedToStart") || (DateTime.UtcNow - started > TimeSpan.FromSeconds(3) && State() == "Running"),
            },
            DateTime.UtcNow + s_serviceStateWait,
            cancellationToken).ConfigureAwait(true);
        var now = AspireWindow.Resources.FirstOrDefault(r => r.Resource.Name == resource.Name)?.Resource;
        return new($"Done ({verb}). " + (now is not null ? AspireResourceLine(now) : $"{resource.DisplayName} isn't listed any more.") + "\n(The Aspire window shows it.)");
    }

    /// <summary>Compose Debug for one service: Compose Up, then the debugger attaches to its .NET process.</summary>
    private async Task<IdeToolResult> ComposeDebugToolAsync(ComposeService service, string? root)
    {
        RequireTrustedForAgentActions();
        if (Debugger.IsActive)
        {
            return IdeToolResult.Error("A debug session is already running: stop it first (stop_run).");
        }

        var shown = Workbench.Notifications.Items.ToHashSet();
        if (!await ComposeDebugAsync(service.File, service.Name).ConfigureAwait(true))
        {
            var said = Workbench.Notifications.Items.Where(n => !shown.Contains(n)).Select(n => n.Message).ToList();
            return IdeToolResult.Error($"{service.Name} couldn't be debugged{(said.Count > 0 ? ": " + string.Join(" ", said) : ".")}\nContainer Tools output:\n{ServiceOutputTail(ContainerLog.GetText(), 30)}");
        }

        return new($"The debugger is attached to {service.Name}'s .NET process in its container (compose file {Relative(service.File, root)}). Set breakpoints, make the service reach them, then debug_wait_for_stop.\n{await DebugStateTextAsync(root).ConfigureAwait(true)}");
    }

    /// <summary>
    /// The debugger on an Aspire project resource: in an AppHost debugged with F5 it already is; one run with Ctrl+F5
    /// (the IDE runs its projects) gets Attach to Process on its program.
    /// </summary>
    private async Task<IdeToolResult> AspireDebugToolAsync(AspireResource resource)
    {
        var run = _aspire ?? throw new IdeToolException($"The AppHost {AspireWindow.AppHost} has stopped: start it again with debug_start.");
        if (resource.Type != "Project")
        {
            // TODO(issue): containers and executables of an AppHost under the debugger come with M63.
            return IdeToolResult.Error($"{resource.DisplayName} is a {resource.Type} resource: only the AppHost's project resources can be debugged here. Run the AppHost with debug_start to debug all its projects.");
        }

        if (run.Debug)
        {
            return new($"{resource.DisplayName} is already in the AppHost's debug session (debug_start on the AppHost debugs its projects). Set breakpoints, then debug_wait_for_stop; debug_get_state shows its threads.");
        }

        if (Debugger.IsActive)
        {
            return IdeToolResult.Error("A debug session is already running: stop it first (stop_run).");
        }

        var path = resource.Properties.FirstOrDefault(p => p.Key == "project.path").Value;
        var program = Runs.Running.LastOrDefault(r => !r.HasExited && (path is { Length: > 0 } && string.Equals(Path.GetFullPath(r.Project), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) || r.Name.Equals(resource.DisplayName, StringComparison.OrdinalIgnoreCase)));
        int? processId = program?.ProcessId;
        if (processId is null && resource.Properties.FirstOrDefault(p => p.Key == "executable.pid").Value is { } pid && int.TryParse(pid, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            processId = parsed;
        }

        if (processId is null)
        {
            return IdeToolResult.Error($"{resource.DisplayName}'s process isn't known ({resource.State ?? "state unknown"}): start it first (service_start), or run the AppHost with debug_start.");
        }

        var shown = Workbench.Notifications.Items.ToHashSet();
        await AttachToProcessAsync(processId.Value.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(true);
        if (!Debugger.IsActive)
        {
            var said = Workbench.Notifications.Items.Where(n => !shown.Contains(n)).Select(n => n.Message).ToList();
            return IdeToolResult.Error($"The debugger couldn't attach to {resource.DisplayName} (process {processId.Value.ToString(CultureInfo.InvariantCulture)}){(said.Count > 0 ? ": " + string.Join(" ", said) : ".")}");
        }

        return new($"The debugger is attached to {resource.DisplayName} (process {processId.Value.ToString(CultureInfo.InvariantCulture)}). Set breakpoints, make it reach them, then debug_wait_for_stop.");
    }

    /// <summary>Actions that build or run the workspace's code (Compose Up builds its Dockerfiles) need a trusted workspace (ADR 0004).</summary>
    private void RequireTrustedForAgentActions()
    {
        if (Solutions.Solution is not { } solution)
        {
            throw new IdeToolException("No solution or folder is open.");
        }

        if (!solution.Trusted)
        {
            throw new IdeToolException("This workspace isn't trusted, so the IDE doesn't build or run its code. The user can trust it (Trust This Folder).");
        }
    }

    // ------------------------------------------------------------------ logs and telemetry

    private async Task<IdeToolResult> ServiceLogsToolAsync(ServiceTarget target, int lines, CancellationToken cancellationToken)
    {
        if (target.Compose is { } service)
        {
            if (service.Container is not { } container)
            {
                return IdeToolResult.Error($"{service.Name} isn't created, so it has no output yet.");
            }

            var output = AspireViewModel.PlainText(await ContainerEngine.LogsAsync(container.Name, lines, cancellationToken).ConfigureAwait(true)).TrimEnd();
            return new($"{service.Name}'s output (container {container.Name}, {(container.IsRunning ? "running" : container.State)}), its last {lines.ToString(CultureInfo.InvariantCulture)} lines at most:\n{(output.Length == 0 ? "(nothing)" : output)}");
        }

        var resource = target.Resource!;
        var run = _aspire ?? throw new IdeToolException($"The AppHost {AspireWindow.AppHost} has stopped, so its resources' consoles are gone.");
        var console = await AspireConsoleAsync(run.Client, resource.Name, cancellationToken).ConfigureAwait(true);
        var tail = console.Skip(Math.Max(0, console.Count - lines)).ToList();
        return new($"{resource.DisplayName}'s console ({resource.State ?? "state unknown"}), its last {tail.Count.ToString(CultureInfo.InvariantCulture)} of {console.Count.ToString(CultureInfo.InvariantCulture)} lines:\n{(tail.Count == 0 ? "(nothing)" : string.Join('\n', tail))}");
    }

    /// <summary>
    /// A resource's console as the resource service replays it: the lines it has, read until none comes for a moment
    /// (the stream stays open while the resource runs), without their escape sequences.
    /// </summary>
    private static async Task<List<string>> AspireConsoleAsync(AspireResourceClient client, string resource, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var last = DateTime.UtcNow;
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        watch.CancelAfter(TimeSpan.FromSeconds(10));
        var reading = Task.Run(async () =>
        {
            await foreach (var line in client.WatchLogsAsync(resource, watch.Token).ConfigureAwait(false))
            {
                lock (lines)
                {
                    lines.Add(AspireViewModel.PlainText(line.Text));
                    last = DateTime.UtcNow;
                }
            }
        }, CancellationToken.None);
        var started = DateTime.UtcNow;
        while (!reading.IsCompleted && !watch.IsCancellationRequested)
        {
            await Task.WhenAny(reading, Task.Delay(100, CancellationToken.None)).ConfigureAwait(true);
            lock (lines)
            {
                if (lines.Count > 0 ? DateTime.UtcNow - last > TimeSpan.FromMilliseconds(500) : DateTime.UtcNow - started > TimeSpan.FromSeconds(3))
                {
                    break;
                }
            }
        }

        await watch.CancelAsync().ConfigureAwait(true);
        try
        {
            await reading.ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or InvalidDataException)
        {
            // Read what came; the service went away or the wait ended.
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (lines)
        {
            return [.. lines];
        }
    }

    /// <summary>
    /// Traces and metrics: the AppHost's resource service (what the IDE reads) has resources, consoles and commands, but
    /// not telemetry, which the Aspire dashboard keeps.
    /// </summary>
    private IdeToolResult ServiceTelemetryText(ServiceTarget target, string what)
    {
        if (target.Compose is { } service)
        {
            return IdeToolResult.Error($"{service.Name} is a compose service: the IDE has no {what} of compose services. service_logs has its output.");
        }

        // TODO(issue): read traces and metrics from the dashboard's telemetry when the IDE can reach it.
        var dashboard = _aspire is not null && AspireWindow.DashboardUrl is { } url ? " at " + url : "";
        return IdeToolResult.Error($"The AppHost's resource service, which the IDE reads, has {target.Name}'s state, console and commands, but not its {what}: those are in the Aspire dashboard{dashboard}. service_logs has its console output.");
    }

    private static string ServiceOutputTail(string text, int lines)
    {
        var all = text.TrimEnd().Split('\n');
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
    }

    /// <summary>
    /// Follows <paramref name="client"/>'s resource service as a running AppHost's, without starting one (tests); the
    /// returned call stops it as the AppHost's end does.
    /// </summary>
    internal Func<Task> AttachAspireResourceService(string appHost, AspireResourceClient client, bool debug = false)
    {
        var run = _aspire = new AspireRun(appHost, debug, new IdeExecutionServer(StartRunSessionAsync), client);
        AspireWindow.Attach(Path.GetFileNameWithoutExtension(appHost), client);
        _ = WatchAspireResourcesAsync(run);
        return () => StopAspireAsync(run);
    }
}
