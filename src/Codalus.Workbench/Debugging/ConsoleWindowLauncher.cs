using System.Diagnostics;
using Codalus.Debugging;
using Codalus.Workbench.Build;

namespace Codalus.Workbench.Debugging;

/// <summary>
/// Starts a debugged program in a console window of its own (Windows, <c>debug.console = externalConsole</c>), held by
/// the startup hook like the integrated terminal's (<see cref="HeldLaunch"/>), so the debugger attaches. Stop closes the
/// windows; a program that ends on its own leaves its window open until a key is pressed.
/// </summary>
public sealed class ConsoleWindowLauncher : ITerminalLauncher
{
    private readonly List<Process> _windows = [];

    public Task<int?> LaunchAsync(TerminalLaunch launch, CancellationToken cancellationToken)
    {
        var start = ConsoleWindow.StartInfo(launch.Title, launch.Program, launch.Arguments, launch.WorkingDirectory, launch.Environment);
        var process = Process.Start(start) ?? throw new InvalidOperationException($"{Path.GetFileName(launch.Program)} did not start.");
        lock (_windows)
        {
            _windows.RemoveAll(w => w.HasExited);
            _windows.Add(process);
        }

        return Task.FromResult<int?>(process.Id);
    }

    /// <summary>Closes the windows still open (Stop).</summary>
    public void CloseAll()
    {
        List<Process> windows;
        lock (_windows)
        {
            windows = [.. _windows];
            _windows.Clear();
        }

        foreach (var window in windows)
        {
            try
            {
                if (!window.HasExited)
                {
                    window.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException or NotSupportedException)
            {
                // Already gone.
            }
            finally
            {
                window.Dispose();
            }
        }
    }
}
