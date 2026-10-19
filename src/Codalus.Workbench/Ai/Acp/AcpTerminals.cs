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

    /// <summary>The most output kept whatever limit the agent asks for.</summary>
    public const int MaxOutputLimit = 4_000_000;

    /// <summary>Variables that change what a command runs (the program found, the code it loads, how git or a runtime behaves).</summary>
    private static readonly string[] s_refusedNames = ["PATH", "PATHEXT", "COMSPEC", "SHELL", "BASH_ENV", "ENV", "ZDOTDIR", "SHELLOPTS", "BASHOPTS", "PS4", "PROMPT_COMMAND", "IFS", "CDPATH", "HOME", "USERPROFILE", "NODE_OPTIONS", "NODE_PATH", "PYTHONPATH", "PYTHONSTARTUP", "PYTHONHOME", "PERL5OPT", "PERL5LIB", "PERLLIB", "RUBYOPT", "RUBYLIB", "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS", "CLASSPATH", "MSBUILDUSERPATH"];

    private static readonly string[] s_refusedPrefixes = ["LD_", "DYLD_", "GIT_", "NPM_CONFIG_", "YARN_", "DOTNET_", "COMPLUS_", "CORECLR_", "MSBUILD", "NUGET_", "BUNDLE_", "GEM_", "CARGO_", "RUSTC", "GOFLAGS", "SSH_", "PIP_", "CONDA_"];

    /// <summary>
    /// The first of <paramref name="names"/> that changes what a command runs (compared ignoring case, as Windows does), or
    /// null: an agent may set ordinary variables for a command it asks to run, which the approval shows, but not these.
    /// </summary>
    public static string? RefusedVariable(IEnumerable<string> names) =>
        names.FirstOrDefault(name => name.Length == 0 || name.Contains('=', StringComparison.Ordinal)
            || s_refusedNames.Contains(name, StringComparer.OrdinalIgnoreCase)
            || s_refusedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

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
    /// On Windows a script on the PATH (<c>npm</c>, <c>npx</c>) starts through <see cref="WindowsCommand"/>, which refuses one
    /// <c>cmd.exe</c> would misread (<see cref="AcpException"/>).
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
        var launch = OperatingSystem.IsWindows() ? WindowsCommand.Launch(command, args, System.Environment.GetEnvironmentVariable("PATH")) : new ProcessLaunchInfo(command, args);
        return (display, launch with { WorkingDirectory = folder, Environment = environment });
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

        var limit = outputByteLimit is > 0 ? (int)Math.Min(outputByteLimit.Value, MaxOutputLimit) : DefaultOutputLimit;
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
    private long _lastNotified;
    private int _notifyPending;
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

    /// <summary>The most output it keeps (the agent's limit, at most <see cref="AcpTerminals.MaxOutputLimit"/>).</summary>
    public int OutputLimit => _limit;

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

        // The output's end, standard output and standard error both, briefly: a grandchild holding a pipe mustn't hold the
        // exit. Standard error is read line by line on its own, and its last lines can come after the exit: without
        // waiting for its end, an agent that read the output once the exit was reported could miss them.
        await Task.WhenAll(_reading, _process.ErrorCompleted).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
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

    /// <summary>How often the output's growth is reported (the step re-renders, redacting the tail, on each report).</summary>
    internal static TimeSpan NotifyInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Reports the output grew, at most once per <see cref="NotifyInterval"/>: a chatty command doesn't re-render per chunk.</summary>
    private void NotifyGrew()
    {
        var now = Environment.TickCount64;
        var wait = Interlocked.Read(ref _lastNotified) + (long)NotifyInterval.TotalMilliseconds - now;
        if (wait <= 0 && Interlocked.CompareExchange(ref _notifyPending, 0, 0) == 0)
        {
            Interlocked.Exchange(ref _lastNotified, now);
            _changed(this);
            return;
        }

        if (Interlocked.Exchange(ref _notifyPending, 1) == 0)
        {
            _ = Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, wait))).ContinueWith(_ =>
            {
                Interlocked.Exchange(ref _lastNotified, Environment.TickCount64);
                Interlocked.Exchange(ref _notifyPending, 0);
                _changed(this);
            }, TaskScheduler.Default);
        }
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
                // Cut from the start, at a character boundary (never between a surrogate pair), counting UTF-8 bytes per char.
                var drop = 0;
                var dropped = 0;
                while (drop < _output.Length && _bytes - dropped > _limit)
                {
                    var c = _output[drop];
                    if (char.IsHighSurrogate(c) && drop + 1 < _output.Length)
                    {
                        dropped += 4;
                        drop += 2;
                    }
                    else
                    {
                        dropped += c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
                        drop++;
                    }
                }

                _output.Remove(0, drop);
                _bytes -= dropped;
                _truncated = true;
            }
        }

        NotifyGrew();
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
