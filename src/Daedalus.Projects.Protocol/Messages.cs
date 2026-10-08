using System.Text.Json.Serialization;

namespace Daedalus.Projects.Protocol;

/// <summary>Method names of the shell ↔ build host protocol (docs/project-protocol.md).</summary>
public static class ProjectMethods
{
    public const string ProtocolVersion = "0.1";

    public const string Initialize = "initialize";
    public const string Shutdown = "shutdown";
    public const string Exit = "exit";

    // shell → host
    public const string OpenSolution = "solution/open";
    public const string CloseSolution = "solution/close";
    public const string SetConfiguration = "solution/setConfiguration";
    public const string Trust = "solution/trust";
    public const string Reevaluate = "project/reevaluate";
    public const string Restore = "restore/run";
    public const string ListDirectory = "fs/list";
    public const string CreateFile = "fs/createFile";
    public const string CreateFolder = "fs/createFolder";
    public const string Rename = "fs/rename";
    public const string Move = "fs/move";
    public const string Delete = "fs/delete";
    public const string AddProject = "solution/addProject";
    public const string RemoveProject = "solution/removeProject";
    public const string AddSolutionFolder = "solution/addFolder";
    public const string RenameSolutionFolder = "solution/renameFolder";
    public const string RemoveSolutionFolder = "solution/removeFolder";
    public const string MoveInSolution = "solution/move";
    public const string AddProjectReference = "project/addReference";
    public const string RemoveProjectReference = "project/removeReference";

    // host → shell
    public const string SolutionChanged = "solution/changed";
    public const string ProjectEvaluated = "project/evaluated";
    public const string EvaluationProgress = "solution/evaluationProgress";
    public const string RestoreOutput = "restore/output";
    public const string RestoreState = "restore/state";
    public const string Log = "log";
}

public static class ProjectErrorCodes
{
    public const int NoSolution = -33100;
    public const int NotTrusted = -33101;
    public const int InvalidOperation = -33102;
    public const int FileSystem = -33103;
}

public sealed record EmptyParams;

public sealed record EmptyResult;

public sealed record InitializeParams(int ProcessId, string ClientName, string ProtocolVersion);

public sealed record InitializeResult(string ServerName, string ServerVersion, string ProtocolVersion, string MSBuildVersion, string SdkPath);

// ------------------------------------------------------------------ solution

/// <summary>
/// Opens a solution (<c>.sln</c>, <c>.slnx</c>) or a single project (<c>.csproj</c>…). With <see cref="Trusted"/>
/// false only the solution file is read: no MSBuild evaluation and no restore.
/// </summary>
public sealed record OpenSolutionParams(string Path, bool Trusted, string? Configuration, string? Platform, bool Restore);

/// <summary>The structure from the solution file, sent before any evaluation.</summary>
public sealed record SolutionInfo(
    string Path,
    string Format,
    SolutionFolderInfo[] Folders,
    SolutionProjectInfo[] Projects,
    SolutionConfigurationInfo[] Configurations,
    SolutionConfigurationInfo? Active,
    bool Trusted);

/// <summary>A solution folder. <see cref="Id"/> is its path inside the solution, e.g. <c>/src/tests/</c>.</summary>
public sealed record SolutionFolderInfo(string Id, string Name, string? Parent, string[] Files);

/// <summary>A project entry of the solution file. <see cref="Parent"/> is the containing solution folder id.</summary>
public sealed record SolutionProjectInfo(string Path, string Name, string? Parent, string? TypeId);

public sealed record SolutionConfigurationInfo(string Configuration, string Platform)
{
    [JsonIgnore]
    public string Name => $"{Configuration}|{Platform}";
}

public sealed record SetConfigurationParams(string Configuration, string Platform);

/// <summary>The user trusted the open solution's folder: evaluate it (and restore when <see cref="Restore"/>).</summary>
public sealed record TrustParams(bool Restore);

public sealed record EvaluationProgressParams(int Evaluated, int Total, bool Completed);

// ------------------------------------------------------------------ projects

public static class ProjectStates
{
    public const string Loading = "loading";
    public const string Loaded = "loaded";
    public const string Failed = "failed";
    public const string Untrusted = "untrusted";
    public const string Unsupported = "unsupported";
    public const string Missing = "missing";
}

/// <summary>An evaluated project. The shell derives the Solution Explorer tree from it.</summary>
public sealed record ProjectInfo(
    string Path,
    string Name,
    string State,
    string? Error,
    string[] TargetFrameworks,
    string? OutputType,
    ProjectItemInfo[] Items,
    ReferenceInfo[] References,
    string[] Diagnostics,
    bool Restored);

