using System.Text.Json.Serialization;

namespace Codalus.Projects.Protocol;

/// <summary>
/// NuGet package management on the build host connection (ADR 0012, docs/project-protocol.md). Every request needs an
/// open, trusted solution; network access happens in the host. <c>offline</c> limits a request to local sources (folders
/// and the global packages folder).
/// </summary>
public static class PackageMethods
{
    public const string Search = "packages/search";
    public const string Details = "packages/details";
    public const string Installed = "packages/installed";
    public const string Status = "packages/status";
    public const string Why = "packages/why";
    public const string Install = "packages/install";
    public const string Uninstall = "packages/uninstall";
    public const string Sources = "packages/sources";
    public const string SetSources = "packages/setSources";
    public const string SignIn = "packages/signIn";

    /// <summary>Host → shell during <see cref="SignIn"/>: what the credential provider says (a device code to enter).</summary>
    public const string SignInMessage = "packages/signInMessage";

    public const string Tools = "tools/list";
    public const string ToolCommand = "tools/run";
}

// ------------------------------------------------------------------ search

/// <summary>
/// Searches every enabled source (or only <see cref="Source"/>, by name). Pages are per source: <see cref="Skip"/> and
/// <see cref="Take"/> apply to each, and results are merged by id (the highest version wins).
/// </summary>
public sealed record PackageSearchParams(string Query, bool Prerelease, int Skip, int Take, string? Source = null, bool Offline = false, bool Refresh = false);

/// <summary><see cref="Errors"/> lists sources that failed (unreachable, unauthorized); the others still answer.</summary>
public sealed record PackageSearchResult(PackageSummary[] Packages, bool HasMore, PackageSourceError[] Errors);

/// <summary>
/// A search hit. <see cref="VulnerabilitySeverity"/> is the highest advisory severity of <see cref="Version"/> (0 low,
/// 1 moderate, 2 high, 3 critical) or -1 when none is known.
/// </summary>
public sealed record PackageSummary(
    string Id,
    string Version,
    string? Description,
    string? Authors,
    string? IconUrl,
    long? Downloads,
    bool Deprecated,
    int VulnerabilitySeverity,
    string Source);

public sealed record PackageSourceError(string Source, string Message);

// ------------------------------------------------------------------ sign-in

/// <summary>
/// Signs in to a source (by name) through NuGet's credential providers, interactively: a provider may show a device
/// code through <see cref="PackageMethods.SignInMessage"/>. Afterwards the source answers this session's requests.
/// </summary>
public sealed record PackageSignInParams(string Source);

public sealed record PackageSignInResult(bool SignedIn, string Message);

public sealed record PackageSignInMessage(string Source, string Message);

// ------------------------------------------------------------------ details

/// <summary>
/// Details of one version (<see cref="Version"/>, or the latest one), and every version for the picker. With
/// <see cref="TargetFrameworks"/> (a project's, M33), each version says whether it supports them, and without a
/// <see cref="Version"/> the details are of the newest version that does.
/// </summary>
public sealed record PackageDetailsParams(string Id, bool Prerelease, string? Version = null, string? Source = null, bool Offline = false, string[]? TargetFrameworks = null);

public sealed record PackageDetails(
    string Id,
    string Version,
    PackageVersionInfo[] Versions,
    string? Title,
    string? Description,
    string? Authors,
    string? LicenseExpression,
    string? LicenseUrl,
    string? ProjectUrl,
    string? Readme,
    bool RequireLicenseAcceptance,
    string? Published,
    long? Downloads,
    string? Tags,
    PackageDependencyGroup[] DependencyGroups,
    PackageDeprecation? Deprecation,
    PackageVulnerability[] Vulnerabilities,
    string Source);

/// <summary>
/// A version for the version picker, newest first. <see cref="Compatible"/> says whether it supports every framework of
/// <see cref="PackageDetailsParams.TargetFrameworks"/>, from its dependency groups (null: not asked, or the package
/// lists no frameworks).
/// </summary>
public sealed record PackageVersionInfo(string Version, long? Downloads, bool Deprecated, int VulnerabilitySeverity, bool? Compatible = null);

