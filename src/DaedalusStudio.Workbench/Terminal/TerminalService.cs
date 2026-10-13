using DaedalusStudio.Platform;
using DaedalusStudio.Platform.Pty;
using DaedalusStudio.Terminal;

namespace DaedalusStudio.Workbench.Terminal;

/// <summary>
/// A terminal profile. <see cref="Shell"/> names the shell family for shell integration (<c>bash</c>, <c>zsh</c>,
/// <c>fish</c>, <c>pwsh</c>, <c>powershell</c>, <c>cmd</c>, <c>sh</c>, or <c>other</c>).
/// <see cref="DotnetDeveloper"/> puts the solution's SDK first on the PATH.
/// </summary>
public sealed record TerminalProfile(string Id, string Name, string Executable, IReadOnlyList<string> Arguments, string Shell, bool DotnetDeveloper = false);

/// <summary>The shells installed on this machine, as terminal profiles (ADR 0014).</summary>
public static class TerminalProfiles
{
    public static IReadOnlyList<TerminalProfile> Detect(PlatformKind platform, Func<string, string?>? resolve = null)
    {
        resolve ??= file => PseudoTerminal.ResolveExecutable(file);
        var profiles = new List<TerminalProfile>();
        void Add(string id, string name, string? path, string shell, params string[] arguments)
        {
            if (path is not null && profiles.All(p => p.Id != id))
            {
                profiles.Add(new TerminalProfile(id, name, path, arguments, shell));
            }
        }

        if (platform == PlatformKind.Windows)
        {
            Add("pwsh", "PowerShell", resolve("pwsh.exe"), "pwsh", "-NoLogo");
            Add("powershell", "Windows PowerShell", resolve("powershell.exe"), "powershell", "-NoLogo");
            Add("cmd", "Command Prompt", resolve("cmd.exe"), "cmd");
            var gitBash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
            Add("gitbash", "Git Bash", File.Exists(gitBash) ? gitBash : null, "bash", "--login", "-i");
        }
        else
        {
            var login = Environment.GetEnvironmentVariable("SHELL");
            if (login is { Length: > 0 } && File.Exists(login))
            {
                var name = Path.GetFileName(login);
                Add(name, name, login, ShellFamily(name));
            }

            foreach (var shell in new[] { "bash", "zsh", "fish", "pwsh", "sh" })
            {
                Add(shell, shell, resolve(shell), ShellFamily(shell));
            }
        }

        if (profiles.Count > 0)
        {
            var first = profiles[0];
            profiles.Add(first with { Id = "dotnet", Name = ".NET Developer (" + first.Name + ")", DotnetDeveloper = true });
        }

        return profiles;
    }

    public static string ShellFamily(string name) => Path.GetFileNameWithoutExtension(name).ToLowerInvariant() switch
    {
        "bash" => "bash",
        "zsh" => "zsh",
        "fish" => "fish",
        "pwsh" => "pwsh",
        "powershell" => "powershell",
        "cmd" => "cmd",
        "sh" or "dash" => "sh",
        _ => "other",
    };
}

/// <summary>
/// A terminal: a program in a pseudo-terminal and the screen its output draws. Created by <see cref="TerminalService"/>.
/// Output is parsed on the PTY's reader thread; read the screen under <see cref="VirtualTerminal.SyncRoot"/>.
/// </summary>
public sealed class TerminalSession : IDisposable
{
    private readonly PseudoTerminal _pty;
    private readonly System.Text.Decoder _decoder = System.Text.Encoding.UTF8.GetDecoder();
    private readonly TaskCompletionSource _firstOutput = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    internal TerminalSession(int id, string name, PseudoTerminal pty, VirtualTerminal terminal, string workingDirectory, Action<string>? outputText = null)
    {
        OutputText = outputText; // before the PTY delivers anything, so a program's first output isn't missed
        Id = id;
        Name = name;
        _pty = pty;
        Terminal = terminal;
        InitialDirectory = workingDirectory;
        terminal.Reply += Write;
        terminal.TitleChanged += () => TitleChanged?.Invoke(this, EventArgs.Empty);
        pty.Output += data =>
        {
            Terminal.Feed(data.Span);
            _firstOutput.TrySetResult();
            if (OutputText is { } handler)
            {
                var chars = new char[_decoder.GetCharCount(data.Span, flush: false)];
                var count = _decoder.GetChars(data.Span, chars, flush: false);
                if (count > 0)
                {
                    handler(new string(chars, 0, count));
                }
            }
        };
        pty.Exited += code =>
        {
            _firstOutput.TrySetResult();
            Exited?.Invoke(this, code);
        };
    }

