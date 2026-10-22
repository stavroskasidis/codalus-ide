using System.Diagnostics;
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
    /// </summary>
    public static async Task<int?> WaitForAppAsync(WatchedApp app, int? previousPid, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var listening = previousPid is null ? 0 : app.HotReload.ListeningCount;
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
                if (pid is { } found)
                {
                    if (app.HotReload.ListeningCount > listening)
                    {
                        return found;
                    }

                    if (app.Address is { } address && (previousPid is not { } previous || !app.IsAlive(previous))
                        && await app.Accepts(address, cancellationToken).ConfigureAwait(false))
                    {
                        return found;
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
    /// <c>BlazorSudoku.Server</c> has no other extension); null for <c>dotnet App.dll</c>.
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
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException or NotSupportedException)
        {
        }
    }

    /// <summary>Whether <paramref name="url"/>'s host and port accept a connection now (one attempt).</summary>
    public static async Task<bool> AcceptsAsync(Uri url, CancellationToken cancellationToken)
    {
        var host = url.Host is "*" or "+" or "0.0.0.0" or "[::]" or "" ? "localhost" : url.Host;
        try
        {
            using var client = new TcpClient();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(TimeSpan.FromMilliseconds(500));
            await client.ConnectAsync(host, url.Port, attempt.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