/// <summary>Dependencies for one target framework (short folder name, e.g. <c>net8.0</c>; <c>any</c> for none).</summary>
public sealed record PackageDependencyGroup(string TargetFramework, PackageDependency[] Dependencies);

public sealed record PackageDependency(string Id, string Range);

/// <summary><see cref="Reasons"/>: <c>Legacy</c>, <c>CriticalBugs</c>, <c>Other</c>.</summary>
public sealed record PackageDeprecation(string[] Reasons, string? Message, string? AlternateId, string? AlternateRange);

public sealed record PackageVulnerability(int Severity, string AdvisoryUrl);

// ------------------------------------------------------------------ installed packages

/// <summary>The packages of one project, or (<see cref="Project"/> null) of every loaded project.</summary>
public sealed record InstalledPackagesParams(string? Project);

public sealed record InstalledPackagesResult(ProjectPackages[] Projects);

/// <summary>
/// A project's packages, from its evaluation and its assets file. <see cref="CentralPackagesFile"/> is the
/// <c>Directory.Packages.props</c> in use when the project manages versions centrally.
/// </summary>
public sealed record ProjectPackages(
    string Project,
    string Name,
    string[] TargetFrameworks,
    bool CentralPackageManagement,
    string? CentralPackagesFile,
    bool Restored,
    InstalledPackage[] Packages,
    TransitivePackage[] Transitive);

public static class InstalledPackageKinds
{
    /// <summary>A <c>PackageReference</c> the user can edit (in the project or an imported file of the repository).</summary>
    public const string Direct = "direct";

    /// <summary>A <c>GlobalPackageReference</c> from <c>Directory.Packages.props</c>.</summary>
    public const string Global = "global";

    /// <summary>Added by the SDK (<c>IsImplicitlyDefined</c>).</summary>
    public const string Implicit = "implicit";
}

/// <summary>
/// A top-level package. <see cref="Requested"/> is the version or range asked for (from the item, its
/// <c>VersionOverride</c>, or the central <c>PackageVersion</c>); <see cref="Resolved"/> the restored version.
/// <see cref="Frameworks"/> lists the target frameworks that reference it (all of them when not conditional).
/// <see cref="DefinedIn"/> is the file that declares it.
/// </summary>
public sealed record InstalledPackage(
    string Id,
    string? Requested,
    string? Resolved,
    string[] Frameworks,
    string Kind,
    string? VersionOverride,
    string? CentralVersion,
    string? DefinedIn);

public sealed record TransitivePackage(string Id, string Resolved, string[] Frameworks);

/// <summary>Why a package is in a project's graph: every chain from a top-level dependency to it.</summary>
public sealed record PackageWhyParams(string Project, string Id);

/// <summary>Each path starts at a top-level package or project reference and ends at the package (<c>id/version</c> steps).</summary>
public sealed record PackageWhyResult(PackageWhyPath[] Paths);

public sealed record PackageWhyPath(string Framework, string[] Chain);

// ------------------------------------------------------------------ status (updates, badges)

/// <summary>The latest version of each package, and what is known about <see cref="PackageStatusQuery.Version"/>.</summary>
public sealed record PackageStatusParams(PackageStatusQuery[] Packages, bool Prerelease, bool Offline = false, bool Refresh = false);

public sealed record PackageStatusQuery(string Id, string? Version);

public sealed record PackageStatusResult(PackageStatus[] Packages, PackageSourceError[] Errors);

/// <summary><see cref="Latest"/> is null when no source has the package.</summary>
public sealed record PackageStatus(string Id, string? Latest, bool Deprecated, int VulnerabilitySeverity, string? IconUrl);

// ------------------------------------------------------------------ edits

