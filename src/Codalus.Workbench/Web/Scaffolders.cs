namespace Codalus.Workbench.Web;

/// <summary>What runs a scaffolder (ADR 0041).</summary>
public enum ScaffoldEngine
{
    /// <summary>An item template of the SDK (<c>dotnet new apicontroller</c>…): no tool needed.</summary>
    DotnetNew,

    /// <summary>The user's <c>dotnet aspnet-codegenerator</c>, in the build host.</summary>
    CodeGenerator,

    /// <summary>A file the IDE writes itself (an empty minimal API endpoint group).</summary>
    BuiltIn,
}

public enum ScaffoldParameterKind
{
    /// <summary>A C# identifier (a class name).</summary>
    Name,
    Text,

    /// <summary>A model class, offered from the DbContexts' entity types.</summary>
    Model,

    /// <summary>A DbContext, offered from the project's contexts; a new name makes the generator create one.</summary>
    DbContext,
    Bool,
    Choice,

    /// <summary>A folder relative to the project.</summary>
    Folder,

    /// <summary>The database provider for a context the generator creates (<c>-dbProvider</c>).</summary>
    DbProvider,
}

/// <summary>
/// One field of a scaffolder's dialog. <see cref="Option"/> is the command-line option that carries it (<c>-m</c>);
/// null with <see cref="Positional"/> makes it an argument in order; a <see cref="ScaffoldParameterKind.Bool"/> adds its
/// option when true.
/// </summary>
public sealed record ScaffoldParameter(
    string Id,
    string Label,
    ScaffoldParameterKind Kind,
    string? Default = null,
    bool Required = false,
    string? Option = null,
    IReadOnlyList<string>? Choices = null,
    string? Description = null,
    bool Positional = false);

/// <summary>
/// A scaffolder of the "Add New Scaffolded Item" dialog. <see cref="Command"/> is the template's short name
/// (<see cref="ScaffoldEngine.DotnetNew"/>) or the generator (<see cref="ScaffoldEngine.CodeGenerator"/>);
/// <see cref="Fixed"/> arguments always follow it.
/// </summary>
public sealed record Scaffolder(
    string Id,
    string Category,
    string Name,
    string Description,
    ScaffoldEngine Engine,
    string Command,
    IReadOnlyList<ScaffoldParameter> Parameters,
    IReadOnlyList<string>? Fixed = null)
{
    public bool UsesEntityFramework => Parameters.Any(p => p.Kind == ScaffoldParameterKind.DbContext);
}

/// <summary>What running a scaffolder means: the template or generator, its arguments, and the output folder.</summary>
public sealed record ScaffoldPlan(Scaffolder Scaffolder, string Command, IReadOnlyList<string> Arguments, string? Name, string? Folder);

/// <summary>
/// The scaffolders (ADR 0041), Visual Studio's "Add New Scaffolded Item" set: empty items through the SDK's item
/// templates, the Entity Framework ones (controllers, views, Razor Pages, Blazor CRUD, minimal APIs) and Identity through
/// <c>dotnet-aspnet-codegenerator</c>. The dialog is generated from each one's <see cref="Scaffolder.Parameters"/>.
/// </summary>
public static class Scaffolders
{
    public const string Api = "API";
    public const string Mvc = "MVC";
    public const string RazorPages = "Razor Pages";
    public const string Blazor = "Blazor";
    public const string MinimalApi = "Minimal API";
    public const string Identity = "Identity";

    public static IReadOnlyList<string> Categories { get; } = [Api, Mvc, RazorPages, Blazor, MinimalApi, Identity];

    /// <summary>The <c>-dbProvider</c> values the code generator knows.</summary>
    public static IReadOnlyList<string> DbProviders { get; } = ["sqlserver", "sqlite", "postgres", "cosmos"];

    private static ScaffoldParameter Model(bool required = true) => new("model", "Model class", ScaffoldParameterKind.Model, Required: required, Option: "-m", Description: "The entity type the pages or actions work on.");

