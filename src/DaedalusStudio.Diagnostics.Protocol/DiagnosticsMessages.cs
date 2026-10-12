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

    /// <summary>Two traces' functions side by side (comparing sessions).</summary>
    public const string CpuCompare = "diag/cpu/compare";

    /// <summary>Async methods and tasks from the TPL's events, the longest first.</summary>
    public const string AsyncTasks = "diag/async/tasks";

    /// <summary>Each thread's state over time, and the thread pool's injections.</summary>
    public const string Threads = "diag/threads";

    /// <summary>First-chance exceptions grouped by type and throw site.</summary>
    public const string Exceptions = "diag/exceptions";

    /// <summary>HTTP requests, database commands or <c>Activity</c> spans.</summary>
    public const string Activities = "diag/activities";

    /// <summary>The process's read and write activity over time.</summary>
    public const string FileIo = "diag/fileio";

    /// <summary>The events viewer: counts by provider and event, and a page of events.</summary>
    public const string EventStats = "diag/events/stats";
    public const string Events = "diag/events/list";

    /// <summary>Writes a profile as Speedscope or Chromium trace JSON.</summary>
    public const string Export = "diag/trace/export";

    public const string HeapSnapshot = "diag/heap/snapshot";
    public const string HeapOpen = "diag/heap/open";
    public const string HeapClose = "diag/heap/close";
    public const string HeapTypes = "diag/heap/types";
    public const string HeapRoots = "diag/heap/roots";
}

/// <summary>What a trace recording collects (<see cref="TraceStartParams.Tools"/>).</summary>
public static class TraceTools
{
    /// <summary>EventPipe's sample profiler (CPU Usage).</summary>
    public const string Cpu = "cpu";

    /// <summary>The runtime's allocation ticks (about one per 100 KB allocated), with their stacks and types.</summary>
    public const string Allocations = "allocations";

    /// <summary>The TPL's task and async-method events (.NET Async).</summary>
    public const string Async = "async";

    /// <summary>Lock contention, the thread pool's events and thread lifetimes (Threads and Contention).</summary>
    public const string Threads = "threads";

    /// <summary>First-chance exceptions with their stacks.</summary>
    public const string Exceptions = "exceptions";

    /// <summary>The process's I/O counters sampled four times a second (what the OS gives).</summary>
    public const string FileIo = "fileio";

    /// <summary>HTTP requests (System.Net.Http), DiagnosticSource database commands and <c>ActivitySource</c> spans.</summary>
    public const string Activities = "activities";

    public static readonly IReadOnlyList<string> All = [Cpu, Allocations, Async, Threads, Exceptions, FileIo, Activities];
}

/// <summary>The stack profiles a trace can hold (<see cref="CpuFilter.Profile"/>); each is viewed like the CPU's.</summary>
public static class ProfileKinds
{
    /// <summary>CPU samples (one a millisecond per thread).</summary>
    public const string Cpu = "cpu";

    /// <summary>Allocation ticks weighed in KB, with the allocated type as the leaf frame.</summary>
    public const string Allocations = "allocations";

    /// <summary>Lock contention weighed in microseconds waited, with a "Lock contention" leaf frame.</summary>
    public const string Contention = "contention";

    /// <summary>First-chance exceptions, one each, with the exception's type as the leaf frame.</summary>
    public const string Exceptions = "exceptions";
}

/// <summary>Kinds of activities (<see cref="ActivitiesParams.Kind"/>).</summary>
public static class ActivityKinds
{
    public const string Http = "http";
    public const string Database = "database";
    public const string Span = "span";
}

/// <summary>Export formats (<see cref="ExportParams.Format"/>).</summary>
public static class ExportFormats
{
    /// <summary>Speedscope's file format (a sampled profile per thread).</summary>
    public const string Speedscope = "speedscope";

