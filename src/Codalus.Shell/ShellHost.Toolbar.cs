using Codalus.LanguageServices.Protocol;
using Codalus.Projects.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Search;

namespace Codalus.Shell;

/// <summary>The standard toolbar and the search box (M29, ADR 0019).</summary>
public sealed partial class ShellHost
{
    /// <summary>The startup list's value for a folder's launch configuration.</summary>
    private const string LaunchChoicePrefix = "launch:";

    public ToolbarViewModel Toolbar { get; private set; } = null!;

    private void InitializeToolbar()
    {
        Toolbar = new ToolbarViewModel(
            id => CommandFor(id),
            (configuration, platform) =>
            {
                if (Solutions.Solution?.Configurations.FirstOrDefault(c => c.Configuration == configuration && c.Platform == platform) is { } picked
                    && picked != Solutions.Solution.Active)
                {
                    _ = Solutions.SetConfigurationAsync(picked, CancellationToken.None);
                }
            },
            path =>
            {
                // In folder mode the startup list holds the launch configurations (ADR 0033).
                if (path?.StartsWith(LaunchChoicePrefix, StringComparison.Ordinal) == true)
                {
                    SelectLaunchConfiguration(path[LaunchChoicePrefix.Length..]);
                }
                else
                {
                    SetStartupProject(path);
                }
            },
            () => ActiveEditor?.Controller?.Editor.Undo(),
            () => ActiveEditor?.Controller?.Editor.Redo())
        {
            // Undo and Redo keep the editor's own keys; the rest are the commands' bindings on this OS.
            KeyLabel = id => (id switch
            {
                "edit.undo" or "edit.redo" => Codalus.Platform.KeySequence.Parse(BuiltIns.EditKeys(id, Workbench.Platform), Workbench.Platform),
                _ => Workbench.Keybindings.GetBinding(id),
            })?.ToLabel(Workbench.Platform),
        };
    }

    private void UpdateToolbarSolution()
    {
        if (Toolbar is null)
        {
            return;
        }

        if (Solutions?.Solution is not { } solution)
        {
            Toolbar.SetSolution(null, null);
            Toolbar.SetStartup([], null, null);
            return;
        }

        Toolbar.SetSolution(
            [.. solution.Configurations.Select(c => (c.Configuration, c.Platform))],
            solution.Active is { } active ? (active.Configuration, active.Platform) : null,
            solution.Name);
        UpdateToolbarStartup();
    }

    private void UpdateToolbarStartup()
    {
        if (Toolbar is null)
        {
            return;
        }

        if (Solutions?.Solution is not { } solution)
        {
            Toolbar.SetStartup([], null, null);
            return;
        }

        // The folder's configurations, read once: the solution closes on another thread, so reading them again (as
        // CurrentLaunchConfiguration does) could find none, and the window's dispose threw (macOS CI, run 41251237351).
        if (solution.IsFolder && Codalus.Workbench.Projects.FolderConfiguration.ReadLaunch(solution.Directory).Configurations is { Count: > 0 } configurations)
        {
            Toolbar.SetStartup([.. configurations.Select(c => new StartupProjectChoice(c.Name, LaunchChoicePrefix + c.Name))], LaunchChoicePrefix + CurrentOf(configurations)!.Name, null);
            return;
        }

        var startup = Runs.StartupProject;
        Toolbar.SetStartup(
            [.. solution.Projects.Where(p => Runs.IsExecutable(p.Path) || Codalus.Workbench.Projects.SolutionService.PathComparer.Equals(p.Path, startup)).Select(p => new StartupProjectChoice(p.Name, p.Path))],
            startup,
            startup is null ? null : Runs.ChosenProfile(startup));
        UpdateToolbarDevice();
    }
}
