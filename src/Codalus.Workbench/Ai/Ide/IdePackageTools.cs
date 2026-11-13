namespace Codalus.Workbench.Ai.Ide;

/// <summary>M60's tools for agents: EF Core migrations (through the user's dotnet-ef) and NuGet packages (through the package manager's one path, with its feeds and credentials).</summary>
/// <remarks>
/// Packages go through <c>PackageService</c> (the build host, the solution's nuget.config, its sources, mappings and
/// signed-in credentials), as the NuGet Package Manager does, so its windows follow; a package that asks for its licence
/// to be accepted is left to the user. Migrations go through <c>EntityFrameworkService</c> (M27a: the startup project
/// built first, then the user's <c>dotnet ef</c> in the build host), as the Entity Framework window does. Everything
/// needs a trusted solution; project files excluded from AI are left out and can't be changed.
/// </remarks>
public static class IdePackageTools
{
    private const string Project =
        """
        "project":{"type":"string","description":"A project's name (get_solution lists them)."}
        """;

    private const string AnyProject =
        """
        "project":{"type":"string","description":"A project's name; every project that references the package by default."}
        """;

    private const string EfTarget =
        """
        "project":{"type":"string","description":"The project with the DbContext and its migrations; the only EF Core project by default."},
        "context":{"type":"string","description":"The DbContext's name or full name; the only one by default."}
        """;

    /// <summary>The group's tools, in the order agents see them.</summary>
    public static IReadOnlyList<IdeToolDefinition> Tools { get; } =
    [
        // ---------------------------------------------------------- NuGet packages
        IdeTools.Read("list_packages", "The NuGet packages of a project, or of every project: each top-level package with the version asked for and the one restored, where its version is set (central package management, an override), and optionally the newer versions the feeds have.", "{" + Project + ""","updates":{"type":"boolean","description":"Ask the feeds for each package's latest version (slower)."},"prerelease":{"type":"boolean","description":"With updates: prerelease versions count."},"transitive":{"type":"boolean","description":"The transitive packages too."}}"""),
        IdeTools.Read("search_packages", "Search the solution's NuGet sources (its nuget.config, with their credentials) as the NuGet Package Manager's Browse does. Returns each package's id, latest version, description, downloads and source, and whether it is deprecated or vulnerable.", """{"query":{"type":"string","description":"Words or a package id."},"prerelease":{"type":"boolean"},"source":{"type":"string","description":"One source's name; every source by default."},"limit":{"type":"integer","description":"At most this many (default 20, at most 100)."}}""", "query"),
        IdeTools.Action("add_package", "Add a NuGet package to a project (a PackageReference, its version in Directory.Packages.props under central package management), then restore, as the NuGet Package Manager does. Without a version, the newest stable one that supports the project's frameworks.", "{" + Project + ""","id":{"type":"string","description":"The package's id."},"version":{"type":"string"},"versionOverride":{"type":"boolean","description":"Under central package management, when the central version differs: a VersionOverride in this project instead of changing the central version (which every project that uses it follows)."}}""", "project", "id"),
        IdeTools.Action("update_package", "Update a NuGet package in a project, or in every project that references it directly, to a version (the newest stable one by default), then restore.", "{" + AnyProject + ""","id":{"type":"string"},"version":{"type":"string"},"versionOverride":{"type":"boolean","description":"Under central package management: a VersionOverride in the project instead of changing the central version."}}""", "id"),
        IdeTools.Action("remove_package", "Remove a NuGet package's reference from a project, then restore.", "{" + Project + ""","id":{"type":"string"}}""", "project", "id"),

        // ---------------------------------------------------------- EF Core migrations
        IdeTools.Read("list_migrations", "The EF Core migrations of a DbContext, oldest first, and whether each is applied to the database the app's configuration connects to (dotnet ef migrations list, after building the startup project).", "{" + EfTarget + "}"),
        IdeTools.Action("add_migration", "Add an EF Core migration for the model's changes (dotnet ef migrations add): the migration, its designer file and the model snapshot. Returns the files.", """{"name":{"type":"string","description":"The migration's name, a C# identifier such as AddBlogRating."},"outputDir":{"type":"string","description":"The folder, relative to the project (Migrations by default)."},""" + EfTarget + "}", "name"),
        IdeTools.Action("apply_migrations", "Update the database the app's configuration connects to (dotnet ef database update): apply the pending migrations, up to one (the last by default). Returns the SQL that ran. Going back to an earlier migration reverts the later ones (their Down scripts may drop data) and needs allowRevert.", """{"migration":{"type":"string","description":"The migration to stop at (its name or id); 0 reverts every migration."},"allowRevert":{"type":"boolean","description":"Allow reverting applied migrations, when the user asked for it."},""" + EfTarget + "}"),
        IdeTools.Read("script_migrations", "The SQL script of EF Core migrations, without applying it (dotnet ef migrations script): from a migration (the empty database by default) to another (the last by default).", """{"from":{"type":"string","description":"The starting point's name or id (its changes aren't in the script); 0 for the empty database."},"to":{"type":"string","description":"The last migration in the script."},"idempotent":{"type":"boolean","description":"Each migration checks the history table first (not on SQLite)."},""" + EfTarget + "}"),
    ];
}
