using System.Diagnostics;
using System.Globalization;

namespace Codalus.Benchmarks.Harness.Platform;

/// <summary>Memory of a process tree (the shell plus its helpers, language host, renderers…).</summary>
public sealed record TreeMemory(long WorkingSetBytes, long PrivateBytes, int ProcessCount, long RootWorkingSetBytes, long RootPrivateBytes);

internal static class ProcessTree
{
    /// <summary>
    /// The root and all of its descendants that are still alive. Parent ids are recycled by the OS: a process
    /// whose recorded parent id now belongs to a newer process would look like that process's child, so
    /// a candidate that started before its supposed parent is skipped.
    /// </summary>
    public static IReadOnlyList<int> Descendants(int rootPid)
    {
        var parents = ParentMap();
        var startTimes = new Dictionary<int, DateTime?>();
        DateTime? Started(int pid)
        {
            if (!startTimes.TryGetValue(pid, out var time))
            {
                startTimes[pid] = time = StartTime(pid);
            }

            return time;
        }

        var result = new List<int> { rootPid };
        for (var i = 0; i < result.Count; i++)
        {
            foreach (var (pid, parent) in parents)
            {
                // A process whose start time can't be read (another user's, protected) is never ours: our launches'
                // descendants are readable. Seen in M34c: a protected process with a recycled parent id added 21 GB.
                if (parent == result[i] && pid != result[i] && !result.Contains(pid)
                    && Started(pid) is { } child && !(Started(parent) is { } parentStart && child < parentStart))
                {
                    result.Add(pid);
                }
            }
        }

        return result;
    }

    private static DateTime? StartTime(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    public static TreeMemory Memory(int rootPid)
    {
        long ws = 0, priv = 0, rootWs = 0, rootPriv = 0;
        var count = 0;
        foreach (var pid in Descendants(rootPid))
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Refresh();
                var (w, p) = Sample(process);
                ws += w;
                priv += p;
                count++;
                if (pid == rootPid)
                {
                    (rootWs, rootPriv) = (w, p);
                }
            }
            catch (ArgumentException)
            {
                // Exited between enumeration and sampling.
            }
            catch (InvalidOperationException)
            {
            }
        }

        return new TreeMemory(ws, priv, count, rootWs, rootPriv);
    }

    /// <summary>Working set and private (unshared, committed) bytes. On Linux, PSS approximates private.</summary>
    private static (long WorkingSet, long Private) Sample(Process process)
    {
        if (OperatingSystem.IsLinux() && File.Exists($"/proc/{process.Id}/smaps_rollup"))
        {
            long rss = 0, pss = 0;
            foreach (var line in File.ReadLines($"/proc/{process.Id}/smaps_rollup"))
            {
                if (line.StartsWith("Rss:", StringComparison.Ordinal))
                {
                    rss = KiloBytes(line);
                }
                else if (line.StartsWith("Pss:", StringComparison.Ordinal))
                {
                    pss = KiloBytes(line);
                }
            }

            return (rss, pss);
        }

        return (process.WorkingSet64, process.PrivateMemorySize64);
    }

    private static long KiloBytes(string line) =>
        long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) * 1024;

    private static List<(int Pid, int Parent)> ParentMap()
    {
        if (OperatingSystem.IsWindows())
        {
            return Win32.ProcessParents();
        }

        var output = Machine.Run("ps", "-A -o pid=,ppid=") ?? string.Empty;
        var map = new List<(int, int)>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var pid) && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var parent))
            {
                map.Add((pid, parent));
            }
        }

        return map;
    }

    /// <summary>Kills the whole tree (best effort), e.g. Electron/JVM helpers that outlive their parent.</summary>
    public static void Kill(int rootPid)
    {
        foreach (var pid in Descendants(rootPid).Reverse())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            catch (AggregateException)
            {
                // Part of the tree couldn't be killed (Access is denied): best effort.
            }
        }
    }
}
