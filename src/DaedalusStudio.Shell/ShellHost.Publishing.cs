using DaedalusStudio.Platform;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Publishing;

namespace DaedalusStudio.Shell;

/// <summary>
/// Publishing (M17c, ADR 0042): the Publish page of a project with its publish profiles, and publishing through the
/// build host's <c>Publish</c> or <c>PublishContainer</c> target, so trim and AOT warnings reach the Error List.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>Opens a project's Publish page (its profiles; a folder profile is made when it has none).</summary>
    public PublishViewModel? OpenPublish(string? argument = null)
    {
        if (!RequireTrustedSolution("Publishing") || RequireProject(argument, "publish") is not { } project)
        {
            return null;
        }

        if (!Solutions.Projects.TryGetValue(project, out var info) || info.IsComposeProject || info.TargetFrameworks.Length == 0)
        {
            StatusBar.ShowMessage("Select an application project to publish.", TimeSpan.FromSeconds(4));
            return null;
        }

        return OpenManagement(
            "publish:" + project,
            () => new PublishViewModel(
                project,
                info.TargetFrameworks,
                profile => PublishAsync(project, profile),
                suggested => Dialogs.InputAsync("New Publish Profile", "The profile's name (its file is Properties/PublishProfiles/<name>.pubxml).", suggested, text => string.IsNullOrWhiteSpace(text) || text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "Enter a file name." : null)),
            vm => "Publish " + vm.ProjectName);
    }

    /// <summary>Publishes <paramref name="project"/> with <paramref name="profile"/>; the result says where it went, or why not.</summary>
    public async Task<string> PublishAsync(string project, PublishProfile profile)
    {
        if (Builds.IsBuilding)
        {
            return "A build is running: publish when it has finished.";
        }

        var container = profile.Target == PublishTarget.Container;
        var properties = PublishProfiles.Properties(profile, container ? ContainerEngine.Name : null);
        Workbench.Output.Show(Builds.Channel);
        var result = await Builds.BuildAsync(container ? BuildTargets.PublishContainer : BuildTargets.Publish, project, properties, CancellationToken.None).ConfigureAwait(true);
        var name = Path.GetFileNameWithoutExtension(project);
        if (result.Canceled)
        {
            return "The publish was cancelled.";
        }

        if (!result.Succeeded)
        {
            return $"The publish failed: {result.Errors} error(s), see the Error List and Output → Build.";
        }

        var warnings = result.Warnings > 0 ? $" ({result.Warnings} warning(s) in the Error List)" : string.Empty;
        if (container)
        {
            var image = $"{(string.IsNullOrEmpty(profile.ContainerRegistry) ? string.Empty : profile.ContainerRegistry + "/")}{profile.ContainerRepository}:{profile.ContainerImageTag ?? "latest"}";
            _ = ContainersWindow.RefreshAsync();
            Workbench.Notifications.Info($"{name} is published as the image {image}{warnings}.");
            return $"Published the image {image}{warnings}.";
        }

        var folder = PublishProfiles.OutputFolder(project, profile);
        var notification = Workbench.Notifications.Info($"{name} is published to {folder}{warnings}.", "Open Folder");
        _ = notification.Result.ContinueWith(
            t =>
            {
                if (t.Result == "Open Folder" && Directory.Exists(folder))
                {
                    ShellIntegration.OpenUrl(new Uri(folder + Path.DirectorySeparatorChar));
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
        return $"Published to {folder}{warnings}.";
    }
}
