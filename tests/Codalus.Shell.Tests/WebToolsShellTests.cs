using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Projects.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>
/// M15c in the shell: Add New Scaffolded Item (a form generated from the scaffolder's parameters, the DbContext offered
/// from Roslyn, the fixture's local dotnet-aspnet-codegenerator) and the Service References document (a gRPC reference
/// added from the fixture's .proto file, then changed), on samples/Scaffolding.
/// </summary>
public sealed class WebToolsShellTests
{
    [AvaloniaFact]
    public async Task Scaffolding_and_service_references()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("Scaffolding");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Scaffolding.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);
        var restore = await f.Shell.Packages.RunToolCommandAsync(new ToolCommandParams(ToolActions.Restore), CancellationToken.None);
        Assert.True(restore.Succeeded, restore.Output);
        Assert.Equal("proto", f.Shell.Languages.LanguageIdOf(Path.Combine(folder, "contracts", "greet.proto")));
        Assert.NotNull(f.Shell.Workbench.Commands.Find("web.addScaffoldedItem"));
        Assert.NotNull(f.Shell.Workbench.Commands.Find("web.serviceReferences"));

        // The form follows the scaffolder: an Entity Framework API controller asks for a model and a context.
        var shop = Path.Combine(folder, "Shop", "Shop.csproj");
        var form = f.Shell.OpenAddScaffoldedItem(shop, Path.Combine(folder, "Shop", "Controllers"));
        Assert.NotNull(form);
        form.Select("api.ef");
        Assert.Equal(["model", "dbContext", "name", "folder", "dbProvider"], form.Fields.Select(x => x.Id));
        Assert.Equal("Controllers", Assert.IsType<ScaffoldTextField>(form.Field("folder")).Text);
        await ShellFixture.PumpUntil(() => form.Status is not null, seconds: 120);
        Assert.False(form.NeedsTool, form.ToolMessage);
        Assert.Equal("sqlite", Assert.IsType<ScaffoldChoiceField>(form.Field("dbProvider")).Selected);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<ScaffoldView>().Any());

        // A missing model is caught before anything runs; then the generator writes the controller, which opens.
        await form.AddAsync();
        Assert.Equal("Model class is required.", form.Error);
        Assert.IsType<ScaffoldTextField>(form.Field("model")).Text = "Product";
        Assert.IsType<ScaffoldTextField>(form.Field("dbContext")).Text = "ShopContext";
        NavigationDepthTests.Screenshot(f, "scaffold");
        await form.AddAsync();
        Assert.True(form.Error is null, form.Error + "\n" + f.Shell.Scaffolding.Channel.GetText());
        Assert.Equal("Added 1 file.", form.Result);
        Assert.True(File.Exists(Path.Combine(folder, "Shop", "Controllers", "ProductsController.cs")));
        Assert.EndsWith("ProductsController.cs", f.Shell.ActiveEditor?.Model.Path, StringComparison.Ordinal);

        // A Razor Page from the SDK's item template needs no tool.
        form.Select("pages.empty");
        Assert.IsType<ScaffoldTextField>(form.Field("name")).Text = "About";
        Assert.IsType<ScaffoldTextField>(form.Field("folder")).Text = "Pages";
        await form.AddAsync();
        Assert.True(form.Error is null, form.Error);
        Assert.True(File.Exists(Path.Combine(folder, "Shop", "Pages", "About.cshtml.cs")));

        // Service references: a gRPC client from the fixture's .proto file, then its services changed.
        var client = Path.Combine(folder, "Client", "Client.csproj");
        var references = f.Shell.OpenServiceReferences(client);
        Assert.NotNull(references);
        await ShellFixture.PumpUntil(() => !references.IsBusy && references.Packages.Length > 0, seconds: 60);
        Assert.True(references.IsEmpty);
        references.KindLabel = "gRPC";
        references.Source = Path.Combine(folder, "contracts", "greet.proto");
        references.GrpcServices = GrpcServiceKinds.Client;
        await references.RunAsync("add");
        Assert.True(references.Error is null, references.Error);
        var row = Assert.Single(references.References);
        Assert.Equal(("gRPC", "greet.proto"), (row.Kind, row.Name));
        Assert.Contains("Grpc.Tools", references.Packages, StringComparison.Ordinal);
        Assert.Contains("<Protobuf Include=\"Protos\\greet.proto\" GrpcServices=\"Client\" />", File.ReadAllText(client), StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<ServiceReferencesView>().Any());
        NavigationDepthTests.Screenshot(f, "service-references");

        references.EditServices = GrpcServiceKinds.Both;
        await references.RunAsync("save");
        Assert.True(references.Error is null, references.Error);
        Assert.Equal(GrpcServiceKinds.Both, references.Selected?.Info.GrpcServices);
        Assert.Contains("GrpcServices=\"Both\"", File.ReadAllText(client), StringComparison.Ordinal);
    }
}
