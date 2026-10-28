using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Codalus.Debugging.Host.CoreClr;

/// <summary>
/// The exit code of the launcher's child, which dbgshim started (not <see cref="Process.Start()"/>, so .NET never
/// reaps it): <c>waitpid</c> off Windows, the process handle (opened before the child runs) on Windows. A child ended
/// by a signal exits with 128 and the signal's number, as a shell reports it.
/// </summary>
internal sealed partial class ChildExit
{
    private const int EINTR = 4;
    private readonly int _pid;
    private readonly Process? _process;

    public ChildExit(int pid)
    {
        _pid = pid;
        if (OperatingSystem.IsWindows())
        {
            _process = Process.GetProcessById(pid);
            _ = _process.SafeHandle; // opened now and kept, so the exit code can be read after the process is gone
        }
    }

    /// <summary>Waits for the child to end; null when its exit code can't be read.</summary>
    public int? Wait()
    {
        if (_process is { } process)
        {
            process.WaitForExit();
            return process.ExitCode;
        }

        while (true)
        {
            var result = waitpid(_pid, out var status, 0);
            if (result == _pid)
            {
                return (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
            }

            if (result == -1 && Marshal.GetLastPInvokeError() == EINTR)
            {
                continue;
            }

            return null;
        }
    }

    [LibraryImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static partial int waitpid(int pid, out int status, int options);
}
