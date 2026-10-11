using System.Diagnostics;
using System.Diagnostics.Tracing;
using DaedalusStudio.Diagnostics.Protocol;
using DaedalusStudio.Rpc;
using Microsoft.Diagnostics.NETCore.Client;

namespace DaedalusStudio.Diagnostics.Host.Sessions;

/// <summary>
/// A process's diagnostics port (Microsoft.Diagnostics.NETCore.Client): finding .NET processes, starting EventPipe
/// sessions, and resuming a process held at startup. A process launched to be profiled from its start may not have
/// opened its port yet, so starting retries briefly while the process lives.
/// </summary>
internal static class DiagnosticsPort
{
    private static readonly TimeSpan s_connectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The runtime's own events a CPU trace needs: method loads (names), IL-to-native maps (source lines), modules.</summary>
    private const long CpuRuntimeKeywords = 0x8 /* Loader */ | 0x10 /* Jit */ | 0x20 /* NGen (ReadyToRun) */ | 0x20000 /* JittedMethodILToNativeMap */;

    public const string SampleProfilerProvider = "Microsoft-DotNETCore-SampleProfiler";

    public const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";

    /// <summary>GC (0x1) and exception (0x8000) events, for the Diagnostic Tools window.</summary>
    public const long RuntimeEventKeywords = 0x1 | 0x8000;

    public static IReadOnlyList<EventPipeProvider> CpuProviders() =>
    [
        new EventPipeProvider(SampleProfilerProvider, EventLevel.Informational, 0xF00000000000),
        new EventPipeProvider(RuntimeProvider, EventLevel.Verbose, CpuRuntimeKeywords),
    ];

    /// <summary>The .NET processes with a diagnostics port, except this host, the shell (<paramref name="parent"/>) and the IDE's other hosts.</summary>
    public static IReadOnlyList<DotnetProcess> Processes(int parent)
    {
        var self = Environment.ProcessId;
        var processes = new List<DotnetProcess>();
        foreach (var id in DiagnosticsClient.GetPublishedProcesses().Distinct())
        {
            if (id == self || id == parent)
            {
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(id);
                var name = process.ProcessName;
                if (name.StartsWith("daedalus-", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // the IDE's own hosts
                }

                processes.Add(new DotnetProcess(id, name, CommandLine(id)));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Exited, or not ours to look at.
            }
        }

        return [.. processes.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id)];
    }


    private static string? CommandLine(int id)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            var raw = File.ReadAllText($"/proc/{id}/cmdline");
            return raw.Replace('\0', ' ').Trim() is { Length: > 0 } line ? line : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The process's name, or <see cref="DiagnosticsErrorCodes.ProcessNotFound"/>.</summary>
    public static string NameOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new JsonRpcHandlerException(DiagnosticsErrorCodes.ProcessNotFound, $"No process has the id {processId}.");
        }
    }

    public static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Starts an EventPipe session, retrying while the port isn't open yet (a process that has just started).</summary>
    public static async Task<EventPipeSession> StartSessionAsync(int processId, IReadOnlyList<EventPipeProvider> providers, bool rundown, bool stacks, int bufferMegabytes, CancellationToken cancellationToken)
    {
        NameOf(processId);
        var client = new DiagnosticsClient(processId);
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (!stacks)
                {
                    try
                    {
                        return await client.StartEventPipeSessionAsync(new EventPipeSessionConfiguration(providers, bufferMegabytes, rundown, requestStackwalk: false), cancellationToken).ConfigureAwait(false);
                    }
                    catch (UnsupportedCommandException)
                    {
                        // Before .NET 6: every event carries a stack.
                    }
                }

                return await client.StartEventPipeSessionAsync(providers, rundown, bufferMegabytes, cancellationToken).ConfigureAwait(false);
            }
            catch (ServerNotAvailableException) when (clock.Elapsed < s_connectTimeout && IsAlive(processId))
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch (ServerNotAvailableException ex)
            {
                throw new JsonRpcHandlerException(
                    IsAlive(processId) ? DiagnosticsErrorCodes.NotAvailable : DiagnosticsErrorCodes.ProcessNotFound,
                    IsAlive(processId) ? $"Process {processId} has no .NET diagnostics port: {ex.Message}" : $"Process {processId} has exited.");
            }
            catch (Exception ex) when (ex is UnsupportedProtocolException or UnsupportedCommandException or ServerErrorException or EndOfStreamException or IOException)
            {
                throw new JsonRpcHandlerException(DiagnosticsErrorCodes.NotAvailable, $"Process {processId} refused the session: {ex.Message}");
            }
        }
    }

    /// <summary>Resumes a process started with <c>DOTNET_DefaultDiagnosticPortSuspend=1</c>.</summary>
    public static async Task ResumeAsync(int processId, CancellationToken cancellationToken)
    {
        NameOf(processId);
        var client = new DiagnosticsClient(processId);
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                client.ResumeRuntime();
                return;
            }
            catch (ServerNotAvailableException) when (clock.Elapsed < s_connectTimeout && IsAlive(processId))
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ServerNotAvailableException or UnsupportedCommandException or ServerErrorException or IOException)
            {
                throw new JsonRpcHandlerException(DiagnosticsErrorCodes.NotAvailable, $"Process {processId} could not be resumed: {ex.Message}");
            }
        }
    }
}
