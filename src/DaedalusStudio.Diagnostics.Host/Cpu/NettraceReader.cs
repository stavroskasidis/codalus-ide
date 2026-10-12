using System.Globalization;
using System.Text.Json;
using DaedalusStudio.Diagnostics.Host.Trace;
using DaedalusStudio.Diagnostics.Protocol;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;

namespace DaedalusStudio.Diagnostics.Host.Cpu;

/// <summary>
/// Reads a <c>.nettrace</c> file through TraceEvent's <see cref="TraceLog"/> (which resolves JIT-compiled and
/// ReadyToRun methods from the runtime's method events and rundown) in one pass into a <see cref="TraceData"/>: the CPU
/// samples and the other stack profiles (allocation ticks, lock contention, exceptions; ADR 0046), the TPL's tasks, the
/// thread pool's events, HTTP requests, database commands and <c>Activity</c> spans, and an index of every event for
/// the events viewer. Source lines are resolved later from the modules' portable PDBs. The <c>.etlx</c> conversion goes
/// to a temporary file; the reader keeps the log open (for lines, stacks and event payloads) until disposed.
/// </summary>
internal sealed class NettraceReader : IDisposable
{
    private const string SampleProvider = "Microsoft-DotNETCore-SampleProfiler";
    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const string TplProvider = "System.Threading.Tasks.TplEventSource";
    private const string HttpProvider = "System.Net.Http";
    private const string DiagnosticSourceProvider = "Microsoft-Diagnostics-DiagnosticSource";

    private readonly TraceLog _log;
    private readonly string _etlx;
    private readonly Dictionary<long, (string File, int Line)?> _lines = [];

    private NettraceReader(TraceLog log, string etlx)
    {
        _log = log;
        _etlx = etlx;
    }

    public static (CpuProfile Profile, NettraceReader Reader) Read(string path, CancellationToken cancellationToken)
    {
        var (data, reader) = ReadAll(path, cancellationToken);
        return (data.Profile(ProfileKinds.Cpu), reader);
    }