/// <summary>
/// Installs or updates <see cref="Id"/> at <see cref="Version"/> in each target project (all its frameworks, or only
/// <see cref="PackageEditTarget.Framework"/>). Under Central Package Management the version goes into the central
/// <c>PackageVersion</c>, or with <see cref="VersionOverride"/> into the project's <c>VersionOverride</c>. A restore
/// of the edited projects follows when <see cref="Restore"/> is true.
/// </summary>
public sealed record PackageInstallParams(PackageEditTarget[] Projects, string Id, string Version, bool VersionOverride = false, bool Restore = true);

public sealed record PackageEditTarget(string Project, string? Framework = null);

/// <summary>Removes the package's references from each project (and, under CPM, its central version once no project uses it).</summary>
public sealed record PackageUninstallParams(string[] Projects, string Id, bool Restore = true);

/// <summary>The project and props files that changed.</summary>
public sealed record PackageEditResult(string[] UpdatedFiles);

// ------------------------------------------------------------------ sources

/// <summary>
/// The effective package sources (in priority order), package source mapping, the config files they come from (most
/// specific first) and the global packages folder.
/// </summary>
public sealed record PackageSourcesResult(PackageSourceInfo[] Sources, PackageSourceMapping[] Mappings, string[] ConfigFiles, string GlobalPackagesFolder);

/// <summary><see cref="ConfigFile"/> is the file that defines the source; machine-wide sources are read-only.</summary>
public sealed record PackageSourceInfo(string Name, string Url, bool Enabled, bool IsMachineWide = false, string? ConfigFile = null);

public sealed record PackageSourceMapping(string Source, string[] Patterns);

/// <summary>Replaces the sources and mappings (NuGet writes each to the config file that defines it; new ones go to the first writable file).</summary>
public sealed record SetPackageSourcesParams(PackageSourceInfo[] Sources, PackageSourceMapping[] Mappings);

// ------------------------------------------------------------------ .NET tools

/// <summary>Local tools from the nearest <c>.config/dotnet-tools.json</c> (null when there is none) and global tools.</summary>
public sealed record ToolsResult(string? Manifest, DotnetToolInfo[] Local, DotnetToolInfo[] Global);

public sealed record DotnetToolInfo(string Id, string Version, string[] Commands);

public static class ToolActions
{
    public const string Install = "install";
    public const string Update = "update";
    public const string Uninstall = "uninstall";
    public const string Restore = "restore";

    /// <summary>Creates the manifest (<c>dotnet new tool-manifest</c>).</summary>
    public const string CreateManifest = "createManifest";
}

/// <summary>Runs <c>dotnet tool &lt;action&gt;</c> (local tools in the solution folder, or global ones).</summary>
public sealed record ToolCommandParams(string Action, string? Id = null, string? Version = null, bool Global = false);

public sealed record ToolCommandResult(bool Succeeded, string Output);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EmptyParams))]
[JsonSerializable(typeof(PackageSearchParams))]
[JsonSerializable(typeof(PackageSearchResult))]
[JsonSerializable(typeof(PackageDetailsParams))]
[JsonSerializable(typeof(PackageDetails))]
[JsonSerializable(typeof(InstalledPackagesParams))]
[JsonSerializable(typeof(InstalledPackagesResult))]
[JsonSerializable(typeof(PackageWhyParams))]
[JsonSerializable(typeof(PackageWhyResult))]
[JsonSerializable(typeof(PackageStatusParams))]
[JsonSerializable(typeof(PackageStatusResult))]
[JsonSerializable(typeof(PackageInstallParams))]
[JsonSerializable(typeof(PackageUninstallParams))]
[JsonSerializable(typeof(PackageEditResult))]
[JsonSerializable(typeof(PackageSourcesResult))]
[JsonSerializable(typeof(SetPackageSourcesParams))]
[JsonSerializable(typeof(EmptyResult))]
[JsonSerializable(typeof(ToolsResult))]
[JsonSerializable(typeof(ToolCommandParams))]
[JsonSerializable(typeof(ToolCommandResult))]
[JsonSerializable(typeof(PackageSignInParams))]
[JsonSerializable(typeof(PackageSignInResult))]
[JsonSerializable(typeof(PackageSignInMessage))]
public sealed partial class PackageJsonContext : JsonSerializerContext;
