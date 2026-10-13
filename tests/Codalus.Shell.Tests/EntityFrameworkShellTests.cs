using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Projects.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>
/// M27a in the shell: the Entity Framework window on samples/Data, the SQL shown before a migration is applied, Add
/// Migration, the model view's diagram and reverse engineering. dotnet-ef is the fixture's local tool.
/// </summary>
public sealed class EntityFrameworkShellTests
{
    [AvaloniaFact]
    public async Task Migrations_model_view_and_reverse_engineering()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("Data");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Data.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);
        var restore = await f.Shell.Packages.RunToolCommandAsync(new ToolCommandParams(ToolActions.Restore), CancellationToken.None);
        Assert.True(restore.Succeeded, restore.Output);
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: LanguageServices.Protocol.WorkspaceStates.Ready, Projects: 1 }, seconds: 180);
        Assert.True(Equals(f.Shell.Workbench.Context.GetValue("efProject"), true));

        // The window: the context from Roslyn (provider and connection name), its migrations from dotnet ef.
        await f.Shell.ShowEntityFrameworkAsync();
        var ef = f.Shell.EfExplorer;
        await ShellFixture.PumpUntil(() => ef.Context is { Name: "BloggingContext" }, seconds: 120);
        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", ef.Context!.Provider);
        Assert.Equal("Blogging", ef.Context.ConnectionName);
        await ShellFixture.PumpUntil(() => ef.Migrations.Count == 1 && !ef.IsBusy, seconds: 300);
        Assert.Equal("InitialCreate", ef.Migrations[0].Name);
        Assert.True(ef.Migrations[0].State == "Pending", $"{ef.Migrations[0].State}: {ef.Message}\n{f.Shell.EntityFramework.Channel.GetText()}");
        Assert.Contains(ef.Connections, c => c.Label == "Blogging (appsettings.json)");
        Assert.Contains(ef.Connections, c => c.Label == "BloggingTest (appsettings.Development.json)");
        await ShellFixture.PumpUntil(() => ef.Status?.StartsWith("dotnet-ef 10.0.12 (local tool)", StringComparison.Ordinal) == true, seconds: 120);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<EntityFrameworkView>().Any());

        // Update Database shows the SQL first; nothing is applied until Apply.
        var update = f.Shell.UpdateDatabaseAsync(null);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen, seconds: 120);
        Assert.Contains(f.Shell.Dialogs.Details, l => l.Text.Contains("CREATE TABLE \"Blogs\"", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(folder, "Blogging", "blogging.db")));
        NavigationDepthTests.Screenshot(f, "ef-update-database");
        f.Shell.Dialogs.Buttons.Single(b => b.Label == "Apply").Command.Execute(null);
        await ShellFixture.PumpUntil(() => update.IsCompleted, seconds: 180);
        Assert.True(await update);
        Assert.True(File.Exists(Path.Combine(folder, "Blogging", "blogging.db")));
        await ShellFixture.PumpUntil(() => ef.Migrations.Count == 1 && ef.Migrations[0].Applied == true && !ef.IsBusy, seconds: 120);

        // A model change and Add Migration: the migration opens in the editor and is listed as pending.
        var contextFile = Path.Combine(folder, "Blogging", "Data", "BloggingContext.cs");
        File.WriteAllText(contextFile, File.ReadAllText(contextFile).Replace("public DateTime Published { get; set; }", "public DateTime Published { get; set; }\n\n    public int Rating { get; set; }", StringComparison.Ordinal));
        f.Shell.EntityFramework.Invalidate();
        await f.Shell.AddMigrationAsync("AddRating");
        await ShellFixture.PumpUntil(() => ef.Migrations.Count == 2 && !ef.IsBusy, seconds: 180);
        Assert.Equal("Pending", ef.Migrations[1].State);
        Assert.EndsWith("_AddRating.cs", f.Shell.ActiveEditor?.Model.Path, StringComparison.Ordinal);

        // The model view: a box per entity type, owned types included, and the debug view.
        var model = await f.Shell.ShowEfModelAsync();
        Assert.NotNull(model);
        await ShellFixture.PumpUntil(() => model.ViewModel.Model is not null || model.ViewModel.Message?.StartsWith("Building", StringComparison.Ordinal) == false, seconds: 180);
        Assert.Null(model.ViewModel.Message);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<EfModelView>().Any(v => v.Diagram.Boxes.Count == 5));
        var diagram = f.Window.GetVisualDescendants().OfType<EfModelView>().Single().Diagram;
        Assert.Contains("Blogging.Data.Person", diagram.Boxes.Keys);
        Assert.Contains(model.ViewModel.Model!.Entities.Single(e => e.Name == "Blogging.Data.Post").Properties, p => p.Name == "Rating");
        Assert.Contains("EntityType: Blog", model.ViewModel.Model.DebugView, StringComparison.Ordinal);
        NavigationDepthTests.Screenshot(f, "ef-model");

        // Reverse engineering from the inventory database.
        var scaffold = f.Shell.OpenReverseEngineer();
        Assert.NotNull(scaffold);
        var form = scaffold.ViewModel;
        form.ConnectionString = "Data Source=" + Path.Combine(folder, "Inventory", "inventory.db");
        form.Provider = "Microsoft.EntityFrameworkCore.Sqlite";
        form.OutputDir = "Inventory";
        form.ContextName = "InventoryContext";
        form.Tables = "Products, Categories";
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<EfScaffoldView>().Any());
        NavigationDepthTests.Screenshot(f, "ef-reverse-engineer");
        await form.RunAsync();
        Assert.Null(form.Error);
        Assert.Equal("Generated 3 files.", form.Result);
        Assert.True(File.Exists(Path.Combine(folder, "Blogging", "Inventory", "InventoryContext.cs")));
        Assert.EndsWith("InventoryContext.cs", f.Shell.ActiveEditor?.Model.Path, StringComparison.Ordinal);
    }
}
