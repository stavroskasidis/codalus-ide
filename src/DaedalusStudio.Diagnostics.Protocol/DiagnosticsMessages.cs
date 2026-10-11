using System.Text.Json.Serialization;

namespace DaedalusStudio.Diagnostics.Protocol;

/// <summary>
/// The diagnostics host protocol (ADR 0044, docs/diagnostics-protocol.md): JSON-RPC over stdio between the shell and
/// <c>daedalus-diaghost</c>. Live sessions (counters, runtime events) push notifications; recorded traces stay in the
/// host, which answers aggregated, paged views of them (call trees a level at a time, pruned flame graphs).
/// </summary>
public static class DiagnosticsMethods
{
    public const string ProtocolVersion = "1";

    public const string Initialize = "initialize";
    public const string Shutdown = "shutdown";
    public const string Exit = "exit";

    /// <summary>The .NET processes on this machine with a diagnostics port.</summary>
    public const string Processes = "diag/processes";

    /// <summary>Resumes a process started with <c>DOTNET_DefaultDiagnosticPortSuspend=1</c> (profiling from launch).</summary>
    public const string Resume = "diag/resume";

    public const string CountersStart = "diag/counters/start";
    public const string CountersStop = "diag/counters/stop";

    /// <summary>Notification (host → shell): counter values of a session.</summary>
    public const string CountersUpdate = "diag/counters/update";

    /// <summary>Notification (host → shell): GC and exception events of a session.</summary>
    public const string RuntimeEvents = "diag/runtimeEvents";

    /// <summary>Notification (host → shell): a live session ended (the process exited, or an error).</summary>
    public const string SessionEnded = "diag/sessionEnded";

    public const string TraceStart = "diag/trace/start";
    public const string TraceStop = "diag/trace/stop";
    public const string TraceOpen = "diag/trace/open";
    public const string TraceClose = "diag/trace/close";

    public const string CpuTree = "diag/cpu/tree";
    public const string CpuCallers = "diag/cpu/callers";
    public const string CpuFunctions = "diag/cpu/functions";
    public const string CpuCallerCallee = "diag/cpu/callerCallee";
    public const string CpuFlame = "diag/cpu/flame";
    public const string CpuTimeline = "diag/cpu/timeline";
    public const string CpuFiles = "diag/cpu/files";
    public const string CpuLines = "diag/cpu/lines";
}

/// <summary>Errors the host answers with.</summary>
public static class DiagnosticsErrorCodes
{
    /// <summary>No process with that id, or it exited.</summary>
    public const int ProcessNotFound = -33300;

    /// <summary>The process has no diagnostics port (not .NET, .NET Core before 3.0, or the port is disabled).</summary>
    public const int NotAvailable = -33301;

    public const int UnknownSession = -33302;

    public const int UnknownTrace = -33303;

    /// <summary>The trace couldn't be read or recorded; the message says why.</summary>
    public const int TraceError = -33304;
}

/// <summary>Kinds of counter providers.</summary>
public static class CounterProviderKinds
{
    /// <summary>An EventSource's EventCounters (<c>System.Runtime</c>, a library's own).</summary>
    public const string EventCounters = "eventCounters";

    /// <summary>A <c>System.Diagnostics.Metrics</c> meter.</summary>
    public const string Meter = "meter";
}

/// <summary>Kinds of counter values.</summary>
public static class CounterValueKinds
{
    /// <summary>An EventCounter's mean over the interval, or a gauge's last value.</summary>
    public const string Gauge = "gauge";

    /// <summary>A change over the interval (an incrementing counter, a meter's counter).</summary>
    public const string Rate = "rate";

    /// <summary>A histogram's percentile (the name ends with its percentile, as in <c>P95</c>).</summary>
    public const string Percentile = "percentile";
}

/// <summary>Kinds of runtime events.</summary>
public static class RuntimeEventKinds
{
    public const string GarbageCollection = "gc";
    public const string Exception = "exception";
}

public sealed record DiagnosticsInitializeParams(int ProcessId, string ProtocolVersion);

public sealed record DiagnosticsInitializeResult(string Name, string Version, string ProtocolVersion);

public sealed record DiagnosticsEmpty;

public sealed record ProcessIdParams(int ProcessId);

/// <summary>A process with a diagnostics port. <c>CommandLine</c> is known where the OS shows it (Linux), else null.</summary>
public sealed record DotnetProcess(int Id, string Name, string? CommandLine);

