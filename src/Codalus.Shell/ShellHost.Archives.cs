using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Mobile;

namespace Codalus.Shell;

/// <summary>
/// The Archive Manager (ADR 0114): a .NET MAUI app's Publish. Archives are the platform SDK's Release publish in the
/// build host; Distribute signs one for a channel with the user's signing identities and hands it off (its folder and
/// the store's upload page). The tools' output goes to Output → Archive.
/// </summary>
public sealed partial class ShellHost
{
    public const string ArchiveChannelId = "codalus.archive";

    private ArchiveService? _archives;

    /// <summary>The archives and the user's signing identities (made on first use).</summary>
    public ArchiveService ArchiveService => _archives ??= new ArchiveService(
        Workbench.Platform,
        new AppArchives(Path.Combine(Paths.Data, "archives"), AppArchives.XcodeArchivesFolder(Workbench.Platform, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))),
        new SigningIdentities(Path.Combine(Paths.Data, "signing.json"), Secrets),
        () => Mobile.Android(),
        (command, onLine, cancellationToken) => RunTool(command, onLine, cancellationToken),
        (target, project, properties, cancellationToken) => Builds.BuildAsync(target, project, properties, cancellationToken),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetEnvironmentVariable);

    /// <summary>For tests: archives and identities in folders of theirs, tools replayed.</summary>
    internal void UseArchiveService(ArchiveService service) => _archives = service;

    /// <summary>The solution's MAUI apps (and other mobile apps), by name.</summary>
    public IReadOnlyList<ArchiveApp> ArchiveApps() =>
        [.. Solutions.Projects
            .Where(p => !p.Value.IsComposeProject && MobileTargets.IsMobile(p.Value) && p.Value.TargetFrameworks.Any(f => MobileTargets.PlatformOf(f) != MobilePlatform.None))
            .Select(p => new ArchiveApp(p.Key, Path.GetFileNameWithoutExtension(p.Key), p.Value.TargetFrameworks))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Opens the Archive Manager on <paramref name="project"/> (else the current project, when it is a MAUI app; else all apps).</summary>
    public ArchiveManagerViewModel? OpenArchiveManager(string? project = null)
    {
        if (!RequireTrustedSolution("Archiving"))
        {
            return null;
        }

        var apps = ArchiveApps();
        if (apps.Count == 0)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.archives.theSolutionHasNoMauiApp", "The solution has no .NET MAUI app to archive."), TimeSpan.FromSeconds(4));
            return null;
        }

        var current = project ?? CurrentProject(null);
        var app = apps.FirstOrDefault(a => Codalus.Workbench.Projects.SolutionService.PathComparer.Equals(a.Project, current));
        var page = OpenManagement("archives", () => new ArchiveManagerViewModel(ArchiveService, apps, app?.Project, ArchiveActions()), vm => vm.Title);
        if (app is not null && page.App?.Project != app.Project)
        {
            page.AppIndex = apps.Count > 1 ? apps.ToList().IndexOf(app) + 1 : 0;
        }

        return page;
    }

    private ArchiveManagerActions ArchiveActions()
    {
        var channel = Workbench.Output.GetOrCreate(ArchiveChannelId, "Archive");
        return new ArchiveManagerActions
        {
            ReadVersions = ReadAppVersionsAsync,
            WriteVersions = async (project, display, version) =>
            {
                await ProjectManagement.SetPropertyAsync(project, "ApplicationDisplayVersion", display, null, CancellationToken.None).ConfigureAwait(true);
                await ProjectManagement.SetPropertyAsync(project, "ApplicationVersion", version, null, CancellationToken.None).ConfigureAwait(true);
            },
            Archive = ArchiveAsync,
            Distribute = async (archive, target, signing) =>
            {
                Workbench.Output.Show(channel);
                channel.AppendLine($"Distributing {archive.Name} {archive.VersionLabel} ({MobileTargets.Title(archive.Platform)}) for {Distribution.Title(target)}");
                var result = await ArchiveService.DistributeAsync(archive, target, signing, channel.AppendLine, CancellationToken.None).ConfigureAwait(true);
                channel.AppendLine(result.Message);
                if (result.Succeeded)
                {
                    var actions = Distribution.UploadPage(target) is null ? new[] { "Open Folder" } : ["Open Folder", "Open Upload Page"];
                    var notification = Workbench.Notifications.Info(Localizer.Format("msg.archives.isReadyFor", "{0} is ready for {1}: {2}", archive.Name, Distribution.Title(target), result.Folder), actions);
                    _ = notification.Result.ContinueWith(
                        t =>
                        {
                            if (t.Result == "Open Folder")
                            {
                                RevealPath(result.Files.Count > 0 ? result.Files[0] : result.Folder);
                            }
                            else if (t.Result == "Open Upload Page" && Distribution.UploadPage(target) is { } page)
                            {
                                OpenExternalUrl(page);
                            }
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnRanToCompletion,
                        TaskScheduler.Default);
                }

                return result;
            },
            Log = channel.AppendLine,
            Reveal = path => RevealPath(path),
            OpenUrl = uri => OpenExternalUrl(uri),
            Confirm = (title, message, action) => Dialogs.ConfirmAsync(title, message, action, danger: true),
        };
    }

    /// <summary>The app's id, title and versions as the project evaluates them, and its Windows framework's package type.</summary>
    public async Task<AppVersions> ReadAppVersionsAsync(string project)
    {
        var result = await ProjectManagement.GetPropertiesAsync(project, ["ApplicationId", "ApplicationTitle", "ApplicationDisplayVersion", "ApplicationVersion"], null, CancellationToken.None).ConfigureAwait(true);
        string? Get(string name) => result.Properties.FirstOrDefault(p => p.Name == name)?.Value is { Length: > 0 } value ? value : null;
        var windows = Solutions.Projects.TryGetValue(project, out var info) ? info.WindowsPackageType : null;
        return new AppVersions(Get("ApplicationId"), Get("ApplicationTitle"), Get("ApplicationDisplayVersion"), Get("ApplicationVersion"), windows);
    }

    /// <summary>Builds an archive (refused while a build runs); its output goes to Output → Build and its errors to the Error List.</summary>
    public async Task<ArchiveResult> ArchiveAsync(ArchiveRequest request)
    {
        if (Builds.IsBuilding)
        {
            return new ArchiveResult(null, "A build is running: archive when it has finished.");
        }

        // The platform's workload, when dotnet lists the installed ones (else the build says what's missing).
        if (Solutions.Projects.TryGetValue(request.Project, out var info)
            && await Mobile.InstalledWorkloadsAsync("dotnet", Path.GetDirectoryName(request.Project)!, CancellationToken.None).ConfigureAwait(true) is { } installed
            && MobileTargets.MissingWorkloads(info, installed, Workbench.Platform) is { Count: > 0 } missing
            && missing.Contains(MobileTargets.Workload(request.Platform, info.UsesMaui) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            return new ArchiveResult(null, $"Archiving for {MobileTargets.Title(request.Platform)} needs the {string.Join(", ", missing)} workload(s): install them from Check Environment or the Devices window.");
        }

        Workbench.Output.Show(Builds.Channel);
        var result = await ArchiveService.ArchiveAsync(request, CancellationToken.None).ConfigureAwait(true);
        if (result.Archive is { } archive)
        {
            Workbench.Notifications.Info(Localizer.Format("msg.archives.isArchived", "{0} {1} is archived for {2}.", archive.Name, archive.VersionLabel, MobileTargets.Title(archive.Platform)));
        }

        return result;
    }
}
