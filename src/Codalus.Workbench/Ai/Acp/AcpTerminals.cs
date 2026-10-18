using System.Globalization;
using System.Text;
using Codalus.Rpc;

namespace Codalus.Workbench.Ai.Acp;

/// <summary>
/// The commands an agent runs through <c>terminal/*</c> (M48, ADR 0079), after the user allowed each one: started through
/// the IDE's process launcher in a workspace folder, their output kept within the agent's byte limit (cut from the start,
/// as the protocol says), stopped after <c>ai.agent.commandTimeoutSeconds</c>, killed when the turn is cancelled, and
/// released when the agent asks. What the agent reads back is redacted.
/// </summary>
public sealed class AcpTerminals : IAsyncDisposable
{
    /// <summary>The most output kept when the agent sets no limit.</summary>
    public const int DefaultOutputLimit = 1_000_000;

    private readonly Dictionary<string, AcpTerminal> _terminals = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly IProcessLauncher _launcher;
    private int _next;

    public AcpTerminals(IProcessLauncher? launcher = null) => _launcher = launcher ?? LocalProcessLauncher.Instance;

    /// <summary>How long a command may run before it is stopped.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>A terminal's output grew or its command ended (any thread).</summary>
    public event EventHandler<AcpTerminal>? Changed;

    /// <summary>
    /// The command line the user sees and the process runs: <paramref name="command"/> with its arguments, or, when it comes
    /// without arguments and has spaces, a shell line (through <c>/bin/sh -c</c> or <c>cmd /c</c>, as agent mode's commands).
    /// </summary>
    public static (string Display, ProcessLaunchInfo Launch) CommandLine(string command, IReadOnlyList<string> args, string folder, IReadOnlyDictionary<string, string>? environment)
    {
        if (args.Count == 0 && command.Any(char.IsWhiteSpace))
        {
            var shell = OperatingSystem.IsWindows()
                ? new ProcessLaunchInfo("cmd.exe", ["/d", "/s", "/c", command])
                : new ProcessLaunchInfo("/bin/sh", ["-c", command]);
            return (command, shell with { WorkingDirectory = folder, Environment = environment });
        }

        var display = string.Join(' ', new[] { command }.Concat(args).Select(Quote));
        return (display, new ProcessLaunchInfo(command, args) { WorkingDirectory = folder, Environment = environment });
    }

