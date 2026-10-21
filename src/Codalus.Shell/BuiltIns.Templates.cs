using System.Text.Json;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Settings;

namespace Codalus.Shell;

/// <summary>
/// New Project and New Solution (ADR 0082): the IDE's own since 0.6.0, through the registries plugins use, with the ids
/// and the setting the retired Templates plugin had (<c>templates.newProject</c>, <c>templates.newSolution</c>,
/// <c>templates.defaultLocation</c>), and Add New Project.
/// </summary>
internal static partial class BuiltIns
{
    public const string TemplatesKeybindings = """
        [
          { "key": "primary+shift+n", "command": "templates.newProject" }
        ]
        """;

    public static IReadOnlyList<SettingDescriptor> TemplatesSettings() =>
    [
        SettingDescriptor.Create(ShellHost.DefaultLocationSetting, "\"\"", SettingType.String, ContributionSource.Shell) with
        {
            Title = "Default Location",
            Category = "Templates",
            Description = "Folder for new solutions and projects when no solution is open. Empty: a 'source/repos' folder in your home folder.",
            Scope = SettingScope.User,
        },
    ];

    public static void RegisterTemplatesCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, string category, NewProjectMode mode, string enablement, bool palette = true) =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell)
                {
                    Category = category,
                    Enablement = WhenClause.Parse(enablement),
                    ShowInPalette = palette,
                },
                async (args, _) =>
                {
                    // An optional { template, language, fromStartWindow }; the result is the created project's path (the
                    // solution's for Blank Solution) once it's open, or null when cancelled. The wizard isn't modal: this
                    // waits for it without blocking anything.
                    var open = NewProjectViewModel.ParseArguments(args, mode);
                    if (shell.Remote is not null)
                    {
                        // TODO(issue): New Project in a remote window, through the server's build host.
                        shell.Workbench.Notifications.Info(Localizer.Get("msg.newProject.remoteUnavailable", "New Project isn't available in a remote window yet."));
                        return null;
                    }

                    var wizard = await Infrastructure.Ui.RunAsync<Task<string?>>(() => shell.OpenNewProjectWizardAsync(open)).ConfigureAwait(false);
                    return ShellHost.PathResult(await wizard.ConfigureAwait(false));
                });

        Add("templates.newProject", "New Project…", "File", NewProjectMode.Project, "!remoteConnected");
        Add("templates.newSolution", "New Solution…", "File", NewProjectMode.Solution, "!remoteConnected");
        Add("solution.addNewProject", "Add New Project…", "Solution", NewProjectMode.Add, "solutionOpen && !solutionIsProjectFile && !remoteConnected");
    }

    public static void RegisterTemplatesMenus(MenuRegistry menus)
    {
        var shell = ContributionSource.Shell;
        MenuItemRule Item(string location, string command, string group, int order = 0, string? when = null, string? title = null) =>
            new(location, command, shell) { Group = group, Order = order, When = WhenClause.Parse(when), Title = title };

        menus.Add(
        [
            Item(FileNewMenu, "templates.newProject", "1-new", 1),
            Item(FileNewMenu, "templates.newSolution", "1-new", 2),
            Item(SolutionExplorerView.AddMenuLocation, "solution.addNewProject", "3-solution", 0, "explorerItemKind == solution || explorerItemKind == solutionFolder", "New Project…"),
            Item(MenuLocations.MenuBar("project"), "solution.addNewProject", "1-add", -1),
        ]);
    }

    /// <summary>The command's JSON argument for the start window's Create a new project.</summary>
    public static JsonElement FromStartWindowArgument()
    {
        using var document = JsonDocument.Parse("""{ "fromStartWindow": true }""");
        return document.RootElement.Clone();
    }
}
