using Codalus.Workbench.Containers;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Output;
using Codalus.Workbench.Remote;

namespace Codalus.Shell;

/// <summary>
/// Dev Containers (M21b, ADR 0053, <see cref="DevContainers"/>): Reopen in Container brings up the folder's dev
/// container with the user's docker or podman and opens a remote window in it, at its workspace folder (with the
/// solution there, when one is open). Opening a folder that has a <c>devcontainer.json</c> offers it once.
/// </summary>
public sealed partial class ShellHost
{
    private readonly HashSet<string> _devContainerOffered = new(StringComparer.OrdinalIgnoreCase);

    public OutputChannel DevContainerLog => Workbench.Output.GetOrCreate("codalus.devcontainers", Localizer.Get("msg.devContainers.channel", "Dev Containers"));

    /// <summary>The folder whose dev container applies: the open solution's or folder's.</summary>
    private string? DevContainerFolder => Solutions.Solution?.Directory;

    /// <summary>When a solution or folder opens: a <c>devcontainer.json</c> there is offered, once per folder and session.</summary>
    private async Task OfferDevContainerAsync(string folder)
    {
        if (Remote is not null || DevContainers.Find(folder) is null || !_devContainerOffered.Add(folder))
        {
            return;
        }

        var reopen = Localizer.Get("msg.devContainers.reopen", "Reopen in Container");
        if (await Workbench.Notifications.Info(Localizer.Get("msg.devContainers.found", "This folder has a dev container (devcontainer.json). Open it in the container?"), reopen).Result.ConfigureAwait(true) == reopen)
        {
            await ReopenInContainerAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Dev Containers: Reopen in Container. <paramref name="folder"/>: the open solution's folder by default.</summary>
    public async Task<string?> ReopenInContainerAsync(string? folder = null)
    {
        folder ??= DevContainerFolder;
        if (folder is null || DevContainers.Find(folder) is not { } file)
        {
            Workbench.Notifications.Info(Localizer.Get("msg.devContainers.none", "Open a folder or solution that has a .devcontainer/devcontainer.json first."));
            return null;
        }

        DevContainerConfig config;
        try
        {
            config = DevContainers.Parse(file, folder);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.devContainers.unreadable", "{0} couldn't be read: {1}", file, ex.Message));
            return null;
        }

        var log = DevContainerLog;
        Workbench.Output.Show(log);
        log.AppendLine($"[{DateTime.Now:T}] {config.Name}: {file}");
        foreach (var property in config.Unsupported)
        {
            log.AppendLine($"  Not supported yet, ignored: \"{property}\"");
        }

        string container;
        try
        {
            StatusBar.ShowMessage(Localizer.Format("msg.devContainers.starting", "Starting the dev container {0}…", config.Name), TimeSpan.FromSeconds(10));
            container = await DevContainers.UpAsync(ContainerEngine, config, folder, line => Infrastructure.Ui.Post(() => log.AppendLine("  " + line)), CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            log.AppendLine(ex.Message);
            Workbench.Notifications.Error(Localizer.Format("msg.devContainers.failed", "The dev container didn't start: {0}", ex.Message));
            return null;
        }

        // The window opens at the workspace in the container, on the open solution's counterpart there.
        var remotePath = config.WorkspaceFolder;
        if (Solutions.Solution?.Path is { } solution && solution.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            remotePath = config.WorkspaceFolder.TrimEnd('/') + "/" + Path.GetRelativePath(folder, solution).Replace('\\', '/');
        }

        var spec = RemoteTargetSpec.Parse("container:" + container, containerTool: ContainerEngine.Tool)!;
        log.AppendLine($"Opening a remote window in {container} at {remotePath}.");
        if (Remote is null && !Solutions.IsOpen && !Editors.Any())
        {
            await ConnectRemoteAsync(spec, remotePath).ConfigureAwait(true);
        }
        else
        {
            List<string> arguments = ["--remote", spec.ToString(), remotePath];
            if (Options.UserDataDir is { } userData)
            {
                arguments.AddRange(["--user-data-dir", userData]);
            }

            StartWindow(arguments);
        }

        return container;
    }
}