    /// <summary>
    /// Completes when the program has written something (a shell's prompt), or exited, or after <paramref name="timeout"/>.
    /// Text sent before a shell is ready can be dropped: some shells flush pending input when they set up the terminal.
    /// </summary>
    public async Task WhenStartedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _firstOutput.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }

    public int Id { get; }

    /// <summary>The profile name (or the program's name for "run in terminal").</summary>
    public string Name { get; }

    public VirtualTerminal Terminal { get; }

    public string InitialDirectory { get; }

    public int ProcessId => _pty.ProcessId;

    public int? ExitCode => _pty.ExitCode;

    /// <summary>The program's title (OSC 0/2), else the name.</summary>
    public string Title => Terminal.Title is { Length: > 0 } title ? title : Name;

    /// <summary>The shell's current directory (shell integration), else where it started.</summary>
    public string CurrentDirectory => Terminal.WorkingDirectory ?? InitialDirectory;

    public event EventHandler? TitleChanged;

    /// <summary>The program exited (raised on a background thread).</summary>
    public event EventHandler<int>? Exited;

    /// <summary>Output as text (UTF-8 decoded, escape sequences included), on the reader thread. For plugins (API 0.2.3).</summary>
    public event Action<string>? OutputText;

    public void Write(string text)
    {
        if (!_disposed && _pty.ExitCode is null)
        {
            _pty.Write(text);
        }
    }

    public void Resize(int columns, int rows)
    {
        Terminal.Resize(columns, rows);
        if (!_disposed && _pty.ExitCode is null)
        {
            _pty.Resize(columns, rows);
        }
    }

    public void Kill() => _pty.Kill();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pty.Dispose();
    }
}

/// <summary>
/// The integrated terminals (ADR 0014): starts profiles in pseudo-terminals, with shell integration for bash, zsh and
/// PowerShell, and runs programs in a terminal (Run Without Debugging with <c>run.console</c> set to the terminal).
/// </summary>
public sealed class TerminalService : IDisposable
{
    private readonly List<TerminalSession> _sessions = [];
    private readonly Lock _gate = new();
    private int _nextId = 1;

    /// <param name="integrationScripts">The folder with the shell integration scripts (<c>terminal/</c> next to the shell).</param>
    public TerminalService(PlatformKind platform, string? integrationScripts)
    {
        Platform = platform;
        IntegrationScripts = integrationScripts is not null && Directory.Exists(integrationScripts) ? integrationScripts : null;
        Profiles = TerminalProfiles.Detect(platform);
    }

    public PlatformKind Platform { get; }

    public string? IntegrationScripts { get; }

    public IReadOnlyList<TerminalProfile> Profiles { get; set; }

    public int Scrollback { get; set; } = VirtualTerminal.DefaultScrollback;

    public bool ShellIntegration { get; set; } = true;

    /// <summary>
    /// Starts pseudo-terminals somewhere else: a remote window's server (M21, ADR 0050). The start info's environment
    /// is this machine's plus the terminal's own variables; the factory sends only what differs.
    /// </summary>
    public Func<PtyStartInfo, PseudoTerminal>? PtyFactory { get; set; }

    /// <summary>The SDK the solution builds with (<c>dotnet</c>'s folder), for the .NET developer profile.</summary>
    public string? DotnetRoot { get; set; }

