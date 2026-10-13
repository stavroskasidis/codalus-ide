using Codalus.Workbench.Web;

namespace Codalus.Workbench.Tests;

/// <summary>The scaffolder catalog and the command lines it makes (M15c, ADR 0041).</summary>
public sealed class ScaffoldersTests
{
    private static readonly string[] s_words = ["Product", "Category", "Box", "Day", "Match"];

    private static ScaffoldPlan Plan(string id, params (string Key, string? Value)[] values) =>
        Scaffolders.Plan(Scaffolders.Find(id)!, values.ToDictionary(v => v.Key, v => v.Value));

    [Fact]
    public void Every_category_has_scaffolders_and_every_parameter_is_well_formed()
    {
        Assert.All(Scaffolders.Categories, category => Assert.Contains(Scaffolders.All, s => s.Category == category));
        Assert.Equal(Scaffolders.All.Count, Scaffolders.All.Select(s => s.Id).Distinct().Count());
        foreach (var scaffolder in Scaffolders.All)
        {
            Assert.Equal(scaffolder.Parameters.Count, scaffolder.Parameters.Select(p => p.Id).Distinct().Count());
            Assert.All(scaffolder.Parameters.Where(p => p.Kind is ScaffoldParameterKind.Choice or ScaffoldParameterKind.DbProvider), p => Assert.NotEmpty(p.Choices!));
            Assert.All(scaffolder.Parameters.Where(p => p.Positional), p => Assert.Equal(ScaffoldEngine.CodeGenerator, scaffolder.Engine));
        }
    }

    [Fact]
    public void Code_generator_command_lines()
    {
        // The controller's name defaults to the model in the plural; options follow in order, fixed arguments last.
        var api = Plan("api.ef", ("model", "Category"), ("dbContext", "ShopContext"));
        Assert.Equal("controller", api.Command);
        Assert.Equal(["-m", "Category", "-dc", "ShopContext", "-name", "CategoriesController", "-outDir", "Controllers", "-api"], api.Arguments);

        var mvc = Plan("mvc.ef", ("model", "Product"), ("dbContext", "ShopContext"), ("scripts", "true"), ("dbProvider", "sqlite"));
        Assert.Equal(["-m", "Product", "-dc", "ShopContext", "-name", "ProductsController", "-outDir", "Controllers", "-udl", "-scripts", "-dbProvider", "sqlite"], mvc.Arguments);

        // Razor Pages: CRUD takes no name or template; a single page takes both, positionally.
        Assert.Equal(["-m", "Product", "-dc", "ShopContext", "-outDir", "Pages/Products", "-udl"], Plan("pages.ef", ("model", "Product"), ("dbContext", "ShopContext"), ("folder", "Pages\\Products\\")).Arguments);
        Assert.Equal(["Edit", "Edit", "-m", "Product", "-dc", "ShopContext", "-outDir", "Pages", "-udl"], Plan("pages.ef", ("name", "Edit"), ("template", "Edit"), ("model", "Product"), ("dbContext", "ShopContext")).Arguments);

        Assert.Equal(["CRUD", "-m", "Product", "-dc", "ShopContext", "-outDir", "Components/Pages"], Plan("blazor.crud", ("model", "Product"), ("dbContext", "ShopContext")).Arguments);
        Assert.Equal(["-m", "Product", "-dc", "ShopContext", "-outDir", "Endpoints", "-o"], Plan("minimal.ef", ("model", "Product"), ("dbContext", "ShopContext"), ("openApi", "true")).Arguments);
        Assert.Equal(["-dc", "ApplicationDbContext", "-fi", "Account.Login;Account.Register"], Plan("identity", ("files", "Account.Login;Account.Register")).Arguments);
    }

    [Fact]
    public void Item_template_and_built_in_plans()
    {
        var empty = Plan("api.actions");
        Assert.Equal(("apicontroller", "ValuesController", "Controllers"), (empty.Command, empty.Name, empty.Folder));
        Assert.Equal(["--actions"], empty.Arguments);
        Assert.Equal(["--no-pagemodel"], Plan("pages.empty", ("name", "About"), ("noPageModel", "true")).Arguments);

        var block = Scaffolders.EndpointsClass("Shop.Endpoints", "OrderEndpoints", "/api/orders", fileScoped: false);
        Assert.StartsWith("namespace Shop.Endpoints\n{\n    public static class OrderEndpoints", block, StringComparison.Ordinal);
        Assert.Contains("MapOrderEndpoints(this IEndpointRouteBuilder routes)", block, StringComparison.Ordinal);
        Assert.Contains(".WithTags(\"Order\")", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Values_are_validated()
    {
        static string? Check(string id, params (string Key, string? Value)[] values) => Scaffolders.Validate(Scaffolders.Find(id)!, values.ToDictionary(v => v.Key, v => v.Value));

        Assert.Equal("Model class is required.", Check("api.ef", ("dbContext", "ShopContext")));
        Assert.Contains("isn't a C# type name", Check("api.ef", ("model", "Pro duct"), ("dbContext", "ShopContext")), StringComparison.Ordinal);
        Assert.Contains("inside the project", Check("api.empty", ("folder", "../Elsewhere")), StringComparison.Ordinal);
        Assert.Contains("one of", Check("blazor.crud", ("template", "Everything"), ("model", "Product"), ("dbContext", "ShopContext")), StringComparison.Ordinal);
        Assert.Equal("A view from a template needs a model class.", Check("mvc.view", ("template", "Edit")));
        Assert.Null(Check("mvc.view", ("template", "Empty")));
        Assert.Null(Check("api.ef", ("model", "Shop.Models.Product"), ("dbContext", "Shop.Data.ShopContext")));
        Assert.Throws<ArgumentException>(() => Plan("api.ef"));
    }

    [Fact]
    public void Plurals_and_providers()
    {
        Assert.Equal(["Products", "Categories", "Boxes", "Days", "Matches"], s_words.Select(Scaffolders.Plural));
        Assert.Equal("sqlite", Scaffolders.DbProviderFor(["Microsoft.EntityFrameworkCore.Sqlite"]));
        Assert.Equal("postgres", Scaffolders.DbProviderFor(["Npgsql.EntityFrameworkCore.PostgreSQL"]));
        Assert.Null(Scaffolders.DbProviderFor(["Pomelo.EntityFrameworkCore.MySql"]));
    }
}
