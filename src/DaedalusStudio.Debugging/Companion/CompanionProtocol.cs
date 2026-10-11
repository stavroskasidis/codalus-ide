using System.Text.Json.Serialization;

namespace DaedalusStudio.Debugging.Companion;

/// <summary>
/// The debug companion host's requests (M14b, ADR 0032): what netcoredbg can't do. JSON-RPC over stdio, like the other
/// hosts; method names carry the <c>daedalus/</c> prefix (docs/protocol.md).
/// </summary>
public static class CompanionMethods
{
    /// <summary>The debugger attributes of a type, read from the debuggee's assemblies' metadata.</summary>
    public const string TypeDisplay = "daedalus/debug/typeDisplay";

    /// <summary>Opens a dump (a Windows minidump or a Linux or macOS core dump) with ClrMD.</summary>
    public const string OpenDump = "daedalus/debug/openDump";

    /// <summary>The managed heap's types by total size.</summary>
    public const string DumpHeap = "daedalus/debug/dumpHeap";

    /// <summary>The addresses of a type's objects.</summary>
    public const string DumpInstances = "daedalus/debug/dumpInstances";

    /// <summary>An object's fields.</summary>
    public const string DumpObject = "daedalus/debug/dumpObject";

    public const string CloseDump = "daedalus/debug/closeDump";

    /// <summary>Writes a dump of a running .NET process (DiagnosticsClient).</summary>
    public const string CreateDump = "daedalus/debug/createDump";
}

public sealed record TypeDisplayParams(string TypeName, IReadOnlyList<string> Modules);

/// <summary>
/// What the attributes on a type (or its nearest base type that has them) say: <c>[DebuggerDisplay]</c>'s value and name
/// strings, <c>[DebuggerTypeProxy]</c>'s type, and the members <c>[DebuggerBrowsable]</c> hides or flattens.
/// </summary>
public sealed record TypeDisplayResult(
    string? Display,
    string? Name,
    string? TypeProxy,
    IReadOnlyList<string> Hidden,
    IReadOnlyList<string> RootHidden,
    string? Module);

public sealed record OpenDumpParams(string Path);

public sealed record DumpFrame(string Method, string Module, int IlOffset, string? File, int? Line);

public sealed record DumpThread(int ManagedId, uint OsId, bool IsFinalizer, string? Exception, IReadOnlyList<DumpFrame> Frames);

public sealed record DumpSummary(
    string Id,
    string Path,
    string Runtime,
    string Architecture,
    int? ProcessId,
    IReadOnlyList<string> Modules,
    IReadOnlyList<DumpThread> Threads);

public sealed record DumpHeapParams(string Id, int Top);

public sealed record DumpHeapType(string Type, long Count, long Size, ulong MethodTable);

public sealed record DumpHeapResult(long TotalObjects, long TotalSize, IReadOnlyList<DumpHeapType> Types);

public sealed record DumpInstancesParams(string Id, ulong MethodTable, int Max);

public sealed record DumpObjectParams(string Id, ulong Address);

public sealed record DumpField(string Name, string Type, string Value, ulong Address);

public sealed record DumpObject(ulong Address, string Type, long Size, string? Value, IReadOnlyList<DumpField> Fields);

public sealed record CloseDumpParams(string Id);

/// <summary><c>Kind</c>: mini, heap, triage or full (DiagnosticsClient's dump types).</summary>
public sealed record CreateDumpParams(int ProcessId, string Path, string Kind);

public sealed record CreateDumpResult(string Path, long Size);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TypeDisplayParams))]
[JsonSerializable(typeof(TypeDisplayResult))]
[JsonSerializable(typeof(OpenDumpParams))]
[JsonSerializable(typeof(DumpSummary))]
[JsonSerializable(typeof(DumpHeapParams))]
[JsonSerializable(typeof(DumpHeapResult))]
[JsonSerializable(typeof(DumpInstancesParams))]
[JsonSerializable(typeof(ulong[]))]
[JsonSerializable(typeof(DumpObjectParams))]
[JsonSerializable(typeof(DumpObject))]
[JsonSerializable(typeof(CloseDumpParams))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(CreateDumpParams))]
[JsonSerializable(typeof(CreateDumpResult))]
public sealed partial class CompanionJsonContext : JsonSerializerContext;
