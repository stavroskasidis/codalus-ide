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

    public static IReadOnlyList<EventPipeProvider> CpuProviders() => TraceProviders([TraceTools.Cpu], null);

    public const string TplProvider = "System.Threading.Tasks.TplEventSource";
    public const string HttpProvider = "System.Net.Http";
    public const string DiagnosticSourceProvider = "Microsoft-Diagnostics-DiagnosticSource";

    /// <summary>
    /// What the activity views ask DiagnosticSource for: database commands of SqlClient and EF Core as start and stop
    /// pairs (with their text), and every <c>ActivitySource</c>'s spans as they stop (with their ids, kind, duration,
    /// status and tags).
    /// </summary>
    internal const string ActivitySpecs =
        "SqlClientDiagnosticListener/System.Data.SqlClient.WriteCommandBefore@Activity1Start:-Command.CommandText;Command.Connection.Database\r\n" +
        "SqlClientDiagnosticListener/System.Data.SqlClient.WriteCommandAfter@Activity1Stop:-OperationId\r\n" +
        "SqlClientDiagnosticListener/System.Data.SqlClient.WriteCommandError@Activity1Stop:-Exception.Message\r\n" +
        "SqlClientDiagnosticListener/Microsoft.Data.SqlClient.WriteCommandBefore@Activity1Start:-Command.CommandText;Command.Connection.Database\r\n" +
        "SqlClientDiagnosticListener/Microsoft.Data.SqlClient.WriteCommandAfter@Activity1Stop:-OperationId\r\n" +
        "SqlClientDiagnosticListener/Microsoft.Data.SqlClient.WriteCommandError@Activity1Stop:-Exception.Message\r\n" +
        "Microsoft.EntityFrameworkCore/Microsoft.EntityFrameworkCore.Database.Command.CommandExecuting@Activity2Start:-Command.CommandText;Command.Connection.Database\r\n" +
        "Microsoft.EntityFrameworkCore/Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted@Activity2Stop:-Duration\r\n" +
        "Microsoft.EntityFrameworkCore/Microsoft.EntityFrameworkCore.Database.Command.CommandError@Activity2Stop:-Exception.Message\r\n" +
        "[AS]*/Stop:-TraceId;SpanId;ParentSpanId;Kind;DisplayName;Duration;Status;TagObjects";

    /// <summary>
    /// The providers of a trace of <paramref name="tools"/> (<see cref="TraceTools"/>): the runtime's method events for
    /// names and source lines always (rundown adds the methods compiled earlier), plus each tool's events, plus any
    /// <paramref name="extra"/> provider (the events viewer's).
    /// </summary>
    public static IReadOnlyList<EventPipeProvider> TraceProviders(IReadOnlyCollection<string> tools, IReadOnlyList<TraceProvider>? extra)
    {
        var providers = new List<EventPipeProvider>();
        var runtime = CpuRuntimeKeywords;
        if (tools.Contains(TraceTools.Cpu))
        {
            providers.Add(new EventPipeProvider(SampleProfilerProvider, EventLevel.Informational, 0xF00000000000));
        }

        if (tools.Contains(TraceTools.Allocations))
        {
            runtime |= 0x1; // GC: its allocation ticks are verbose
        }

        if (tools.Contains(TraceTools.Threads))
        {
            runtime |= 0x4000 /* Contention */ | 0x10000 /* Threading */;
        }

        if (tools.Contains(TraceTools.Exceptions))
        {
            runtime |= 0x8000;
        }

        providers.Add(new EventPipeProvider(RuntimeProvider, EventLevel.Verbose, runtime));
        long tpl = 0;
        if (tools.Contains(TraceTools.Async))
        {
            tpl |= 0x8 /* AsyncCausalityOperation */ | 0x10 /* AsyncCausalityRelation */;
        }

        if (tools.Contains(TraceTools.Activities))
        {
            tpl |= 0x80; // TasksFlowActivityIds: a request's start and stop share an activity id
            providers.Add(new EventPipeProvider(HttpProvider, EventLevel.Informational, -1));
            providers.Add(new EventPipeProvider(DiagnosticSourceProvider, EventLevel.Verbose, 0x2 /* Events */ | 0x800 /* IgnoreShortCutKeywords */, new Dictionary<string, string> { ["FilterAndPayloadSpecs"] = ActivitySpecs }));
        }

        if (tpl != 0)
        {
            providers.Add(new EventPipeProvider(TplProvider, EventLevel.Informational, tpl));
        }

        foreach (var provider in extra ?? [])
        {
            if (providers.FindIndex(p => p.Name.Equals(provider.Name, StringComparison.OrdinalIgnoreCase)) is >= 0 and var index)
            {
                var known = providers[index];
                providers[index] = new EventPipeProvider(known.Name, (EventLevel)Math.Max((int)known.EventLevel, Math.Clamp(provider.Level, 0, 5)), known.Keywords | provider.Keywords, known.Arguments);
            }
            else
            {
                providers.Add(new EventPipeProvider(provider.Name, (EventLevel)Math.Clamp(provider.Level, 0, 5), provider.Keywords, provider.Arguments?.ToDictionary(a => a.Key, a => a.Value)));
            }
        }

        return providers;
    }

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
