using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Codalus.Diagnostics.Protocol;

namespace Codalus.Diagnostics.Host.Trace;

/// <summary>
/// The File I/O tool (ADR 0046): EventPipe has no file events, and per-file detail needs the kernel (ETW's file I/O
/// events through a kernel session, which needs administrator rights and the proprietary KernelTraceControl we don't
/// ship; <c>perf</c> or eBPF on Linux; DTrace on macOS). So the process's own I/O counters are sampled four times a
/// second, which every OS gives without privileges:
/// <list type="bullet">
/// <item><b>Windows</b>: <c>GetProcessIoCounters</c>: bytes and operations read and written, all I/O (files, network, devices).</item>
/// <item><b>Linux</b>: <c>/proc/&lt;pid&gt;/io</c>: <c>rchar</c>/<c>wchar</c> (bytes through read and write calls: files, pipes, sockets) and <c>syscr</c>/<c>syscw</c> (the calls).</item>
/// <item><b>macOS</b>: <c>proc_pid_rusage</c>: bytes read from and written to disk (cached reads don't count); no operation counts.</item>
/// </list>
/// The samples are written next to the trace as <c>name.io.json</c>.
/// </summary>
internal sealed partial class IoSampler : IAsyncDisposable
{
    private static readonly TimeSpan s_interval = TimeSpan.FromMilliseconds(250);

    private readonly int _processId;
    private readonly List<IoSample> _samples = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Task _loop = Task.CompletedTask;

    public IoSampler(int processId) => _processId = processId;

    public static string PlatformName => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "macOS" : RuntimeInformation.OSDescription;

    public static string SourceDescription =>
        OperatingSystem.IsWindows() ? "GetProcessIoCounters: bytes and operations read and written, all I/O (files, network and devices)"
        : OperatingSystem.IsLinux() ? "/proc/<pid>/io: bytes through read and write calls (files, pipes and sockets) and the calls"
        : OperatingSystem.IsMacOS() ? "proc_pid_rusage: bytes read from and written to disk (cached reads don't count); no operation counts"
        : "not available on this OS";

    /// <summary><c>trace.nettrace</c> → <c>trace.io.json</c>.</summary>
    public static string SidecarPath(string tracePath) => Path.ChangeExtension(tracePath, ".io.json");

    public void Start()
    {
        var token = _stop.Token;
        _loop = Task.Run(async () =>
        {
            var last = Read(_processId);
            var lastAt = _clock.Elapsed.TotalMilliseconds;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(s_interval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                var now = Read(_processId);
                var at = _clock.Elapsed.TotalMilliseconds;
                if (now is { } current && last is { } previous)
                {
                    lock (_samples)
                    {
                        _samples.Add(new IoSample(
                            lastAt,
                            Math.Max(0, current.ReadBytes - previous.ReadBytes),
                            Math.Max(0, current.WriteBytes - previous.WriteBytes),
                            current.ReadOperations - previous.ReadOperations is { } reads ? Math.Max(0, reads) : null,
                            current.WriteOperations - previous.WriteOperations is { } writes ? Math.Max(0, writes) : null));
                    }
                }

                if (now is null)
                {
                    break; // exited
                }

                last = now;
                lastAt = at;
            }
        }, CancellationToken.None);
    }

    /// <summary>Stops sampling and writes the samples next to the trace.</summary>
    public async Task WriteAsync(string tracePath)
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        IoRecording recording;
        lock (_samples)
        {
            recording = new IoRecording(PlatformName, SourceDescription, [.. _samples]);
        }

        await File.WriteAllTextAsync(SidecarPath(tracePath), JsonSerializer.Serialize(recording, DiagnosticsJsonContext.Default.IoRecording)).ConfigureAwait(false);
    }

    /// <summary>The process's totals so far, or null when it has exited or the OS doesn't say.</summary>
    internal static (long ReadBytes, long WriteBytes, long? ReadOperations, long? WriteOperations)? Read(int processId)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited || !GetProcessIoCounters(process.Handle, out var counters))
                {
                    return null;
                }

                return ((long)counters.ReadTransferCount, (long)counters.WriteTransferCount, (long)counters.ReadOperationCount, (long)counters.WriteOperationCount);
            }

            if (OperatingSystem.IsLinux())
            {
                var values = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var line in File.ReadAllLines($"/proc/{processId}/io"))
                {
                    var colon = line.IndexOf(':', StringComparison.Ordinal);
                    if (colon > 0 && long.TryParse(line[(colon + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    {
                        values[line[..colon]] = value;
                    }
                }

                return (values.GetValueOrDefault("rchar"), values.GetValueOrDefault("wchar"), values.GetValueOrDefault("syscr"), values.GetValueOrDefault("syscw"));
            }

            if (OperatingSystem.IsMacOS())
            {
                // rusage_info_v2: a 16-byte uuid, then 64-bit fields; ri_diskio_bytesread is the 17th, ri_diskio_byteswritten the 18th.
                var buffer = new byte[512];
                unsafe
                {
                    fixed (byte* pointer = buffer)
                    {
                        if (proc_pid_rusage(processId, 2, pointer) != 0)
                        {
                            return null;
                        }
                    }
                }

                return (BitConverter.ToInt64(buffer, 16 + (16 * 8)), BitConverter.ToInt64(buffer, 16 + (17 * 8)), null, null);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [LibraryImport("/usr/lib/libSystem.dylib")]
    private static unsafe partial int proc_pid_rusage(int pid, int flavor, byte* buffer);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
