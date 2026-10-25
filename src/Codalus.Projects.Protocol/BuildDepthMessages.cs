using System.Text.Json.Serialization;

namespace Codalus.Projects.Protocol;

/// <summary>
/// Build depth on the build host connection (ADR 0034): batch builds, the fast up-to-date check, build timings, build
/// events and binary logs (docs/project-protocol.md).
/// </summary>
public static class BuildDepthMethods
{
    public const string Batch = "build/batch";
    public const string UpToDate = "build/upToDate";
    public const string Timings = "build/timings";

    public const string GetBuildEvents = "buildEvents/get";
    public const string SetBuildEvents = "buildEvents/set";

    public const string OpenBinaryLog = "binlog/open";
    public const string BinaryLogChildren = "binlog/children";
    public const string SearchBinaryLog = "binlog/search";
    public const string CloseBinaryLog = "binlog/close";
}

/// <summary>MSBuild verbosities for the Output window and the build log file.</summary>
public static class BuildVerbosities
{
    public const string Quiet = "quiet";
    public const string Minimal = "minimal";
    public const string Normal = "normal";
    public const string Detailed = "detailed";
    public const string Diagnostic = "diagnostic";
}

/// <summary>
/// How builds run (ADR 0034): the Output window's verbosity, a build log file and its verbosity, the most projects built
/// in parallel (0: one per processor), a binary log, the fast up-to-date check, and global properties (code analysis
/// on build).
/// </summary>
public sealed record BuildOptions
{
    public string? Verbosity { get; init; }

    /// <summary>Writes the build log to this file (null: none), at <see cref="LogFileVerbosity"/>.</summary>
    public string? LogFile { get; init; }

    public string? LogFileVerbosity { get; init; }

    public int MaxParallelProjects { get; init; }

    /// <summary>Captures a binary log in this file (null: none).</summary>
    public string? BinaryLog { get; init; }

    public bool UpToDateCheck { get; init; } = true;

    public Dictionary<string, string>? Properties { get; init; }
}

/// <summary>One project × configuration × platform of a batch build.</summary>
public sealed record BatchBuildEntry(string Project, string Configuration, string Platform);

public sealed record BatchBuildParams(string Target, BatchBuildEntry[] Entries, BuildOptions? Options = null);

public sealed record BatchBuildEntryResult(string Project, string Configuration, string Platform, bool Succeeded, bool Skipped, int Errors, int Warnings, long ElapsedMilliseconds);

public sealed record BatchBuildResult(bool Succeeded, bool Canceled, int Errors, int Warnings, long ElapsedMilliseconds, BatchBuildEntryResult[] Entries);

/// <summary>The fast up-to-date check alone: for one project (and what it references), or every loaded project.</summary>
public sealed record UpToDateParams(string? Project);

public sealed record UpToDateProject(string Project, bool UpToDate, string Reason);

public sealed record UpToDateResult(bool UpToDate, UpToDateProject[] Projects);

/// <summary>
/// The last build's timings (ADR 0034): each project's evaluation-to-finish span on its node, the targets and tasks that
/// took longest, and the critical path (the chain of project dependencies that set the build's length). Times are
/// milliseconds from the build's start.
/// </summary>
public sealed record BuildTimingsResult(long TotalMilliseconds, ProjectTiming[] Projects, TargetTiming[] Targets, TaskTiming[] Tasks, string[] CriticalPath, string? BinaryLog);

/// <summary>A project's build: <see cref="Id"/> is unique in the build (a project may build once per target framework).</summary>
public sealed record ProjectTiming(string Id, string Project, string Name, string? TargetFramework, int Node, long Start, long Duration, bool Succeeded, string[] Targets, string[] DependsOn);

public sealed record TargetTiming(string Project, string Name, long Duration);

public sealed record TaskTiming(string Name, long Duration, int Count);

// ------------------------------------------------------------------ build events

public sealed record BuildEventsParams(string Project);

/// <summary>
/// Pre-build and post-build events, as the classic IDE stores them: <c>PreBuild</c> and <c>PostBuild</c> targets with an
/// <c>Exec</c>. <see cref="RunPostBuildEvent"/> is <c>Always</c>, <c>OnBuildSuccess</c> (default) or <c>OnOutputUpdated</c>.
/// </summary>
public sealed record BuildEventsResult(string Project, string PreBuild, string PostBuild, string RunPostBuildEvent);

/// <summary>Writes the events (an empty command removes its target; null keeps it).</summary>
public sealed record SetBuildEventsParams(string Project, string? PreBuild, string? PostBuild, string? RunPostBuildEvent);

// ------------------------------------------------------------------ binary logs

public sealed record OpenBinaryLogParams(string Path);

/// <summary>An open binary log: its root node, and a summary of the build it recorded.</summary>
public sealed record BinaryLogInfo(string Id, string Path, BinaryLogNode Root, bool Succeeded, long DurationMilliseconds, int Errors, int Warnings, int Nodes);

/// <summary>
/// A node of a binary log's tree: <see cref="Kind"/> is the StructuredLogger type (<c>Project</c>, <c>Target</c>,
/// <c>Task</c>, <c>Message</c>, <c>Error</c>, <c>Warning</c>, <c>Property</c>, <c>Item</c>, <c>Folder</c>…).
/// </summary>
public sealed record BinaryLogNode(int Id, string Kind, string Text, bool HasChildren, long? DurationMilliseconds = null, string? File = null, int Line = 0, bool IsError = false, bool IsWarning = false);

public sealed record BinaryLogChildrenParams(string Id, int Node);

public sealed record BinaryLogChildrenResult(BinaryLogNode[] Children);

public sealed record SearchBinaryLogParams(string Id, string Query, int MaxResults = 500);

/// <summary>A search result: the node and the path of node ids from the root to it (to reveal it).</summary>
public sealed record BinaryLogSearchResult(BinaryLogNode Node, int[] Path);

public sealed record SearchBinaryLogResult(BinaryLogSearchResult[] Results, bool Truncated);

public sealed record CloseBinaryLogParams(string Id);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EmptyParams))]
[JsonSerializable(typeof(EmptyResult))]
[JsonSerializable(typeof(BatchBuildParams))]
[JsonSerializable(typeof(BatchBuildResult))]
[JsonSerializable(typeof(UpToDateParams))]
[JsonSerializable(typeof(UpToDateResult))]
[JsonSerializable(typeof(BuildTimingsResult))]
[JsonSerializable(typeof(BuildEventsParams))]
[JsonSerializable(typeof(BuildEventsResult))]
[JsonSerializable(typeof(SetBuildEventsParams))]
[JsonSerializable(typeof(OpenBinaryLogParams))]
[JsonSerializable(typeof(BinaryLogInfo))]
[JsonSerializable(typeof(BinaryLogChildrenParams))]
[JsonSerializable(typeof(BinaryLogChildrenResult))]
[JsonSerializable(typeof(SearchBinaryLogParams))]
[JsonSerializable(typeof(SearchBinaryLogResult))]
[JsonSerializable(typeof(CloseBinaryLogParams))]
public sealed partial class BuildDepthJsonContext : JsonSerializerContext;
