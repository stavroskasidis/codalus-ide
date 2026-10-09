namespace DaedalusStudio.Platform.Pty;

/// <summary>What to start in a pseudo-terminal.</summary>
public sealed record PtyStartInfo(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment, int Columns, int Rows);

/// <summary>
/// A program running in a pseudo-terminal (ADR 0014): ConPTY on Windows (10 1809 and later), <c>openpty</c> and
/// <c>posix_spawn</c> on Linux and macOS. Output arrives on a reader thread through <see cref="Output"/>; <see cref="Exited"/> fires once with the
/// exit code after the output has been read.
/// </summary>
public abstract class PseudoTerminal : IDisposable
{
    private readonly Lock _outputGate = new();
    private List<ReadOnlyMemory<byte>>? _early = [];
    private Action<ReadOnlyMemory<byte>>? _output;
    private int _exitRaised;

    public int ProcessId { get; protected set; }

    /// <summary>The program's exit code, once it exited.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>
    /// Output bytes (UTF-8) from the program; raised on the reader thread. Output that arrives before the first handler
    /// is attached (a program can write and exit before <see cref="Start"/> returns) is delivered to that handler.
    /// </summary>
    public event Action<ReadOnlyMemory<byte>>? Output
    {
        add
        {
            List<ReadOnlyMemory<byte>>? early;
            lock (_outputGate)
            {
                _output += value;
                early = _early;
                _early = null;

                // Delivered under the lock, so later output can't overtake it.
                foreach (var chunk in early ?? [])
                {
                    value?.Invoke(chunk);
                }
            }
        }

        remove
        {
            lock (_outputGate)
            {
                _output -= value;
            }
        }
    }

    public event Action<int>? Exited;

    /// <summary>Starts <paramref name="startInfo"/> in a new pseudo-terminal.</summary>
    public static PseudoTerminal Start(PtyStartInfo startInfo) =>
        OperatingSystem.IsWindows() ? WindowsPseudoTerminal.Create(startInfo) : UnixPseudoTerminal.Create(startInfo);

    /// <summary>Whether this OS can host pseudo-terminals (Windows 10 1809+, Linux, macOS).</summary>
    public static bool IsSupported =>
        !OperatingSystem.IsWindows() || OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    /// <summary>Sends input to the program.</summary>
    public abstract void Write(ReadOnlySpan<byte> data);

    public void Write(string text) => Write(System.Text.Encoding.UTF8.GetBytes(text));

    public abstract void Resize(int columns, int rows);

    /// <summary>Ends the program (and, on Unix, its process group).</summary>
    public abstract void Kill();

    protected void RaiseOutput(ReadOnlyMemory<byte> data)
    {
        Action<ReadOnlyMemory<byte>>? handler;
        lock (_outputGate)
        {
            if (_early is not null)
            {
                _early.Add(data);
                return;
            }

            handler = _output;
        }

        handler?.Invoke(data);
    }

    protected void RaiseExited(int code)
    {
        if (Interlocked.Exchange(ref _exitRaised, 1) == 0)
        {
            ExitCode = code;
            Exited?.Invoke(code);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected abstract void Dispose(bool disposing);

    /// <summary>Finds <paramref name="file"/> on the PATH (or returns it when it is a path).</summary>
    public static string? ResolveExecutable(string file, IReadOnlyDictionary<string, string>? environment = null)
    {
        if (Path.IsPathRooted(file) || file.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) || file.Contains('/', StringComparison.Ordinal))
        {
            return File.Exists(file) ? Path.GetFullPath(file) : null;
        }

        var path = environment?.GetValueOrDefault("PATH") ?? environment?.GetValueOrDefault("Path") ?? System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows()
            ? (System.Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in OperatingSystem.IsWindows() && Path.HasExtension(file) ? [string.Empty, .. extensions] : extensions)
            {
                var candidate = Path.Combine(directory.Trim('"'), file + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
