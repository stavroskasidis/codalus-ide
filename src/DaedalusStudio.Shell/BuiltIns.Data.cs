using System.Text.Json;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Shell.Views;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Context;
using DaedalusStudio.Workbench.Menus;
using DaedalusStudio.Workbench.ToolWindows;

namespace DaedalusStudio.Shell;

/// <summary>Data and EF Core (M27): the Entity Framework window and its commands.</summary>
internal static partial class BuiltIns
{
    public static IEnumerable<ToolWindowDescriptor> DataToolWindows()
    {
        var shell = ContributionSource.Shell;
        yield return new ToolWindowDescriptor(EntityFrameworkViewModel.ToolWindowId, "Entity Framework", ToolWindowLocation.Right, ToolWindowKind.BuiltIn, shell) { Icon = "database" };
    }

    public static void RegisterDataCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, Func<string?, Task> handler, string? enablement = "solutionOpen && solutionTrusted") =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell) { Category = "Entity Framework", Enablement = WhenClause.Parse(enablement) },
                async (args, _) =>
                {
                    // An optional first argument: a migration name (Add Migration, Update Database to).
                    var argument = args.Count > 0 && args[0].ValueKind == JsonValueKind.String ? args[0].GetString() : null;
                    await Infrastructure.Ui.RunAsync(() => handler(argument)).ConfigureAwait(false);
                    return null;
                });

        Add("ef.show", "Entity Framework", _ => shell.ShowEntityFrameworkAsync(), "solutionOpen");
        Add("ef.refresh", "Refresh Contexts and Migrations", _ => shell.EfExplorer.ReloadAsync(fromTool: true));
        Add("ef.addMigration", "Add Migration…", name => shell.AddMigrationAsync(name));
        Add("ef.removeMigration", "Remove Last Migration", _ => shell.RemoveMigrationAsync());
        Add("ef.updateDatabase", "Update Database", migration => shell.UpdateDatabaseAsync(migration));
        Add("ef.script", "Generate Migrations Script…", _ => shell.ScriptMigrationsAsync());
        Add("ef.bundle", "Create Migrations Bundle…", _ => shell.BundleMigrationsAsync());
        Add("ef.model", "Show the Model (Diagram and Debug View)", _ => shell.ShowEfModelAsync());
        Add("ef.optimize", "Generate Compiled Model…", _ => shell.OptimizeEfModelAsync());
        Add("ef.scaffold", "Reverse Engineer (Scaffold a DbContext)…", _ =>
        {
            shell.OpenReverseEngineer();
            return Task.CompletedTask;
        });
        Add("ef.installTool", "Install dotnet-ef", _ => shell.InstallEfToolAsync());
    }

    public static void RegisterDataMenus(MenuRegistry menus)
    {
        var shell = ContributionSource.Shell;
        MenuItemRule Item(string location, string command, string group, int order = 0, string? when = null, string? title = null) =>
            new(location, command, shell) { Group = group, Order = order, When = WhenClause.Parse(when), Title = title };

        menus.Add(
        [
            Item(MenuLocations.MenuBar("project"), "ef.show", "ef", 1, "efProject"),
            Item(MenuLocations.MenuBar("project"), "ef.addMigration", "ef", 2, "efProject"),
            Item(MenuLocations.MenuBar("project"), "ef.updateDatabase", "ef", 3, "efProject"),
            Item(MenuLocations.MenuBar("project"), "ef.scaffold", "ef", 4),
            Item(SolutionExplorerView.ContextMenuLocation, "ef.show", "4-ef", 1, "explorerItemKind == project && efProject", "Entity Framework"),
            Item(SolutionExplorerView.ContextMenuLocation, "ef.scaffold", "4-ef", 2, "explorerItemKind == project", "Reverse Engineer…"),
        ]);
    }
}
