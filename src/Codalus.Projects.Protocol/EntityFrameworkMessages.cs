using System.Text.Json.Serialization;

namespace Codalus.Projects.Protocol;

/// <summary>
/// EF Core on the build host connection (ADR 0036, docs/project-protocol.md). The host runs the user's own
/// <c>dotnet ef</c> (and, for the model view, <c>codalus-efagent</c>) as child processes in the startup project's
/// folder; every request needs an open, trusted solution, and cancelling one kills its process tree.
/// </summary>
public static class EfMethods
{
    /// <summary>Whether <c>dotnet ef</c> is available for a project, and the project's EF Core version and providers.</summary>
    public const string Status = "ef/status";

    /// <summary><c>dotnet ef dbcontext list</c>.</summary>
    public const string Contexts = "ef/contexts";

    /// <summary><c>dotnet ef dbcontext info</c>: the provider and the database a context connects to.</summary>
    public const string ContextInfo = "ef/contextInfo";

    /// <summary><c>dotnet ef migrations list</c>, with whether each one is applied to the connection.</summary>
    public const string Migrations = "ef/migrations";

    public const string AddMigration = "ef/addMigration";

    public const string RemoveMigration = "ef/removeMigration";

    /// <summary><c>dotnet ef migrations script</c>: the SQL, without applying it.</summary>
    public const string Script = "ef/script";

    /// <summary><c>dotnet ef database update [migration]</c>: applies or reverts migrations.</summary>
    public const string UpdateDatabase = "ef/updateDatabase";

    public const string Bundle = "ef/bundle";

    /// <summary><c>dotnet ef dbcontext scaffold</c>: reverse engineering.</summary>
    public const string Scaffold = "ef/scaffold";

    /// <summary><c>dotnet ef dbcontext optimize</c>: a compiled model.</summary>
    public const string Optimize = "ef/optimize";

    /// <summary>The design-time model (entities, keys, relationships, owned types) and its debug view, from <c>codalus-efagent</c>.</summary>
    public const string Model = "ef/model";

    /// <summary>Host → shell notification: a message of the running <c>dotnet ef</c> (info, warn, error, verbose).</summary>
    public const string Output = "ef/output";
}

/// <summary>
/// The project a command targets (<c>--project</c>: the context's and the migrations' assembly), the startup project
/// (<c>--startup-project</c>; null: the project itself), the context (<c>--context</c>, a full or short type name; null:
/// the only one), and the connection string (<c>--connection</c>; null: the app's configuration). With
/// <see cref="NoBuild"/> the caller has built the startup project already.
/// </summary>
public sealed record EfTarget(string Project, string? StartupProject = null, string? Context = null, string? Connection = null, bool NoBuild = true);

public sealed record EfStatusParams(string Project, string? StartupProject = null);

/// <summary>
/// <see cref="ToolVersion"/> is null when <c>dotnet ef</c> can't run; <see cref="ToolSource"/> is <c>local</c> (the tool
/// manifest) or <c>global</c>. <see cref="ManifestListsTool"/>: the manifest lists dotnet-ef (a restore may be all that's
/// missing). <see cref="EfCoreVersion"/> and <see cref="Providers"/> come from the project's package references;
/// <see cref="HasDesignPackage"/>: the startup project references Microsoft.EntityFrameworkCore.Design (or .Tools).
/// </summary>
public sealed record EfStatusResult(
    string? ToolVersion,
    string? ToolSource,
    string? Manifest,
    bool ManifestListsTool,
    string? EfCoreVersion,
    string[] Providers,
    bool HasDesignPackage,
    string? Message);

public sealed record EfContextsResult(EfContextItem[] Contexts);

public sealed record EfContextItem(string Name, string FullName, string? AssemblyQualifiedName);

public sealed record EfContextInfoResult(string Type, string? ProviderName, string? DatabaseName, string? DataSource, string? Options);

/// <summary>
/// The migrations, oldest first. <see cref="EfMigrationItem.Applied"/> is null when the database couldn't be reached
/// (<see cref="ConnectionError"/> says why); the list itself still comes from the assembly.
/// </summary>
public sealed record EfMigrationsResult(EfMigrationItem[] Migrations, string? ConnectionError);

public sealed record EfMigrationItem(string Id, string Name, bool? Applied);

public sealed record EfAddMigrationParams(EfTarget Target, string Name, string? OutputDir = null, string? Namespace = null);

/// <summary>The files a command wrote (migration, designer and snapshot files; scaffolded or compiled-model files).</summary>
public sealed record EfFilesResult(string[] Files);

/// <summary><see cref="Force"/> reverts the migration first when it is applied.</summary>
public sealed record EfRemoveMigrationParams(EfTarget Target, bool Force = false);