    /// <summary>The Chromium trace event format (Perfetto, chrome://tracing, Edge's performance tool).</summary>
    public const string Chromium = "chromium";
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

/// <summary>
/// Records a trace to <c>OutputPath</c>: the <c>Tools</c> named (<see cref="TraceTools"/>; null: CPU only) with the
/// runtime's method events for names, plus any other EventSource or EventPipe <c>Providers</c> (the events viewer).
/// </summary>
public sealed record TraceStartParams(string Session, int ProcessId, string OutputPath, IReadOnlyList<string>? Tools = null, IReadOnlyList<TraceProvider>? Providers = null);

/// <summary>An extra provider for a trace: an EventSource or EventPipe provider by name, its keywords, level (0–5) and arguments.</summary>
public sealed record TraceProvider(string Name, long Keywords = -1, int Level = 5, IReadOnlyDictionary<string, string>? Arguments = null);

public sealed record TraceStopResult(string Path, long Bytes, double DurationSeconds);

public sealed record TraceOpenParams(string Path);

public sealed record TraceIdParams(string Trace);

public sealed record ThreadSummary(int Id, string Name, int Samples);

/// <summary>A stack profile of a trace (<see cref="ProfileKinds"/>): <c>Total</c> in its <c>Unit</c>, from <c>Events</c> events.</summary>
public sealed record ProfileSummary(string Kind, string Unit, long Total, int Events);

/// <summary>
/// An opened trace. <c>Samples</c> counts the samples in managed code (the CPU view); <c>ExternalSamples</c> those in
/// native code or waiting, which the filter's <c>IncludeExternal</c> adds. <c>Profiles</c>: the stack profiles that have
/// events; the counts say what the other views have (tasks, spans, requests, commands, thread pool events, events).
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
    IReadOnlyList<ThreadSummary> Threads,
    IReadOnlyList<ProfileSummary>? Profiles = null,
    int Events = 0,
    int Tasks = 0,
    int Requests = 0,
    int Commands = 0,
    int Spans = 0,
    int ThreadPoolEvents = 0,
    bool FileIo = false);

/// <summary>
/// Which samples a view counts: a time range (milliseconds from the trace's start), threads, and native ones; and
/// which stack profile (<see cref="ProfileKinds"/>; null: the CPU's).
/// </summary>
public sealed record CpuFilter(double? StartMilliseconds = null, double? EndMilliseconds = null, IReadOnlyList<int>? Threads = null, bool IncludeExternal = false, string? Profile = null);

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

// ------------------------------------------------------------------ comparing traces

/// <summary>A trace's functions against a baseline's (both open), matched by name and module.</summary>
public sealed record CpuCompareParams(string Baseline, string Trace, CpuFilter Filter, int Count);

/// <summary>A function's self and total shares (0 to 1) in the baseline and the trace; <c>Delta</c>: the change of its total share.</summary>
public sealed record CpuCompareRow(string Name, string? Module, double BaselineSelf, double BaselineTotal, double Self, double Total, double Delta);

/// <summary>The rows with the largest changes first.</summary>
public sealed record CpuCompareResult(long BaselineTotal, long Total, IReadOnlyList<CpuCompareRow> Functions);

// ------------------------------------------------------------------ .NET Async

/// <summary>Tasks and async methods (<c>Search</c> in their names), the longest first, a page at a time; the filter's time range picks those that overlap it.</summary>
public sealed record AsyncTasksParams(string Trace, CpuFilter Filter, string? Search, int Offset, int Count);

/// <summary>
/// A task or async method from the TPL's events: its name (<c>Async: …</c> for async methods), the method it runs
/// (from the stack, when it shows), when it started and how long it ran (milliseconds), the thread that started it,
/// how it ended (null: still running when the trace ended), and the stack it was created on (the first frames).
/// </summary>
public sealed record AsyncTask(int Id, string Name, string? Method, double Start, double Duration, int Thread, string? Status, IReadOnlyList<string> Stack);

public sealed record AsyncTasksResult(int Total, double DurationMilliseconds, IReadOnlyList<AsyncTask> Tasks, int More);

// ------------------------------------------------------------------ threads

