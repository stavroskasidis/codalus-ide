using Codalus.Workbench.Build;

namespace Codalus.Workbench.Debugging;

/// <summary>Where a Blazor Web App's server debugged under <c>dotnet watch</c> is (#75).</summary>
public enum WatchedServerState
{
    /// <summary>Waiting for the server to serve, then attaching.</summary>
    Starting,

    /// <summary>The debugger is attached to the server and the page joined.</summary>
    Attached,

    /// <summary><c>dotnet watch</c> announced a restart: the debugger detaches from the server.</summary>
    Releasing,

    /// <summary>The debugger left the server: waiting for the server <c>dotnet watch</c> starts next, to attach to it.</summary>
    Following,

    /// <summary>Ending: <c>dotnet watch</c> and the server are stopped.</summary>
    Stopping,

    /// <summary>Over.</summary>
    Ended,
}

/// <summary>Why a watched server's session ended.</summary>
public enum WatchedServerEnd
{
    /// <summary>Stop, Restart, or Stop Running Programs.</summary>
    Stopped,

    /// <summary>The app exited, and <c>dotnet watch</c> waits for a file to change.</summary>
    AppExited,

    /// <summary><c>dotnet watch</c> exited.</summary>
    WatchExited,

    /// <summary><c>dotnet watch</c> didn't start the app again in time after the debugger left it.</summary>
    NotRestarted,

    /// <summary>The debugger couldn't attach to the server <c>dotnet watch</c> started again.</summary>
    AttachFailed,
}

/// <summary>What a watched server's session reports to the IDE (on any thread).</summary>
public enum WatchedServerChangeKind
{
    /// <summary>The debugger left the server for a restart (or lost it); it follows the next one.</summary>
    Following,

    /// <summary>The debugger attached to the server <c>dotnet watch</c> started again (<see cref="WatchedServerChange.ProcessId"/>).</summary>
    Followed,

    /// <summary>A server left over from the last start (held by the debugger past its restart) was ended.</summary>
    OrphanEnded,

    /// <summary>The session ended (<see cref="WatchedServerChange.End"/>).</summary>
    Ended,
}

public sealed record WatchedServerChange(WatchedServerChangeKind Kind, int? ProcessId = null, WatchedServerEnd? End = null);

/// <summary>What a <see cref="WatchedServerSession"/> needs from the IDE.</summary>
public interface IWatchedServerHost
{
    /// <summary>The next app <c>dotnet watch</c> starts (not <paramref name="previousPid"/>), once it serves; null when it doesn't (<see cref="WatchedServer.WaitForAppAsync"/>).</summary>
    Task<int?> WaitForAppAsync(int? previousPid, CancellationToken cancellationToken);

    /// <summary>Attaches the debugger to the server and opens its page in the same session; false when it couldn't.</summary>
    Task<bool> AttachAsync(int processId, CancellationToken cancellationToken);

    /// <summary>Detaches the debugger from the server, leaving it running (the page closes); completes when the session ended.</summary>
    Task ReleaseAsync();

    /// <summary>Ends the debug session, if one runs.</summary>
    Task StopDebuggingAsync();

    /// <summary>Stops <c>dotnet watch</c> (and the processes it started).</summary>
    Task StopWatchAsync();

    bool IsAlive(int processId);

    void Kill(int processId);

    void OnChanged(WatchedServerChange change);
}

/// <summary>
/// A Blazor Web App's server debugged under <c>dotnet watch</c> (#75, ADR 0040): netcoredbg attaches to the app
/// <c>dotnet watch</c> starts, and follows it when an edit restarts it. A state machine fed from the output reader's
/// thread (<see cref="HotReloadSession"/>'s events) and the debugger's end (<see cref="OnDebugEnded"/>); its state
/// changes only by compare-and-swap, so the order those arrive in doesn't matter:
/// <list type="bullet">
/// <item>"Restart is needed": the debugger detaches from the server, which then ends on the signal <c>dotnet watch</c>
/// forwards. Held by the debugger, it would outlive the restart on Linux and macOS and keep its port; ended by the IDE
/// before <c>dotnet watch</c> commits to the restart, it would make <c>dotnet watch</c> wait for a file change instead.</item>
/// <item>The debug session's end that wasn't asked for (the detach, the debugger lost, a restart the output didn't
/// announce): the session follows the next server, or ends when the app exited, <c>dotnet watch</c> exited or no server
/// came.</item>
/// <item>A new server while the last one still runs: the last one is an orphan, and is ended. TODO(issue): propose
/// upstream (dotnet/sdk) that <c>dotnet run</c> time out its wait after forwarding SIGTERM, which leaves it.</item>
/// <item>Stop (or Restart): <c>dotnet watch</c> stops, and the last server is ended if it outlived it.</item>
/// </list>
/// </summary>
public sealed class WatchedServerSession : IDisposable
{
    private readonly HotReloadSession _hotReload;
    private readonly Task _watchExited;
    private readonly IWatchedServerHost _host;
    private readonly TimeSpan _followTimeout;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state;
    private int _processId;
    private int _appEnded;
    private int _stopping;