/// <summary>
/// The SQL from <see cref="From"/> (null: the empty database) to <see cref="To"/> (null: the last migration); a
/// <see cref="From"/> newer than <see cref="To"/> gives the revert (Down) script.
/// </summary>
public sealed record EfScriptParams(EfTarget Target, string? From = null, string? To = null, bool Idempotent = false, bool NoTransactions = false);

public sealed record EfScriptResult(string Sql);

/// <summary><see cref="Migration"/>: the target (null: the last one; <c>0</c>: revert every migration).</summary>
public sealed record EfUpdateDatabaseParams(EfTarget Target, string? Migration = null);

public sealed record EfBundleParams(EfTarget Target, string Output, bool SelfContained = false, string? Runtime = null, bool Force = true);

/// <summary>Reverse engineering: <see cref="Tables"/> and <see cref="Schemas"/> empty means everything.</summary>
public sealed record EfScaffoldParams(
    string Project,
    string? StartupProject,
    string Connection,
    string Provider,
    string? OutputDir = null,
    string? ContextDir = null,
    string? ContextName = null,
    string? Namespace = null,
    string? ContextNamespace = null,
    string[]? Tables = null,
    string[]? Schemas = null,
    bool DataAnnotations = false,
    bool NoOnConfiguring = true,
    bool UseDatabaseNames = false,
    bool NoPluralize = false,
    bool Force = false,
    bool NoBuild = true);

public sealed record EfOptimizeParams(EfTarget Target, string? OutputDir = null, string? Namespace = null);

/// <summary>A context's design-time model, from <c>codalus-efagent</c>.</summary>
public sealed record EfModelResult(string Context, string? Provider, string? ProductVersion, EfEntityInfo[] Entities, string? DebugView);

/// <summary>
/// An entity type. Owned types have <see cref="IsOwned"/> and their <see cref="Owner"/>; shared-type entities (the
/// join of a many-to-many) have a <see cref="Name"/> that isn't a CLR type's.
/// </summary>
public sealed record EfEntityInfo(
    string Name,
    string? ClrType,
    string? Table,
    string? Schema,
    string? View,
    bool IsOwned,
    string? Owner,
    string? BaseType,
    bool IsKeyless,
    EfPropertyInfo[] Properties,
    EfKeyInfo[] Keys,
    EfForeignKeyInfo[] ForeignKeys,
    EfNavigationInfo[] Navigations,
    EfIndexInfo[] Indexes);

public sealed record EfPropertyInfo(string Name, string? ClrType, bool Nullable, bool IsKey, bool IsForeignKey, bool IsShadow, string? Column, string? ColumnType, int? MaxLength);

public sealed record EfKeyInfo(string[] Properties, bool IsPrimary);

public sealed record EfForeignKeyInfo(
    string[] Properties,
    string Principal,
    string[] PrincipalProperties,
    string? DeleteBehavior,
    bool IsUnique,
    bool IsRequired,
    bool IsOwnership,
    string? DependentToPrincipal,
    string? PrincipalToDependent);

/// <summary><see cref="IsSkip"/>: a many-to-many navigation through a join entity.</summary>
public sealed record EfNavigationInfo(string Name, string? Target, bool IsCollection, bool IsOnDependent, string? Inverse, bool IsSkip);

public sealed record EfIndexInfo(string[] Properties, bool IsUnique, string? Name);

/// <summary><see cref="Level"/>: <c>info</c>, <c>warn</c>, <c>error</c> or <c>verbose</c>; the command line comes as <c>info</c> with a <c>&gt;</c>.</summary>
public sealed record EfOutputParams(string Level, string Line);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EfTarget))]
[JsonSerializable(typeof(EfStatusParams))]
[JsonSerializable(typeof(EfStatusResult))]
[JsonSerializable(typeof(EfContextsResult))]
[JsonSerializable(typeof(EfContextItem[]))]
[JsonSerializable(typeof(EfContextInfoResult))]
[JsonSerializable(typeof(EfMigrationsResult))]
[JsonSerializable(typeof(EfMigrationItem[]))]
[JsonSerializable(typeof(EfAddMigrationParams))]
[JsonSerializable(typeof(EfFilesResult))]
[JsonSerializable(typeof(EfRemoveMigrationParams))]
[JsonSerializable(typeof(EfScriptParams))]
[JsonSerializable(typeof(EfScriptResult))]
[JsonSerializable(typeof(EfUpdateDatabaseParams))]
[JsonSerializable(typeof(EfBundleParams))]
[JsonSerializable(typeof(EfScaffoldParams))]
[JsonSerializable(typeof(EfOptimizeParams))]
[JsonSerializable(typeof(EfModelResult))]
[JsonSerializable(typeof(EfOutputParams))]
[JsonSerializable(typeof(EmptyResult))]
public sealed partial class EntityFrameworkJsonContext : JsonSerializerContext;
