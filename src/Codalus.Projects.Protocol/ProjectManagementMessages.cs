using System.Text.Json.Serialization;

namespace Codalus.Projects.Protocol;

/// <summary>Project properties, references and solution management on the build host connection (ADR 0013).</summary>
public static class ProjectManagementMethods
{
    public const string GetProperties = "projectProperties/get";
    public const string SetProperty = "projectProperties/set";

    public const string AddReference = "references/add";
    public const string RemoveReference = "references/remove";

    public const string Configurations = "solution/configurations";
    public const string SetProjectConfiguration = "solution/setProjectConfiguration";
    public const string AddConfiguration = "solution/addConfiguration";
    public const string RemoveConfiguration = "solution/removeConfiguration";
    public const string Dependencies = "solution/dependencies";
    public const string SetDependencies = "solution/setDependencies";

    public const string Unload = "project/unload";
    public const string Reload = "project/reload";
    public const string SaveFilter = "solution/saveFilter";

    public const string SdkInfo = "sdk/info";
    public const string SetGlobalJson = "sdk/setGlobalJson";
}

// ------------------------------------------------------------------ properties

/// <summary>
/// Reads properties with provenance, evaluated with the active solution configuration or <see cref="Configuration"/>
/// (a project configuration name such as <c>Release</c>).
/// </summary>
public sealed record GetPropertiesParams(string Project, string[] Names, string? Configuration = null);

public sealed record ProjectPropertiesResult(string Project, string Configuration, string Platform, string[] Configurations, ProjectPropertyValue[] Properties);

public static class PropertyOrigins
{
    /// <summary>Set in the project file.</summary>
    public const string Project = "project";

    /// <summary>Set in an imported file of the repository (Directory.Build.props, a shared .props).</summary>
    public const string Import = "import";

    /// <summary>A default of the SDK, NuGet or MSBuild.</summary>
    public const string Sdk = "sdk";

    /// <summary>A global property (Configuration, Platform, a solution property…).</summary>
    public const string Global = "global";

    /// <summary>Not set anywhere.</summary>
    public const string Unset = "unset";
}

/// <summary>
/// A property's evaluated value and where it comes from. <see cref="Unevaluated"/> is the text as written where it is
/// defined; <see cref="DefinedIn"/>/<see cref="Line"/> locate that definition; <see cref="Condition"/> is the
/// condition on the property or its group (empty when unconditional). <see cref="Origin"/> is a <see cref="PropertyOrigins"/>
/// value. <see cref="ProjectValue"/> is the value set in the project file for the asked scope, when there is one.
/// </summary>
public sealed record ProjectPropertyValue(
    string Name,
    string Value,
    string? Unevaluated,
    string Origin,
    string? DefinedIn,
    int Line,
    string? Condition,
    string? ProjectValue);

/// <summary>
/// Sets (or, with <see cref="Value"/> null, removes) a property in the project file, for every configuration or only
/// <see cref="Configuration"/>. Writes the minimal change (ADR 0013).
/// </summary>
public sealed record SetPropertyParams(string Project, string Name, string? Value, string? Configuration = null);

public sealed record SetPropertyResult(bool Changed);

// ------------------------------------------------------------------ references

public static class ReferenceEditKinds
{
    public const string Project = "project";

    /// <summary>A <c>Reference</c> with a <c>HintPath</c>.</summary>
    public const string Assembly = "assembly";

    /// <summary>An <c>Analyzer</c> item (an analyzer or source generator assembly).</summary>
    public const string Analyzer = "analyzer";
}

public sealed record ReferenceEditParams(string Project, string Kind, string Path);

// ------------------------------------------------------------------ configurations

/// <summary>
/// The solution's configurations and, per project, what each solution configuration builds. <see cref="BuildTypes"/>
/// and <see cref="Platforms"/> are the solution's dimensions (Debug, Release / Any CPU, x64…).
/// </summary>
public sealed record SolutionConfigurationsResult(string[] BuildTypes, string[] Platforms, ProjectConfigurationEntries[] Projects);

/// <summary><see cref="ProjectConfigurations"/>/<see cref="ProjectPlatforms"/> are what the project file declares.</summary>
public sealed record ProjectConfigurationEntries(string Project, string Name, string[] ProjectConfigurations, string[] ProjectPlatforms, ProjectConfigurationEntry[] Entries);

public sealed record ProjectConfigurationEntry(string SolutionConfiguration, string SolutionPlatform, string Configuration, string Platform, bool Build);

public sealed record SetProjectConfigurationParams(string Project, string SolutionConfiguration, string SolutionPlatform, string? Configuration, string? Platform, bool? Build);

public static class ConfigurationDimensions
{
    public const string BuildType = "buildType";
    public const string Platform = "platform";
}

/// <summary>Adds a solution build type or platform (copying the project mappings of <see cref="CopyFrom"/>), or removes one.</summary>
public sealed record SolutionConfigurationEditParams(string Dimension, string Name, string? CopyFrom = null);

// ------------------------------------------------------------------ dependencies

/// <summary>Each project's explicit build dependencies in the solution file (besides its project references).</summary>
public sealed record SolutionDependenciesResult(ProjectDependencies[] Projects);

public sealed record ProjectDependencies(string Project, string[] DependsOn);

public sealed record SetDependenciesParams(string Project, string[] DependsOn);

// ------------------------------------------------------------------ load, unload, filters

/// <summary>Writes a solution filter (<c>.slnf</c>) with <see cref="Projects"/> (default: the loaded projects).</summary>
public sealed record SaveFilterParams(string Path, string[]? Projects = null);

// ------------------------------------------------------------------ SDKs

public sealed record SdkInfoResult(
    string? ResolvedSdk,
    string? ResolvedVersion,
    string? GlobalJson,
    string? RequestedVersion,
    string? RollForward,
    DotnetSdk[] Sdks,
    DotnetWorkload[] Workloads,
    string? Error,
    string? Warning = null);

public sealed record DotnetSdk(string Version, string Path);

public sealed record DotnetWorkload(string Id, string Version, string Source);

/// <summary>Writes <c>global.json</c> next to the solution (null <see cref="Version"/>: removes the SDK pin).</summary>
public sealed record SetGlobalJsonParams(string? Version, string? RollForward);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EmptyParams))]
[JsonSerializable(typeof(EmptyResult))]
[JsonSerializable(typeof(ProjectPathParams))]
[JsonSerializable(typeof(GetPropertiesParams))]
[JsonSerializable(typeof(ProjectPropertiesResult))]
[JsonSerializable(typeof(SetPropertyParams))]
[JsonSerializable(typeof(SetPropertyResult))]
[JsonSerializable(typeof(ReferenceEditParams))]
[JsonSerializable(typeof(SolutionConfigurationsResult))]
[JsonSerializable(typeof(SetProjectConfigurationParams))]
[JsonSerializable(typeof(SolutionConfigurationEditParams))]
[JsonSerializable(typeof(SolutionDependenciesResult))]
[JsonSerializable(typeof(SetDependenciesParams))]
[JsonSerializable(typeof(SaveFilterParams))]
[JsonSerializable(typeof(SdkInfoResult))]
[JsonSerializable(typeof(SetGlobalJsonParams))]
public sealed partial class ProjectManagementJsonContext : JsonSerializerContext;
