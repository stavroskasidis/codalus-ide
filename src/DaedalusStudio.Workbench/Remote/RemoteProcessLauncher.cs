using System.Text;
using System.Threading.Channels;
using DaedalusStudio.Platform.Pty;
using DaedalusStudio.Remote.Protocol;
using DaedalusStudio.Rpc;

namespace DaedalusStudio.Workbench.Remote;

/// <summary>
/// Starts service processes through the server (M21, ADR 0050): a <see cref="SupervisedProcess"/> whose launch has
/// this launcher runs on the remote machine, its JSON on a channel with paths rewritten both ways, and is restarted
/// there like a local host. The file name, arguments and working folder are mapped to the remote machine's paths.
/// </summary>
public sealed class RemoteProcessLauncher(RemoteConnection connection, RemotePathMap map, bool mapJson = true) : IProcessLauncher
{
    public async Task<IHostProcess> StartAsync(ProcessLaunchInfo launch, CancellationToken cancellationToken)
    {
        if (!connection.IsConnected)
        {
            throw new IOException($"The connection to {connection.Display} is closed.");
        }

        var parameters = new SpawnParams(
            0,
            map.ToRemote(launch.FileName),
            [.. launch.Arguments.Select(map.ToRemote)],
            launch.WorkingDirectory is { } directory ? map.ToRemote(directory) : null,
            launch.Environment?.ToDictionary(e => e.Key, e => map.ToRemote(e.Value), StringComparer.Ordinal));
        try
        {
            return await connection.SpawnAsync(parameters, cancellationToken, mapJson ? map : null).ConfigureAwait(false);
        }
        catch (JsonRpcRemoteException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }
}

/// <summary>
/// A terminal on the remote machine (M21): the server's pseudo-terminal, its bytes on a channel. It is usable at once:
/// input typed before the server answered is sent once the terminal runs. Environment variables that are only this
/// machine's aren't sent, just the ones the terminal service set itself.
/// </summary>
public sealed class RemotePseudoTerminal : PseudoTerminal
{
    private readonly RemoteConnection _connection;
    private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<ChannelProcess?> _process = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private (int Columns, int Rows)? _pendingSize;

    private RemotePseudoTerminal(RemoteConnection connection) => _connection = connection;

    /// <summary>Starts <paramref name="info"/> on the server (with <paramref name="loginShell"/>, the user's login shell).</summary>
    public static RemotePseudoTerminal Start(RemoteConnection connection, RemotePathMap map, PtyStartInfo info, bool loginShell)
    {
        var terminal = new RemotePseudoTerminal(connection);
        var environment = info.Environment
            .Where(e => !string.Equals(Environment.GetEnvironmentVariable(e.Key), e.Value, StringComparison.Ordinal))
            .ToDictionary(e => e.Key, e => map.ToRemote(e.Value), StringComparer.Ordinal);
        var parameters = new PtyOpenParams(0, loginShell ? null : map.ToRemote(info.FileName), [.. info.Arguments.Select(map.ToRemote)], map.ToRemote(info.WorkingDirectory), environment, info.Columns, info.Rows);
        _ = Task.Run(() => terminal.RunAsync(parameters));
        return terminal;
    }

    /// <summary>Completes once the server started the program (or failed to).</summary>
    public Task Started => _process.Task;

    private async Task RunAsync(PtyOpenParams parameters)
    {
        ChannelProcess process;
        try
        {
            process = await _connection.OpenTerminalAsync(parameters, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonRpcRemoteException or JsonRpcConnectionLostException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            _process.TrySetResult(null);
            RaiseOutput(Encoding.UTF8.GetBytes($"The terminal couldn't start on {_connection.Display}: {ex.Message}\r\n"));
            RaiseExited(-1);
            return;
        }

        ProcessId = process.Id ?? 0;
        _process.TrySetResult(process);
        if (_pendingSize is { } size)
        {
            Resize(size.Columns, size.Rows);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in _input.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
                {
                    await process.Channel.WriteAsync(chunk, _lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        });

        var buffer = new byte[16 * 1024];
        try
        {
            while (await process.Channel.ReadAsync(buffer, _lifetime.Token).ConfigureAwait(false) is var read and > 0)
            {
                RaiseOutput(buffer.AsSpan(0, read).ToArray());
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }

        // The output ended: the program exited (its code comes as a notification, which may lag the last bytes).
        var code = await Task.WhenAny(process.Exited, Task.Delay(2000)).ConfigureAwait(false) == process.Exited ? process.Exited.Result : null;
        RaiseExited(code ?? -1);
    }

    public override void Write(ReadOnlySpan<byte> data) => _input.Writer.TryWrite(data.ToArray());

    public override void Resize(int columns, int rows)
    {
        if (_process.Task is { IsCompletedSuccessfully: true, Result: { } process })
        {
            _ = _connection.ResizeTerminalAsync(process.Channel.Id, columns, rows).ContinueWith(static _ => { }, TaskScheduler.Default);
        }
        else
        {
            _pendingSize = (columns, rows);
        }
    }

    public override void Kill()
    {
        if (_process.Task is { IsCompletedSuccessfully: true, Result: { } process })
        {
            process.Kill();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Kill();
            _input.Writer.TryComplete();
            _lifetime.Cancel();
            if (_process.Task is { IsCompletedSuccessfully: true, Result: { } process })
            {
                _ = process.DisposeAsync().AsTask();
            }
        }
    }
}
