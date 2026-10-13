using System.Text.Json;
using DaedalusStudio.Shell.Views;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Context;
using DaedalusStudio.Workbench.Menus;

namespace DaedalusStudio.Shell;

/// <summary>
/// Project-system depth (M26a, ADR 0033): the Properties window, Show All Files, Include and Exclude, Add New and Add
/// Existing Item, templates, folder mode with its tasks and launch configurations, and External Tools.
/// </summary>
internal static partial class BuiltIns
{
    public const string ProjectSystemKeybindings = """
        [
          { "key": "f4", "command": "workbench.view.properties" },
          { "key": "primary+shift+a", "command": "project.addNewItem", "when": "solutionOpen" },
          { "key": "shift+alt+a", "command": "project.addExistingItem", "when": "solutionOpen && solutionTrusted" },
          { "key": "primary+shift+alt+o", "command": "workbench.openFolder" }
        ]
        """;

    public static void RegisterProjectSystemCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        var explorer = shell.Explorer;
        void Add(string id, string title, string category, CommandHandler handler, string? enablement = null, bool palette = true) =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell)
                {
                    Category = category,
                    Enablement = WhenClause.Parse(enablement),
                    ShowInPalette = palette,
                },
                handler);

        // An optional string argument (a path or a name), for plugins and tests.
        static string? Argument(IReadOnlyList<JsonElement> args) => args.Count > 0 && args[0].ValueKind == JsonValueKind.String ? args[0].GetString() : null;

        const string Open = "solutionOpen";
        const string Trusted = "solutionOpen && solutionTrusted";
        Add("workbench.openFolder", "Open Folder…", "File", async (args, _) =>
        {
            await Infrastructure.Ui.RunAsync(() => shell.OpenFolderWorkspaceAsync(Argument(args))).ConfigureAwait(false);
            return null;
        });

        Add("explorer.showAllFiles", "Show All Files", "Explorer", Ui(() => explorer.ShowAllFiles = !explorer.ShowAllFiles), Open);
        Add("explorer.switchViews", "Switch Views", "Explorer", Ui(() => explorer.SetFolderView(!explorer.IsFolderView)), "folderMode");
        Add("explorer.excludeFromProject", "Exclude From Project", "Project", Ui(shell.ExcludeFromProjectAsync), Trusted + " && explorerItemInProject");
        Add("explorer.includeInProject", "Include In Project", "Project", Ui(shell.IncludeSelectedInProjectAsync), Trusted + " && explorerItemExcluded");

        Add("project.addNewItem", "Add New Item…", "Project", async (args, _) =>
        {
            await Infrastructure.Ui.RunAsync(() => shell.OpenAddNewItem(Argument(args))).ConfigureAwait(false);
            return null;
        }, Open);
        Add("project.addExistingItem", "Add Existing Item…", "Project", Ui(() => shell.AddExistingItemAsync(asLink: false)), Trusted);
        Add("project.addExistingItemAsLink", "Add Existing Item As Link…", "Project", Ui(() => shell.AddExistingItemAsync(asLink: true)), Trusted);
        Add("project.exportTemplate", "Export Template…", "Project", Ui(() => shell.OpenExportTemplate()), Open);
        Add("tools.templates", "Templates…", "Tools", Ui(() => shell.OpenTemplateManager()));
        Add("tools.externalTools", "External Tools…", "Tools", Ui(() => shell.OpenExternalTools()));

        // Folder mode (ADR 0033).
        Add("folder.loadAllProjects", "Load All Projects", "Folder", Ui(shell.LoadAllFolderProjectsAsync), "folderMode && solutionTrusted");
        Add("folder.loadProject", "Load Project", "Folder", Ui(shell.LoadSelectedFolderProjectAsync), "folderMode && solutionTrusted && explorerItemIsProjectFile && !explorerProjectLoaded", palette: false);
        Add("folder.unloadProject", "Unload Project", "Folder", Ui(shell.UnloadSelectedFolderProjectAsync), "folderMode && explorerItemIsProjectFile && explorerProjectLoaded", palette: false);
        Add("folder.openProject", "Open Project", "Folder", Ui(shell.OpenSelectedProjectAsync), "explorerItemIsProjectFile", palette: false);
        Add("tasks.run", "Run Task…", "Tasks", async (args, _) =>
        {
            await Infrastructure.Ui.RunAsync(() => shell.RunTaskAsync(Argument(args))).ConfigureAwait(false);
            return null;
        }, Trusted);
        Add("tasks.configure", "Configure Tasks", "Tasks", Ui(shell.ConfigureTasksAsync), Open);
        Add("launch.configure", "Configure Launch", "Debug", Ui(shell.ConfigureLaunchAsync), Open);
        Add("run.selectLaunchConfiguration", "Select Launch Configuration…", "Debug", async (args, _) =>
        {
            if (Argument(args) is { } name)
            {
                await Infrastructure.Ui.RunAsync(() => shell.SelectLaunchConfiguration(name)).ConfigureAwait(false);
            }
            else
            {
                await Infrastructure.Ui.RunAsync(shell.SelectLaunchConfigurationAsync).ConfigureAwait(false);
            }

            return null;
        }, "folderMode");
    }

    public static void RegisterProjectSystemMenus(MenuRegistry menus)
    {
        var shell = ContributionSource.Shell;
        MenuItemRule Item(string location, string command, string group, int order = 0, string? when = null, string? title = null) =>
            new(location, command, shell) { Group = group, Order = order, When = WhenClause.Parse(when), Title = title };

        const string Context = SolutionExplorerView.ContextMenuLocation;
        const string Containers = "(explorerItemKind == project || explorerItemKind == folder || explorerItemKind == file) && !explorerFolderView";
        menus.Add(
        [
            Item(SolutionExplorerView.AddMenuLocation, "project.addNewItem", "1-new", -3, Containers, "New Item…"),
            Item(SolutionExplorerView.AddMenuLocation, "project.addExistingItem", "1-new", -2, Containers, "Existing Item…"),
            Item(SolutionExplorerView.AddMenuLocation, "project.addExistingItemAsLink", "1-new", -1, Containers, "Existing Item As Link…"),
            Item(Context, "explorer.excludeFromProject", "3-edit", 8, "explorerItemInProject"),
            Item(Context, "explorer.includeInProject", "3-edit", 8, "explorerItemExcluded"),
            Item(Context, "folder.loadAllProjects", "4-solution", 0, "explorerItemKind == solution && folderMode"),
            Item(Context, "explorer.switchViews", "4-solution", 1, "explorerItemKind == solution && folderMode"),
            Item(Context, "folder.loadProject", "6-load", 3, "folderMode && explorerItemIsProjectFile && !explorerProjectLoaded"),
            Item(Context, "folder.unloadProject", "6-load", 4, "folderMode && explorerItemIsProjectFile && explorerProjectLoaded"),
            Item(Context, "folder.openProject", "6-load", 5, "explorerItemIsProjectFile"),
            Item(Context, "project.exportTemplate", "8-templates", 1, "explorerItemKind == project || explorerItemKind == file"),
            Item(Context, "workbench.view.properties", "9-properties", 2, "explorerItemKind && explorerItemKind != project", "Properties"),
        ]);
    }
}
