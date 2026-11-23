using System.Globalization;
using System.Text;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// What a thread of this process waits in, on Linux: the return addresses on its stack, read from the stack pointer
/// the kernel holds for it while it's in a system call (<c>/proc/self/task/&lt;tid&gt;/syscall</c>), as
/// <c>module+0xoffset</c> for every word that points into a loaded module's code. A scan, not an unwind: a stale word
/// can show up between real frames, but the first ones are the wait and its caller. The 1.0 validation's 50 WSL sweeps
/// (2026-11-20) stalled two menu items' first runs for 528 and 759 ms with the UI thread asleep in a futex, on an idle
/// machine, in no posted job; the kernel can't say whose lock it was, this can. The published app's managed code is in
/// <c>codalus</c>, its symbols in <c>codalus.dbg</c> beside it: <c>addr2line -f -C -e codalus.dbg 0x&lt;offset&gt;</c>
/// names each frame. Null elsewhere, or for a thread running in user space.
/// </summary>
internal static class ThreadStack
{
    private readonly record struct Mapping(ulong Start, ulong End, string Path, ulong Base);

    /// <summary>The thread's system call and the code addresses on its stack, innermost first; null when it isn't in one.</summary>
    public static string? Scan(int threadId, int frames = 24, int bytes = 64 * 1024)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            return Scan(threadId, Maps(), frames, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every thread of this process (Linux): its id and name, its state and the kernel function it waits in, and its
    /// stack's code addresses; null elsewhere. A thread asleep on a lock is waiting for the one that holds it.
    /// </summary>
    public static string? AllThreads(int frames = 16, int bytes = 16 * 1024)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            var maps = Maps();
            var text = new StringBuilder();
            foreach (var task in Directory.GetDirectories("/proc/self/task").OrderBy(t => t.Length).ThenBy(t => t, StringComparer.Ordinal))
            {
                if (!int.TryParse(Path.GetFileName(task), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    continue;
                }

                var stat = Read(Path.Combine(task, "stat"));
                var state = stat.LastIndexOf(')') is var end and >= 0 && end + 2 < stat.Length ? stat[end + 2].ToString() : "?";
                var wchan = Read(Path.Combine(task, "wchan"));
                string? stack;
                try
                {
                    stack = Scan(id, maps, frames, bytes);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    stack = null; // the thread ended
                }

                text.Append(CultureInfo.InvariantCulture, $"{id} {Read(Path.Combine(task, "comm"))}: {state}")
                    .Append(state != "R" && wchan is { Length: > 0 } and not "0" ? " in " + wchan : string.Empty)
                    .Append(stack is null ? string.Empty : "; " + stack)
                    .Append('\n');
            }

            return text.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Scan(int threadId, List<Mapping> maps, int frames, int bytes)
    {
        // "nr arg1 … arg6 sp pc" in a system call, "-1 sp pc" blocked outside one, "running" on a core.
        var syscall = File.ReadAllText($"/proc/self/task/{threadId.ToString(CultureInfo.InvariantCulture)}/syscall")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (syscall.Length < 3 || !Hex(syscall[^2], out var sp) || !Hex(syscall[^1], out var pc))
        {
            return null;
        }

        var stack = maps.Find(m => sp >= m.Start && sp < m.End);
        var buffer = new byte[(stack.End > sp ? (int)Math.Min((ulong)bytes, stack.End - sp) : bytes) & ~7];
        int length;
        using (var memory = File.OpenHandle("/proc/self/mem"))
        {
            length = RandomAccess.Read(memory, buffer, (long)sp) & ~7;
        }

        var found = new List<string>();
        for (var i = 0; i < length && found.Count < frames; i += 8)
        {
            if (Name(maps, BitConverter.ToUInt64(buffer, i)) is { } name && (found.Count == 0 || found[^1] != name))
            {
                found.Add(name);
            }
        }

        var call = syscall.Length > 3
            ? $"system call {syscall[0]} at {Name(maps, pc) ?? "0x" + pc.ToString("x", CultureInfo.InvariantCulture)}"
            : "blocked out of a system call";
        return found.Count == 0 ? call : $"{call}; on its stack {string.Join(", ", found)}";
    }

    /// <summary>The process's mappings, from <c>/proc/self/maps</c>: a module's code with the address the module is loaded at.</summary>
    private static List<Mapping> Maps()
    {
        var all = new List<(ulong Start, ulong End, bool Code, ulong Offset, string Path)>();
        foreach (var line in File.ReadLines("/proc/self/maps"))
        {
            // "start-end perms offset dev inode path"
            var fields = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5 || fields[0].Split('-') is not [var start, var end]
                || !Hex(start, out var s) || !Hex(end, out var e) || !Hex(fields[2], out var offset))
            {
                continue;
            }

            all.Add((s, e, fields[1].Contains('x', StringComparison.Ordinal), offset, fields.Length > 5 ? fields[5].Trim() : string.Empty));
        }

        // A module's base is where its first page (file offset 0) is mapped; an address less it is the module's own.
        var bases = all.Where(m => m.Offset == 0 && m.Path.StartsWith('/'))
            .GroupBy(m => m.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Min(m => m.Start), StringComparer.Ordinal);
        return all.Select(m => new Mapping(m.Start, m.End, m.Code ? m.Path : string.Empty, m.Code && bases.TryGetValue(m.Path, out var b) ? b : 0)).ToList();
    }

    private static string? Name(List<Mapping> maps, ulong address)
    {
        foreach (var map in maps)
        {
            if (address >= map.Start && address < map.End)
            {
                return map is { Path.Length: > 0, Base: > 0 }
                    ? $"{Path.GetFileName(map.Path)}+0x{(address - map.Base).ToString("x", CultureInfo.InvariantCulture)}"
                    : null;
            }
        }

        return null;
    }

    private static bool Hex(string text, out ulong value) =>
        ulong.TryParse(text.StartsWith("0x", StringComparison.Ordinal) ? text[2..] : text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);

    private static string Read(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
