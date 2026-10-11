using System.Text.Json.Serialization;

namespace DaedalusStudio.Projects.Protocol;

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
    public const string SetActiveFramework = "project/setActiveFramework";
    public const string DesignTimeBuild = "project/designTimeBuild";
    public const string Build = "build/run";
    public const string CancelBuild = "build/cancel";
    public const string RunInfo = "project/runInfo";

    // host → shell
    public const string SolutionChanged = "solution/changed";
    public const string ProjectEvaluated = "project/evaluated";
    public const string EvaluationProgress = "solution/evaluationProgress";
    public const string RestoreOutput = "restore/output";
    public const string RestoreState = "restore/state";
    public const string CompilerInputs = "project/compilerInputs";
    public const string DesignTimeProgress = "solution/designTimeProgress";
    public const string BuildOutput = "build/output";
    public const string BuildDiagnostic = "build/diagnostic";
    public const string Log = "log";
}

public static class ProjectErrorCodes
{
    public const int NoSolution = -33100;
    public const int NotTrusted = -33101;
    public const int InvalidOperation = -33102;
    public const int FileSystem = -33103;

    /// <summary>A tool the request runs isn't installed (dotnet-ef, ADR 0036).</summary>
    public const int ToolMissing = -33104;
}

public sealed record EmptyParams;

public sealed record EmptyResult;

public sealed record InitializeParams(int ProcessId, string ClientName, string ProtocolVersion);

/// <param name="SdkWarning">Why the SDK the solution's folder resolves to isn't the one in use (ADR 0025), or null.</param>
public sealed record InitializeResult(string ServerName, string ServerVersion, string ProtocolVersion, string MSBuildVersion, string SdkPath, string? SdkWarning = null);

// ------------------------------------------------------------------ solution

/// <summary>
/// Opens a solution (<c>.sln</c>, <c>.slnx</c>), a single project (<c>.csproj</c>…) or a folder (folder mode, ADR 0033). With <see cref="Trusted"/>
/// false only the solution file is read: no MSBuild evaluation and no restore. <see cref="ActiveFrameworks"/> maps
/// multi-targeted project paths to the framework the language service sees (default: the first one).
/// </summary>
public sealed record OpenSolutionParams(string Path, bool Trusted, string? Configuration, string? Platform, bool Restore, Dictionary<string, string>? ActiveFrameworks = null);

// A solution filter (.slnf) opens its solution with only the filter's projects loaded (ADR 0013); SolutionInfo.Path is
// the solution itself and SolutionInfo.FilterPath the filter.

/// <summary>
/// The structure from the solution file, sent before any evaluation. In folder mode (ADR 0033) <see cref="Format"/> is
/// <c>folder</c>, <see cref="Path"/> the folder, and <see cref="Projects"/> the projects loaded from it.
/// </summary>
public sealed record SolutionInfo(
    string Path,
    string Format,
    SolutionFolderInfo[] Folders,
    SolutionProjectInfo[] Projects,
    SolutionConfigurationInfo[] Configurations,
    SolutionConfigurationInfo? Active,
    bool Trusted,
    string? FilterPath = null)
{
    /// <summary>The workspace folder: the folder itself in folder mode, else the solution (or project) file's folder.</summary>
    [JsonIgnore]
    public string Directory => IsFolder ? Path : System.IO.Path.GetDirectoryName(Path)!;

    /// <summary>Folder mode: a folder opened without a solution file (ADR 0033).</summary>
    [JsonIgnore]
    public bool IsFolder => Format == SolutionFormats.Folder;

    /// <summary>The name shown for the workspace: the folder's or the solution file's.</summary>
    [JsonIgnore]
    public string Name => IsFolder ? System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path)) : System.IO.Path.GetFileNameWithoutExtension(Path);
}

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

    /// <summary>Unloaded by the user (or left out by a solution filter): not evaluated, not built (ADR 0013).</summary>
    public const string Unloaded = "unloaded";
}

/// <summary>
/// An evaluated project. The shell derives the Solution Explorer tree from it. <see cref="IsTestProject"/> is the
/// <c>IsTestProject</c> property; <see cref="TestPlatform"/> is <c>mtp</c> when <c>IsTestingPlatformApplication</c> is
/// true, <c>vstest</c> for other test projects, and null otherwise (ADR 0010).
/// </summary>
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
    bool Restored,
    bool IsTestProject = false,
    string? TestPlatform = null);

public static class TestPlatforms
{
    public const string VSTest = "vstest";

    /// <summary>Microsoft.Testing.Platform.</summary>
    public const string Mtp = "mtp";
}

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

    /// <summary>Analyzers and source generators; only in the shell's tree (from design-time builds).</summary>
    public const string Analyzer = "analyzer";
}

/// <summary>
/// A dependency. <see cref="TargetFramework"/> groups it when the project multi-targets. <see cref="Resolved"/>
/// is false for packages that are not restored (or not found in the assets file).
/// </summary>
public sealed record ReferenceInfo(string Kind, string Name, string? Version, string? Path, string TargetFramework, bool Resolved);

