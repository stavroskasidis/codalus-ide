using System.Text.Json;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Shell.Views;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Context;
using DaedalusStudio.Workbench.Menus;
using DaedalusStudio.Workbench.Settings;
using DaedalusStudio.Workbench.ToolWindows;

namespace DaedalusStudio.Shell;

/// <summary>Data and EF Core (M27): the Entity Framework window and its commands.</summary>
internal static partial class BuiltIns
{
    public const string DataKeybindings = """
        [
          { "key": "ctrl+shift+e", "command": "data.execute", "when": "editorLanguageId == sql" },
          { "key": "ctrl+shift+enter", "command": "data.executeStatement", "when": "editorLanguageId == sql" },
          { "key": "alt+pause", "command": "data.cancelQuery", "when": "queryRunning" },
          { "key": "ctrl+shift+l", "command": "data.estimatedPlan", "when": "editorLanguageId == sql" },
          { "key": "ctrl+shift+m", "command": "data.actualPlan", "when": "editorLanguageId == sql" },
          { "key": "ctrl+alt+d", "command": "data.explorer", "when": "keyboardScheme != vscode" }
        ]
        """;

    public static IReadOnlyList<SettingDescriptor> DataSettings() =>
    [
        SettingDescriptor.Create(ShellHost.MaxRowsSetting, "10000", SettingType.Integer, ContributionSource.Shell) with
        {
            Title = "Maximum Rows per Result",
            Category = "Data",
            Description = "The query editor shows at most this many rows of each result set; the results say when they were cut.",
        },
        SettingDescriptor.Create("data.pageSize", "200", SettingType.Integer, ContributionSource.Shell) with
        {
            Title = "Table Data Page Size",
            Category = "Data",
            Description = "Rows per page when viewing and editing a table's data.",
        },
    ];

    public static IEnumerable<ToolWindowDescriptor> DataToolWindows()
    {
        var shell = ContributionSource.Shell;
        yield return new ToolWindowDescriptor(EntityFrameworkViewModel.ToolWindowId, "Entity Framework", ToolWindowLocation.Right, ToolWindowKind.BuiltIn, shell) { Icon = "database" };
        yield return new ToolWindowDescriptor(DatabaseExplorerViewModel.ToolWindowId, "Database Explorer", ToolWindowLocation.Right, ToolWindowKind.BuiltIn, shell) { Icon = "database" };
        yield return new ToolWindowDescriptor(QueryResultsViewModel.ToolWindowId, "Query Results", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "table" };
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

        // The database explorer and the query editor (M27b).
        void Data(string id, string title, Func<Task> handler, string? enablement = null) =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell) { Category = "Data", Enablement = WhenClause.Parse(enablement) },
                async (_, _) =>
                {
                    await Infrastructure.Ui.RunAsync(handler).ConfigureAwait(false);
                    return null;
                });
        Task Sync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        Data("data.explorer", "Database Explorer", () => Sync(shell.ShowDatabaseExplorer));
        Data("data.addConnection", "Add Connection…", () => Sync(() => shell.OpenConnectionForm()));
        Data("data.editConnection", "Edit Connection…", () => Sync(shell.EditConnection));
        Data("data.removeConnection", "Remove Connection", shell.RemoveConnectionAsync);
        Data("data.disconnect", "Disconnect", shell.DisconnectDataAsync);
        Data("data.refresh", "Refresh", shell.RefreshDataAsync);
        Data("data.newQuery", "New Query", () => shell.OpenQueryAsync());
        Data("data.viewData", "View Data", () => shell.ViewTableDataAsync());
        Data("data.execute", "Execute Query", () => shell.ExecuteQueryAsync());
        Data("data.executeStatement", "Execute Current Statement", () => shell.ExecuteQueryAsync(statementOnly: true));
        Data("data.cancelQuery", "Cancel Query", () => Sync(shell.CancelQuery));
        Data("data.estimatedPlan", "Display Estimated Execution Plan", () => shell.ExecuteQueryAsync(plan: DaedalusStudio.Data.Protocol.PlanKinds.Estimated));
        Data("data.actualPlan", "Execute with Actual Execution Plan", () => shell.ExecuteQueryAsync(plan: DaedalusStudio.Data.Protocol.PlanKinds.Actual));
        Data("data.chooseConnection", "Change the Query's Connection…", () => shell.ChooseQueryConnectionAsync());
        Data("data.openPlan", "Open the Execution Plan", shell.OpenPlanAsync);
        Data("data.clearHistory", "Clear the Query History", () => Sync(shell.ClearQueryHistory));
        Data("data.results", "Query Results", () => Sync(() => shell.Layout.ShowToolWindow(QueryResultsViewModel.ToolWindowId, focus: true)));
        Data("data.reverseEngineer", "Reverse Engineer with EF Core…", () => shell.ReverseEngineerFromDatabaseAsync());
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

            Item(MenuLocations.MenuBar("tools"), "data.explorer", "data", 1),
            Item(MenuLocations.MenuBar("tools"), "data.addConnection", "data", 2),
            Item(MenuLocations.MenuBar("tools"), "data.newQuery", "data", 3),
            Item(MenuLocations.EditorContext, "data.execute", "0-sql", 0, "editorLanguageId == sql"),
            Item(MenuLocations.EditorContext, "data.executeStatement", "0-sql", 1, "editorLanguageId == sql"),
            Item(MenuLocations.EditorContext, "data.estimatedPlan", "0-sql", 2, "editorLanguageId == sql"),
            Item(MenuLocations.EditorContext, "data.chooseConnection", "0-sql", 3, "editorLanguageId == sql"),
            Item(DatabaseExplorerView.ContextMenuLocation, "data.viewData", "1-data", 0, "dataNodeKind == table || dataNodeKind == view"),
            Item(DatabaseExplorerView.ContextMenuLocation, "data.newQuery", "1-data", 1, "dataNodeKind && dataNodeKind != group && dataNodeKind != message"),
            Item(DatabaseExplorerView.ContextMenuLocation, "data.reverseEngineer", "2-ef", 0, "dataNodeKind == connection || dataNodeKind == database || dataNodeKind == table || dataNodeKind == folder"),
            Item(DatabaseExplorerView.ContextMenuLocation, "data.refresh", "3-connection", 0, "dataNodeKind && dataNodeKind != group"),
            Item(DatabaseExplorerView.ContextMenuLocation, "data.disconnect", "3-connection", 1, "dataNodeKind == connection"),
            Item(DatabaseExplorerView.ContextMenuLocation, "data.editConnection", "3-connection", 2, "dataNodeKind == connection"),
            Item(DatabaseExplorerView.ContextMenuLocation, "data.removeConnection", "3-connection", 3, "dataNodeKind == connection"),
            Item(DatabaseExplorerView.ContextMenuLocation, "data.addConnection", "4-add", 0),
        ]);
    }
}
