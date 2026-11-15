using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Codalus.Platform;
using Codalus.Workbench.Build;

namespace Codalus.Workbench.Debugging;

/// <summary>
/// What finding the app <c>dotnet watch</c> started needs (#75): its session, its process, the app's name and address.
/// </summary>
/// <param name="HotReload">The <c>dotnet watch</c> session's state, from its output.</param>
/// <param name="WatchExited">Completes when <c>dotnet watch</c> exits.</param>
/// <param name="WatchProcessId">The <c>dotnet watch</c> process: the app is its grandchild (through <c>dotnet run</c>).</param>
/// <param name="AppName">The app's process name (<see cref="WatchedServer.AppNameOf"/>); null when it runs as <c>dotnet App.dll</c>, which only the agent's line finds.</param>
/// <param name="Address">The launch profile's address, probed when the app doesn't log "Now listening on"; null when it has none.</param>
public sealed record WatchedApp(HotReloadSession HotReload, Task WatchExited, int WatchProcessId, string? AppName, Uri? Address)
{
    /// <summary>A process's children (<see cref="ProcessTree.Children"/>; tests replace it).</summary>
    public Func<int, IReadOnlyList<int>> Children { get; init; } = ProcessTree.Children;

    /// <summary>A process's name, or null once it's gone.</summary>
    public Func<int, string?> NameOf { get; init; } = WatchedServer.ProcessName;

    /// <summary>Whether a process is still running.</summary>
    public Func<int, bool> IsAlive { get; init; } = WatchedServer.IsRunning;

    /// <summary>Whether the address accepts a connection now (tests replace it).</summary>
    public Func<Uri, CancellationToken, Task<bool>> Accepts { get; init; } = WatchedServer.AcceptsAsync;

    /// <summary>
    /// What the wait saw last, each time it changes (the candidate, the listening lines, the previous app, the probe):
    /// a follow that never ends says why in the failure's report.
    /// </summary>
    public Action<string>? Observed { get; init; }
}

