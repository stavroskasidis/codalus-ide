using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DaedalusStudio.Data.Protocol;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Shell.Views;

namespace DaedalusStudio.Shell.Tests;

/// <summary>
/// M27b in the shell: a SQLite connection added in the form, the Database Explorer's tree, table data edited and
/// committed, a query editor with completion from the schema, results, a plan and the history; and the solution's own
/// connection strings.
/// </summary>
public sealed class DataShellTests
{
    private static async Task<DbNodeViewModel> ChildAsync(DbNodeViewModel node, Func<DbNodeViewModel, bool> match)
    {
        node.IsExpanded = true;
        await ShellFixture.PumpUntil(() => node.Children.Any(match), seconds: 60);
        return node.Children.First(match);
    }

    [AvaloniaFact]
    public async Task Explorer_table_data_and_the_query_editor()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("Data");
        var database = Path.Combine(folder, "Inventory", "inventory.db");

        // Add a connection through the form.
        f.Shell.ShowDatabaseExplorer();
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<DatabaseExplorerView>().Any());
        var form = f.Shell.OpenConnectionForm().ViewModel;
        form.EngineName = "SQLite";
        form.File = database;
        Assert.Equal("Data Source=" + database, form.BuildConnectionString());
        await form.TestAsync();
        Assert.StartsWith("Connected: SQLite 3.", form.Result, StringComparison.Ordinal);
        await form.SaveAsync();
        var connection = Assert.Single(f.Shell.DbExplorer.Roots);
        Assert.Equal("inventory.db", connection.Label);

        // The tree: Tables → Products → its columns, keys and indexes.
        var tables = await ChildAsync(connection, n => n.Label == "Tables");
        var products = await ChildAsync(tables, n => n.Label == "Products");
        Assert.Contains(products.Children, n => n.Kind == DbNodeKinds.Column && n.Label == "Sku" && n.Description!.StartsWith("TEXT", StringComparison.Ordinal));
        Assert.Contains(products.Children, n => n.Label == "Keys");
        var views = await ChildAsync(connection, n => n.Label == "Views");
        Assert.Contains(views.Children, n => n.Label == "ProductsInStock");
        f.Shell.DbExplorer.Selected = products;
        Assert.Equal("table", f.Shell.Workbench.Context.GetValue("dataNodeKind"));
        NavigationDepthTests.Screenshot(f, "data-explorer");

        // Table data: a page, an edit, the commit.
        var document = await f.Shell.ViewTableDataAsync(products);
        Assert.NotNull(document);
        var table = document.ViewModel;
        await ShellFixture.PumpUntil(() => table.Grid is { Rows.Count: 5 } && table.Status?.Contains("of 5", StringComparison.Ordinal) == true, seconds: 60);
        Assert.False(table.IsReadOnly);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<TableDataView>().Any(v => v.Grid.Model == table.Grid));
        var nameColumn = Array.FindIndex(table.Grid!.Columns, c => c.Name == "Name");
        table.Grid.Rows[0].Set(nameColumn, "Masala Chai");
        Assert.True(table.HasPendingChanges);
        Assert.True(document.IsModified);
        NavigationDepthTests.Screenshot(f, "data-table");
        Assert.True(await table.CommitAsync());
        Assert.Equal("Masala Chai", table.Grid!.Rows[0].Values[nameColumn]);
        Assert.False(document.IsModified);
        table.AddRow();
        table.Revert();
        Assert.False(table.HasPendingChanges);

        // A query editor on the table: completion from the schema, results, a plan and the history.
        var editor = await f.Shell.OpenQueryAsync(products);
        Assert.NotNull(editor);
        await ShellFixture.PumpUntil(() => editor.Controller is not null);
        Assert.Equal(ShellHost.SqlLanguageId, editor.Model.LanguageId);
        Assert.Contains("FROM \"Products\" LIMIT 100", editor.Model.Text.Text, StringComparison.Ordinal);
        Assert.Contains(f.Shell.StatusBar.Right, i => i.Item.Id == ShellHost.SqlConnectionStatusItem && i.Item.Text.Contains("inventory.db", StringComparison.Ordinal));

        editor.Model.Text.Text = "SELECT p.Name, c. FROM Products p JOIN Categories c ON c.CategoryId = p.CategoryId";
        var client = await f.Shell.Languages.ClientAsync(ShellHost.SqlLanguageId);
        Assert.NotNull(client);
        CompletionList? completion = null;
        await ShellFixture.PumpUntil(() =>
        {
            completion = client.CompletionAsync(editor.Model.Uri, new Position(0, "SELECT p.Name, c.".Length), null, ShellFixture.RequestTimeout()).GetAwaiter().GetResult();
            return completion.Items.Any(i => i.Label == "Description");
        }, seconds: 60);
        Assert.DoesNotContain(completion!.Items, i => i.Label == "Sku");

        editor.Model.Text.Text = "SELECT Name, UnitPrice FROM Products ORDER BY ProductId;";
        var result = await f.Shell.ExecuteQueryAsync();
        Assert.NotNull(result);
        Assert.Equal(5, Assert.Single(f.Shell.QueryResults.Results).Grid.Rows.Count);
        Assert.Equal("Masala Chai", f.Shell.QueryResults.Results[0].Grid.Rows[0].Values[0]);
        Assert.Contains("5 row(s)", f.Shell.QueryResults.Status, StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<QueryResultsView>().Any(v => v.ResultGrids.Any()));
        NavigationDepthTests.Screenshot(f, "data-query");

        await f.Shell.ExecuteQueryAsync(plan: PlanKinds.Estimated);
        Assert.True(f.Shell.QueryResults.HasPlan);
        Assert.Contains(f.Shell.QueryResults.Plan, n => n.Operation == "Scan");
        Assert.Equal(2, f.Shell.QueryResults.SelectedTab);
        NavigationDepthTests.Screenshot(f, "data-plan");

        // An error goes to the messages; the history keeps every run.
        editor.Model.Text.Text = "SELECT * FROM Nowhere";
        await f.Shell.ExecuteQueryAsync();
        Assert.Contains("no such table", f.Shell.QueryResults.Messages, StringComparison.Ordinal);
        Assert.Equal(1, f.Shell.QueryResults.SelectedTab);
        Assert.True(f.Shell.QueryResults.History.Count >= 3);
        Assert.Equal("SELECT * FROM Nowhere", f.Shell.QueryResults.History[0].Entry.Text);

        // The statement at the caret.
        Assert.Equal("SELECT 2;", ShellHost.StatementAt("SELECT 1;\nSELECT 2;\nSELECT 3", 12));
        Assert.Equal("SELECT 3", ShellHost.StatementAt("SELECT 1;\nGO\nSELECT 3\n", 14));
        Assert.Equal("SELECT ';'", ShellHost.StatementAt("SELECT ';'", 3));
    }

    [AvaloniaFact]
    public async Task The_solutions_connection_strings_are_listed()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("Data");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Data.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);
        f.Shell.ShowDatabaseExplorer();
        await ShellFixture.PumpUntil(() => f.Shell.DbExplorer.Roots.Any(r => r.Kind == DbNodeKinds.Group), seconds: 60);
        var group = f.Shell.DbExplorer.Roots.Single(r => r.Kind == DbNodeKinds.Group);
        var blogging = group.Children.Single(c => c.Label == "Blogging: Blogging");
        Assert.Contains("appsettings.json", blogging.Description, StringComparison.Ordinal);
        Assert.Contains(group.Children, c => c.Label == "Blogging: BloggingTest");
        var connection = f.Shell.Data.Find(blogging.ConnectionId!)!;
        Assert.Equal(DataEngines.Sqlite, connection.Engine);
        Assert.Equal("Data Source=" + Path.Combine(folder, "Blogging", "blogging.db"), connection.ConnectionString); // relative to the project
        Assert.False(File.Exists(Path.Combine(f.UserData, "state", "data-connections.json")) && File.ReadAllText(Path.Combine(f.UserData, "state", "data-connections.json")).Contains("blogging", StringComparison.OrdinalIgnoreCase));
    }
}
