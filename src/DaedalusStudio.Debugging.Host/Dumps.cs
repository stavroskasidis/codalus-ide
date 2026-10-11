using System.Collections.Concurrent;
using System.Globalization;
using DaedalusStudio.Debugging.Companion;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;

namespace DaedalusStudio.Debugging.Host;

/// <summary>
/// Dump sessions (M14b): a dump opened with ClrMD, read-only — threads and their managed stacks (with source lines from
/// the modules' portable PDBs when they are on this machine), each thread's exception, heap statistics and objects.
/// </summary>
internal sealed class Dumps : IDisposable
{
    private const int MaxFrames = 200;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly SourceLines _lines = new();
    private int _next;

    public DumpSummary Open(string path)
    {
        var target = DataTarget.LoadDump(path);
        try
        {
            var clr = target.ClrVersions.FirstOrDefault() ?? throw new InvalidOperationException("The dump has no .NET runtime in it.");
            var runtime = clr.CreateRuntime();
            var id = Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture);
            _sessions[id] = new Session(target, runtime);
            var threads = runtime.Threads.Where(t => t.IsAlive).Select(Thread).ToList();
            return new DumpSummary(
                id,
                path,
                $".NET {RuntimeVersion(clr)}",
                target.DataReader.Architecture.ToString(),
                target.DataReader.ProcessId is var pid && pid != 0 ? pid : null,
                [.. runtime.EnumerateModules().Select(m => m.Name ?? "?").Distinct()],
                threads);
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The runtime's version. ClrMD reads it from the module's version resource, which ELF and Mach-O modules don't
    /// have (0.0.0.0): then it is the name of the shared framework folder the runtime was loaded from
    /// (<c>…/shared/Microsoft.NETCore.App/10.0.12/libcoreclr.so</c>).
    /// </summary>
    internal static string RuntimeVersion(ClrInfo clr) =>
        clr.Version.Major > 0 ? clr.Version.ToString()
        : FolderVersion(clr.ModuleInfo.FileName) ?? clr.Version.ToString();

    internal static string? FolderVersion(string? runtimePath) =>
        runtimePath is null ? null
        : Path.GetFileName(Path.GetDirectoryName(runtimePath.Replace('\\', '/'))) is { } folder && Version.TryParse(folder.Split('-')[0], out _) ? folder
        : null;

    private DumpThread Thread(ClrThread thread)
    {
        var frames = new List<DumpFrame>();
        var leaf = true;
        foreach (var frame in thread.EnumerateStackTrace().Take(MaxFrames))
        {
            var isLeaf = leaf;
            leaf = false;
            if (frame.Method is not { } method)
            {
                continue; // runtime frames (transitions) have no method
            }

            var ilOffset = IlOffset(method, frame.InstructionPointer, isLeaf);
            var module = method.Type?.Module?.Name;
            var (file, line) = module is null ? (null, null) : _lines.Find(module, method.MetadataToken, ilOffset);
            frames.Add(new DumpFrame(method.Signature ?? $"{method.Type?.Name}.{method.Name}", Path.GetFileName(module ?? "?"), ilOffset, file, line));
        }

        var exception = thread.CurrentException is { } current ? $"{current.Type?.Name}: {current.Message}" : null;
        return new DumpThread(thread.ManagedThreadId, thread.OSThreadId, thread.IsFinalizer, exception, frames);
    }

    /// <summary>
    /// The IL offset of a frame, from the method's IL-to-native map; -1 when the dump doesn't have the map (a dump without
    /// the JIT's data, such as a heap dump: ClrMD's own lookup then answers 0, a wrong line that looks right). A caller's
    /// instruction pointer is the return address, just past its call, so the call is found one byte before it; prolog and
    /// unmapped entries (negative offsets) don't count. The leaf frame (the walk's first, when it is managed) is where the
    /// thread was interrupted, a breakpoint or a fault: its instruction pointer is the current instruction. A thread in
    /// native code starts with a transition frame instead, and its first managed frame holds a return address.
    /// </summary>
    internal static int IlOffset(ClrMethod method, ulong instructionPointer, bool leaf = false)
    {
        var target = leaf ? instructionPointer : instructionPointer - 1;
        var best = -1;
        ulong bestStart = 0;
        foreach (var entry in method.ILOffsetMap)
        {
            if (entry.ILOffset < 0)
            {
                continue;
            }

            // End addresses are exclusive: each range ends where the next one starts.
            if (entry.StartAddress <= target && target < entry.EndAddress)
            {
                return entry.ILOffset;
            }

            // Otherwise the nearest mapped range before the address.
            if (entry.StartAddress <= target && entry.StartAddress >= bestStart)
            {
                (best, bestStart) = (entry.ILOffset, entry.StartAddress);
            }
        }

        return best;
    }

    public DumpHeapResult Heap(string id, int top)
    {
        var heap = Get(id).Runtime.Heap;
        var types = new Dictionary<ulong, (string Name, long Count, long Size)>();
        long objects = 0, size = 0;
        foreach (var obj in heap.EnumerateObjects())
        {
            if (!obj.IsValid || obj.Type is not { } type)
            {
                continue;
            }

            objects++;
            size += (long)obj.Size;
            types[type.MethodTable] = types.TryGetValue(type.MethodTable, out var known)
                ? (known.Name, known.Count + 1, known.Size + (long)obj.Size)
                : (type.Name ?? "?", 1, (long)obj.Size);
        }

        return new DumpHeapResult(objects, size, [.. types.OrderByDescending(t => t.Value.Size).Take(top)
            .Select(t => new DumpHeapType(t.Value.Name, t.Value.Count, t.Value.Size, t.Key))]);
    }

    public ulong[] Instances(string id, ulong methodTable, int max) =>
        [.. Get(id).Runtime.Heap.EnumerateObjects().Where(o => o.IsValid && o.Type?.MethodTable == methodTable).Take(max).Select(o => o.Address)];

    public DumpObject Object(string id, ulong address)
    {
        var heap = Get(id).Runtime.Heap;
        var obj = heap.GetObject(address);
        if (!obj.IsValid || obj.Type is not { } type)
        {
            throw new InvalidOperationException($"There is no object at 0x{address:x}.");
        }

        var fields = new List<DumpField>();
        foreach (var field in type.Fields)
        {
            fields.Add(Field(obj, field));
        }

        return new DumpObject(address, type.Name ?? "?", (long)obj.Size, type.IsString ? obj.AsString(4096) : null, fields);
    }

    private static DumpField Field(ClrObject obj, ClrInstanceField field)
    {
        var name = field.Name ?? "?";
        var type = field.Type?.Name ?? field.ElementType.ToString();
        try
        {
            if (field.IsObjectReference)
            {
                var value = field.ReadObject(obj.Address, interior: false);
                return !value.IsValid || value.IsNull
                    ? new DumpField(name, type, "null", 0)
                    : new DumpField(name, value.Type?.Name ?? type, value.Type?.IsString == true ? Quote(value.AsString(1024)) : $"{{{value.Type?.Name}}} 0x{value.Address:x}", value.Address);
            }

            var text = field.ElementType switch
            {
                ClrElementType.Boolean => field.Read<bool>(obj.Address, false) ? "true" : "false",
                ClrElementType.Char => $"'{field.Read<char>(obj.Address, false)}'",
                ClrElementType.Int8 => field.Read<sbyte>(obj.Address, false).ToString(CultureInfo.InvariantCulture),
                ClrElementType.UInt8 => field.Read<byte>(obj.Address, false).ToString(CultureInfo.InvariantCulture),
                ClrElementType.Int16 => field.Read<short>(obj.Address, false).ToString(CultureInfo.InvariantCulture),
                ClrElementType.UInt16 => field.Read<ushort>(obj.Address, false).ToString(CultureInfo.InvariantCulture),
                ClrElementType.Int32 => field.Read<int>(obj.Address, false).ToString(CultureInfo.InvariantCulture),
                ClrElementType.UInt32 => field.Read<uint>(obj.Address, false).ToString(CultureInfo.InvariantCulture),
                ClrElementType.Int64 => field.Read<long>(obj.Address, false).ToString(CultureInfo.InvariantCulture),
                ClrElementType.UInt64 => field.Read<ulong>(obj.Address, false).ToString(CultureInfo.InvariantCulture),
                ClrElementType.Float => field.Read<float>(obj.Address, false).ToString(CultureInfo.InvariantCulture),
                ClrElementType.Double => field.Read<double>(obj.Address, false).ToString(CultureInfo.InvariantCulture),
                ClrElementType.NativeInt or ClrElementType.Pointer or ClrElementType.FunctionPointer => $"0x{field.Read<nint>(obj.Address, false):x}",
                ClrElementType.NativeUInt => $"0x{field.Read<nuint>(obj.Address, false):x}",
                ClrElementType.Struct => $"{{{type}}}",
                _ => $"({field.ElementType})",
            };
            return new DumpField(name, type, text, 0);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            return new DumpField(name, type, $"<{ex.Message}>", 0);
        }
    }

    private static string Quote(string? text) => text is null ? "null" : $"\"{text}\"";

    public bool Close(string id)
    {
        if (_sessions.TryRemove(id, out var session))
        {
            session.Dispose();
            return true;
        }

        return false;
    }

    /// <summary>How long a dump may take before the request fails.</summary>
    public static readonly TimeSpan DumpTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Create Dump: a dump of a running .NET process through its diagnostics port (the runtime starts createdump). A
    /// createdump that doesn't finish in <paramref name="timeout"/> is ended: on macOS it can stall on a program the
    /// debugger holds paused.
    /// </summary>
    public static async Task<CreateDumpResult> CreateAsync(int processId, string path, string kind, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var type = kind switch
        {
            "mini" => DumpType.Normal,
            "triage" => DumpType.Triage,
            "full" => DumpType.Full,
            _ => DumpType.WithHeap,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var started = DateTime.Now.AddSeconds(-1);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await new DiagnosticsClient(processId).WriteDumpAsync(type, path, logDumpGeneration: false, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            EndCreatedump(started);
            throw new TimeoutException($"The dump wasn't written within {timeout.TotalSeconds:0} s. On macOS, continue or detach a paused program before writing its dump.");
        }

        return new CreateDumpResult(path, new FileInfo(path).Length);
    }

    /// <summary>Ends the createdump processes started for this request (none older than it).</summary>
    private static void EndCreatedump(DateTime since)
    {
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("createdump"))
        {
            using (process)
            {
                try
                {
                    if (process.StartTime >= since)
                    {
                        process.Kill();
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Gone, or not ours to inspect.
                }
            }
        }
    }

    private Session Get(string id) => _sessions.TryGetValue(id, out var session) ? session : throw new InvalidOperationException($"No dump session {id}.");

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
        _lines.Dispose();
    }

    private sealed record Session(DataTarget Target, ClrRuntime Runtime) : IDisposable
    {
        public void Dispose()
        {
            Runtime.Dispose();
            Target.Dispose();
        }
    }
}