public sealed record ProjectPathParams(string Path);

/// <summary><c>project/runInfo</c>: <see cref="TargetFramework"/> picks a framework of a multi-targeted project (default: the active one).</summary>
public sealed record RunInfoParams(string Path, string? TargetFramework = null);

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

// ------------------------------------------------------------------ design-time builds

/// <summary>Chooses which target framework of a multi-targeted project the language service sees.</summary>
public sealed record SetActiveFrameworkParams(string Path, string TargetFramework);

/// <summary>Runs design-time builds again: for one project, or (null) for every project.</summary>
public sealed record DesignTimeBuildParams(string? Path);

public static class CompilerInputStates
{
    public const string Ok = "ok";
    public const string Failed = "failed";
}

/// <summary>
/// What the compiler would get for a project (one target framework): the <c>csc</c>/<c>vbc</c> command line from a
/// design-time build (<c>Compile</c> with <c>SkipCompilerExecution</c>), and the project's outputs so that
/// references to other projects' outputs can be mapped back to those projects. <see cref="Language"/> is
/// <c>C#</c>, <c>VB</c> or <c>F#</c>.
/// </summary>
public sealed record CompilerInputsInfo(
    string Path,
    string TargetFramework,
    string Language,
    string State,
    string? Error,
    string[] Arguments,
    string[] ProjectReferences,
    string? OutputPath,
    string? OutputRefPath);

public sealed record DesignTimeProgressParams(int Built, int Total, bool Completed);

// ------------------------------------------------------------------ builds

public static class BuildTargets
{
    public const string Build = "build";
    public const string Rebuild = "rebuild";
    public const string Clean = "clean";

    /// <summary><c>dotnet pack</c>: builds, then writes the project's <c>.nupkg</c> (and <c>.snupkg</c>).</summary>
    public const string Pack = "pack";
}

/// <summary>
/// Builds the solution (<see cref="Project"/> null) or one project (and the projects it references) with the active
/// configuration. <see cref="Target"/> is <c>build</c>, <c>rebuild</c> or <c>clean</c>. A restore runs first unless
/// <see cref="Restore"/> is false (never for clean). <c>build/cancel</c> stops it; the result then says so.
/// <see cref="Options"/> (ADR 0034) sets verbosity, logs, parallelism and the fast up-to-date check; without them the
/// build runs as before (minimal verbosity, no up-to-date check).
/// </summary>
public sealed record BuildParams(string Target, string? Project, bool Restore = true, BuildOptions? Options = null);

/// <summary>
/// Sent once every <c>build/output</c> and <c>build/diagnostic</c> of the build has been sent. <see cref="UpToDate"/>:
/// the fast up-to-date check found nothing to build, so MSBuild didn't run. <see cref="BinaryLog"/>: the binary log
/// the build wrote, when the options asked for one.
/// </summary>
public sealed record BuildResultInfo(bool Succeeded, bool Canceled, int Errors, int Warnings, long ElapsedMilliseconds, bool UpToDate = false, string? BinaryLog = null);

/// <summary>A line for the Build output channel.</summary>
public sealed record BuildOutputParams(string Line);

public static class BuildSeverities
{
    public const string Error = "error";
    public const string Warning = "warning";
}

/// <summary>
/// An error or warning of a build. <see cref="File"/> is absolute (or null when MSBuild gave none); lines and
/// columns are 1-based as MSBuild reports them, 0 when unknown. <see cref="Project"/> is the project file.
/// </summary>
public sealed record BuildDiagnosticInfo(
    string Severity,
    string? Code,
    string Message,
    string? File,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string? Project,
    string? HelpLink);

/// <summary>
/// How to start a project (<c>project/runInfo</c>), from the SDK's <c>RunCommand</c>, <c>RunArguments</c> and
/// <c>RunWorkingDirectory</c> properties (what <c>dotnet run</c> uses) for the active configuration and framework.
/// <see cref="LaunchSettings"/> is <c>Properties/launchSettings.json</c> when it exists.
/// </summary>
public sealed record RunInfo(
    string Path,
    string? TargetFramework,
    string? OutputType,
    string? TargetPath,
    string? RunCommand,
    string? RunArguments,
    string? RunWorkingDirectory,
    string? LaunchSettings);

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
[JsonSerializable(typeof(SetActiveFrameworkParams))]
[JsonSerializable(typeof(DesignTimeBuildParams))]
[JsonSerializable(typeof(CompilerInputsInfo))]
[JsonSerializable(typeof(DesignTimeProgressParams))]
[JsonSerializable(typeof(BuildParams))]
[JsonSerializable(typeof(BuildResultInfo))]
[JsonSerializable(typeof(BuildOutputParams))]
[JsonSerializable(typeof(BuildDiagnosticInfo))]
[JsonSerializable(typeof(RunInfo))]
[JsonSerializable(typeof(RunInfoParams))]
public sealed partial class ProjectJsonContext : JsonSerializerContext;