/// <summary>
/// An item of the project. <see cref="Kind"/> is the MSBuild item type (<c>Compile</c>, <c>None</c>,
/// <c>Content</c>, <c>EmbeddedResource</c>, <c>Folder</c>…). <see cref="Link"/> is the display path relative to
/// the project for files outside its folder; <see cref="DependentUpon"/> nests the file under another one.
/// </summary>
public sealed record ProjectItemInfo(string Kind, string Path, string? Link, string? DependentUpon);

public static class ReferenceKinds
{
    public const string Framework = "framework";
    public const string Package = "package";
    public const string Project = "project";
    public const string Assembly = "assembly";
}

/// <summary>
/// A dependency. <see cref="TargetFramework"/> groups it when the project multi-targets. <see cref="Resolved"/>
/// is false for packages that are not restored (or not found in the assets file).
/// </summary>
public sealed record ReferenceInfo(string Kind, string Name, string? Version, string? Path, string TargetFramework, bool Resolved);

public sealed record ProjectPathParams(string Path);

// ------------------------------------------------------------------ restore

public sealed record RestoreParams(string? Path);

public sealed record RestoreOutputParams(string Line);

/// <summary><see cref="State"/> is <c>running</c>, <c>succeeded</c> or <c>failed</c>.</summary>
public sealed record RestoreStateParams(string State, string? Message);

// ------------------------------------------------------------------ file system

public sealed record ListDirectoryParams(string Path);

public sealed record DirectoryEntry(string Path, bool IsDirectory);

public sealed record ListDirectoryResult(DirectoryEntry[] Entries);

public sealed record CreateFileParams(string Path, string? Contents);

public sealed record CreateFolderParams(string Path);

public sealed record RenameParams(string OldPath, string NewPath);

/// <summary>Moves (or copies) files and folders into <see cref="TargetFolder"/>.</summary>
public sealed record MoveParams(string[] Sources, string TargetFolder, bool Copy);

/// <summary>Deletes to the OS trash.</summary>
public sealed record DeleteParams(string[] Paths);

/// <summary>Project and solution files that were edited to keep up with a file operation.</summary>
public sealed record FileOperationResult(string[] UpdatedFiles, string[] CreatedPaths);

// ------------------------------------------------------------------ solution and reference edits

public sealed record AddProjectParams(string ProjectPath, string? SolutionFolder);

public sealed record SolutionFolderParams(string Name, string? Parent);

public sealed record RenameSolutionFolderParams(string Id, string NewName);

public sealed record RemoveSolutionFolderParams(string Id);

/// <summary>Moves a project (by path) or a solution folder (by id) into <see cref="TargetFolder"/> (null = root).</summary>
public sealed record MoveInSolutionParams(string Item, string? TargetFolder);

public sealed record ProjectReferenceParams(string ProjectPath, string ReferencePath);

// ------------------------------------------------------------------ log

/// <summary><see cref="Level"/> is <c>debug</c>, <c>info</c>, <c>warning</c> or <c>error</c>.</summary>
public sealed record LogParams(string Level, string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EmptyParams))]
[JsonSerializable(typeof(EmptyResult))]
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(OpenSolutionParams))]
[JsonSerializable(typeof(SolutionInfo))]
[JsonSerializable(typeof(SetConfigurationParams))]
[JsonSerializable(typeof(TrustParams))]
[JsonSerializable(typeof(EvaluationProgressParams))]
[JsonSerializable(typeof(ProjectInfo))]
[JsonSerializable(typeof(ProjectPathParams))]
[JsonSerializable(typeof(RestoreParams))]
[JsonSerializable(typeof(RestoreOutputParams))]
[JsonSerializable(typeof(RestoreStateParams))]
[JsonSerializable(typeof(ListDirectoryParams))]
[JsonSerializable(typeof(ListDirectoryResult))]
[JsonSerializable(typeof(CreateFileParams))]
[JsonSerializable(typeof(CreateFolderParams))]
[JsonSerializable(typeof(RenameParams))]
[JsonSerializable(typeof(MoveParams))]
[JsonSerializable(typeof(DeleteParams))]
[JsonSerializable(typeof(FileOperationResult))]
[JsonSerializable(typeof(AddProjectParams))]
[JsonSerializable(typeof(SolutionFolderParams))]
[JsonSerializable(typeof(RenameSolutionFolderParams))]
[JsonSerializable(typeof(RemoveSolutionFolderParams))]
[JsonSerializable(typeof(MoveInSolutionParams))]
[JsonSerializable(typeof(ProjectReferenceParams))]
[JsonSerializable(typeof(LogParams))]
public sealed partial class ProjectJsonContext : JsonSerializerContext;
