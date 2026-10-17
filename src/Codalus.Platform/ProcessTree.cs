using System.Globalization;
using System.Runtime.InteropServices;

namespace Codalus.Platform;

/// <summary>
/// The processes a process started, by their ids: <c>/proc</c> on Linux, <c>proc_listchildpids</c> on macOS, a
/// Toolhelp snapshot on Windows. .NET has no public way to read a process's parent.
/// </summary>
public static partial class ProcessTree
{
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
                if (Marshal.ReadInt32(entry, parentOffset) == processId)
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

        return children;
    }

    private const uint SnapProcess = 0x2;

    private static readonly IntPtr InvalidHandle = new(-1);

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

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