    public static (TraceData Data, NettraceReader Reader) ReadAll(string path, CancellationToken cancellationToken)
    {
        var etlx = Path.Combine(Path.GetTempPath(), "daedalus-diag-" + Guid.NewGuid().ToString("N") + ".etlx");
        TraceLog log;
        try
        {
            TraceLog.CreateFromEventPipeDataFile(path, etlx, new TraceLogOptions { ContinueOnError = true, ConversionLog = TextWriter.Null });
            log = new TraceLog(etlx);
        }
        catch
        {
            TryDelete(etlx);
            throw;
        }

        var reader = new NettraceReader(log, etlx);
        try
        {
            var data = reader.Build(cancellationToken);
            data.Io = ReadIo(path);
            return (data, reader);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>The I/O counters recorded next to the trace (<c>name.io.json</c>), if any.</summary>
    private static IoRecording? ReadIo(string path)
    {
        var io = IoSampler.SidecarPath(path);
        try
        {
            return File.Exists(io) ? JsonSerializer.Deserialize(File.ReadAllText(io), DiagnosticsJsonContext.Default.IoRecording) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Builds one stack profile: TraceLog's call stacks become the profile's stacks once each, root first.</summary>
    private sealed class ProfileBuilder
    {
        private readonly NettraceReader _reader;
        private readonly int[] _stacks;
        private readonly int[] _methods;
        private readonly Dictionary<string, int> _unknown = new(StringComparer.Ordinal);
        private readonly Dictionary<(int Parent, string Name), int> _pseudo = [];
        private readonly List<CallStackIndex> _pending = [];

        public ProfileBuilder(NettraceReader reader, string unit)
        {
            _reader = reader;
            Profile = new CpuProfile { Unit = unit };
            _stacks = new int[Math.Max(1, reader._log.CallStacks.Count)];
            Array.Fill(_stacks, -1);
            _methods = new int[Math.Max(1, reader._log.CodeAddresses.Methods.Count)];
            Array.Fill(_methods, -1);
        }

        public CpuProfile Profile { get; }

        private int FunctionOf(CodeAddressIndex address)
        {
            var addresses = _reader._log.CodeAddresses;
            var method = addresses.MethodIndex(address);
            if (method != MethodIndex.Invalid)
            {
                ref var known = ref _methods[(int)method];
                if (known < 0)
                {
                    known = Profile.AddFunction(addresses.Methods.FullMethodName(method), ModuleName(addresses.ModuleFile(address)));
                }

                return known;
            }

            var module = ModuleName(addresses.ModuleFile(address)) ?? "?";
            if (!_unknown.TryGetValue(module, out var id))
            {
                id = Profile.AddFunction($"[{module}] ?", module);
                _unknown[module] = id;
            }

            return id;
        }

        public int StackOf(CallStackIndex index)
        {
            if (index == CallStackIndex.Invalid)
            {
                return -1;
            }

            var callStacks = _reader._log.CallStacks;
            _pending.Clear();
            var at = index;
            while (at != CallStackIndex.Invalid && _stacks[(int)at] < 0)
            {
                _pending.Add(at);
                at = callStacks.Caller(at);
            }

            var parent = at == CallStackIndex.Invalid ? -1 : _stacks[(int)at];
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                var address = callStacks.CodeAddressIndex(_pending[i]);
                parent = Profile.AddStack(parent, FunctionOf(address), (long)address);
                _stacks[(int)_pending[i]] = parent;
            }

            return parent;
        }

        /// <summary>A pseudo frame (an allocated type, an exception's type) on top of a stack; it has no source line.</summary>
        public int Leaf(int parentStack, string name, string module)
        {
            if (!_pseudo.TryGetValue((parentStack, name), out var stack))
            {
                stack = Profile.AddStack(parentStack, Profile.AddFunction(name, module), -1);
                _pseudo[(parentStack, name)] = stack;
            }

            return stack;
        }
    }

    private TraceData Build(CancellationToken cancellationToken)
    {
        var data = new TraceData(this);
        var cpu = new ProfileBuilder(this, "samples");
        var allocations = new ProfileBuilder(this, "KB");
        var contention = new ProfileBuilder(this, "µs");
        var exceptions = new ProfileBuilder(this, "exceptions");
        var pendingContention = new Dictionary<int, (double Time, CallStackIndex Stack)>();
        var tasks = new Dictionary<int, int>();
        var requests = new Dictionary<Guid, int>();
        var commands = new Dictionary<Guid, int>();

        using var source = _log.Events.GetSource();
        var sampler = new SampleProfilerTraceEventParser(source);
        var count = 0;
        sampler.ThreadSample += data2 =>
        {
            var stack = cpu.StackOf(data2.CallStackIndex());
            if (stack >= 0)
            {
                cpu.Profile.AddSample(data2.TimeStampRelativeMSec, data2.ThreadID, stack, data2.Type != ClrThreadSampleType.Managed);
            }
        };

        // Registers the templates of the EventSources in the trace (the TPL, System.Net.Http, DiagnosticSource...).
        source.Dynamic.All += _ => { };
        source.AllEvents += e =>
        {
            if ((++count & 4095) == 0 && cancellationToken.IsCancellationRequested)
            {
                source.StopProcessing();
                return;
            }

            var provider = e.ProviderName;
            var name = e.EventName;
            data.Index(e, provider, name);
            var time = e.TimeStampRelativeMSec;
            switch (provider)
            {
                case RuntimeProvider:
                    switch (name)
                    {
                        case "GC/AllocationTick":
                            if (e.CallStackIndex() is var allocStack && allocStack != CallStackIndex.Invalid)
                            {
                                var amount = Number(e.PayloadByName("AllocationAmount64")) ?? Number(e.PayloadByName("AllocationAmount")) ?? 0;
                                var type = e.PayloadByName("TypeName") as string is { Length: > 0 } t ? t : "(unknown type)";
                                var leaf = allocations.Leaf(allocations.StackOf(allocStack), type, "[type]");
                                allocations.Profile.AddSample(time, e.ThreadID, leaf, false, (int)Math.Clamp((amount + 1023) / 1024, 1, int.MaxValue));
                            }

                            break;
                        case "Contention/Start":
                            pendingContention[e.ThreadID] = (time, e.CallStackIndex());
                            break;
                        case "Contention/Stop":
                            if (pendingContention.Remove(e.ThreadID, out var started))
                            {
                                var waited = Number(e.PayloadByName("DurationNs")) is { } ns and > 0 ? ns / 1_000_000.0 : time - started.Time;
                                data.Contentions.Add((e.ThreadID, started.Time, started.Time + waited));
                                if (started.Stack != CallStackIndex.Invalid)
                                {
                                    var leaf = contention.Leaf(contention.StackOf(started.Stack), "Lock contention", "[contention]");
                                    contention.Profile.AddSample(started.Time, e.ThreadID, leaf, false, (int)Math.Clamp(waited * 1000, 1, int.MaxValue));
                                }
                            }

                            break;
                        case "Exception/Start":
                            {
                                var type = e.PayloadByName("ExceptionType") as string is { Length: > 0 } t ? t : "(unknown exception)";
                                var message = e.PayloadByName("ExceptionMessage") as string;
                                data.Exceptions.Add(new ExceptionEvent(time, type, string.IsNullOrEmpty(message) ? null : message, e.ThreadID));
                                if (SkipThrowMachinery(e.CallStackIndex()) is var throwStack && throwStack != CallStackIndex.Invalid)
                                {
                                    var leaf = exceptions.Leaf(exceptions.StackOf(throwStack), type, "[exception]");
                                    exceptions.Profile.AddSample(time, e.ThreadID, leaf, false);
                                    data.ExceptionStacks.Add((leaf, data.Exceptions.Count - 1));
                                }
                            }

                            break;
                        case "ThreadPoolWorkerThreadAdjustment/Adjustment":
                            data.Pool.Add(new ThreadPoolEvent(time, "adjustment", (int)(Number(e.PayloadByName("NewWorkerThreadCount")) ?? 0), e.PayloadByName("Reason")?.ToString()));
                            break;
                        case "ThreadPoolWorkerThread/Start":
                            data.Pool.Add(new ThreadPoolEvent(time, "start", (int)(Number(e.PayloadByName("ActiveWorkerThreadCount")) ?? 0), null));
                            break;
                        case "ThreadPoolWorkerThread/Stop":
                            data.Pool.Add(new ThreadPoolEvent(time, "retire", (int)(Number(e.PayloadByName("ActiveWorkerThreadCount")) ?? 0), null));
                            break;
                    }

                    break;
                case TplProvider:
                    if (name == "TraceOperation/Start" && Number(e.PayloadByName("TaskID")) is { } startId)
                    {
                        tasks[(int)startId] = data.Tasks.Count;
                        data.Tasks.Add(new TaskRecord((int)startId, e.PayloadByName("OperationName") as string ?? "Task", time, e.ThreadID, e.CallStackIndex()));
                    }
                    else if (name == "TraceOperation/Stop" && Number(e.PayloadByName("TaskID")) is { } stopId && tasks.Remove((int)stopId, out var at))
                    {
                        data.Tasks[at] = data.Tasks[at] with { End = time, Status = e.PayloadByName("Status")?.ToString() };
                    }

                    break;
                case HttpProvider:
                    if (name == "Request/Start")
                    {
                        var target = string.Create(CultureInfo.InvariantCulture, $"{e.PayloadByName("scheme")}://{e.PayloadByName("host")}:{e.PayloadByName("port")}{e.PayloadByName("pathAndQuery")}");
                        requests[e.ActivityID] = data.Requests.Count;
                        data.Requests.Add(new ActivityRecord(time, null, target, "HTTP/" + e.PayloadByName("versionMajor") + "." + e.PayloadByName("versionMinor"), null, HttpProvider, null, null));
                    }
                    else if (name is "Request/Stop" or "RequestFailed" or "RequestFailed/Start" && requests.TryGetValue(e.ActivityID, out var request))
                    {
                        var record = data.Requests[request];
                        if (name == "Request/Stop")
                        {
                            requests.Remove(e.ActivityID);
                            var status = Number(e.PayloadByName("statusCode")) is { } code and > 0 ? code.ToString(CultureInfo.InvariantCulture) : null;
                            data.Requests[request] = record with { End = time, Status = record.Status ?? status, Failed = record.Failed || (status is { } s && s[0] is '4' or '5') };
                        }
                        else
                        {
                            data.Requests[request] = record with { Status = "failed: " + e.PayloadByName("exceptionMessage"), Failed = true };
                        }
                    }

                    break;
                case DiagnosticSourceProvider:
                    OnDiagnosticSource(e, name, time, data, commands);
                    break;
            }
        };
        source.Process();
        cancellationToken.ThrowIfCancellationRequested();

        string? processName = null;
        int? processId = null;
        if (_log.Processes.FirstOrDefault(p => p.ProcessID > 0) is { } process)
        {
            processId = process.ProcessID;
            processName = string.IsNullOrEmpty(process.Name) ? null : process.Name;
        }

        var duration = _log.SessionDuration.TotalMilliseconds;
        data.Duration = duration;
        foreach (var (kind, builder) in new[] { (ProfileKinds.Cpu, cpu), (ProfileKinds.Allocations, allocations), (ProfileKinds.Contention, contention), (ProfileKinds.Exceptions, exceptions) })
        {
            var profile = builder.Profile;
            profile.ProcessId = processId;
            profile.ProcessName = processName;
            profile.Duration = duration;
            foreach (var thread in _log.Threads)
            {
                if (!string.IsNullOrEmpty(thread.ThreadInfo) && profile.ThreadNames.ContainsKey(thread.ThreadID))
                {
                    profile.NameThread(thread.ThreadID, $"{thread.ThreadInfo} ({thread.ThreadID})");
                }
            }

            profile.Seal();
            data.Profiles[kind] = profile;
        }

        data.ThreadNames = _log.Threads
            .Where(t => !string.IsNullOrEmpty(t.ThreadInfo))
            .GroupBy(t => t.ThreadID)
            .ToDictionary(g => g.Key, g => $"{g.First().ThreadInfo} ({g.Key})");
        data.Seal();
        return data;
    }

    /// <summary>
    /// The stack of an exception without the runtime's own throwing machinery on top (on .NET 9 and later, the managed
    /// exception dispatch in <c>System.Runtime.EH</c>; throw helpers): its leaf is the throw site.
    /// </summary>
    private CallStackIndex SkipThrowMachinery(CallStackIndex stack)
    {
        var callStacks = _log.CallStacks;
        var addresses = _log.CodeAddresses;
        for (var at = stack; at != CallStackIndex.Invalid; at = callStacks.Caller(at))
        {
            var method = addresses.MethodIndex(callStacks.CodeAddressIndex(at));
            var name = method == MethodIndex.Invalid ? null : addresses.Methods.FullMethodName(method);
            if (name is not null
                && !name.StartsWith("System.Runtime.EH.", StringComparison.Ordinal)
                && !name.StartsWith("System.Runtime.ExceptionServices.InternalCalls.", StringComparison.Ordinal)
                && !name.StartsWith("System.ThrowHelper.", StringComparison.Ordinal))
            {
                return at;
            }
        }

        return stack;
    }

    /// <summary>DiagnosticSource's events: database commands (start and stop pairs by activity) and <c>Activity</c> spans (stop events).</summary>
    private static void OnDiagnosticSource(TraceEvent e, string name, double time, TraceData data, Dictionary<Guid, int> commands)
    {
        var sourceName = e.PayloadByName("SourceName") as string;
        if (name == "Activity/Stop")
        {
            var args = Arguments(e);
            var duration = args.TryGetValue("Duration", out var d) && TimeSpan.TryParse(d, CultureInfo.InvariantCulture, out var span) ? span.TotalMilliseconds : 0;
            var display = args.GetValueOrDefault("DisplayName") is { Length: > 0 } shown ? shown : e.PayloadByName("ActivityName") as string ?? "span";
            var tags = args.GetValueOrDefault("TagObjects");
            var status = args.GetValueOrDefault("Status") is { Length: > 0 } st && st != "Unset" ? st : null;
            var parent = args.GetValueOrDefault("ParentSpanId") is { Length: > 0 } p && p.Any(c => c != '0') ? p : null;
            data.Spans.Add(new ActivityRecord(time - duration, time, display, tags, status, sourceName, args.GetValueOrDefault("SpanId"), parent) { Failed = status == "Error", Kind = args.GetValueOrDefault("Kind") });
            return;
        }

        if (name.StartsWith("Activity", StringComparison.Ordinal) && name.EndsWith("/Start", StringComparison.Ordinal))
        {
            var args = Arguments(e);
            if (args.GetValueOrDefault("CommandText") is { Length: > 0 } text)
            {
                commands[e.ActivityID] = data.Commands.Count;
                data.Commands.Add(new ActivityRecord(time, null, text.Trim(), args.GetValueOrDefault("Database"), null, sourceName, null, null));
            }
        }
        else if (name.StartsWith("Activity", StringComparison.Ordinal) && name.EndsWith("/Stop", StringComparison.Ordinal) && commands.Remove(e.ActivityID, out var index))
        {
            var args = Arguments(e);
            var error = args.GetValueOrDefault("Message");
            data.Commands[index] = data.Commands[index] with { End = time, Status = error is null ? null : "failed: " + error, Failed = error is not null };
        }
    }

    /// <summary>DiagnosticSource's <c>Arguments</c>: a list of key/value pairs.</summary>
    private static Dictionary<string, string> Arguments(TraceEvent e)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (e.PayloadByName("Arguments") is System.Collections.IEnumerable list)
        {
            foreach (var item in list)
            {
                if (item is IDictionary<string, object> pair && pair.TryGetValue("Key", out var key) && key is string k)
                {
                    result[k] = pair.TryGetValue("Value", out var value) ? value?.ToString() ?? string.Empty : string.Empty;
                }
            }
        }

        return result;
    }

    internal static double? Number(object? value) => value switch
    {
        null => null,
        string text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
        IConvertible convertible when value is not bool => convertible.ToDouble(CultureInfo.InvariantCulture),
        _ => null,
    };

    private static string? ModuleName(TraceModuleFile? module) =>
        module is null ? null : Path.GetFileNameWithoutExtension(module.FilePath is { Length: > 0 } path ? path : module.Name);

    /// <summary>A TraceLog call stack's frames as method names, the innermost first (at most <paramref name="max"/>).</summary>
    public IReadOnlyList<string> Frames(CallStackIndex stack, int max)
    {
        var frames = new List<string>();
        var callStacks = _log.CallStacks;
        var addresses = _log.CodeAddresses;
        for (var at = stack; at != CallStackIndex.Invalid && frames.Count < max; at = callStacks.Caller(at))
        {
            var address = callStacks.CodeAddressIndex(at);
            var method = addresses.MethodIndex(address);
            frames.Add(method != MethodIndex.Invalid ? addresses.Methods.FullMethodName(method) : $"[{ModuleName(addresses.ModuleFile(address)) ?? "?"}] ?");
        }

        return frames;
    }

    /// <summary>An event's payload as <c>name=value</c> pairs.</summary>
    public string Payload(int eventIndex)
    {
        var e = _log.GetEvent((EventIndex)eventIndex);
        if (e is null)
        {
            return string.Empty;
        }

        var names = e.PayloadNames ?? [];
        var parts = new List<string>(names.Length);
        for (var i = 0; i < names.Length; i++)
        {
            string text;
            try
            {
                text = e.PayloadString(i, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or FormatException)
            {
                text = "?";
            }

            parts.Add(names[i] + "=" + (text.Length > 500 ? text[..500] + "…" : text));
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Resolves source lines from portable PDBs next to the modules (or where they were built), for every profile.
    /// Framework modules have no PDBs here and are skipped after a few misses; no symbol server is asked.
    /// </summary>
    public void ResolveLines(TraceData data, CancellationToken cancellationToken)
    {
        var directories = _log.ModuleFiles
            .Select(m => m.FilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(Path.GetDirectoryName)
            .Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        using var symbols = new SymbolReader(TextWriter.Null, string.Join(';', directories), null)
        {
            SecurityCheck = _ => true,
        };
        var misses = new Dictionary<ModuleFileIndex, (int Tries, int Hits)>();
        var addresses = _log.CodeAddresses;
        (string File, int Line)? Resolve(long address)
        {
            if (address < 0 || address >= addresses.Count)
            {
                return null;
            }

            if (_lines.TryGetValue(address, out var known))
            {
                return known;
            }

            var index = (CodeAddressIndex)(int)address;
            var module = addresses.ModuleFileIndex(index);
            var tally = misses.GetValueOrDefault(module);
            if (tally is { Hits: 0, Tries: >= 32 } || addresses.MethodIndex(index) == MethodIndex.Invalid)
            {
                return null;
            }

            SourceLocation? location = null;
            try
            {
                location = addresses.GetSourceLine(symbols, index);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or BadImageFormatException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }

            var file = location?.SourceFile?.BuildTimeFilePath;
            misses[module] = (tally.Tries + 1, tally.Hits + (file is null ? 0 : 1));
            (string, int)? result = file is null || location!.LineNumber <= 0 || location.LineNumber >= 0xFEEFEE ? null : (file, location.LineNumber);
            _lines[address] = result;
            return result;
        }

        foreach (var kind in new[] { ProfileKinds.Cpu, ProfileKinds.Exceptions, ProfileKinds.Allocations, ProfileKinds.Contention })
        {
            data.Profile(kind).ResolveLines(Resolve, cancellationToken);
        }
    }

    public void Dispose()
    {
        _log.Dispose();
        TryDelete(_etlx);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
