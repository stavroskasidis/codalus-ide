using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
/// SPIKE_CHILD=1: the debuggee starts itself again as a child, through Process.Start (.NET's copy of the environment)
/// and natively (posix_spawn with libc's environ; CreateProcessW inheriting Win32's block). A child held at runtime
/// startup (an inherited DOTNET_DefaultDiagnosticPortSuspend) never exits on its own: it is killed after 10 s.
/// The child exits 3, or 4 if it still sees the variable.
/// </summary>
static class Children
{
    public static int Child() => Environment.GetEnvironmentVariable("DOTNET_DefaultDiagnosticPortSuspend") is null ? 3 : 4;

    public static void Spawn()
    {
        var self = Environment.ProcessPath!;
        using (var child = Process.Start(new ProcessStartInfo(self, "child") { RedirectStandardOutput = true })!)
        {
            var exited = child.WaitForExit(10_000);
            if (!exited) child.Kill();
            child.WaitForExit();
            var output = child.StandardOutput.ReadToEnd().Trim().ReplaceLineEndings(" | ");
            Console.WriteLine($"managed child: {(exited ? $"exit {child.ExitCode}" : "held (killed after 10 s)")}{(output.Length > 0 ? $", printed: {output}" : "")}");
        }
        Console.WriteLine($"native child: {(OperatingSystem.IsWindows() ? Windows(self) : Unix(self))}");
    }

    private static string Unix(string self)
    {
        var environ = OperatingSystem.IsMacOS() ? Marshal.ReadIntPtr(_NSGetEnviron()) : Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load("libc.so.6"), "environ"));
        var argv = new[] { Marshal.StringToHGlobalAnsi(self), Marshal.StringToHGlobalAnsi("child"), IntPtr.Zero };
        var error = posix_spawn(out var pid, self, IntPtr.Zero, IntPtr.Zero, argv, environ);
        if (error != 0) return $"posix_spawn failed: {error}";
        for (var n = 0; n < 1000; n++)
        {
            if (waitpid(pid, out var status, 1 /* WNOHANG */) == pid) return $"exit {(status >> 8) & 0xff}";
            Thread.Sleep(10);
        }
        kill(pid, 9);
        waitpid(pid, out _, 0);
        return "held (killed after 10 s)";
    }

    private static string Windows(string self)
    {
        var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        if (!CreateProcessW(null, $"\"{self}\" child", IntPtr.Zero, IntPtr.Zero, false, 0x08000000 /* CREATE_NO_WINDOW */, IntPtr.Zero, null, ref startup, out var info))
            return $"CreateProcessW failed: {Marshal.GetLastPInvokeError()}";
        var exited = WaitForSingleObject(info.hProcess, 10_000) == 0;
        if (!exited) TerminateProcess(info.hProcess, 1);
        GetExitCodeProcess(info.hProcess, out var code);
        CloseHandle(info.hProcess);
        CloseHandle(info.hThread);
        return exited ? $"exit {code}" : "held (killed after 10 s)";
    }

    [DllImport("kernel32")] private static extern int WaitForSingleObject(IntPtr handle, int milliseconds);
    [DllImport("kernel32")] private static extern bool TerminateProcess(IntPtr handle, int code);
    [DllImport("kernel32")] private static extern bool GetExitCodeProcess(IntPtr handle, out int code);
    [DllImport("kernel32")] private static extern bool CloseHandle(IntPtr handle);

    [DllImport("libc")] private static extern int posix_spawn(out int pid, string path, IntPtr fileActions, IntPtr attributes, IntPtr[] argv, IntPtr envp);
    [DllImport("libc")] private static extern int waitpid(int pid, out int status, int options);
    [DllImport("libc")] private static extern int kill(int pid, int signal);
    [DllImport("libc")] private static extern IntPtr _NSGetEnviron();

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? application, string commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, int flags, IntPtr environment, string? directory, ref STARTUPINFO startup, out PROCESS_INFORMATION info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
}
