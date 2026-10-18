using System.Runtime.InteropServices;

namespace Codalus.Platform;

/// <summary>
/// The file names of the modules a process has loaded, read in one pass: <c>/proc/&lt;id&gt;/maps</c> on Linux, psapi on
/// Windows (unknown on macOS). Attach to Process and the debug service tell .NET and .NET Framework processes apart by
/// them (M43b, ADR 0076).
/// </summary>
/// <remarks>
/// On Windows this isn't <see cref="System.Diagnostics.Process.Modules"/>, nor <c>GetModuleBaseNameW</c> a module: each
/// of those walks the process's loader list again, through another process's memory, to find the module (three walks a
/// module for <see cref="System.Diagnostics.Process.Modules"/>), so a process's list costs a walk times its modules, and
/// Attach to Process reads every process: on a loaded four-core CI runner that took more than a minute. Here the loader
/// list is walked once (<c>EnumProcessModulesEx</c>), and each module's file name is the name of the file mapped at its
/// address, which the kernel looks up in the process's address map (<c>GetMappedFileNameW</c>) without reading the
/// process's memory. That works the same for a WOW64 process's 32-bit modules, and a module unloaded meanwhile is just
/// left out: it isn't loaded any more. Only a list that can't be walked (a process that isn't initialized yet, or is
/// loading or unloading a module that moment) is walked again, a few times; a process that has exited isn't.
/// </remarks>
public static partial class ProcessModules
{
    /// <summary>How many times a Windows process's module list is walked when the walk fails.</summary>
    internal const int Attempts = 4;

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
            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                if (attempt > 0)
                {
                    if (HasExited(process))
                    {
                        return null;
                    }

                    Thread.Sleep(attempt - 1); // a yield, then a timer tick or two: no long waits on a process that can't be read
                }

                if (Handles(process) is { } handles)
                {
                    return MappedNames(process, handles);
                }
            }

            return null; // no list to read: not initialized, or a process without one (System, Registry)
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>The process's module handles (their base addresses), from one walk of its loader list; null when the walk failed.</summary>
    private static unsafe IntPtr[]? Handles(IntPtr process)
    {
        var handles = new IntPtr[256];
        while (true)
        {
            uint needed;
            fixed (IntPtr* buffer = handles)
            {
                if (!EnumProcessModulesEx(process, buffer, (uint)(handles.Length * IntPtr.Size), out needed, ListModulesAll))
                {
                    return null; // ERROR_PARTIAL_COPY: not initialized, or the list changed while it was walked
                }
            }

            var count = (int)(needed / IntPtr.Size);
            if (count <= handles.Length)
            {
                return handles[..count];
            }

            handles = new IntPtr[count + 16]; // too small (or it grew meanwhile): room for the list and a few more
        }
    }

    /// <summary>The file names of the images mapped at <paramref name="handles"/>, each once; a module unloaded meanwhile is left out.</summary>
    private static unsafe List<string> MappedNames(IntPtr process, IntPtr[] handles)
    {
        var names = new List<string>(handles.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = System.Buffers.ArrayPool<char>.Shared.Rent(MappedPathLength);
        try
        {
            fixed (char* buffer = path)
            {
                foreach (var handle in handles)
                {
                    var length = (int)GetMappedFileNameW(process, handle, buffer, MappedPathLength);
                    if (FileName(path.AsSpan(0, Math.Min(length, MappedPathLength))) is { } name && seen.Add(name))
                    {
                        names.Add(name);
                    }
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<char>.Shared.Return(path);
        }

        return names;
    }

    /// <summary>
    /// The file name in a mapped file's NT path (<c>\Device\HarddiskVolume3\Windows\System32\ntdll.dll</c>); null for
    /// an empty path (the address has nothing mapped any more).
    /// </summary>
    internal static string? FileName(ReadOnlySpan<char> mappedPath)
    {
        var name = mappedPath[(mappedPath.LastIndexOf('\\') + 1)..];
        return name.IsEmpty ? null : name.ToString();
    }

    private static bool HasExited(IntPtr process) => GetExitCodeProcess(process, out var code) && code != StillActive;

    private const uint ProcessQueryInformation = 0x0400;

    private const uint ProcessVmRead = 0x0010;

    private const uint ListModulesAll = 0x03;

    private const uint StillActive = 259;

    /// <summary>The longest NT path a mapped file can have (a UNICODE_STRING's), in characters.</summary>
    private const int MappedPathLength = 32767;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "K32EnumProcessModulesEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumProcessModulesEx(IntPtr process, IntPtr* modules, uint size, out uint needed, uint filter);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetMappedFileNameW", SetLastError = true)]
    private static unsafe partial uint GetMappedFileNameW(IntPtr process, IntPtr address, char* name, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