public sealed record ProcessesResult(IReadOnlyList<DotnetProcess> Processes);

/// <summary>A counter provider: an EventSource's counters or a meter; <c>Counters</c> limits it to these names (null: all).</summary>
public sealed record CounterProvider(string Name, string Kind, IReadOnlyList<string>? Counters = null);

public sealed record CountersStartParams(string Session, int ProcessId, IReadOnlyList<CounterProvider> Providers, double IntervalSeconds, bool RuntimeEvents);

public sealed record SessionStartResult(string ProcessName);

public sealed record SessionParams(string Session);

/// <summary>One counter's value at an interval. <c>Tags</c> are a meter measurement's dimensions (<c>k=v,k=v</c>).</summary>
public sealed record CounterValue(string Provider, string Name, string DisplayName, string? Unit, double Value, string Kind, string? Tags = null);

/// <summary>Counter values that arrived since the last update; <c>At</c> is seconds since the session started.</summary>
public sealed record CountersUpdate(string Session, double At, IReadOnlyList<CounterValue> Values);

/// <summary>A garbage collection (with its generation, reason and pause) or a first-chance exception (type and message).</summary>
public sealed record RuntimeEvent(double At, string Kind, string Text, int? Generation = null, double? DurationMilliseconds = null, string? Type = null);

public sealed record RuntimeEventsUpdate(string Session, IReadOnlyList<RuntimeEvent> Events);

public sealed record SessionEndedParams(string Session, string? Error);

/// <summary>Records a CPU sampling trace (EventPipe's sample profiler with the runtime's method events) to <c>OutputPath</c>.</summary>
public sealed record TraceStartParams(string Session, int ProcessId, string OutputPath);

public sealed record TraceStopResult(string Path, long Bytes, double DurationSeconds);

public sealed record TraceOpenParams(string Path);

public sealed record TraceIdParams(string Trace);

public sealed record ThreadSummary(int Id, string Name, int Samples);

/// <summary>
/// An opened trace. <c>Samples</c> counts the samples in managed code (the CPU view); <c>ExternalSamples</c> those in
/// native code or waiting, which the filter's <c>IncludeExternal</c> adds.
/// </summary>
public sealed record TraceSummary(
    string Trace,
    string Path,
    string? ProcessName,
    int? ProcessId,
    double DurationMilliseconds,
    int Samples,
    int ExternalSamples,
    int Functions,
    IReadOnlyList<ThreadSummary> Threads);

/// <summary>Which samples a view counts: a time range (milliseconds from the trace's start), threads, and native ones.</summary>
public sealed record CpuFilter(double? StartMilliseconds = null, double? EndMilliseconds = null, IReadOnlyList<int>? Threads = null, bool IncludeExternal = false);

/// <summary>Children of a top-down call tree node (<c>Node</c> -1: the roots), <c>Count</c> at a time from <c>Offset</c>.</summary>
public sealed record CpuTreeParams(string Trace, CpuFilter Filter, int Node, int Offset, int Count);

/// <summary>
/// A call tree node. Top-down: <c>Id</c> is the node. Bottom-up: <c>Id</c> is the function, and the node is named by
/// its path of function ids from the hot function up through its callers.
/// </summary>
public sealed record CpuNode(int Id, int Function, string Name, string? Module, int Inclusive, int Exclusive, bool HasChildren);

/// <summary>A page of nodes. <c>Total</c>: the samples the filter counts (100%); <c>More</c>: the nodes after this page.</summary>
public sealed record CpuTreeResult(int Total, IReadOnlyList<CpuNode> Nodes, int More);

/// <summary>Bottom-up: the callers of a path (empty <c>Path</c>: the functions with self time, the hottest first).</summary>
public sealed record CpuCallersParams(string Trace, CpuFilter Filter, IReadOnlyList<int> Path, int Offset, int Count);

public sealed record CpuFunctionsParams(string Trace, CpuFilter Filter, string? Search, int Offset, int Count);

/// <summary>A function's totals; <c>File</c> and <c>Line</c> are its hottest source line once symbols are read.</summary>
public sealed record CpuFunction(int Id, string Name, string? Module, int Inclusive, int Exclusive, string? File = null, int? Line = null);

public sealed record CpuFunctionsResult(int Total, IReadOnlyList<CpuFunction> Functions, int More);

