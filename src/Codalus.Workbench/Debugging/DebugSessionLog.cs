using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Codalus.Workbench.Ai;

namespace Codalus.Workbench.Debugging;

/// <summary>
/// The debugger's own messages of each debug session, on disk (<c>&lt;logs&gt;/debugger</c>, the last <see cref="Kept"/>
/// sessions): the Debug Console's lines that are the debugger's own (<see cref="DebugOutput.IsDiagnostic"/>: the
/// adapter's log, the starts and their limits, what failed, why the session ended), each with the time since the session
/// began, so a slow start shows how long each step took. Not the program's output, logpoints' messages or the DAP
/// traffic (no variable values); secrets redacted as written.
/// Report a Problem attaches the latest one. The IDE's own log lives in memory only, and a session that went wrong
/// (a review of 1.0 on macOS: "the first time I tried this I got a crash") left nothing to send; this is what it sends.
/// <c>CODALUS_CLR_DEBUGGER_LOG</c> still writes the Codalus debugger's full traffic for a deeper look.
/// </summary>
public sealed class DebugSessionLog : IDisposable
{
    /// <summary>How many sessions' logs are kept: older ones are deleted as a session begins.</summary>
    public const int Kept = 10;

    /// <summary>The longest a session's log gets; past it, one line says it was cut.</summary>
    public const long MaxBytes = 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly string _folder;
    private StreamWriter? _writer;
    private long _started;
    private bool _cut;
    private DebugState _state = DebugState.Idle;

    public DebugSessionLog(string folder)
    {
        _folder = folder;
    }

    /// <summary>The latest session's log, or null when there is none.</summary>
    public string? Latest
    {
        get
        {
            try
            {
                return Directory.Exists(_folder)
                    ? new DirectoryInfo(_folder).GetFiles("session-*.log").OrderByDescending(f => f.Name, StringComparer.Ordinal).FirstOrDefault()?.FullName
                    : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>The latest session's log as text (it may still be written), or null when there is none or it can't be read.</summary>
    public string? ReadLatest()
    {
        if (Latest is not { } path)
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes <paramref name="debugger"/>'s sessions: one begins as it leaves Idle, and ends as it returns to it.</summary>
    public void Follow(DebugService debugger)
    {
        debugger.StateChanged += (_, _) => OnState(debugger.State);
        debugger.Output += (_, output) =>
        {
            if (output.IsDiagnostic)
            {
                Write(output.Text);
            }
        };
        debugger.StartFailed += (_, why) => Write("[session] the start failed: " + why);
        debugger.Ended += (_, code) => Write(code is { } exit ? $"[session] ended: the program exited with code {exit.ToString(CultureInfo.InvariantCulture)}" : "[session] ended");
    }

    private void OnState(DebugState state)
    {
        DebugState previous;
        lock (_gate)
        {
            previous = _state;
            _state = state;
        }

        if (previous == DebugState.Idle && state != DebugState.Idle)
        {
            Begin();
        }
    }

    /// <summary>Starts a new session's log, and deletes the oldest past <see cref="Kept"/>.</summary>
    public void Begin()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
            _cut = false;
            _started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                Directory.CreateDirectory(_folder);
                foreach (var old in new DirectoryInfo(_folder).GetFiles("session-*.log").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(Kept - 1))
                {
                    old.Delete();
                }

                var path = Path.Combine(_folder, $"session-{DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}.log");
                var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                _writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Debug session, {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}; {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _writer = null; // no log this session: debugging goes on
            }
        }
    }

    /// <summary>Writes <paramref name="text"/>'s lines to the session's log, each after the time since it began.</summary>
    public void Write(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_gate)
        {
            if (_writer is not { } writer || _cut)
            {
                return;
            }

            try
            {
                if (writer.BaseStream.Length > MaxBytes)
                {
                    _cut = true;
                    writer.WriteLine("[log] cut here: the session's log is over 1 MB");
                    return;
                }

                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_started).TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);
                foreach (var line in SecretRedactor.Redact(text).Text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'))
                {
                    writer.WriteLine($"+{elapsed} s  {line}");
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _writer = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