    private static string Quote(string arg) =>
        arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '&' or '|' or ';' or '<' or '>' or '$' or '`') ? arg : "\"" + arg.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>Starts an allowed command and returns its terminal.</summary>
    public async Task<AcpTerminal> StartAsync(string display, ProcessLaunchInfo launch, long? outputByteLimit, CancellationToken cancellationToken)
    {
        IHostProcess process;
        try
        {
            process = await _launcher.StartAsync(launch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new AcpException($"The command couldn't start: {ex.Message}");
        }

        string id;
        lock (_gate)
        {
            id = "term_" + (++_next).ToString(CultureInfo.InvariantCulture);
        }

        var limit = outputByteLimit is > 0 ? (int)Math.Min(outputByteLimit.Value, int.MaxValue) : DefaultOutputLimit;
        var terminal = new AcpTerminal(id, display, launch.WorkingDirectory ?? "", process, limit, Timeout, t => Changed?.Invoke(this, t));
        lock (_gate)
        {
            _terminals[id] = terminal;
        }

        terminal.Start();
        return terminal;
    }

    public AcpTerminal Get(string id)
    {
        lock (_gate)
        {
            return _terminals.TryGetValue(id, out var terminal) ? terminal : throw new AcpException($"There is no terminal '{id}' (it was released, or never created).");
        }
    }

    public AcpTerminal? Find(string id)
    {
        lock (_gate)
        {
            return _terminals.GetValueOrDefault(id);
        }
    }

    /// <summary>Kills the command if it still runs and forgets the terminal.</summary>
    public async Task ReleaseAsync(string id)
    {
        AcpTerminal? terminal;
        lock (_gate)
        {
            _terminals.Remove(id, out terminal);
        }

        if (terminal is not null)
        {
            await terminal.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Kills every running command (the turn was cancelled); the terminals stay until released.</summary>
    public void KillAll()
    {
        List<AcpTerminal> all;
        lock (_gate)
        {
            all = [.. _terminals.Values];
        }

        foreach (var terminal in all)
        {
            terminal.Kill("cancelled");
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<AcpTerminal> all;
        lock (_gate)
        {
            all = [.. _terminals.Values];
            _terminals.Clear();
        }

        foreach (var terminal in all)
        {
            await terminal.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>One command an agent runs: its output so far and how it ended.</summary>
public sealed class AcpTerminal : IAsyncDisposable
{
    private readonly IHostProcess _process;
    private readonly int _limit;
    private readonly TimeSpan _timeout;
    private readonly Action<AcpTerminal> _changed;
    private readonly StringBuilder _output = new();
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource<AcpTerminalExitStatus> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private int _bytes;
    private bool _truncated;
    private string? _killedBy;
    private Task _reading = Task.CompletedTask;

    internal AcpTerminal(string id, string commandLine, string folder, IHostProcess process, int limit, TimeSpan timeout, Action<AcpTerminal> changed)
    {
        Id = id;
        CommandLine = commandLine;
        Folder = folder;
        _process = process;
        _limit = limit;
        _timeout = timeout;
        _changed = changed;
    }

    public string Id { get; }

    /// <summary>The command line the user allowed.</summary>
    public string CommandLine { get; }

    public string Folder { get; }

    /// <summary>The exit status once the command ended, else null.</summary>
    public AcpTerminalExitStatus? ExitStatus => _exit.Task.IsCompletedSuccessfully ? _exit.Task.Result : null;

    public Task<AcpTerminalExitStatus> Exited => _exit.Task;

    /// <summary>The output so far (raw; the agent and the chat get it redacted) and whether its start was cut.</summary>
    public (string Text, bool Truncated) Output
    {
        get
        {
            lock (_gate)
            {
                return (_output.ToString(), _truncated);
            }
        }
    }

    internal void Start()
    {
        try
        {
            _process.Input.Close();
        }
        catch (IOException)
        {
        }

        _process.ErrorLine += line => Append(line + "\n");
        _process.BeginErrorRead();
        _reading = Task.Run(ReadAsync);
        _ = WatchAsync();
    }

    private async Task ReadAsync()
    {
        var decoder = new UTF8Encoding(false).GetDecoder();
        var bytes = new byte[8192];
        var chars = new char[8192 + 4];
        try
        {
            while (true)
            {
                var read = await _process.Output.ReadAsync(bytes, _stop.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                Append(new string(chars, 0, count));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private async Task WatchAsync()
    {
        int? code = null;
        try
        {
            code = await _process.Exited.WaitAsync(_timeout, _stop.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Kill("timeout");
            code = await _process.Exited.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            code = _process.Exited.IsCompleted ? await _process.Exited.ConfigureAwait(false) : null;
        }

        // The output's end, briefly: a grandchild holding the pipe mustn't hold the exit.
        await _reading.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_killedBy is { } reason)
        {
            Append(reason switch
            {
                "timeout" => string.Create(CultureInfo.InvariantCulture, $"\n[Codalus IDE stopped the command after {_timeout.TotalSeconds:0} seconds.]\n"),
                "cancelled" => "\n[Codalus IDE stopped the command: the turn was cancelled.]\n",
                _ => "\n[The command was stopped.]\n",
            });
        }

        _exit.TrySetResult(new AcpTerminalExitStatus(_killedBy is null ? code : null, _killedBy is null ? null : "SIGKILL"));
        _changed(this);
    }

    private void Append(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            _output.Append(text);
            _bytes += Encoding.UTF8.GetByteCount(text);
            if (_bytes > _limit)
            {
                // Cut from the start, at a character boundary (never between a surrogate pair).
                var drop = 0;
                var dropped = 0;
                while (drop < _output.Length && _bytes - dropped > _limit)
                {
                    var c = _output[drop];
                    var width = char.IsHighSurrogate(c) && drop + 1 < _output.Length ? 2 : 1;
                    dropped += Encoding.UTF8.GetByteCount(_output.ToString(drop, width));
                    drop += width;
                }

                _output.Remove(0, drop);
                _bytes -= dropped;
                _truncated = true;
            }
        }

        _changed(this);
    }

    /// <summary>Kills the command (and its children) if it still runs.</summary>
    public void Kill(string reason = "killed")
    {
        if (_exit.Task.IsCompleted)
        {
            return;
        }

        _killedBy ??= reason;
        _process.Kill();
    }

    public async ValueTask DisposeAsync()
    {
        Kill("released");
        await ((Task)_exit.Task).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await _stop.CancelAsync().ConfigureAwait(false);
        await _process.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
