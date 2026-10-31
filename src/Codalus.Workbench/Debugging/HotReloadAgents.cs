using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using Codalus.LanguageServices.Protocol;

namespace Codalus.Workbench.Debugging;

/// <summary>
/// The IDE's side of the startup hook's Hot Reload agent (ADR 0090): programs the Codalus debugger launches connect to
/// this pipe (<see cref="PipeVariable"/>), and after each edit the debugger applied, <see cref="NotifyAsync"/> tells
/// every one of them its updated types, so it calls the program's <c>[MetadataUpdateHandler]</c>s (Blazor re-renders
/// then). The debugging API applies deltas without them, unlike <c>dotnet watch</c>'s agent.
/// </summary>
public sealed class HotReloadAgents : IAsyncDisposable
{
    public const string PipeVariable = "CODALUS_HOTRELOAD_PIPE";

    private const int BufferSize = 64 * 1024;

    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly List<Agent> _agents = [];
    private Task? _accepting;

    /// <summary>The pipe's name, unique to this IDE.</summary>
    public string PipeName { get; } = "codalus-hotreload-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>The programs connected now.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _agents.Count;
            }
        }
    }

    /// <summary>The variable a launched program's agent connects with; starts listening on first use.</summary>
    public KeyValuePair<string, string> Variable()
    {
        lock (_gate)
        {
            _accepting ??= Task.Run(AcceptAsync);
        }

        return new(PipeVariable, PipeName);
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            // Buffered: with none, a write on Windows waits until the program reads it, which one held at a breakpoint doesn't.
            var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, BufferSize, BufferSize);
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                if (_stop.IsCancellationRequested)
                {
                    return;
                }

                continue;
            }

            lock (_gate)
            {
                _agents.Add(new Agent(pipe));
            }
        }
    }

    /// <summary>
    /// Tells every connected program an edit's updated types, and waits up to <paramref name="timeout"/> for each to say
    /// its handlers ran. A program stopped at a breakpoint answers once it runs: the wait then ends with the others. The
    /// handlers' errors, one per program that had any; a program that ended is forgotten.
    /// </summary>
    public async Task<IReadOnlyList<string>> NotifyAsync(IReadOnlyList<HotReloadModuleUpdate> updates, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Agent[] agents;
        lock (_gate)
        {
            agents = [.. _agents];
        }

        if (agents.Length == 0)
        {
            return [];
        }

        var line = "update " + string.Join(";", updates.Select(u => u.ModuleId.ToString("D") + ":" + string.Join(",", u.UpdatedTypes.Select(t => t.ToString(CultureInfo.InvariantCulture)))));
        var answers = await Task.WhenAll(agents.Select(a => a.SendAsync(line, timeout, cancellationToken))).ConfigureAwait(false);
        lock (_gate)
        {
            _agents.RemoveAll(a => a.Ended);
        }

        return [.. answers.Where(a => a is { Length: > 0 } && a.StartsWith("error ", StringComparison.Ordinal)).Select(a => a!["error ".Length..])];
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        Agent[] agents;
        lock (_gate)
        {
            agents = [.. _agents];
            _agents.Clear();
        }

        foreach (var agent in agents)
        {
            await agent.DisposeAsync().ConfigureAwait(false);
        }

        if (_accepting is { } accepting)
        {
            await accepting.ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    /// <summary>
    /// One program's agent: a line out per edit, a line back per edit, in order. Lines go out from a loop of their own,
    /// so an edit never waits on a program that doesn't read (one held at a breakpoint); an answer that comes after its
    /// wait ended is read and dropped.
    /// </summary>
    private sealed class Agent : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly Channel<byte[]> _lines = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Lock _gate = new();
        private readonly Queue<TaskCompletionSource<string?>> _waiting = new();
        private readonly Task _reading;
        private readonly Task _writing;

        public Agent(NamedPipeServerStream pipe)
        {
            _pipe = pipe;
            _reading = Task.Run(ReadAsync);
            _writing = Task.Run(WriteAsync);
        }

        private async Task WriteAsync()
        {
            try
            {
                await foreach (var line in _lines.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    await _pipe.WriteAsync(line).ConfigureAwait(false);
                    await _pipe.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The program ended: its reader sees it too and ends the waits.
            }
        }

        public bool Ended { get; private set; }

        private async Task ReadAsync()
        {
            using var reader = new StreamReader(_pipe, new UTF8Encoding(false), leaveOpen: true);
            try
            {
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    lock (_gate)
                    {
                        if (_waiting.TryDequeue(out var answer))
                        {
                            answer.TrySetResult(line);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }

            lock (_gate)
            {
                Ended = true;
                while (_waiting.TryDequeue(out var answer))
                {
                    answer.TrySetResult(null);
                }
            }
        }

        public async Task<string?> SendAsync(string line, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (Ended)
                {
                    return null;
                }

                // In the lock: answers come back in the order the lines go out.
                _waiting.Enqueue(answer);
                _lines.Writer.TryWrite(Encoding.UTF8.GetBytes(line + "\n"));
            }

            try
            {
                return await answer.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _lines.Writer.TryComplete();
            await _pipe.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(_reading, _writing).ConfigureAwait(false);
        }
    }
}
