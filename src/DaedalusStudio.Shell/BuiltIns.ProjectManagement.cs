using System.Text.Json;
using DaedalusStudio.Shell.Views;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Context;
using DaedalusStudio.Workbench.Menus;

namespace DaedalusStudio.Shell;

/// <summary>Project properties, references, Configuration Manager, dependencies, load/unload, filters and SDKs (ADR 0013).</summary>
internal static partial class BuiltIns
{
    public const string ProjectManagementKeybindings = """
        [
          { "key": "alt+enter", "command": "project.properties", "when": "focusedView == solutionExplorer && explorerItemKind == project" }
        ]
        """;

    public static void RegisterProjectManagementCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, string category, Func<string?, Task> handler, string? enablement, bool palette = true) =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell)
                {
                    Category = category,
                    Enablement = WhenClause.Parse(enablement),
                    ShowInPalette = palette,
                },
                async (args, _) =>
                {
                    // An optional first argument: a project path (for plugins and tests).
                    var project = args.Count > 0 && args[0].ValueKind == JsonValueKind.String ? args[0].GetString() : null;
                    await Infrastructure.Ui.RunAsync(() => handler(project)).ConfigureAwait(false);
                    return null;
                });
        Func<string?, Task> Sync(Action<string?> action) => p =>
        {
            action(p);
            return Task.CompletedTask;
        };

        const string Trusted = "solutionOpen && solutionTrusted";
        Add("project.properties", "Properties", "Project", Sync(p => shell.OpenProjectProperties(p)), Trusted);
        Add("project.references", "Reference Manager…", "Project", Sync(p => shell.OpenReferenceManager(p)), Trusted);
        Add("solution.configurationManager", "Configuration Manager…", "Build", Sync(_ => shell.OpenConfigurationManager()), Trusted);
        Add("solution.projectDependencies", "Project Dependencies…", "Project", Sync(p => shell.OpenProjectDependencies(p)), Trusted);
        Add("project.unload", "Unload Project", "Project", shell.UnloadProjectAsync, Trusted + " && !explorerProjectUnloaded");
        Add("project.reload", "Reload Project", "Project", shell.ReloadProjectAsync, Trusted + " && explorerProjectUnloaded");
        Add("solution.saveFilter", "Save As Solution Filter…", "File", _ => shell.SaveSolutionFilterAsync(), Trusted);
        Add("solution.sdks", ".NET SDKs…", "Tools", Sync(_ => shell.OpenSdks()), Trusted);
    }

    public static void RegisterProjectManagementMenus(MenuRegistry menus)
    {
        var shell = ContributionSource.Shell;
        MenuItemRule Item(string location, string command, string group, int order = 0, string? when = null, string? title = null) =>
            new(location, command, shell) { Group = group, Order = order, When = WhenClause.Parse(when), Title = title };

        const string Context = SolutionExplorerView.ContextMenuLocation;
        menus.Add(
        [
            Item(MenuLocations.MenuBar("project"), "project.references", "add", 4),
            Item(MenuLocations.MenuBar("project"), "solution.projectDependencies", "solution", 4),
            Item(MenuLocations.MenuBar("project"), "solution.sdks", "solution", 5),
            Item(MenuLocations.MenuBar("project"), "project.properties", "z-properties", 1, title: "Project Properties"),
            Item(MenuLocations.MenuBar("build"), "solution.configurationManager", "4-configuration", 2),
            Item(MenuLocations.MenuBar("file"), "solution.saveFilter", "solution", 4),

            Item(Context, "project.references", "2-references", 3, "explorerItemKind == project || explorerItemKind == dependencies"),
            Item(Context, "solution.configurationManager", "4-solution", 4, "explorerItemKind == solution"),
            Item(Context, "solution.projectDependencies", "4-solution", 5, "explorerItemKind == solution || explorerItemKind == project"),
            Item(Context, "solution.saveFilter", "4-solution", 6, "explorerItemKind == solution"),
            Item(Context, "project.unload", "6-load", 1, "explorerItemKind == project && !explorerProjectUnloaded"),
            Item(Context, "project.reload", "6-load", 2, "explorerItemKind == project && explorerProjectUnloaded"),
            Item(Context, "project.properties", "9-properties", 1, "explorerItemKind == project"),
        ]);
    }
}
