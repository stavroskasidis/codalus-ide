using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using Codalus.Debugging.Host.NetFx;

namespace Codalus.Debugging.Host.CoreClr;

/// <summary>
/// The adapter's side of a program started by the launcher (<see cref="Launcher"/>, ADR 0086): the launcher process,
/// started with the launch's environment, working directory and either pipes for the Debug Console or a console window
/// of its own, and the pipe it reports on.
/// </summary>
internal sealed class LaunchedProgram : IDisposable
{
    /// <summary>How long the launcher may take to report the program's id.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly NamedPipeServerStream _pipe;
    private readonly Process _launcher;
    private readonly List<Task> _pumps = [];
    private readonly TaskCompletionSource<int?> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private StreamWriter? _writer;

    private LaunchedProgram(NamedPipeServerStream pipe, Process launcher)
    {
        _pipe = pipe;
        _launcher = launcher;
    }

    /// <summary>The program's process id.</summary>
    public int ProcessId { get; private set; }

    /// <summary>Starts the launcher, which starts the program suspended; returns once it has reported the program's id.</summary>
    public static LaunchedProgram Start(NetFxDebugArguments arguments, Action<string, string> output)
    {
        var program = arguments.Program!;
        var name = "codalus-" + Guid.NewGuid().ToString("N")[..12];
        var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var (host, hostArguments) = HostCommand();
        var info = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            WorkingDirectory = string.IsNullOrEmpty(arguments.Cwd) ? Path.GetDirectoryName(Path.GetFullPath(program)) : arguments.Cwd,
            CreateNoWindow = !arguments.NewConsole,
            RedirectStandardInput = !arguments.NewConsole,
            RedirectStandardOutput = !arguments.NewConsole,
            RedirectStandardError = !arguments.NewConsole,
        };
        foreach (var argument in (string[])[.. hostArguments, CoreClrAdapter.LaunchArgument, name, program, .. arguments.Args ?? []])
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (variable, value) in arguments.Env ?? [])
        {
            info.Environment[variable] = value;
        }

        Process launcher;
        try
        {
            launcher = Process.Start(info) ?? throw new ClrException("The launcher didn't start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            pipe.Dispose();
            throw new ClrException($"The debugger's launcher couldn't start: {ex.Message}", ex);
        }

        var launched = new LaunchedProgram(pipe, launcher);
        try
        {
            if (!arguments.NewConsole)
            {
                // The Debug Console: the program's output through the launcher's pipes, and no input.
                launcher.StandardInput.Close();
                launched._pumps.Add(Pump(launcher.StandardOutput, "stdout", output));
                launched._pumps.Add(Pump(launcher.StandardError, "stderr", output));
            }

            launched.Connect(Path.GetFileName(program));
            return launched;
        }
        catch
        {
            launched.Dispose();
            throw;
        }
    }

    /// <summary>Lets the program run: the adapter has registered for its runtime's startup.</summary>
    public void Resume() => Send("resume");

    /// <summary>The adapter detached: the program goes on when the pipe closes.</summary>
    public void Detach() => Send("detach");

    /// <summary>The program's exit code, once the launcher has sent it, and its last output; null when it doesn't come in time.</summary>
    public int? WaitForExit(TimeSpan timeout)
    {
        int? code = null;
        try
        {
            if (_exit.Task.Wait(timeout))
            {
                code = _exit.Task.Result;
            }

            Task.WaitAll([.. _pumps], TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }

        return code;
    }

    /// <summary>Closes the pipe: the launcher ends the program unless it was told to detach.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _writer?.Dispose();
            }
            catch (IOException)
            {
                // The launcher went away first.
            }

            _pipe.Dispose();
        }

        _launcher.Dispose();
    }

    /// <summary>The launcher is this host again: its apphost, or <c>dotnet</c> with its assembly.</summary>
    private static (string Host, string[] Arguments) HostCommand()
    {
        var path = Environment.ProcessPath ?? throw new ClrException("The debugger's own executable isn't known.");
        return string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? (path, [typeof(LaunchedProgram).Assembly.Location])
            : (path, []);
    }

    private void Connect(string program)
    {
        using var timeout = new CancellationTokenSource(StartTimeout);
        try
        {
            var connected = _pipe.WaitForConnectionAsync(timeout.Token);
            var exited = _launcher.WaitForExitAsync(timeout.Token);
            if (Task.WaitAny(connected, exited) == 1 || !connected.IsCompletedSuccessfully)
            {
                throw new ClrException($"The debugger's launcher ended before starting {program} (exit code {ExitCodeOf(_launcher)}).");
            }
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
        {
            throw new ClrException($"The debugger's launcher didn't start {program} in {StartTimeout.TotalSeconds:0} seconds.");
        }

        // Connected: setting AutoFlush flushes, which an unconnected pipe refuses.
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        var reader = new StreamReader(_pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var first = reader.ReadLineAsync(timeout.Token).AsTask();
        string? report;
        try
        {
            report = first.Wait(StartTimeout) ? first.Result : null;
        }
        catch (AggregateException)
        {
            report = null;
        }

        if (report is not null && report.StartsWith("error ", StringComparison.Ordinal))
        {
            throw new ClrException(report[6..]);
        }

        if (report is null || !report.StartsWith("pid ", StringComparison.Ordinal) || !int.TryParse(report.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            throw new ClrException($"The debugger's launcher didn't report {program}'s process id.");
        }

        ProcessId = id;
        _ = Task.Run(async () =>
        {
            using (reader)
            {
                try
                {
                    while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                    {
                        if (line.StartsWith("exit ", StringComparison.Ordinal) && int.TryParse(line.AsSpan(5), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code))
                        {
                            _exit.TrySetResult(code == -1 ? null : code);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                }

                _exit.TrySetResult(null);
            }
        });
    }

    private void Send(string message)
    {
        lock (_gate)
        {
            try
            {
                (_writer ?? throw new ObjectDisposedException(nameof(LaunchedProgram))).WriteLine(message);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                throw new ClrException($"The debugger's launcher went away ({ex.Message}).", ex);
            }
        }
    }

    private static string ExitCodeOf(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode.ToString(CultureInfo.InvariantCulture) : "none";
        }
        catch (InvalidOperationException)
        {
            return "none";
        }
    }

    private static Task Pump(StreamReader reader, string category, Action<string, string> output) =>
        Task.Run(async () =>
        {
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                output(category, new string(buffer, 0, read));
            }
        });
}
