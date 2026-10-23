using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Codalus.Platform;

/// <summary>
/// The processes a process started, by their ids: <c>/proc</c> on Linux, <c>proc_listchildpids</c> on macOS, a
/// Toolhelp snapshot on Windows (.NET has no public way to read a process's parent), and ending a process with them.
/// </summary>
public static partial class ProcessTree
{
    /// <summary>
    /// Ends <paramref name="process"/> and every process it started, as <see cref="Process.Kill(bool)"/> with
    /// <c>entireProcessTree</c> does, but on macOS and Linux finds the children with <see cref="Children"/>. .NET's walk
    /// lists the system's processes, and on macOS reads each one's task information to do so, which blocks for many
    /// minutes while ReportCrash holds a crashed process (anywhere on the machine): the caller's thread with it, and on
    /// CI every test that ended a process tree, until HangDump ended the run. As in .NET's walk, each process is stopped
    /// before its children are listed, so none starts another meanwhile, and the stopped ones are then ended. A process
    /// that can't be stopped (it ended, or isn't ours) is left with its children, and this process never ends itself.
    /// </summary>
    /// <exception cref="InvalidOperationException">No process is associated with <paramref name="process"/>.</exception>
    public static void Kill(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            process.Kill(entireProcessTree: true);
            return;
        }

        if (process.HasExited)
        {
            return;
        }

        var stopped = new List<int>();
        try
        {
            Stop(process.Id, stopped);

            // A process stopped in the middle of a fork has a child the listing missed: it shows once the fork is done.
            // Look again, after a moment, until a look finds no one new (a child it missed kept the host's standard
            // output open, so its end went unnoticed: HostRuntimeTests on macOS). .NET's walk, slower, never missed one.
            for (var round = 0; round < 10; round++)
            {
                var count = stopped.Count;
                Thread.Sleep(10);
                foreach (var id in stopped.ToArray())
                {
                    foreach (var child in Children(id))
                    {
                        Stop(child, stopped);
                    }
                }

                if (stopped.Count == count)
                {
                    break;
                }
            }
        }
        finally
        {
            foreach (var id in stopped)
            {
                _ = kill(id, SigKill);
            }
        }
    }

    private static void Stop(int processId, List<int> stopped)
    {
        if (processId == Environment.ProcessId || stopped.Contains(processId) || kill(processId, SigStop) != 0)
        {
            return;
        }

        stopped.Add(processId);
        foreach (var child in Children(processId))
        {
            Stop(child, stopped);
        }
    }

    private const int SigKill = 9;

    private static int SigStop => OperatingSystem.IsMacOS() ? 17 : 19;

    /// <summary>The ids of <paramref name="processId"/>'s child processes that are running; empty when there are none, or they can't be listed.</summary>
    public static IReadOnlyList<int> Children(int processId)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsChildren(processId);
            }

            if (OperatingSystem.IsMacOS())
            {
                return MacChildren(processId);
            }

            if (OperatingSystem.IsLinux())
            {
                return LinuxChildren(processId);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return [];
    }

    private static List<int> LinuxChildren(int processId)
    {
        var children = new List<int>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                continue;
            }

            string stat;
            try
            {
                stat = File.ReadAllText(Path.Combine(directory, "stat"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // it ended meanwhile
            }

            // "pid (comm) state ppid ...": the command can hold spaces and parentheses, so read after the last ')'.
            var fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2 && fields[0] != "Z" && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent) && parent == processId)
            {
                children.Add(id);
            }
        }

        return children;
    }

    private static List<int> MacChildren(int processId)
    {
        var buffer = new int[256];
        var count = proc_listchildpids(processId, buffer, buffer.Length * sizeof(int));
        return count <= 0 ? [] : [.. buffer.Take(Math.Min(count, buffer.Length)).Where(id => id > 0)];
    }

    private static List<int> WindowsChildren(int processId)
    {
        var children = new List<int>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle)
        {
            return children;
        }

        // PROCESSENTRY32W: dwSize, cntUsage, th32ProcessID, th32DefaultHeapID (pointer-sized), th32ModuleID, cntThreads,
        // th32ParentProcessID, pcPriClassBase, dwFlags, szExeFile[260] (UTF-16), padded to the pointer size.
        var heapId = IntPtr.Size == 8 ? 16 : 12;
        var parentOffset = heapId + IntPtr.Size + 8;
        var size = (parentOffset + 12 + 260 * 2 + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size;
        var entry = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.WriteInt32(entry, 0, size);
            for (var more = Process32FirstW(snapshot, entry); more; more = Process32NextW(snapshot, entry))
            {
                // A console program's console host (conhost.exe, or Windows Terminal's OpenConsole.exe) is its child
                // too: not a program it started.
                if (Marshal.ReadInt32(entry, parentOffset) == processId && !IsConsoleHost(Marshal.PtrToStringUni(entry + parentOffset + 12)))
                {
                    children.Add(Marshal.ReadInt32(entry, 8));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(entry);
            CloseHandle(snapshot);
        }

        // An entry keeps its parent's id after the parent ended, and Windows reuses process ids: a process created before
        // this one is an earlier process's child that this one's id was given to (a build server whose build ended, say),
        // not a program this one started. Process.Kill's tree walk checks the same.
        return StartedSinceParent(CreationTime(processId), children, CreationTime);
    }

    /// <summary>
    /// The listed <paramref name="children"/> of a parent created at <paramref name="parentCreated"/> that it can have
    /// started: those created at or after it. A child created earlier is an earlier holder's of the parent's (reused) id.
    /// A creation time that can't be read (the process ended, or may not be queried) leaves the child in, and an unknown
    /// parent creation time leaves every child in: only a process known to be older than its parent is left out.
    /// </summary>
    /// <param name="created">A process's creation time, as UTC file time, or null when it can't be read.</param>
    internal static List<int> StartedSinceParent(long? parentCreated, List<int> children, Func<int, long?> created)
    {
        if (parentCreated is not { } parent || children.Count == 0)
        {
            return children;
        }

        children.RemoveAll(id => created(id) is { } childCreated && childCreated < parent);
        return children;
    }

    /// <summary>
    /// When a process was created, as the kernel recorded it (UTC file time, from the same clock for every process); null
    /// when it ended or can't be queried. Read straight from a handle: no <see cref="System.Diagnostics.Process"/>, whose
    /// lookup by id lists every process when it can't open one, and whose start time is converted to local time.
    /// </summary>
    private static long? CreationTime(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return GetProcessTimes(handle, out var creation, out _, out _, out _) ? creation : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool IsConsoleHost(string? image) =>
        string.Equals(image, "conhost.exe", StringComparison.OrdinalIgnoreCase) || string.Equals(image, "OpenConsole.exe", StringComparison.OrdinalIgnoreCase);

    private const uint SnapProcess = 0x2;

    private const uint ProcessQueryLimitedInformation = 0x1000;

    private static readonly IntPtr InvalidHandle = new(-1);

    [LibraryImport("libc", EntryPoint = "kill")]
    private static partial int kill(int processId, int signal);

    [LibraryImport("/usr/lib/libSystem.dylib")]
    private static partial int proc_listchildpids(int processId, [Out] int[] buffer, int bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(IntPtr snapshot, IntPtr entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(IntPtr snapshot, IntPtr entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