    public IReadOnlyList<TerminalSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessions];
            }
        }
    }

    public event EventHandler<TerminalSession>? Added;

    public event EventHandler<TerminalSession>? Removed;

    public TerminalProfile? FindProfile(string? id) => Profiles.FirstOrDefault(p => p.Id == id) ?? (Profiles.Count > 0 ? Profiles[0] : null);

    /// <summary>
    /// Starts a shell of <paramref name="profile"/> in <paramref name="workingDirectory"/>. <paramref name="outputText"/>
    /// receives its output as text from the start (see <see cref="TerminalSession.OutputText"/>).
    /// </summary>
    public TerminalSession Start(TerminalProfile profile, string workingDirectory, int columns = 120, int rows = 30, Action<string>? outputText = null)
    {
        var environment = BaseEnvironment();
        var arguments = new List<string>(profile.Arguments);
        if (ShellIntegration && IntegrationScripts is { } scripts)
        {
            ApplyShellIntegration(profile, scripts, arguments, environment);
        }

        if (profile.DotnetDeveloper && DotnetRoot is { } root)
        {
            var key = environment.Keys.FirstOrDefault(k => k.Equals("PATH", StringComparison.OrdinalIgnoreCase)) ?? "PATH";
            environment[key] = root + Path.PathSeparator + environment.GetValueOrDefault(key);
            environment["DOTNET_ROOT"] = root;
        }

        return Launch(profile.Name, profile.Executable, arguments, workingDirectory, environment, columns, rows, outputText);
    }

    /// <summary>Runs a program (not a shell) in a new terminal, e.g. a console app for Run Without Debugging.</summary>
    public TerminalSession Run(string name, string program, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string>? environment, int columns = 120, int rows = 30, Action<string>? outputText = null)
    {
        var env = BaseEnvironment();
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            env[key] = value;
        }

        return Launch(name, program, arguments, workingDirectory, env, columns, rows, outputText);
    }

    /// <summary>
    /// Runs a program in a new terminal for the debugger (M14c, DAP <c>runInTerminal</c>): a null variable is removed
    /// from the environment the terminal would otherwise inherit.
    /// </summary>
    public TerminalSession Run(DaedalusStudio.Debugging.TerminalLaunch launch, int columns = 120, int rows = 30)
    {
        var env = BaseEnvironment();
        foreach (var (key, value) in launch.Environment)
        {
            if (value is null)
            {
                env.Remove(key);
            }
            else
            {
                env[key] = value;
            }
        }

        return Launch(launch.Title, launch.Program, launch.Arguments, launch.WorkingDirectory, env, columns, rows, null);
    }

    private TerminalSession Launch(string name, string program, IReadOnlyList<string> arguments, string workingDirectory, Dictionary<string, string> environment, int columns, int rows, Action<string>? outputText)
    {
        var terminal = new VirtualTerminal(columns, rows, Scrollback);
        var pty = PtyFactory is { } factory
            ? factory(new PtyStartInfo(program, arguments, workingDirectory, environment, columns, rows))
            : PseudoTerminal.Start(new PtyStartInfo(program, arguments, Directory.Exists(workingDirectory) ? workingDirectory : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), environment, columns, rows));
        TerminalSession session;
        lock (_gate)
        {
            session = new TerminalSession(_nextId++, name, pty, terminal, workingDirectory, outputText);
            _sessions.Add(session);
        }

        Added?.Invoke(this, session);
        return session;
    }

    public void Close(TerminalSession session)
    {
        bool removed;
        lock (_gate)
        {
            removed = _sessions.Remove(session);
        }

        session.Dispose();
        if (removed)
        {
            Removed?.Invoke(this, session);
        }
    }

    private static Dictionary<string, string> BaseEnvironment()
    {
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? string.Empty, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        environment["TERM"] = "xterm-256color";
        environment["COLORTERM"] = "truecolor";
        environment["TERM_PROGRAM"] = "daedalus";
        environment.Remove("DOTNET_HOST_PATH");
        return environment;
    }

    /// <summary>
    /// Starts bash, zsh and PowerShell with our script, which loads the user's own startup files and then reports the
    /// working directory (OSC 7) and command marks (OSC 133).
    /// </summary>
    internal static void ApplyShellIntegration(TerminalProfile profile, string scripts, List<string> arguments, Dictionary<string, string> environment)
    {
        switch (profile.Shell)
        {
            case "bash":
                // A login shell ignores --init-file, so the script loads the login files itself when asked to.
                if (arguments.Remove("--login") | arguments.Remove("-l"))
                {
                    environment["DAEDALUS_BASH_LOGIN"] = "1";
                }

                arguments.Remove("-i");
                arguments.InsertRange(0, ["--init-file", Path.Combine(scripts, "daedalus-bash.sh"), "-i"]);
                break;
            case "zsh":
                environment["DAEDALUS_USER_ZDOTDIR"] = environment.GetValueOrDefault("ZDOTDIR") ?? environment.GetValueOrDefault("HOME") ?? string.Empty;
                environment["ZDOTDIR"] = Path.Combine(scripts, "zsh");
                break;
            case "pwsh":
            case "powershell":
                arguments.AddRange(["-NoExit", "-Command", $". '{Path.Combine(scripts, "daedalus.ps1").Replace("'", "''", StringComparison.Ordinal)}'"]);
                break;
        }
    }

    public void Dispose()
    {
        foreach (var session in Sessions)
        {
            session.Dispose();
        }
    }
}
