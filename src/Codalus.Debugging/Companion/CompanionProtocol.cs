using System.Text.Json.Serialization;

namespace Codalus.Debugging.Companion;

/// <summary>
/// The debug companion host's requests (M14b, ADR 0032): what netcoredbg can't do. JSON-RPC over stdio, like the other
/// hosts; method names carry the <c>codalus/</c> prefix (docs/protocol.md).
/// </summary>
public static class CompanionMethods
{
    /// <summary>The debugger attributes of a type, read from the debuggee's assemblies' metadata.</summary>
    public const string TypeDisplay = "codalus/debug/typeDisplay";

    /// <summary>Opens a dump (a Windows minidump or a Linux or macOS core dump) with ClrMD.</summary>
    public const string OpenDump = "codalus/debug/openDump";

    /// <summary>The managed heap's types by total size.</summary>
    public const string DumpHeap = "codalus/debug/dumpHeap";

    /// <summary>The addresses of a type's objects.</summary>
    public const string DumpInstances = "codalus/debug/dumpInstances";

    /// <summary>An object's fields.</summary>
    public const string DumpObject = "codalus/debug/dumpObject";

    public const string CloseDump = "codalus/debug/closeDump";

    /// <summary>Writes a dump of a running .NET process (DiagnosticsClient).</summary>
    public const string CreateDump = "codalus/debug/createDump";
}

public sealed record TypeDisplayParams(string TypeName, IReadOnlyList<string> Modules);

/// <summary>
/// What the attributes on a type (or its nearest base type that has them) say: <c>[DebuggerDisplay]</c>'s value and name
/// strings, <c>[DebuggerTypeProxy]</c>'s type, and the members <c>[DebuggerBrowsable]</c> hides or flattens.
/// </summary>
/// <remarks>
/// <see cref="Members"/> lists the type's fields, properties and events and its base types', most derived first (null
/// when the type isn't in the modules): what the data tip groups and draws by (D44).
/// </remarks>
public sealed record TypeDisplayResult(
    string? Display,
    string? Name,
    string? TypeProxy,
    IReadOnlyList<string> Hidden,
    IReadOnlyList<string> RootHidden,
    string? Module,
    IReadOnlyList<TypeMember>? Members = null);

/// <summary>
/// A member of a type or of one of its base types (D44). <see cref="Kind"/>: <c>field</c>, <c>property</c>, <c>event</c>,
/// <c>constant</c> or <c>indexer</c> (netcoredbg lists one by its name, with the exception its call without arguments
/// throws). An explicit interface implementation is named as the metadata names it
/// (<c>System.Collections.ICollection.IsSynchronized</c>), as netcoredbg does. <see cref="Access"/>: <c>public</c>, <c>private</c>, <c>protected</c>, <c>internal</c>,
/// <c>protected internal</c> or <c>private protected</c> (a property's most accessible accessor's).
/// <see cref="DeclaringType"/> and <see cref="Module"/> say whose it is (the solution's own code, or a library's);
/// <see cref="CanWrite"/> is false for a property without a setter, a read-only field and a constant; <see cref="Type"/>
/// is its declared type.
/// </summary>
public sealed record TypeMember(string Name, string Kind, string Access, bool IsStatic, string DeclaringType, string? Module, bool CanWrite, string? Type);

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