    private static ScaffoldParameter Context(string? defaultName = null) => new("dbContext", "DbContext class", ScaffoldParameterKind.DbContext, defaultName, Required: true, Option: "-dc", Description: "An existing context, or a new name to create one.");

    private static ScaffoldParameter Provider() => new("dbProvider", "Database provider", ScaffoldParameterKind.DbProvider, Option: "-dbProvider", Choices: DbProviders, Description: "The provider of a context the generator creates, and of the existing one.");

    private static ScaffoldParameter Folder(string folder, string option = "-outDir") => new("folder", "Folder", ScaffoldParameterKind.Folder, folder, Option: option);

    private static ScaffoldParameter Layout() => new("defaultLayout", "Use the default layout", ScaffoldParameterKind.Bool, "true", Option: "-udl");

    private static ScaffoldParameter Scripts() => new("scripts", "Reference script libraries", ScaffoldParameterKind.Bool, "false", Option: "-scripts");

    public static IReadOnlyList<Scaffolder> All { get; } =
    [
        // API
        new("api.empty", Api, "API Controller - Empty", "An empty API controller.", ScaffoldEngine.DotnetNew, "apicontroller",
            [new("name", "Controller name", ScaffoldParameterKind.Name, "ValuesController", Required: true), Folder("Controllers")]),
        new("api.actions", Api, "API Controller with read/write actions", "An API controller with GET, POST, PUT and DELETE actions to fill in.", ScaffoldEngine.DotnetNew, "apicontroller",
            [new("name", "Controller name", ScaffoldParameterKind.Name, "ValuesController", Required: true), Folder("Controllers")], ["--actions"]),
        new("api.ef", Api, "API Controller with actions, using Entity Framework", "An API controller whose actions read and write a model through a DbContext.", ScaffoldEngine.CodeGenerator, "controller",
            [Model(), Context(), new("name", "Controller name", ScaffoldParameterKind.Name, Option: "-name", Description: "Default: the model's name in the plural + Controller."), Folder("Controllers"), Provider()], ["-api"]),

        // MVC
        new("mvc.empty", Mvc, "MVC Controller - Empty", "An empty MVC controller.", ScaffoldEngine.DotnetNew, "mvccontroller",
            [new("name", "Controller name", ScaffoldParameterKind.Name, "HomeController", Required: true), Folder("Controllers")]),
        new("mvc.actions", Mvc, "MVC Controller with read/write actions", "An MVC controller with Index, Details, Create, Edit and Delete actions to fill in.", ScaffoldEngine.DotnetNew, "mvccontroller",
            [new("name", "Controller name", ScaffoldParameterKind.Name, "HomeController", Required: true), Folder("Controllers")], ["--actions"]),
        new("mvc.ef", Mvc, "MVC Controller with views, using Entity Framework", "A controller and its Index, Details, Create, Edit and Delete views for a model.", ScaffoldEngine.CodeGenerator, "controller",
            [Model(), Context(), new("name", "Controller name", ScaffoldParameterKind.Name, Option: "-name", Description: "Default: the model's name in the plural + Controller."), Folder("Controllers"), Layout(), Scripts(), Provider()]),
        new("mvc.view.empty", Mvc, "Razor View - Empty", "An empty Razor view.", ScaffoldEngine.DotnetNew, "view",
            [new("name", "View name", ScaffoldParameterKind.Name, "Index", Required: true), Folder("Views/Home")]),
        new("mvc.view", Mvc, "Razor View", "A view from a template (Create, Edit, Delete, Details, List) for a model.", ScaffoldEngine.CodeGenerator, "view",
            [
                new("name", "View name", ScaffoldParameterKind.Name, "Index", Required: true, Positional: true),
                new("template", "Template", ScaffoldParameterKind.Choice, "List", Required: true, Choices: ["Empty", "Create", "Edit", "Delete", "Details", "List"], Positional: true),
                Model(required: false),
                new("dbContext", "DbContext class", ScaffoldParameterKind.DbContext, Option: "-dc"),
                Folder("Views/Home"),
                Layout(),
                new("partial", "Create as a partial view", ScaffoldParameterKind.Bool, "false", Option: "-partial"),
                Scripts(),
            ]),

        // Razor Pages
        new("pages.empty", RazorPages, "Razor Page - Empty", "An empty Razor Page with its page model.", ScaffoldEngine.DotnetNew, "page",
            [new("name", "Page name", ScaffoldParameterKind.Name, "Index", Required: true), Folder("Pages"), new("noPageModel", "Without a page model", ScaffoldParameterKind.Bool, "false", Option: "--no-pagemodel")]),
        new("pages.ef", RazorPages, "Razor Pages using Entity Framework (CRUD)", "Create, Delete, Details, Edit and List pages for a model, or one of them.", ScaffoldEngine.CodeGenerator, "razorpage",
            [
                new("name", "Page name", ScaffoldParameterKind.Name, "Index", Positional: true, Description: "For a single page."),
                new("template", "Template", ScaffoldParameterKind.Choice, "CRUD", Required: true, Choices: ["CRUD", "Create", "Edit", "Delete", "Details", "List", "Empty"], Positional: true),
                Model(),
                Context(),
                Folder("Pages"),
                Layout(),
                Scripts(),
                Provider(),
            ]),

        // Blazor
        new("blazor.component", Blazor, "Razor Component", "An empty Razor component.", ScaffoldEngine.DotnetNew, "razorcomponent",
            [new("name", "Component name", ScaffoldParameterKind.Name, "Component", Required: true), Folder("Components/Pages")]),
        new("blazor.crud", Blazor, "Razor Components using Entity Framework (CRUD)", "Blazor Create, Delete, Details, Edit and Index pages for a model, or one of them.", ScaffoldEngine.CodeGenerator, "blazor",
            [
                new("template", "Template", ScaffoldParameterKind.Choice, "CRUD", Required: true, Choices: ["CRUD", "Create", "Delete", "Details", "Edit", "Index"], Positional: true),
                Model(),
                Context(),
                Folder("Components/Pages"),
                Provider(),
            ]),

        // Minimal API
        new("minimal.empty", MinimalApi, "Minimal API Endpoints - Empty", "A static class that maps a group of endpoints, to call from Program.cs.", ScaffoldEngine.BuiltIn, "endpoints",
            [new("name", "Class name", ScaffoldParameterKind.Name, "TodoEndpoints", Required: true), new("route", "Route prefix", ScaffoldParameterKind.Text, "/api/todos", Required: true), Folder("Endpoints")]),
        new("minimal.ef", MinimalApi, "Minimal API Endpoints using Entity Framework", "CRUD endpoints for a model, mapped in Program.cs.", ScaffoldEngine.CodeGenerator, "minimalapi",
            [
                Model(),
                Context(),
                new("name", "Endpoints class", ScaffoldParameterKind.Name, Option: "-e", Description: "Default: the model's name + Endpoints."),
                Folder("Endpoints"),
                new("openApi", "Enable OpenAPI (needs Microsoft.AspNetCore.OpenApi)", ScaffoldParameterKind.Bool, "false", Option: "-o"),
                new("noTypedResults", "Don't use TypedResults", ScaffoldParameterKind.Bool, "false", Option: "-ntr"),
                Provider(),
            ]),

        // Identity
        new("identity", Identity, "Identity", "ASP.NET Core Identity's pages (all, or a list), its DbContext and user class.", ScaffoldEngine.CodeGenerator, "identity",
            [
                Context("ApplicationDbContext"),
                new("user", "User class", ScaffoldParameterKind.Name, Option: "-u", Description: "A class deriving from IdentityUser; empty: IdentityUser."),
                new("files", "Files to override", ScaffoldParameterKind.Text, Option: "-fi", Description: "Semicolon-separated (Account.Login;Account.Register); empty: all of them."),
                new("defaultUi", "Use the default UI (no pages; only the context and the user)", ScaffoldParameterKind.Bool, "false", Option: "-udui"),
                new("force", "Overwrite existing files", ScaffoldParameterKind.Bool, "false", Option: "-f"),
                Provider(),
            ]),
    ];