public sealed record CpuCallerCalleeParams(string Trace, CpuFilter Filter, int Function);

public sealed record CpuCallerCalleeResult(int Total, CpuFunction Function, IReadOnlyList<CpuFunction> Callers, IReadOnlyList<CpuFunction> Callees);

/// <summary>
/// The flame graph under a top-down node (<c>Root</c> -1: everything), without frames narrower than
/// <c>MinFraction</c> of it and at most <c>MaxFrames</c>.
/// </summary>
public sealed record CpuFlameParams(string Trace, CpuFilter Filter, int Root, double MinFraction, int MaxFrames);

/// <summary>A flame graph frame: <c>Start</c> and <c>Width</c> in samples from the root's left edge, <c>Depth</c> from the root (0).</summary>
public sealed record FlameFrame(int Node, int Function, string Name, int Depth, int Start, int Width, int Self);

/// <summary><c>Path</c>: the nodes from the top down to <c>Root</c> (the zoom's breadcrumb); <c>Truncated</c>: narrower frames were left out.</summary>
public sealed record CpuFlameResult(int Total, IReadOnlyList<FlameFrame> Frames, IReadOnlyList<CpuNode> Path, bool Truncated);

/// <summary>The samples over time (the filter's threads; its time range is ignored), in <c>Buckets</c> equal buckets.</summary>
public sealed record CpuTimelineParams(string Trace, CpuFilter Filter, int Buckets);

/// <summary><c>Samples</c> per bucket; a sample is a thread seen once by the sampler (every millisecond by default).</summary>
public sealed record CpuTimelineResult(double DurationMilliseconds, double BucketMilliseconds, IReadOnlyList<int> Samples);

public sealed record CpuFilesParams(string Trace, CpuFilter Filter);

public sealed record CpuFile(string Path, int Inclusive);

/// <summary>Source files with samples, the hottest first; <c>Resolved</c> is false while symbols are still being read.</summary>
public sealed record CpuFilesResult(int Total, IReadOnlyList<CpuFile> Files, bool Resolved);

public sealed record CpuLinesParams(string Trace, CpuFilter Filter, string File);

/// <summary>A line's samples: on the stack (inclusive) and running there (exclusive).</summary>
public sealed record CpuLine(int Line, int Inclusive, int Exclusive);

public sealed record CpuLinesResult(int Total, IReadOnlyList<CpuLine> Lines);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DiagnosticsInitializeParams))]
[JsonSerializable(typeof(DiagnosticsInitializeResult))]
[JsonSerializable(typeof(DiagnosticsEmpty))]
[JsonSerializable(typeof(ProcessIdParams))]
[JsonSerializable(typeof(ProcessesResult))]
[JsonSerializable(typeof(CountersStartParams))]
[JsonSerializable(typeof(SessionStartResult))]
[JsonSerializable(typeof(SessionParams))]
[JsonSerializable(typeof(CountersUpdate))]
[JsonSerializable(typeof(RuntimeEventsUpdate))]
[JsonSerializable(typeof(SessionEndedParams))]
[JsonSerializable(typeof(TraceStartParams))]
[JsonSerializable(typeof(TraceStopResult))]
[JsonSerializable(typeof(TraceOpenParams))]
[JsonSerializable(typeof(TraceIdParams))]
[JsonSerializable(typeof(TraceSummary))]
[JsonSerializable(typeof(CpuTreeParams))]
[JsonSerializable(typeof(CpuTreeResult))]
[JsonSerializable(typeof(CpuCallersParams))]
[JsonSerializable(typeof(CpuFunctionsParams))]
[JsonSerializable(typeof(CpuFunctionsResult))]
[JsonSerializable(typeof(CpuCallerCalleeParams))]
[JsonSerializable(typeof(CpuCallerCalleeResult))]
[JsonSerializable(typeof(CpuFlameParams))]
[JsonSerializable(typeof(CpuFlameResult))]
[JsonSerializable(typeof(CpuTimelineParams))]
[JsonSerializable(typeof(CpuTimelineResult))]
[JsonSerializable(typeof(CpuFilesParams))]
[JsonSerializable(typeof(CpuFilesResult))]
[JsonSerializable(typeof(CpuLinesParams))]
[JsonSerializable(typeof(CpuLinesResult))]
public sealed partial class DiagnosticsJsonContext : JsonSerializerContext;
