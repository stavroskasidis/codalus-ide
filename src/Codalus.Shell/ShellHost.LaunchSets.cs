using Codalus.Projects.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Build;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Projects;

namespace Codalus.Shell;

/// <summary>
/// Sets of startup projects in the toolbar (ADR 0109): the unnamed set (<c>&lt;Multiple Startup Projects&gt;</c>, the
/// Startup Projects page's own) and the named sets of the solution's launch files, chosen like a startup project.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>The startup list's value for a set of startup projects: the prefix and its name (none for the unnamed set).</summary>
    internal const string LaunchSetChoicePrefix = "startupSet:";

    /// <summary>The toolbar's name of the unnamed set.</summary>
    public static string MultipleStartupProjectsLabel => Localizer.Get("msg.launchSets.multipleStartupProjects", "<Multiple Startup Projects>");

    /// <summary>Reads the solution's launch files again off the UI thread, and logs what couldn't be read.</summary>
    public async Task ReloadLaunchSetsAsync()
    {
        try
        {
            foreach (var problem in await Task.Run(Runs.ReloadLaunchSets).ConfigureAwait(true))
            {
                Workbench.Log.AppendLine("[launch profiles] " + problem);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Workbench.Log.AppendLine("[launch profiles] The solution's launch files couldn't be read: " + ex.Message);
        }
    }

    /// <summary>A change in the solution's folder (a checkout, the classic IDE saving): its launch files may have changed.</summary>
    private void OnLaunchFilesMayHaveChanged(FilesChangedParams changes)
    {
        if (Solutions.Solution is { IsFolder: false } solution
            && (changes.Rescan || changes.Directories.Any(d => SolutionService.PathComparer.Equals(Path.TrimEndingDirectorySeparator(d), Path.TrimEndingDirectorySeparator(solution.Directory)))))
        {
            Ui.Post(() => _ = ReloadLaunchSetsAsync());
        }
    }

    /// <summary>
    /// The toolbar's sets: the unnamed set once it has a project to start (or while it's the active one), then the named
    /// sets of the launch files.
    /// </summary>
    private List<StartupProjectChoice> LaunchSetChoices()
    {
        var choices = new List<StartupProjectChoice>();
        if (Runs.StartupEntries.Any(e => e.Action != StartupAction.None) || (Runs.MultipleStartup && Runs.ActiveLaunchSet is null))
        {
            choices.Add(new StartupProjectChoice(MultipleStartupProjectsLabel, LaunchSetChoicePrefix));
        }

        choices.AddRange(Runs.LaunchSets.Select(s => new StartupProjectChoice(s.Name, LaunchSetChoicePrefix + s.Name)));
        return choices;
    }

    /// <summary>The toolbar's value of what Start starts: the active set, or the startup project.</summary>
    private string? ToolbarStartupChoice() =>
        !Runs.MultipleStartup ? Runs.StartupProject : LaunchSetChoicePrefix + Runs.ActiveLaunchSet?.Name;

    /// <summary>A set chosen in the toolbar (or the palette): the unnamed set for an empty name.</summary>
    public void SelectLaunchSet(string? name)
    {
        Runs.SelectLaunchSet(string.IsNullOrEmpty(name) ? null : name);
        SaveEditorSession();
    }

    /// <summary>The toolbar's startup list (or Select Startup Project) chose a project, a folder's launch configuration or a set.</summary>
    internal void SelectToolbarStartup(string? path)
    {
        // In folder mode the startup list holds the launch configurations (ADR 0033).
        if (path?.StartsWith(LaunchChoicePrefix, StringComparison.Ordinal) == true)
        {
            SelectLaunchConfiguration(path[LaunchChoicePrefix.Length..]);
        }
        else if (path?.StartsWith(LaunchSetChoicePrefix, StringComparison.Ordinal) == true)
        {
            SelectLaunchSet(path[LaunchSetChoicePrefix.Length..]);
        }
        else
        {
            SetStartupProject(path);
        }
    }
}