public sealed record ThreadsParams(string Trace, int Buckets);

/// <summary>
/// A thread's states in buckets (one character each: <c>R</c> running managed code, <c>W</c> waiting or in native
/// code, <c>C</c> blocked on a lock, a space when it wasn't seen), and its milliseconds in each.
/// </summary>
public sealed record ThreadTimeline(int Id, string Name, string States, double Running, double Waiting, double Contention);

/// <summary>A thread pool event: an injection or retirement (<c>Reason</c>, as the runtime names it) with the new worker count.</summary>
public sealed record ThreadPoolEvent(double At, string Kind, int Workers, string? Reason);

public sealed record ThreadsResult(double DurationMilliseconds, double BucketMilliseconds, IReadOnlyList<ThreadTimeline> Threads, IReadOnlyList<ThreadPoolEvent> Pool);

// ------------------------------------------------------------------ exceptions

public sealed record ExceptionsParams(string Trace, CpuFilter Filter);

/// <summary>Exceptions of one type thrown at one site (the function that threw), with its line once symbols are read.</summary>
public sealed record ExceptionGroup(string Type, string ThrowSite, int Count, string? Message, string? File, int? Line);

public sealed record ExceptionEvent(double At, string Type, string? Message, int Thread);

/// <summary>The groups, the most frequent first, and the last exceptions (at most 500).</summary>
public sealed record ExceptionsResult(int Total, IReadOnlyList<ExceptionGroup> Groups, IReadOnlyList<ExceptionEvent> Recent);

// ------------------------------------------------------------------ activities

/// <summary><c>Kind</c>: <see cref="ActivityKinds"/>; the longest first unless <c>ByTime</c>.</summary>
public sealed record ActivitiesParams(string Trace, string Kind, CpuFilter Filter, bool ByTime, int Offset, int Count);

/// <summary>
/// A request, command or span: when it started and how long it took (milliseconds), its name (the request line, the
/// command's text, the span's name), detail (status code, tags), its span ids and depth in its trace (spans).
/// </summary>
public sealed record ActivityItem(double Start, double Duration, string Name, string? Detail, string? Status, string? Source, string? Id, string? Parent, int Depth, bool Failed);

public sealed record ActivitiesResult(int Total, double DurationMilliseconds, IReadOnlyList<ActivityItem> Items, int More);

// ------------------------------------------------------------------ file I/O

/// <summary>I/O counters over an interval: bytes and (where the OS counts them) operations, read and written.</summary>
public sealed record IoSample(double At, long ReadBytes, long WriteBytes, long? ReadOperations, long? WriteOperations);

/// <summary>The I/O counters recorded with a trace (a sidecar <c>.io.json</c>); <c>Source</c> says what the OS counts.</summary>
public sealed record IoRecording(string Platform, string Source, IReadOnlyList<IoSample> Samples);

public sealed record FileIoResult(bool Available, string Platform, string Source, IReadOnlyList<IoSample> Samples, long ReadBytes, long WriteBytes, long? ReadOperations, long? WriteOperations);

// ------------------------------------------------------------------ the events viewer

/// <summary>Event counts by provider and name.</summary>
public sealed record EventCount(string Provider, string Name, int Count);

public sealed record EventStatsResult(int Total, IReadOnlyList<EventCount> Events);

/// <summary>
/// Which events to list: by provider and event name (null: any), text in the payload, a time range and threads.
/// </summary>
public sealed record EventFilter(IReadOnlyList<string>? Providers = null, IReadOnlyList<string>? Names = null, string? Text = null, double? StartMilliseconds = null, double? EndMilliseconds = null, IReadOnlyList<int>? Threads = null);

public sealed record EventsParams(string Trace, EventFilter Filter, int Offset, int Count);

/// <summary>An event: its index in the trace, time, provider, name, thread, process and payload (<c>name=value</c> pairs).</summary>
public sealed record TraceEventRow(int Index, double At, string Provider, string Name, int Thread, string Payload);

