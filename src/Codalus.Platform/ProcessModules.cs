using System.Runtime.InteropServices;

namespace Codalus.Platform;

/// <summary>
/// The file names of the modules a process has loaded, read in one pass: <c>/proc/&lt;id&gt;/maps</c> on Linux, psapi on
/// Windows (unknown on macOS). Attach to Process and the debug service tell .NET and .NET Framework processes apart by
/// them (M43b, ADR 0076).
/// </summary>
/// <remarks>
/// On Windows this isn't <see cref="System.Diagnostics.Process.Modules"/>: that reads each module's information, base
/// name and path, each by walking the process's loader list again, and silently leaves out a module whose read fails
/// while the process loads or unloads a module (ERROR_PARTIAL_COPY), so a snapshot taken at the wrong moment can lack
/// <c>clr.dll</c>; on a process that can't be read it retries 50 times a millisecond apart, a second or so per process
/// on a loaded machine. Here each module costs one read, a snapshot with a failed read is taken again (a few times, a
/// few milliseconds apart), and a process that has exited isn't retried.
/// </remarks>
public static partial class ProcessModules
{
    /// <summary>How many times a Windows snapshot is taken when the process's module list changes while it's read.</summary>
    internal const int Attempts = 8;

    /// <summary>
    /// The file names (<c>clr.dll</c>, <c>libcoreclr.so</c>) of the modules process <paramref name="processId"/> has
    /// loaded, each once; null when they can't be read (the process ended, no access, macOS).
    /// </summary>
    public static IReadOnlyList<string>? Names(int processId)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return LinuxNames(File.ReadLines($"/proc/{processId}/maps"));
            }

            if (OperatingSystem.IsWindows())
            {
                return WindowsNames(processId);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return null;
    }

    /// <summary>The file names of the files mapped in a <c>/proc/&lt;id&gt;/maps</c> listing (the path is the last field, and starts with '/').</summary>
    internal static List<string> LinuxNames(IEnumerable<string> maps)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var line in maps)
        {
            var slash = line.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0)
            {
                continue;
            }

            var path = line.AsSpan(slash);
            if (path.EndsWith(" (deleted)", StringComparison.Ordinal))
            {
                path = path[..^" (deleted)".Length];
            }

            var name = path[(path.LastIndexOf('/') + 1)..].ToString();
            if (name.Length > 0 && seen.Add(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static List<string>? WindowsNames(int processId)
    {
        var process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, (uint)processId);
        if (process == IntPtr.Zero)
        {
            return null; // ended, or no access
        }

        try
        {
            List<string>? partial = null;
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                if (attempt > 0)
                {
                    if (HasExited(process))
                    {
                        return null;
                    }

                    Thread.Sleep(Math.Min(1 << (attempt - 1), 8));
                }

                var (names, complete) = Snapshot(process);
                if (complete)
                {
                    return names;
                }

                partial = names ?? partial;
            }

            // Still changing: what was read is better than nothing.
            return partial;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>
    /// One snapshot of the process's modules. Not complete when the list can't be read (a process that isn't initialized
    /// yet, or ended) or a module's name can't be (it was being loaded or unloaded): the caller takes it again.
    /// </summary>
    private static unsafe (List<string>? Names, bool Complete) Snapshot(IntPtr process)
    {
        var handles = new IntPtr[256];
        uint needed;
        while (true)
        {
            fixed (IntPtr* buffer = handles)
            {
                if (!EnumProcessModulesEx(process, buffer, (uint)(handles.Length * IntPtr.Size), out needed, ListModulesAll))
                {
                    return (null, false);
                }
            }

            if (needed <= handles.Length * IntPtr.Size)
            {
                break;
            }

            handles = new IntPtr[(needed / IntPtr.Size) + 16]; // too small (or it grew meanwhile): room for the list and a few more
        }

        var count = (int)(needed / IntPtr.Size);
        var names = new List<string>(count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var complete = true;
        var name = stackalloc char[MaxPath];
        for (var i = 0; i < count; i++)
        {
            var length = GetModuleBaseNameW(process, handles[i], name, MaxPath);
            if (length == 0)
            {
                complete = false; // unloaded or still loading (ERROR_PARTIAL_COPY, ERROR_INVALID_HANDLE)
                continue;
            }

            var text = new string(name, 0, (int)length);
            if (seen.Add(text))
            {
                names.Add(text);
            }
        }

        return (names, complete);
    }

    private static bool HasExited(IntPtr process) => GetExitCodeProcess(process, out var code) && code != StillActive;

    private const uint ProcessQueryInformation = 0x0400;

    private const uint ProcessVmRead = 0x0010;

    private const uint ListModulesAll = 0x03;

    private const uint StillActive = 259;

    private const int MaxPath = 260;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "K32EnumProcessModulesEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumProcessModulesEx(IntPtr process, IntPtr* modules, uint size, out uint needed, uint filter);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetModuleBaseNameW", SetLastError = true)]
    private static unsafe partial uint GetModuleBaseNameW(IntPtr process, IntPtr module, char* name, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