    public static Scaffolder? Find(string id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>The <c>-dbProvider</c> value for an EF Core provider package, or null.</summary>
    public static string? DbProviderFor(IEnumerable<string> providerPackages)
    {
        foreach (var package in providerPackages)
        {
            switch (package)
            {
                case "Microsoft.EntityFrameworkCore.Sqlite": return "sqlite";
                case "Microsoft.EntityFrameworkCore.SqlServer": return "sqlserver";
                case "Npgsql.EntityFrameworkCore.PostgreSQL": return "postgres";
                case "Microsoft.EntityFrameworkCore.Cosmos": return "cosmos";
            }
        }

        return null;
    }

    /// <summary>Checks the values; returns the first problem, or null.</summary>
    public static string? Validate(Scaffolder scaffolder, IReadOnlyDictionary<string, string?> values)
    {
        foreach (var parameter in scaffolder.Parameters)
        {
            var value = Value(values, parameter);
            if (parameter.Required && value.Length == 0 && !SkippedPositional(scaffolder, parameter, values))
            {
                return $"{parameter.Label} is required.";
            }

            if (value.Length > 0 && parameter.Kind is ScaffoldParameterKind.Name or ScaffoldParameterKind.Model or ScaffoldParameterKind.DbContext && !IsTypeName(value))
            {
                return $"{parameter.Label}: '{value}' isn't a C# type name.";
            }

            if (value.Length > 0 && parameter.Kind == ScaffoldParameterKind.Folder && (Path.IsPathRooted(value) || value.Split('/', '\\').Contains("..")))
            {
                return $"{parameter.Label} must be a folder inside the project.";
            }

            if (value.Length > 0 && parameter.Choices is { } choices && parameter.Kind != ScaffoldParameterKind.Text && !choices.Contains(value, StringComparer.Ordinal))
            {
                return $"{parameter.Label} is one of {string.Join(", ", choices)}.";
            }
        }

        if (scaffolder.Id == "mvc.view" && Value(values, scaffolder.Parameters.First(p => p.Id == "template")) != "Empty" && Value(values, "model").Length == 0)
        {
            return "A view from a template needs a model class.";
        }

        return null;
    }

    /// <summary>
    /// The command line of a scaffolder: positional arguments in order, then the options of non-empty values, then the
    /// fixed arguments. Throws when <see cref="Validate"/> wouldn't pass.
    /// </summary>
    public static ScaffoldPlan Plan(Scaffolder scaffolder, IReadOnlyDictionary<string, string?> values)
    {
        if (Validate(scaffolder, values) is { } problem)
        {
            throw new ArgumentException(problem);
        }

        string ValueOf(string id) => scaffolder.Parameters.FirstOrDefault(p => p.Id == id) is { } parameter ? Value(values, parameter) : Value(values, id);
        var name = ValueOf("name") is { Length: > 0 } given ? given : null;
        if (name is null && scaffolder.Command == "controller" && Value(values, "model") is { Length: > 0 } model)
        {
            // The controller generator needs a name: Visual Studio's default, the model in the plural.
            name = Plural(model.Split('.')[^1]) + "Controller";
            values = new Dictionary<string, string?>(values) { ["name"] = name };
        }
        var folder = ValueOf("folder") is { Length: > 0 } dir ? dir.Replace('\\', '/').Trim('/') : null;
        var arguments = new List<string>();
        if (scaffolder.Engine == ScaffoldEngine.CodeGenerator)
        {
            foreach (var parameter in scaffolder.Parameters.Where(p => p.Positional && !SkippedPositional(scaffolder, p, values)))
            {
                arguments.Add(Value(values, parameter));
            }
        }

        foreach (var parameter in scaffolder.Parameters.Where(p => p.Option is not null && !p.Positional))
        {
            var value = Value(values, parameter);
            if (parameter.Kind == ScaffoldParameterKind.Bool)
            {
                if (bool.TryParse(value, out var on) && on)
                {
                    arguments.Add(parameter.Option!);
                }
            }
            else if (value.Length > 0 && !(scaffolder.Engine == ScaffoldEngine.DotnetNew && parameter.Kind == ScaffoldParameterKind.Folder))
            {
                arguments.Add(parameter.Option!);
                arguments.Add(parameter.Kind == ScaffoldParameterKind.Folder ? value.Replace('\\', '/').Trim('/') : value);
            }
        }

        arguments.AddRange(scaffolder.Fixed ?? []);
        return new ScaffoldPlan(scaffolder, scaffolder.Command, arguments, name, folder);
    }

    /// <summary>A simple English plural (Product → Products, Category → Categories, Box → Boxes).</summary>
    public static string Plural(string word) =>
        word.EndsWith('y') && word.Length > 1 && !"aeiou".Contains(char.ToLowerInvariant(word[^2]), StringComparison.Ordinal) ? word[..^1] + "ies"
        : word.EndsWith('s') || word.EndsWith('x') || word.EndsWith("ch", StringComparison.Ordinal) || word.EndsWith("sh", StringComparison.Ordinal) ? word + "es"
        : word + "s";

    /// <summary>A Razor Pages "CRUD" run takes neither a page name nor a template.</summary>
    private static bool SkippedPositional(Scaffolder scaffolder, ScaffoldParameter parameter, IReadOnlyDictionary<string, string?> values) =>
        scaffolder.Id == "pages.ef" && parameter.Positional && scaffolder.Parameters.FirstOrDefault(p => p.Id == "template") is { } template && Value(values, template) == "CRUD";

    private static string Value(IReadOnlyDictionary<string, string?> values, ScaffoldParameter parameter) =>
        values.TryGetValue(parameter.Id, out var value) && value is not null ? value.Trim() : parameter.Default ?? string.Empty;

    private static string Value(IReadOnlyDictionary<string, string?> values, string id) =>
        values.TryGetValue(id, out var value) && value is not null ? value.Trim() : string.Empty;

    /// <summary>A C# type name, possibly qualified (<c>Shop.Models.Product</c>).</summary>
    public static bool IsTypeName(string value) =>
        value.Split('.').All(part => part.Length > 0 && (char.IsLetter(part[0]) || part[0] == '_') && part.All(c => char.IsLetterOrDigit(c) || c == '_'));

    /// <summary>The empty minimal API endpoint group (<see cref="ScaffoldEngine.BuiltIn"/>).</summary>
    public static string EndpointsClass(string @namespace, string name, string route, bool fileScoped)
    {
        var method = name.EndsWith("Endpoints", StringComparison.Ordinal) ? name[..^"Endpoints".Length] : name;
        var tag = method.Length > 0 ? method : name;
        var indent = fileScoped ? string.Empty : "    ";
        var body = string.Join('\n', new[]
        {
            $"public static class {name}",
            "{",
            $"    public static RouteGroupBuilder Map{tag}Endpoints(this IEndpointRouteBuilder routes)",
            "    {",
            $"        var group = routes.MapGroup(\"{route.Replace("\"", string.Empty, StringComparison.Ordinal)}\").WithTags(\"{tag}\");",
            string.Empty,
            "        group.MapGet(\"/\", () => Results.Ok());",
            "        group.MapGet(\"/{id:int}\", (int id) => Results.Ok());",
            "        group.MapPost(\"/\", () => Results.Created());",
            "        group.MapPut(\"/{id:int}\", (int id) => Results.NoContent());",
            "        group.MapDelete(\"/{id:int}\", (int id) => Results.NoContent());",
            string.Empty,
            "        return group;",
            "    }",
            "}",
        }.Select(l => l.Length == 0 ? l : indent + l));
        return fileScoped
            ? $"namespace {@namespace};\n\n{body}\n"
            : $"namespace {@namespace}\n{{\n{body}\n}}\n";
    }
}