    public WatchedServerSession(HotReloadSession hotReload, Task watchExited, IWatchedServerHost host, TimeSpan followTimeout)
    {
        _hotReload = hotReload;
        _watchExited = watchExited;
        _host = host;
        _followTimeout = followTimeout;
        hotReload.RestartAnnounced += OnRestartAnnounced;
        hotReload.AppStarted += OnAppStarted;
        hotReload.AppEnded += OnAppEnded;
        _ = watchExited.ContinueWith(_ => OnWatchExited(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    public WatchedServerState State => (WatchedServerState)Volatile.Read(ref _state);

    /// <summary>Whether the session goes on (the debugger may be between two servers).</summary>
    public bool IsActive => State is not WatchedServerState.Ended;

    /// <summary>Whether the debugger is between two servers (detaching, or waiting for the next one).</summary>
    public bool IsFollowing => State is WatchedServerState.Releasing or WatchedServerState.Following;

    /// <summary>The server the debugger attached to last.</summary>
    public int? ProcessId => Volatile.Read(ref _processId) is var id and > 0 ? id : null;

    /// <summary>Completes when the session ended (after a stop, once <c>dotnet watch</c> and the server ended).</summary>
    public Task Completion => _completion.Task;

    /// <summary>
    /// Waits for the server to serve and attaches to it. False when it didn't serve or couldn't be attached to: the
    /// session is then over, and <c>dotnet watch</c> left running for the caller's fallback.
    /// </summary>
    public async Task<bool> StartAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancel.Token);
        int? pid;
        try
        {
            pid = await _host.WaitForAppAsync(null, linked.Token).WaitAsync(timeout, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            pid = null;
        }

        if (pid is not { } found || State != WatchedServerState.Starting)
        {
            Abandon();
            return false;
        }

        Volatile.Write(ref _processId, found);
        if (!await _host.AttachAsync(found, linked.Token).ConfigureAwait(false))
        {
            Abandon();
            return false;
        }

        if (Transition(WatchedServerState.Starting, WatchedServerState.Attached))
        {
            return true;
        }

        // Stopped meanwhile (dotnet watch exited, or Stop): the stop ends the session.
        await _host.StopDebuggingAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// The debug session ended. <paramref name="requested"/>: by Stop or Restart (<see cref="DebugService.StopRequested"/>),
    /// so <c>dotnet watch</c> stops too; otherwise the session follows the next server.
    /// </summary>
    public void OnDebugEnded(bool requested)
    {
        if (requested)
        {
            if (State is not (WatchedServerState.Following or WatchedServerState.Ended))
            {
                RequestStop();
            }

            return;
        }

        if (Transition(WatchedServerState.Releasing, WatchedServerState.Following) || Transition(WatchedServerState.Attached, WatchedServerState.Following))
        {
            _ = FollowAsync();
        }
        else if (State == WatchedServerState.Stopping)
        {
            _ = StopAsync(WatchedServerEnd.Stopped);
        }
    }

    /// <summary>Stop, Restart or Stop Running Programs: <c>dotnet watch</c> and the server end. Completes with <see cref="Completion"/>.</summary>
    public Task RequestStop()
    {
        if (State == WatchedServerState.Ended)
        {
            return _completion.Task;
        }

        Volatile.Write(ref _state, (int)WatchedServerState.Stopping);
        return StopAsync(WatchedServerEnd.Stopped);
    }

    private bool Transition(WatchedServerState from, WatchedServerState to) =>
        Interlocked.CompareExchange(ref _state, (int)to, (int)from) == (int)from;

    private void OnRestartAnnounced()
    {
        if (!Transition(WatchedServerState.Attached, WatchedServerState.Releasing))
        {
            return;
        }

        _host.OnChanged(new WatchedServerChange(WatchedServerChangeKind.Following, ProcessId));
        _ = Task.Run(async () =>
        {
            try
            {
                await _host.ReleaseAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or IOException)
            {
                // The session ends anyway: its end follows the next server.
            }
        });
    }

    private void OnAppStarted(int? processId)
    {
        // A server started while the last one still runs: dotnet watch already left it, so it's an orphan.
        if (processId is not { } started || State == WatchedServerState.Starting || ProcessId is not { } last || last == started)
        {
            return;
        }

        EndOrphan(last);
    }

    private void OnAppEnded() => Volatile.Write(ref _appEnded, 1);

    private void OnWatchExited()
    {
        if (State != WatchedServerState.Ended)
        {
            Volatile.Write(ref _state, (int)WatchedServerState.Stopping);
            _ = StopAsync(WatchedServerEnd.WatchExited);
        }
    }

    private void EndOrphan(int processId)
    {
        if (_host.IsAlive(processId))
        {
            _host.Kill(processId);
            _host.OnChanged(new WatchedServerChange(WatchedServerChangeKind.OrphanEnded, processId));
        }
    }

    private async Task FollowAsync()
    {
        if (!IsFollowing || State != WatchedServerState.Following)
        {
            return;
        }

        _host.OnChanged(new WatchedServerChange(WatchedServerChangeKind.Following, ProcessId));
        var previous = ProcessId;
        int? next;
        try
        {
            next = await _host.WaitForAppAsync(previous, _cancel.Token).WaitAsync(_followTimeout, _cancel.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            next = null;
        }

        if (State != WatchedServerState.Following)
        {
            return; // stopped meanwhile
        }

        if (next is not { } pid)
        {
            Volatile.Write(ref _state, (int)WatchedServerState.Stopping);
            await StopAsync(_watchExited.IsCompleted ? WatchedServerEnd.WatchExited : Volatile.Read(ref _appEnded) != 0 ? WatchedServerEnd.AppExited : WatchedServerEnd.NotRestarted).ConfigureAwait(false);
            return;
        }

        if (previous is { } last && last != pid)
        {
            EndOrphan(last);
        }

        Volatile.Write(ref _processId, pid);
        Volatile.Write(ref _appEnded, 0);
        if (!await _host.AttachAsync(pid, _cancel.Token).ConfigureAwait(false))
        {
            if (State == WatchedServerState.Following)
            {
                Volatile.Write(ref _state, (int)WatchedServerState.Stopping);
                await StopAsync(WatchedServerEnd.AttachFailed).ConfigureAwait(false);
            }

            return;
        }

        if (Transition(WatchedServerState.Following, WatchedServerState.Attached))
        {
            _host.OnChanged(new WatchedServerChange(WatchedServerChangeKind.Followed, pid));
        }
        else
        {
            await _host.StopDebuggingAsync().ConfigureAwait(false);
        }
    }

    private async Task StopAsync(WatchedServerEnd end)
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 1)
        {
            await _completion.Task.ConfigureAwait(false);
            return;
        }

        await _cancel.CancelAsync().ConfigureAwait(false);
        Unsubscribe();
        try
        {
            await _host.StopDebuggingAsync().ConfigureAwait(false);
            await _host.StopWatchAsync().ConfigureAwait(false);
        }
        finally
        {
            // A server the debugger held past dotnet watch's end isn't in its process tree any more.
            if (ProcessId is { } last && _host.IsAlive(last))
            {
                _host.Kill(last);
            }

            Volatile.Write(ref _state, (int)WatchedServerState.Ended);
            _host.OnChanged(new WatchedServerChange(WatchedServerChangeKind.Ended, End: end));
            _completion.TrySetResult();
        }
    }

    /// <summary>The start failed: the session is over without stopping <c>dotnet watch</c> (the caller's fallback keeps it).</summary>
    private void Abandon()
    {
        if (Volatile.Read(ref _stopping) == 1)
        {
            return; // a stop ends the session
        }

        Unsubscribe();
        _cancel.Cancel();
        Volatile.Write(ref _state, (int)WatchedServerState.Ended);
        _completion.TrySetResult();
    }

    /// <summary>Frees the session's cancellation once it's over (<see cref="Completion"/>).</summary>
    public void Dispose()
    {
        Unsubscribe();
        _cancel.Dispose();
    }

    private void Unsubscribe()
    {
        _hotReload.RestartAnnounced -= OnRestartAnnounced;
        _hotReload.AppStarted -= OnAppStarted;
        _hotReload.AppEnded -= OnAppEnded;
    }
}