/// <summary>
/// Finds the app process <c>dotnet watch</c> starts and waits until it serves (#75): the debugger attaches only then, as
/// attaching while a runtime starts fails on macOS and races on Linux (ADR 0007).
/// </summary>
public static class WatchedServer
{
    /// <summary>
    /// The id of the next app <c>dotnet watch</c> starts (not <paramref name="previousPid"/>), once it serves: it logged
    /// "Now listening on", or (its logging filters the host's messages) its address accepts connections while the
    /// previous one has exited. Its id comes from the agent's line (SDK 11 on), else from <c>dotnet watch</c>'s process
    /// tree. Null when <c>dotnet watch</c> exits, says the app exited, or <paramref name="timeout"/> passes first.
    /// <paramref name="listeningBefore"/>: how many "Now listening on" lines are older than the app waited for (0 for the
    /// first; after a restart, the count when it was announced, as the next server can log its line before the wait begins).
    /// </summary>
    public static async Task<int?> WaitForAppAsync(WatchedApp app, int? previousPid, int listeningBefore, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var listening = listeningBefore;
        string? seen = null;
        var ended = 0;
        void OnEnded() => Volatile.Write(ref ended, 1);
        app.HotReload.AppEnded += OnEnded;
        try
        {
            var clock = Stopwatch.StartNew();
            int? pid = null;
            while (clock.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (app.WatchExited.IsCompleted || Volatile.Read(ref ended) != 0)
                {
                    return null;
                }

                if (pid is { } known && !app.IsAlive(known))
                {
                    pid = null; // it ended before it served (a failed start): dotnet watch may start another
                }

                pid ??= Candidate(app, previousPid);
                var count = app.HotReload.ListeningCount;
                var previousAlive = previousPid is { } previous && app.IsAlive(previous);
                var accepts = false;
                if (pid is { } found)
                {
                    if (count > listening)
                    {
                        return found;
                    }

                    if (app.Address is { } address && !previousAlive)
                    {
                        accepts = await app.Accepts(address, cancellationToken).ConfigureAwait(false);
                        if (accepts)
                        {
                            return found;
                        }
                    }
                }

                if (app.Observed is { } observed)
                {
                    var now = FormattableString.Invariant(
                        $"candidate {(pid is { } p ? p.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none")} (the agent's {app.HotReload.AppProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}); \"Now listening on\" {count}, waiting for more than {listening}; previous {previousPid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} {(previousAlive ? "alive" : "gone")}; {(app.Address is { } probed ? $"{probed.Authority} {(accepts ? "accepts" : pid is null || previousAlive ? "not probed" : "refuses")}" : "no address to probe")}");
                    if (now != seen)
                    {
                        seen = now;
                        observed(now);
                    }
                }

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }
        finally
        {
            app.HotReload.AppEnded -= OnEnded;
        }
    }

    /// <summary>The new app's id: the agent's (SDK 11 on), else the first process named as the app under <c>dotnet watch</c>.</summary>
    private static int? Candidate(WatchedApp app, int? previousPid)
    {
        if (app.HotReload.AppProcessId is { } announced && announced != previousPid && app.IsAlive(announced))
        {
            return announced;
        }

        // SDK 10's agent doesn't say the id: dotnet watch → dotnet run → the app (Windows: no deeper than that either).
        if (app.AppName is not { } appName)
        {
            return null;
        }

        var level = new List<int> { app.WatchProcessId };
        for (var depth = 0; depth < 4 && level.Count > 0; depth++)
        {
            var next = new List<int>();
            foreach (var parent in level)
            {
                foreach (var child in app.Children(parent))
                {
                    if (child != previousPid && IsApp(app.NameOf(child), appName))
                    {
                        return child;
                    }

                    next.Add(child);
                }
            }

            level = next;
        }

        return null;
    }

    /// <summary>
    /// The process name of the program a plan starts: its apphost's file name without <c>.exe</c> (a name such as
    /// <c>ContosoStore.Server</c> has no other extension); null for <c>dotnet App.dll</c>.
    /// </summary>
    public static string? AppNameOf(LaunchPlan plan)
    {
        var name = Path.GetFileName(plan.FileName);
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name.Length == 0 || name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? null : name;
    }

    /// <summary>Whether a process name is the app's: Linux keeps only a name's first 15 characters.</summary>
    internal static bool IsApp(string? processName, string appName) =>
        processName is { Length: > 0 } name
        && (string.Equals(name, appName, StringComparison.OrdinalIgnoreCase)
            || name.Length >= 15 && appName.StartsWith(name, StringComparison.OrdinalIgnoreCase));

    internal static string? ProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited ? null : process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Whether a process runs (a zombie on Linux doesn't).</summary>
    public static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return false;
            }

            if (OperatingSystem.IsLinux())
            {
                var stat = File.ReadAllText($"/proc/{processId}/stat");
                return stat[(stat.LastIndexOf(')') + 1)..].TrimStart() is not ['Z', ..];
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Ends a process and its children; one already gone is fine.</summary>
    public static void Kill(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            ProcessTree.Kill(process);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException or NotSupportedException)
        {
        }
    }

    /// <summary>
    /// Whether <paramref name="url"/>'s host and port accept a connection now (one attempt). Each of the host's
    /// addresses is tried at once: <c>localhost</c> is <c>::1</c> before <c>127.0.0.1</c> on Windows, where a refused
    /// connection takes about two seconds (it retries the SYN), so trying them in turn never reached an app that
    /// listens only on IPv4 within the attempt.
    /// </summary>
    public static Task<bool> AcceptsAsync(Uri url, CancellationToken cancellationToken) => AcceptsAsync(url, TimeSpan.FromMilliseconds(500), cancellationToken);

    /// <summary>
    /// <see cref="AcceptsAsync(Uri, CancellationToken)"/> within <paramref name="attempt"/>. The connects are the OS's
    /// own (non-blocking, waited for with <see cref="Socket.Select"/>, their error read from the socket), not the
    /// runtime's asynchronous ones: those end on the thread pool, which a busy machine kept from running a connect
    /// that had gone through until the attempt was up, and an app that accepted showed as not listening (macOS CI, run
    /// 43686325192). The attempt bounds the network's answer, not the IDE's own scheduling.
    /// </summary>
    internal static async Task<bool> AcceptsAsync(Uri url, TimeSpan attempt, CancellationToken cancellationToken)
    {
        var host = url.Host is "*" or "+" or "0.0.0.0" or "[::]" or "" ? "localhost" : url.DnsSafeHost; // [::1] → ::1
        var started = Stopwatch.StartNew();
        IPAddress[] addresses;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(attempt);
            // localhost is the loopback addresses (RFC 6761), with no lookup: its completion would wait for the thread pool too.
            addresses = IPAddress.TryParse(host, out var literal) ? [literal]
                : string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ? Socket.OSSupportsIPv6 ? [IPAddress.IPv6Loopback, IPAddress.Loopback] : [IPAddress.Loopback]
                : await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var sockets = new List<Socket>();
        try
        {
            var connecting = new List<Socket>();
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { Blocking = false };
                sockets.Add(socket);
                try
                {
                    socket.Connect(new IPEndPoint(address, url.Port));
                    return true; // at once
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.InProgress)
                {
                    connecting.Add(socket);
                }
                catch (SocketException)
                {
                    // refused at once
                }
            }

            var left = attempt - started.Elapsed;
            return connecting.Count > 0 && await Task.Run(() => AnyConnects(connecting, left), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var socket in sockets)
            {
                socket.Dispose();
            }
        }
    }

    /// <summary>
    /// Whether any of <paramref name="connecting"/> connects within <paramref name="left"/>: writable with no error. A
    /// refused one is writable too, with its error (Linux, macOS), or in error (Windows).
    /// </summary>
    private static bool AnyConnects(List<Socket> connecting, TimeSpan left)
    {
        var started = Stopwatch.StartNew();
        var pending = new List<Socket>(connecting);
        while (pending.Count > 0)
        {
            var writable = new List<Socket>(pending);
            var failed = new List<Socket>(pending);
            var wait = left - started.Elapsed;
            Socket.Select(null, writable, failed, wait <= TimeSpan.Zero ? 0 : (int)Math.Min(int.MaxValue, wait.TotalMicroseconds));
            if (writable.Count == 0 && failed.Count == 0)
            {
                return false; // the attempt is up
            }

            foreach (var socket in failed)
            {
                pending.Remove(socket);
            }

            foreach (var socket in writable)
            {
                if (pending.Remove(socket) && socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error) is 0)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