public sealed record EventsResult(int Total, IReadOnlyList<TraceEventRow> Events, int More);

/// <summary>Writes the filter's samples of a profile as <c>Format</c> (<see cref="ExportFormats"/>) to <c>OutputPath</c>.</summary>
public sealed record ExportParams(string Trace, CpuFilter Filter, string Format, string OutputPath);

public sealed record ExportResult(string Path, long Bytes);

// ------------------------------------------------------------------ memory

/// <summary>A GC heap snapshot of a process (the way dotnet-gcdump takes one: an induced full GC that reports the heap), recorded to <c>OutputPath</c>.</summary>
public sealed record HeapSnapshotParams(int ProcessId, string OutputPath);

public sealed record HeapSnapshotResult(string Path, long Bytes);

public sealed record HeapOpenParams(string Path);

/// <summary>An opened snapshot: its objects, their bytes, the types and the roots.</summary>
public sealed record HeapSummary(string Snapshot, string Path, int Objects, long Bytes, int Types, int Roots);

public sealed record HeapIdParams(string Snapshot);

/// <summary>Types by size, or, against a <c>Baseline</c> snapshot (also open), by growth; <c>Search</c> in the names.</summary>
public sealed record HeapTypesParams(string Snapshot, string? Baseline, string? Search, int Offset, int Count);

/// <summary>A type's objects and bytes; against a baseline, the changes.</summary>
public sealed record HeapType(int Id, string Name, int Count, long Bytes, int? CountDelta = null, long? BytesDelta = null);

public sealed record HeapTypesResult(int Objects, long Bytes, IReadOnlyList<HeapType> Types, int More);

/// <summary>
/// Paths to roots: what keeps a type's objects alive. <c>Path</c> holds type ids from the type up through its
/// referrers; the result is the next referrers (by the first path found from a root to each object).
/// </summary>
public sealed record HeapRootsParams(string Snapshot, IReadOnlyList<int> Path, int Offset, int Count);

/// <summary>
/// A referrer of the path's objects: a type (<c>Id</c> ≥ 0) or a root (<c>Id</c> &lt; 0: <c>Name</c> says which
/// kind), with the count and bytes of the type's objects held through it.
/// </summary>
public sealed record HeapReferrer(int Id, string Name, int Count, long Bytes, bool HasChildren);

public sealed record HeapRootsResult(IReadOnlyList<HeapReferrer> Referrers, int More);

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
[JsonSerializable(typeof(CpuCompareParams))]
[JsonSerializable(typeof(CpuCompareResult))]
[JsonSerializable(typeof(AsyncTasksParams))]
[JsonSerializable(typeof(AsyncTasksResult))]
[JsonSerializable(typeof(ThreadsParams))]
[JsonSerializable(typeof(ThreadsResult))]
[JsonSerializable(typeof(ExceptionsParams))]
[JsonSerializable(typeof(ExceptionsResult))]
[JsonSerializable(typeof(ActivitiesParams))]
[JsonSerializable(typeof(ActivitiesResult))]
[JsonSerializable(typeof(IoRecording))]
[JsonSerializable(typeof(FileIoResult))]
[JsonSerializable(typeof(EventStatsResult))]
[JsonSerializable(typeof(EventsParams))]
[JsonSerializable(typeof(EventsResult))]
[JsonSerializable(typeof(ExportParams))]
[JsonSerializable(typeof(ExportResult))]
[JsonSerializable(typeof(HeapSnapshotParams))]
[JsonSerializable(typeof(HeapSnapshotResult))]
[JsonSerializable(typeof(HeapOpenParams))]
[JsonSerializable(typeof(HeapSummary))]
[JsonSerializable(typeof(HeapIdParams))]
[JsonSerializable(typeof(HeapTypesParams))]
[JsonSerializable(typeof(HeapTypesResult))]
[JsonSerializable(typeof(HeapRootsParams))]
[JsonSerializable(typeof(HeapRootsResult))]
public sealed partial class DiagnosticsJsonContext : JsonSerializerContext;
