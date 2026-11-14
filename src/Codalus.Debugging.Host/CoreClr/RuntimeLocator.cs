using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Codalus.Debugging.Host.CoreClr;

/// <summary>
/// Finds the CoreCLR library a process loaded without asking dbgshim to look (M58b's follow-up, ADR 0094). dbgshim's own
/// search (<c>EnumerateCLRs</c>, and the startup callback of <c>RegisterForRuntimeStartup</c>) reads every file mapped
/// in the process as an executable, looking for a single-file app's runtime; in an x64 process under Rosetta 2 one of
/// those files crashed its reader, and the debugger with it (docs/ci-flakes.md). Given the library's path, dbgshim reads
/// only that file.
/// </summary>
internal static partial class RuntimeLocator
{
    /// <summary>
    /// Whether this debugger starts and finds runtimes itself (<see cref="DbgShimDebugger"/>'s held start): when it runs
    /// as x64 under Rosetta 2, or <c>CODALUS_DEBUG_HELD_START</c> is <c>1</c> (the tests' way to run that path on any
    /// machine; <c>0</c> turns it off).
    /// </summary>
    public static bool StartsHeld { get; } = Environment.GetEnvironmentVariable(Variable) switch
    {
        "1" => true,
        "0" => false,
        _ => IsTranslated(),
    };

    public const string Variable = "CODALUS_DEBUG_HELD_START";

    /// <summary>
    /// The CoreCLR library <paramref name="processId"/> has mapped: one path where the process's files can be listed
    /// (Linux's maps; macOS's memory regions, as dbgshim lists them, or <c>lsof</c>), else every runtime's library next
    /// to the program, newest first, of which the process maps one (dbgshim says which, when asked for a library's
    /// version string). Empty: it hasn't loaded one.
    /// </summary>
    /// <param name="program">The program's executable, to look next to when the files can't be listed.</param>
    public static IReadOnlyList<string> Find(int processId, string? program = null)
    {
        var name = OperatingSystem.IsMacOS() ? "libcoreclr.dylib" : OperatingSystem.IsWindows() ? "coreclr.dll" : "libcoreclr.so";
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return FromMaps(File.ReadLines($"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/maps"), name) is { } mapped ? [mapped] : [];
            }

            if (OperatingSystem.IsMacOS())
            {
                return (FromRegions(processId, name) ?? FromLsof(Lsof(processId), name)) is { } listed ? [listed] : NextTo(program, name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The process ended, or its files aren't ours to list.
        }

        return [];
    }

    /// <summary>The mapped file named <paramref name="name"/> in <c>/proc/&lt;pid&gt;/maps</c>: its lines end with the file's path.</summary>
    internal static string? FromMaps(IEnumerable<string> lines, string name)
    {
        foreach (var line in lines)
        {
            var at = line.IndexOf('/');
            if (at >= 0 && line.AsSpan(at).TrimEnd() is var path && Path.GetFileName(path).SequenceEqual(name))
            {
                return path.ToString();
            }
        }

        return null;
    }

    /// <summary>The file named <paramref name="name"/> in <c>lsof -Fn</c>'s output: one <c>n&lt;path&gt;</c> line per open or mapped file.</summary>
    internal static string? FromLsof(IEnumerable<string> lines, string name) =>
        lines.Where(l => l.Length > 1 && l[0] == 'n' && l[1] == '/').Select(l => l[1..]).FirstOrDefault(p => Path.GetFileName(p) == name);

    /// <summary>
    /// The libraries next to the program's own runtimes: the program's folder for a self-contained one, and
    /// <c>&lt;dotnet&gt;/shared/Microsoft.NETCore.App/*/</c> for a program <c>dotnet</c> starts, the newest first.
    /// </summary>
    internal static IReadOnlyList<string> NextTo(string? program, string name)
    {
        if (program is null || Path.GetDirectoryName(Path.GetFullPath(program)) is not { } folder)
        {
            return [];
        }

        var found = new List<string>();
        var own = Path.Combine(folder, name);
        if (File.Exists(own))
        {
            found.Add(own);
        }

        var shared = Path.Combine(folder, "shared", "Microsoft.NETCore.App");
        if (Directory.Exists(shared))
        {
            found.AddRange(Directory.EnumerateDirectories(shared)
                .Select(d => (Path: Path.Combine(d, name), Version: Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : new Version()))
                .Where(c => File.Exists(c.Path))
                .OrderByDescending(c => c.Version)
                .Select(c => c.Path));
        }

        return found;
    }

    /// <summary>
    /// The file named <paramref name="name"/> among a macOS process's memory regions (<c>proc_pidinfo</c>,
    /// <c>PROC_PIDREGIONPATHINFO</c>): what dbgshim itself walks, without reading the files. Null when there is none, or
    /// the regions can't be read.
    /// </summary>
    internal static unsafe string? FromRegions(int processId, string name)
    {
        // struct proc_regionwithpathinfo (xnu, bsd/sys/proc_info.h): proc_regioninfo, 96 bytes, with pri_address at 80
        // and pri_size at 88; then vnode_info, 152 bytes; then the file's path, MAXPATHLEN (1024) bytes.
        const int Flavor = 8, Size = 1272, Address = 80, Length = 88, PathAt = 248, PathSize = 1024;
        var buffer = stackalloc byte[Size];
        ulong address = 0;
        try
        {
            for (var regions = 0; regions < 200_000; regions++)
            {
                if (proc_pidinfo(processId, Flavor, address, buffer, Size) != Size)
                {
                    return null;
                }

                var path = new ReadOnlySpan<byte>(buffer + PathAt, PathSize);
                var end = path.IndexOf((byte)0);
                var text = System.Text.Encoding.UTF8.GetString(end < 0 ? path : path[..end]);
                if (text.Length > 0 && text[0] == '/' && Path.GetFileName(text) == name)
                {
                    return text;
                }

                var next = BitConverter.ToUInt64(new ReadOnlySpan<byte>(buffer + Address, 8)) + BitConverter.ToUInt64(new ReadOnlySpan<byte>(buffer + Length, 8));
                if (next <= address)
                {
                    return null;
                }

                address = next;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return null;
    }

    [LibraryImport("libproc")]
    private static unsafe partial int proc_pidinfo(int pid, int flavor, ulong argument, byte* buffer, int size);

    /// <summary>The files <paramref name="processId"/> has open or mapped, as <c>lsof</c> lists them (the user's own tool, one run).</summary>
    internal static List<string> Lsof(int processId)
    {
        var start = new ProcessStartInfo("/usr/sbin/lsof")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-a", "-p", processId.ToString(CultureInfo.InvariantCulture), "-d", "txt", "-Fn" })
        {
            start.ArgumentList.Add(argument);
        }

        using var lsof = Process.Start(start) ?? throw new InvalidOperationException("lsof didn't start.");
        _ = lsof.StandardError.ReadToEndAsync();
        var lines = new List<string>();
        while (lsof.StandardOutput.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        lsof.WaitForExit(5000);
        return lines;
    }

    /// <summary>Whether this process is an x64 one that Rosetta 2 translates (<c>sysctl.proc_translated</c>).</summary>
    private static bool IsTranslated()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return false;
        }

        try
        {
            nuint size = sizeof(int);
            return sysctlbyname("sysctl.proc_translated", out var translated, ref size, IntPtr.Zero, 0) == 0 && translated == 1;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("libSystem.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int sysctlbyname(string name, out int value, ref nuint size, IntPtr newValue, nuint newSize);
}
