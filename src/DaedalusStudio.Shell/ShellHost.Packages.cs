using DaedalusStudio.Platform;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Packages;
using DaedalusStudio.Workbench.Projects;
using DaedalusStudio.Workbench.Localization;

namespace DaedalusStudio.Shell;

/// <summary>NuGet packages, package sources, .NET tools and Pack (ADR 0012).</summary>
public sealed partial class ShellHost
{
    public PackageService Packages { get; private set; } = null!;

    private void InitializePackages()
    {
        Packages = new PackageService(Solutions) { Offline = Workbench.Settings.GetBoolean(PackageService.OfflineSetting, false) };
        Workbench.Settings.Changed += (_, e) =>
        {
            if (e.Keys.Contains(PackageService.OfflineSetting))
            {
                Packages.Offline = Workbench.Settings.GetBoolean(PackageService.OfflineSetting, false);
            }
        };

        // Package documents belong to the solution that opened them.
        Solutions.SolutionChanged += (_, _) => Ui.Post(() =>
        {
            if (!Solutions.IsOpen)
            {
                foreach (var document in Layout.Documents.Where(d => d is PackageManagerDocument or PackageSourcesDocument or DotnetToolsDocument).ToList())
                {
                    Layout.CloseDocument(document);
                }
            }
        });
    }

    private bool RequireTrustedSolution(string feature)
    {
        if (Solutions.Solution is not { } solution)
        {
            StatusBar.ShowMessage(Localizer.Format("msg.packages.openASolutionToUse", "Open a solution to use {0}.", feature), TimeSpan.FromSeconds(4));
            return false;
        }

        if (!solution.Trusted)
        {
            Workbench.Notifications.Warning(Localizer.Format("msg.packages.isAvailableOnlyInTrustedFolders", "{0} is available only in trusted folders. Trust the solution folder first.", feature));
            return false;
        }

        return true;
    }

    /// <summary>Opens the NuGet Package Manager of a project (the current one when null), or of the solution.</summary>
    public PackageManagerViewModel? OpenPackageManager(string? project, bool solution = false)
    {
        if (!RequireTrustedSolution("the NuGet Package Manager"))
        {
            return null;
        }

        project = solution ? null : CurrentProject(project);
        if (!solution && project is null)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.packages.selectAProjectToManageIts", "Select a project to manage its packages."), TimeSpan.FromSeconds(4));
            return null;
        }

        if (Layout.FindDocument<PackageManagerDocument>(PackageManagerDocument.DocumentIdFor(project)) is { } open)
        {
            Layout.OpenDocument(open, focus: true);
            return open.ViewModel;
        }

        var context = new PackageManagerContext(
            Dialogs,
            Workbench.Notifications,
            url => ShellIntegration.OpenUrl(new Uri(url)),
            OpenPackageSources,
            () => Workbench.Output.Show(RestoreLog));
        var viewModel = new PackageManagerViewModel(Packages, Solutions, project, context);
        Layout.OpenDocument(new PackageManagerDocument(viewModel), focus: true);
        return viewModel;
    }

    public void OpenPackageSources()
    {
        if (!RequireTrustedSolution("Package Sources"))
        {
            return;
        }

        Layout.OpenDocument(
            Layout.FindDocument<PackageSourcesDocument>(PackageSourcesDocument.DocumentId)
            ?? new PackageSourcesDocument(new PackageSourcesViewModel(Packages, Workbench.Notifications)),
            focus: true);
    }

    public void OpenDotnetTools()
    {
        if (!RequireTrustedSolution(".NET tools"))
        {
            return;
        }

        Layout.OpenDocument(
            Layout.FindDocument<DotnetToolsDocument>(DotnetToolsDocument.DocumentId)
            ?? new DotnetToolsDocument(new DotnetToolsViewModel(Packages, Dialogs, path => OpenFileAsync(path))),
            focus: true);
    }

    /// <summary>
    /// <c>dotnet pack</c> of a project (the current one when null): the build output shows progress and diagnostics;
    /// a notification offers the created packages' folder.
    /// </summary>
    public async Task<IReadOnlyList<string>> PackAsync(string? argument)
    {
        if (CurrentProject(argument) is not { } project)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.packages.selectAProjectToPack", "Select a project to pack."), TimeSpan.FromSeconds(4));
            return [];
        }

        var created = new List<string>();
        void OnOutput(object? sender, string line)
        {
            if (PackOutput.CreatedPackage(line) is { } package)
            {
                lock (created)
                {
                    created.Add(package);
                }
            }
        }

        Solutions.BuildOutput += OnOutput;
        bool succeeded;
        try
        {
            succeeded = await BuildAsync(BuildTargets.Pack, project).ConfigureAwait(true);
        }
        finally
        {
            Solutions.BuildOutput -= OnOutput;
        }

        if (succeeded && created.Count > 0)
        {
            var folder = Path.GetDirectoryName(created[0])!;
            var notification = Workbench.Notifications.Info(
                $"Created {string.Join(", ", created.Select(Path.GetFileName))}.",
                OperatingSystem.IsMacOS() ? "Reveal in Finder" : "Open Folder");
            _ = notification.Result.ContinueWith(
                t =>
                {
                    if (t.Result is not null)
                    {
                        ShellIntegration.RevealInFileManager(created[0]);
                    }
                },
                TaskScheduler.Default);
            Builds.Channel.AppendLine($"Packages: {folder}");
        }

        return created;
    }
}

/// <summary>Reads NuGet's pack output.</summary>
public static class PackOutput
{
    /// <summary>NuGet's "Successfully created package '&lt;path&gt;'." line (untranslated English output).</summary>
    public static string? CreatedPackage(string line)
    {
        const string Marker = "Successfully created package '";
        var start = line.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += Marker.Length;
        var end = line.IndexOf('\'', start);
        return end > start ? line[start..end] : null;
    }
}
