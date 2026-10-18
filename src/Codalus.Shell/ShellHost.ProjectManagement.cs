using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>Project properties, references, configurations, dependencies, load/unload, filters and SDKs (ADR 0013).</summary>
public sealed partial class ShellHost
{
    public ProjectManagementService ProjectManagement { get; private set; } = null!;

    private void InitializeProjectManagement()
    {
        ProjectManagement = new ProjectManagementService(Solutions);
        Solutions.SolutionChanged += (_, _) => Ui.Post(() =>
        {
            if (!Solutions.IsOpen)
            {
                foreach (var document in Layout.Documents.Where(d => d is ProjectPropertiesDocument or SolutionManagementDocument).ToList())
                {
                    Layout.CloseDocument(document);
                }
            }
        });
    }

    /// <summary>The project's property pages (the current project when null).</summary>
    public ProjectPropertiesViewModel? OpenProjectProperties(string? argument)
    {
        if (!RequireTrustedSolution("Project properties") || RequireProject(argument, "see its properties") is not { } project)
        {
            return null;
        }

        if (Layout.FindDocument<ProjectPropertiesDocument>(ProjectPropertiesDocument.DocumentIdFor(project)) is { } open)
        {
            Layout.OpenDocument(open, focus: true);
            return open.ViewModel;
        }

        var viewModel = new ProjectPropertiesViewModel(ProjectManagement, Solutions, project, Dialogs, path => OpenFileAsync(path), buildDepth: BuildDepth);
        Layout.OpenDocument(new ProjectPropertiesDocument(viewModel), focus: true);
        return viewModel;
    }

    public ReferenceManagerViewModel? OpenReferenceManager(string? argument)
    {
        if (!RequireTrustedSolution("The Reference Manager") || RequireProject(argument, "manage its references") is not { } project)
        {
            return null;
        }

        return OpenManagement(
            "references:" + project,
            () => new ReferenceManagerViewModel(ProjectManagement, Solutions, project, (title, name, patterns) => Window?.PickFileAsync(title, name, patterns) ?? Task.FromResult<string?>(null)),
            vm => vm.Title);
    }

    public ConfigurationManagerViewModel? OpenConfigurationManager()
    {
        if (!RequireSolutionFile("The Configuration Manager"))
        {
            return null;
        }

        return OpenManagement(
            "configurationManager",
            () => new ConfigurationManagerViewModel(ProjectManagement, Dialogs, Solutions.Solution?.Active, configuration => Solutions.SetConfigurationAsync(configuration, CancellationToken.None)),
            _ => "Configuration Manager");
    }

    public ProjectDependenciesViewModel? OpenProjectDependencies(string? argument)
    {
        if (!RequireSolutionFile("Project Dependencies"))
        {
            return null;
        }

        var project = CurrentProject(argument);
        return OpenManagement("projectDependencies", () => new ProjectDependenciesViewModel(ProjectManagement, Solutions, project), _ => "Project Dependencies");
    }

    public SdkViewModel? OpenSdks()
    {
        if (!RequireTrustedSolution(".NET SDKs"))
        {
            return null;
        }

        return OpenManagement("sdks", () => new SdkViewModel(ProjectManagement, Workbench.Notifications, ReopenSolutionAsync), _ => ".NET SDKs");
    }

    private T OpenManagement<T>(string id, Func<T> create, Func<T, string> title)
        where T : Infrastructure.ObservableObject
    {
        if (Layout.FindDocument<SolutionManagementDocument>(id) is { ViewModel: T existing } open)
        {
            Layout.OpenDocument(open, focus: true);
            return existing;
        }

        var viewModel = create();
        Layout.OpenDocument(new SolutionManagementDocument(id, title(viewModel), viewModel), focus: true);
        return viewModel;
    }

    private string? RequireProject(string? argument, string what)
    {
        var project = CurrentProject(argument);
        if (project is null)
        {
            StatusBar.ShowMessage(Localizer.Format("msg.projectManagement.selectAProjectTo", "Select a project to {0}.", what), TimeSpan.FromSeconds(4));
        }

        return project;
    }

    private bool RequireSolutionFile(string feature)
    {
        if (!RequireTrustedSolution(feature))
        {
            return false;
        }

        if (Solutions.Solution?.Format == "project")
        {
            StatusBar.ShowMessage(Localizer.Format("msg.projectManagement.needsASolutionFileThisIs", "{0} needs a solution file; this is a project opened on its own.", feature), TimeSpan.FromSeconds(5));
            return false;
        }

        return true;
    }

    // ------------------------------------------------------------------ load and unload

    public async Task UnloadProjectAsync(string? argument)
    {
        if (RequireProject(argument, "unload it") is not { } project)
        {
            return;
        }

        await RunManagementAsync(() => ProjectManagement.UnloadAsync(project, CancellationToken.None)).ConfigureAwait(true);
    }

    public async Task ReloadProjectAsync(string? argument)
    {
        if (RequireProject(argument, "reload it") is not { } project)
        {
            return;
        }

        await RunManagementAsync(() => ProjectManagement.ReloadAsync(project, CancellationToken.None)).ConfigureAwait(true);
    }

    /// <summary>Writes the loaded projects to a <c>.slnf</c> (Save As Solution Filter).</summary>
    public async Task SaveSolutionFilterAsync()
    {
        if (!RequireSolutionFile("Solution filters") || Window is null || Solutions.Solution is not { } solution)
        {
            return;
        }

        var suggested = Path.GetFileNameWithoutExtension(solution.Path) + ".slnf";
        if (await Window.SaveFileAsync("Save As Solution Filter", suggested, "Solution filters", ["*.slnf"]).ConfigureAwait(true) is not { } path)
        {
            return;
        }

        if (await RunManagementAsync(() => ProjectManagement.SaveFilterAsync(path, null, CancellationToken.None)).ConfigureAwait(true))
        {
            Workbench.Notifications.Info(Localizer.Format("msg.projectManagement.savedOpenItToLoadOnly", "Saved {0}. Open it to load only these projects.", Path.GetFileName(path)));
        }
    }

    /// <summary>Closes and reopens the solution (or filter): a new build host, e.g. after global.json changed.</summary>
    public async Task ReopenSolutionAsync()
    {
        if (Solutions.Solution is not { } solution)
        {
            return;
        }

        await OpenSolutionAsync(solution.FilterPath ?? solution.Path).ConfigureAwait(true);
    }

    /// <summary>Runs a project management request; false (and an error notification) when the build host refused it.</summary>
    private async Task<bool> RunManagementAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is JsonRpcRemoteException or InvalidOperationException or JsonRpcConnectionLostException or IOException)
        {
            Workbench.Notifications.Error(ex.Message);
            return false;
        }
    }
}
