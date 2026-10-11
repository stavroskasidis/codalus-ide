using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;

namespace DaedalusStudio.Diagnostics.Host.Cpu;

/// <summary>
/// Reads a <c>.nettrace</c> file's sample profiler events into a <see cref="CpuProfile"/> through TraceEvent's
/// <see cref="TraceLog"/> (which resolves JIT-compiled and ReadyToRun methods from the runtime's method events and
/// rundown), and later resolves source lines from the modules' portable PDBs. The <c>.etlx</c> conversion goes to a
/// temporary file next to nothing the user owns; the reader keeps the log open for line resolution until disposed.
/// </summary>
internal sealed class NettraceReader : IDisposable
{
    private readonly TraceLog _log;
    private readonly string _etlx;

    private NettraceReader(TraceLog log, string etlx)
    {
        _log = log;
        _etlx = etlx;
    }

    public static (CpuProfile Profile, NettraceReader Reader) Read(string path, CancellationToken cancellationToken)
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
            return (reader.Build(cancellationToken), reader);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private CpuProfile Build(CancellationToken cancellationToken)
    {
        var profile = new CpuProfile();
        var stacks = new int[Math.Max(1, _log.CallStacks.Count)];
        Array.Fill(stacks, -1);
        var methods = new int[Math.Max(1, _log.CodeAddresses.Methods.Count)];
        Array.Fill(methods, -1);
        var unknown = new Dictionary<string, int>(StringComparer.Ordinal);
        var callStacks = _log.CallStacks;
        var addresses = _log.CodeAddresses;

        int FunctionOf(CodeAddressIndex address)
        {
            var method = addresses.MethodIndex(address);
            if (method != MethodIndex.Invalid)
            {
                ref var known = ref methods[(int)method];
                if (known < 0)
                {
                    known = profile.AddFunction(addresses.Methods.FullMethodName(method), ModuleName(addresses.ModuleFile(address)));
                }

                return known;
            }

            var module = ModuleName(addresses.ModuleFile(address)) ?? "?";
            if (!unknown.TryGetValue(module, out var id))
            {
                id = profile.AddFunction($"[{module}] ?", module);
                unknown[module] = id;
            }

            return id;
        }

        // A call stack (TraceLog's, an address with its caller) becomes a profile stack once, root first.
        var pending = new List<CallStackIndex>();
        int StackOf(CallStackIndex index)
        {
            if (index == CallStackIndex.Invalid)
            {
                return -1;
            }

            pending.Clear();
            var at = index;
            while (at != CallStackIndex.Invalid && stacks[(int)at] < 0)
            {
                pending.Add(at);
                at = callStacks.Caller(at);
            }

            var parent = at == CallStackIndex.Invalid ? -1 : stacks[(int)at];
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                var address = callStacks.CodeAddressIndex(pending[i]);
                parent = profile.AddStack(parent, FunctionOf(address), (long)address);
                stacks[(int)pending[i]] = parent;
            }

            return parent;
        }

        using var source = _log.Events.GetSource();
        var sampler = new SampleProfilerTraceEventParser(source);
        var count = 0;
        sampler.ThreadSample += data =>
        {
            if ((++count & 4095) == 0 && cancellationToken.IsCancellationRequested)
            {
                source.StopProcessing();
                return;
            }

            var stack = StackOf(data.CallStackIndex());
            if (stack >= 0)
            {
                profile.AddSample(data.TimeStampRelativeMSec, data.ThreadID, stack, data.Type != ClrThreadSampleType.Managed);
            }
        };
        source.Process();
        cancellationToken.ThrowIfCancellationRequested();

        if (_log.Processes.FirstOrDefault(p => p.ProcessID > 0) is { } process)
        {
            profile.ProcessId = process.ProcessID;
            profile.ProcessName = string.IsNullOrEmpty(process.Name) ? null : process.Name;
        }

        profile.Duration = _log.SessionDuration.TotalMilliseconds;
        foreach (var thread in _log.Threads)
        {
            if (!string.IsNullOrEmpty(thread.ThreadInfo) && profile.ThreadNames.ContainsKey(thread.ThreadID))
            {
                profile.NameThread(thread.ThreadID, $"{thread.ThreadInfo} ({thread.ThreadID})");
            }
        }

        profile.Seal();
        return profile;
    }

    private static string? ModuleName(TraceModuleFile? module) =>
        module is null ? null : Path.GetFileNameWithoutExtension(module.FilePath is { Length: > 0 } path ? path : module.Name);

    /// <summary>
    /// Resolves source lines from portable PDBs next to the modules (or where they were built). Framework modules have
    /// no PDBs here and are skipped after a few misses; no symbol server is asked.
    /// </summary>
    public void ResolveLines(CpuProfile profile, CancellationToken cancellationToken)
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
        profile.ResolveLines(
            address =>
            {
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
                return file is null || location!.LineNumber <= 0 || location.LineNumber >= 0xFEEFEE ? null : (file, location.LineNumber);
            },
            cancellationToken);
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
