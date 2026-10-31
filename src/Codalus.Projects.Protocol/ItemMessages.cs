using System.Text.Json.Serialization;

namespace Codalus.Projects.Protocol;

/// <summary>
/// Project items, Show All Files, folder mode and item templates on the build host connection (ADR 0033,
/// docs/project-protocol.md).
/// </summary>
public static class ItemMethods
{
    public const string Get = "items/get";
    public const string Set = "items/set";
    public const string Exclude = "items/exclude";
    public const string Include = "items/include";
    public const string AddExisting = "items/addExisting";

    /// <summary>F# compile order (M36): moves a <c>Compile</c> item before or after another.</summary>
    public const string Move = "items/move";

    public const string ListTree = "fs/listTree";

    /// <summary>Host → shell: folders whose set of files changed on disk (debounced).</summary>
    public const string FilesChanged = "fs/changed";

    public const string Discover = "folder/discover";
    public const string SetFolderProjects = "folder/setProjects";

    public const string Instantiate = "templates/instantiate";
}

/// <summary>Well-known item metadata names.</summary>
public static class ItemMetadataNames
{
    public const string CopyToOutputDirectory = "CopyToOutputDirectory";
    public const string Generator = "Generator";
    public const string CustomToolNamespace = "CustomToolNamespace";
    public const string Link = "Link";
    public const string DependentUpon = "DependentUpon";
}

/// <summary>An item edit's operations, as written in the project file.</summary>
public static class ItemOperations
{
    public const string Include = "include";
    public const string Update = "update";
    public const string Remove = "remove";
}

public sealed record ItemParams(string Project, string Path);

/// <summary>
/// A file of a project, as the Properties window shows it. <see cref="ItemType"/> is the item type it is evaluated as
/// (null: the file isn't an item: excluded, or outside the globs). <see cref="FromGlob"/> is true when a wildcard
/// <c>Include</c> brings it in. <see cref="Elements"/> are the project file's items that name it (or a wildcard that
/// covers it), in file order. <see cref="Metadata"/> are the well-known metadata and every metadata the project file
/// sets for it. <see cref="ItemTypes"/> are the Build Action choices.
/// </summary>
public sealed record ItemInfoResult(
    string Project,
    string Path,
    string? ItemType,
    bool IsLinked,
    string? Link,
    bool FromGlob,
    ItemElementInfo[] Elements,
    ItemMetadataInfo[] Metadata,
    string[] ItemTypes);

/// <summary>A project file item that names the file: its <see cref="Operation"/> (<see cref="ItemOperations"/>), as written.</summary>
public sealed record ItemElementInfo(string Operation, string ItemType, string Value, int Line, string? Condition);

/// <summary>
/// A metadata value: <see cref="Value"/> evaluated; <see cref="ProjectValue"/> as written in the project file's own items
/// for this file (null when the project file doesn't set it).
/// </summary>
public sealed record ItemMetadataInfo(string Name, string Value, string? ProjectValue);

/// <summary>
/// Changes a file's item type (Build Action) and metadata (null value: back to the inherited one). Writes the minimal
/// change: <c>Update</c> items for globbed files, <c>Remove</c> plus <c>Include</c> when the type changes (ADR 0033).
/// </summary>
public sealed record ItemSetParams(string Project, string Path, string? ItemType, Dictionary<string, string?>? Metadata);

public sealed record ItemEditResult(bool Changed, string[] UpdatedFiles);

/// <summary>Files or folders to include in, or exclude from, a project.</summary>
public sealed record ItemPathsParams(string Project, string[] Paths);

/// <summary>
/// <c>items/move</c>: moves <see cref="Path"/>'s <c>Compile</c> item before (<see cref="After"/> false) or after
/// <see cref="Target"/>'s, in the project file (F#'s compile order, M36).
/// </summary>
public sealed record MoveItemParams(string Project, string Path, string Target, bool After);

/// <summary>
/// Add Existing Item: copies <see cref="Files"/> into <see cref="TargetFolder"/> (a folder of the project), or, with
/// <see cref="AsLink"/>, adds links to them shown in that folder.
/// </summary>
public sealed record AddExistingItemsParams(string Project, string TargetFolder, string[] Files, bool AsLink);

/// <summary>
/// Everything under <see cref="Path"/> (folders first), up to <see cref="MaxEntries"/> (default 20,000). With
/// <see cref="Folders"/>, only these folders below <see cref="Path"/> are gone into; the others are listed without what
/// is in them (Show All Files lists a folder that isn't in its project once it's expanded, as the classic IDE does).
/// </summary>
public sealed record ListTreeParams(string Path, int? MaxEntries = null, string[]? Folders = null);

public sealed record ListTreeResult(DirectoryEntry[] Entries, bool Truncated);

/// <summary>Folders whose files were created, deleted or renamed.</summary>
public sealed record FilesChangedParams(string[] Directories)
{
    /// <summary>
    /// The watcher dropped events (the OS's buffer overflowed, as in a large checkout): anything under the folders it
    /// watches may have changed, deep below <see cref="Directories"/> (those folders then), so Show All Files lists every
    /// project again. Left out when false.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Rescan { get; init; }
}

/// <summary>Folder mode: the project files under the folder.</summary>
public sealed record DiscoverProjectsResult(string[] Projects);

/// <summary>Folder mode: the projects of the folder's in-memory solution.</summary>
public sealed record SetFolderProjectsParams(string[] Projects);

/// <summary>
/// Runs a <c>dotnet new</c> item template (by short name) into <see cref="OutputDirectory"/>, with <see cref="Project"/> as
/// its context. <see cref="Arguments"/> are extra template options.
/// </summary>
public sealed record InstantiateTemplateParams(string Template, string Name, string OutputDirectory, string? Project, string? Language = null, string[]? Arguments = null);

/// <summary>The files the template created, and the CLI's output.</summary>
public sealed record InstantiateTemplateResult(string[] CreatedPaths, string Output);

/// <summary>A solution's (or folder's) workspace folder: the folder itself in folder mode, else the solution file's folder.</summary>
public static class SolutionFormats
{
    public const string Sln = "sln";
    public const string Slnx = "slnx";
    public const string Project = "project";

    /// <summary>Folder mode (ADR 0033): <see cref="SolutionInfo.Path"/> is the folder.</summary>
    public const string Folder = "folder";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EmptyParams))]
[JsonSerializable(typeof(EmptyResult))]
[JsonSerializable(typeof(ItemParams))]
[JsonSerializable(typeof(ItemInfoResult))]
[JsonSerializable(typeof(ItemSetParams))]
[JsonSerializable(typeof(ItemEditResult))]
[JsonSerializable(typeof(ItemPathsParams))]
[JsonSerializable(typeof(MoveItemParams))]
[JsonSerializable(typeof(AddExistingItemsParams))]
[JsonSerializable(typeof(FileOperationResult))]
[JsonSerializable(typeof(ListTreeParams))]
[JsonSerializable(typeof(ListTreeResult))]
[JsonSerializable(typeof(FilesChangedParams))]
[JsonSerializable(typeof(DiscoverProjectsResult))]
[JsonSerializable(typeof(SetFolderProjectsParams))]
[JsonSerializable(typeof(InstantiateTemplateParams))]
[JsonSerializable(typeof(InstantiateTemplateResult))]
public sealed partial class ItemJsonContext : JsonSerializerContext;
