using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
/// The exit code of a child started outside Process.Start (dbgshim's CreateProcessForLaunch). Off Windows, waitpid:
/// it fails (ECHILD) once mscordbi in this process has reaped the child. On Windows, the process's handle, opened
/// before the child runs.
/// </summary>
sealed class ChildExit
{
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

    public int Wait(Action<string> log)
    {
        if (_process is { } process)
        {
            process.WaitForExit();
            return process.ExitCode;
        }
        var result = waitpid(_pid, out var status, 0);
        if (result != _pid) log($"waitpid failed: errno {Marshal.GetLastPInvokeError()}, parent of {_pid} is {ParentOf(_pid)}");
        return result == _pid ? (status >> 8) & 0xff : -1;
    }

    private static string ParentOf(int p) => File.Exists($"/proc/{p}/stat") ? File.ReadAllText($"/proc/{p}/stat").Split(' ')[3] + $" (we are {Environment.ProcessId})" : "gone";

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);
}
