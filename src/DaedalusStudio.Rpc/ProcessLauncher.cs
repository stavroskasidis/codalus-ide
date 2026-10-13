using System.Diagnostics;

namespace DaedalusStudio.Rpc;

/// <summary>
/// A started service process, here or on another machine (M21, ADR 0050): its stdio as streams and its lifetime.
/// Standard error arrives as <see cref="ErrorLine"/> once <see cref="BeginErrorRead"/> was called, so a handler
/// attached first sees every line.
/// </summary>
public interface IHostProcess : IAsyncDisposable
{
    /// <summary>The process's standard output.</summary>
    Stream Output { get; }

    /// <summary>The process's standard input.</summary>
    Stream Input { get; }

    /// <summary>The process id on its machine, when known.</summary>
    int? Id { get; }

    /// <summary>Completes with the exit code (null when it isn't known) once the process ended.</summary>
    Task<int?> Exited { get; }

    event Action<string>? ErrorLine;

    void BeginErrorRead();

    /// <summary>Ends the process (and its children where the OS allows).</summary>
    void Kill();
}

/// <summary>Starts service processes: here (<see cref="LocalProcessLauncher"/>) or through a remote server (M21).</summary>
public interface IProcessLauncher
{
    Task<IHostProcess> StartAsync(ProcessLaunchInfo launch, CancellationToken cancellationToken);
}

/// <summary>Starts processes on this machine, with their stdio redirected.</summary>
public sealed class LocalProcessLauncher : IProcessLauncher
{
    public static LocalProcessLauncher Instance { get; } = new();

    public Task<IHostProcess> StartAsync(ProcessLaunchInfo launch, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(launch.FileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (launch.WorkingDirectory is { } directory)
        {
            psi.WorkingDirectory = directory;
        }

        foreach (var argument in launch.Arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in launch.Environment ?? new Dictionary<string, string>())
        {
            psi.Environment[key] = value;
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {launch.FileName}.");
        return Task.FromResult<IHostProcess>(new LocalHostProcess(process));
    }
}

/// <summary>A <see cref="Process"/> as an <see cref="IHostProcess"/>.</summary>
public sealed class LocalHostProcess : IHostProcess
{
    private readonly Process _process;
    private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _errorReading;

    public LocalHostProcess(Process process)
    {
        _process = process;
        process.EnableRaisingEvents = true;
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                ErrorLine?.Invoke(line);
            }
        };
        process.Exited += (_, _) => _exited.TrySetResult(ExitCode());
        if (process.HasExited)
        {
            _exited.TrySetResult(ExitCode());
        }
    }

    public Process Process => _process;

    public Stream Output => _process.StandardOutput.BaseStream;

    public Stream Input => _process.StandardInput.BaseStream;

    public int? Id
    {
        get
        {
            try
            {
                return _process.Id;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public Task<int?> Exited => _exited.Task;

    public event Action<string>? ErrorLine;

    public void BeginErrorRead()
    {
        if (Interlocked.Exchange(ref _errorReading, 1) == 0)
        {
            _process.BeginErrorReadLine();
        }
    }

    public void Kill()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    private int? ExitCode()
    {
        try
        {
            return _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _process.Dispose();
        return ValueTask.CompletedTask;
    }
}
