using System.Diagnostics;
using System.Diagnostics.Tracing;
using Codalus.Diagnostics.Protocol;
using Codalus.Rpc;
using Microsoft.Diagnostics.NETCore.Client;

namespace Codalus.Diagnostics.Host.Sessions;

/// <summary>
/// A process's diagnostics port (Microsoft.Diagnostics.NETCore.Client): finding .NET processes, starting EventPipe
/// sessions, and resuming a process held at startup. A process launched to be profiled from its start may not have
/// opened its port yet, so starting retries briefly while the process lives. A try the port doesn't answer is given up
/// and made again, so a session's start never waits on it for good.
/// </summary>
internal static class DiagnosticsPort
{
    private static readonly TimeSpan s_connectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long one try to start a session waits for the port's answer before it is made again.</summary>
    private static readonly TimeSpan s_attemptTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a live process's port may leave session starts unanswered before starting fails, saying so.</summary>
    private static readonly TimeSpan s_answerTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long a resume waits for the port's answer: less than the shell gives a profiling start (60 s) with the wait for the port.</summary>
    private static readonly TimeSpan s_resumeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long a Windows pipe just made is left for the runtime to start waiting on before connecting (<see cref="WaitForPortAsync"/>).</summary>
    private static readonly TimeSpan s_listenGrace = TimeSpan.FromMilliseconds(250);

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
        foreach (var id in PublishedProcesses())
        {
            if (id == self || id == parent)
            {
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(id);
                var name = process.ProcessName;
                if (name.StartsWith("codalus-", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// The ids of the processes with a diagnostics port. The client library's list reads every process's
    /// <c>/proc/&lt;pid&gt;/environ</c> on Linux and throws when one exits during the scan (or can't be read, as in a
    /// sandbox), which a busy machine makes common: it's asked again, and then the ports are listed directly.
    /// </summary>
    internal static IReadOnlyList<int> PublishedProcesses()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return [.. DiagnosticsClient.GetPublishedProcesses().Distinct()];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A process ended while the client read it: ask again.
            }
        }

        return ListedPorts(OperatingSystem.IsWindows() ? @"\\.\pipe\" : Path.GetTempPath());
    }

    /// <summary>The process ids of the diagnostics ports in <paramref name="folder"/>: <c>dotnet-diagnostic-{pid}</c> pipes, <c>dotnet-diagnostic-{pid}-{key}-socket</c> sockets.</summary>
    internal static IReadOnlyList<int> ListedPorts(string folder)
    {
        const string Prefix = "dotnet-diagnostic-";
        var ids = new SortedSet<int>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, Prefix + "*"))
            {
                var name = Path.GetFileName(path.AsSpan())[Prefix.Length..];
                var end = name.IndexOf('-');
                if (int.TryParse(end < 0 ? name : name[..end], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0)
                {
                    ids.Add(id);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No folder to list: no processes.
        }

        return [.. ids];
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

    /// <summary>
    /// Starts an EventPipe session, retrying while the port isn't open yet (a process that has just started) and while
    /// it doesn't answer. On Windows the client waits for the runtime's pipe without a time limit (one the runtime hasn't
    /// made yet, or a connection it missed), which held a trace's start until the caller gave up after 3 minutes (the
    /// Windows CI runner): each try now waits <see cref="s_attemptTimeout"/>, and after <see cref="s_answerTimeout"/>
    /// starting fails with the reason. A start that does its work before it answers (<paramref name="slowStart"/>: a
    /// heap snapshot's induced GC, as long as the heap is large) waits for its answer instead. A just-started process's
    /// port is waited for before the first try (<see cref="WaitForPortAsync"/>), as a connection the Windows runtime
    /// misses can't be tried again.
    /// </summary>
    public static async Task<EventPipeSession> StartSessionAsync(int processId, IReadOnlyList<EventPipeProvider> providers, bool rundown, bool stacks, int bufferMegabytes, bool slowStart, CancellationToken cancellationToken)
    {
        NameOf(processId);
        await WaitForPortAsync(processId, cancellationToken).ConfigureAwait(false);
        var client = new DiagnosticsClient(processId);
        var clock = Stopwatch.StartNew();
        while (true)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (!slowStart)
            {
                attempt.CancelAfter(s_attemptTimeout);
            }

            var starting = StartOnceAsync(client, providers, rundown, stacks, bufferMegabytes, attempt.Token);
            try
            {
                // WaitAsync too: the try ends on time even where the client doesn't watch its token.
                return await starting.WaitAsync(attempt.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                DisposeWhenStarted(starting);
                if (!IsAlive(processId))
                {
                    throw new JsonRpcHandlerException(DiagnosticsErrorCodes.ProcessNotFound, $"Process {processId} has exited.");
                }

                if (clock.Elapsed >= s_answerTimeout)
                {
                    throw new JsonRpcHandlerException(
                        DiagnosticsErrorCodes.NotAvailable,
                        $"Process {processId}'s .NET diagnostics port didn't answer within {s_answerTimeout.TotalSeconds:0} s (tried every {s_attemptTimeout.TotalSeconds:0} s).");
                }
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

    private static async Task<EventPipeSession> StartOnceAsync(DiagnosticsClient client, IReadOnlyList<EventPipeProvider> providers, bool rundown, bool stacks, int bufferMegabytes, CancellationToken cancellationToken)
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

    /// <summary>A try given up on that starts its session after all: the session is stopped (its connection closed).</summary>
    private static void DisposeWhenStarted(Task<EventPipeSession> starting) =>
        _ = starting.ContinueWith(
            static t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    t.Result.Dispose();
                }
                else
                {
                    _ = t.Exception; // observed
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

    /// <summary>
    /// Resumes a process started with <c>DOTNET_DefaultDiagnosticPortSuspend=1</c>. Memory Usage alone resumes as its first
    /// contact with the port, right after the launch, so the port is waited for first (<see cref="WaitForPortAsync"/>).
    /// The client's resume is synchronous and reads its answer without a time limit: it runs on its own thread, and a
    /// port that doesn't answer within <see cref="s_resumeTimeout"/> fails the resume, saying so, before the shell's
    /// own time limit cancels it without a reason.
    /// </summary>
    public static async Task ResumeAsync(int processId, CancellationToken cancellationToken)
    {
        NameOf(processId);
        await WaitForPortAsync(processId, cancellationToken).ConfigureAwait(false);
        var client = new DiagnosticsClient(processId);
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var resuming = Task.Factory.StartNew(client.ResumeRuntime, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try
            {
                await resuming.WaitAsync(s_resumeTimeout, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException && !resuming.IsCompleted)
            {
                // Given up on: its thread ends when the port answers or closes (the process ending).
                _ = resuming.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                if (ex is OperationCanceledException)
                {
                    throw;
                }

                throw new JsonRpcHandlerException(
                    IsAlive(processId) ? DiagnosticsErrorCodes.NotAvailable : DiagnosticsErrorCodes.ProcessNotFound,
                    IsAlive(processId) ? $"Process {processId}'s .NET diagnostics port didn't answer the resume within {s_resumeTimeout.TotalSeconds:0} s." : $"Process {processId} has exited.");
            }
            catch (ServerNotAvailableException) when (clock.Elapsed < s_connectTimeout && IsAlive(processId))
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ServerNotAvailableException or UnsupportedCommandException or ServerErrorException or IOException or TimeoutException)
            {
                // TimeoutException: the client's own 30 s wait for the Windows pipe.
                throw new JsonRpcHandlerException(DiagnosticsErrorCodes.NotAvailable, $"Process {processId} could not be resumed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Waits for a just-started process's diagnostics port before the first connection to it (Windows). The runtime makes
    /// its named pipe (<c>CreateNamedPipe</c>) and only then starts waiting for a client on it (an overlapped
    /// <c>ConnectNamedPipe</c>). A client that connects in between is connected, but the runtime takes the
    /// <c>ERROR_PIPE_CONNECTED</c> it gets as a wait in progress and never signals it, so the port never reads the
    /// client's command and never makes another pipe: a process held at startup is held for good. The diagnostics client
    /// waits for a pipe by trying to connect until it exists, which lands right in that gap; listing <c>\\.\pipe\</c>
    /// doesn't connect, so the pipe is waited for that way, and once it appears the runtime is given
    /// <see cref="s_listenGrace"/> to start waiting on it. A pipe already there when asked is connected to at once. Off
    /// Windows the port is a Unix socket, whose listen queue keeps an early client.
    /// </summary>
    internal static async Task WaitForPortAsync(int processId, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || PipeListed(processId))
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        while (!PipeListed(processId))
        {
            if (clock.Elapsed >= s_connectTimeout || !IsAlive(processId))
            {
                return; // the connection says what's wrong
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }

        await Task.Delay(s_listenGrace, cancellationToken).ConfigureAwait(false);
    }

    private static bool PipeListed(int processId) =>
        ListedPorts(@"\\.\pipe\").Contains(processId);
}
