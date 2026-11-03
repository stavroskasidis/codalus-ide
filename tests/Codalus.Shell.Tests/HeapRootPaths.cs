using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;

namespace Codalus.Shell.Tests;

/// <summary>
/// What keeps objects of this process alive: the paths from GC roots to them, read with ClrMD from a snapshot of the
/// process. A disposed shell that stays reachable says what holds it (Windows CI, run 41601353003, held one for 30 s with
/// nothing logged that named the holder; docs/ci-flakes.md).
/// </summary>
internal static class HeapRootPaths
{
    /// <summary>Up to <paramref name="maxPaths"/> root paths to the objects of <paramref name="typeName"/> that <paramref name="isTarget"/> picks, one per line.</summary>
    public static string Describe(string typeName, Func<ClrObject, bool> isTarget, int maxPaths = 3)
    {
        if (OperatingSystem.IsMacOS())
        {
            // ClrMD's macOS snapshot is a core file of the whole process: dumping the Shell tests' process lost the macOS
            // runner (eng/ci-test.sh). Windows takes the snapshot in memory; Linux writes the core, as ClrMD does.
            return "no snapshot of the process on macOS (a core file of the test host risks the runner)";
        }

        var clock = Stopwatch.StartNew();
        try
        {
            // No symbol server: the runtime is read with the DAC beside this process's own runtime.
            using var target = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId, new DataTargetOptions { SymbolPaths = [] });
            var clr = target.ClrVersions.Single();
            var dac = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), OperatingSystem.IsWindows() ? "mscordaccore.dll" : OperatingSystem.IsMacOS() ? "libmscordaccore.dylib" : "libmscordaccore.so");
            using var runtime = clr.CreateRuntime(dac, ignoreMismatch: clr.Version.Major == 0); // ELF and Mach-O runtimes have no version resource
            var heap = runtime.Heap;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var roots = new GCRoot(heap, o => o.Type?.Name == typeName && Picked(isTarget, o));
            var lines = new List<string>();
            foreach (var (root, path) in roots.EnumerateRootPaths(cancellation.Token).Take(maxPaths))
            {
                var chain = new List<string>();
                for (var link = path; link is not null; link = link.Next)
                {
                    chain.Add(heap.GetObject(link.Object).Type?.Name ?? $"0x{link.Object:x}");
                }

                lines.Add($"{root.RootKind}{(root.IsPinned ? " (pinned)" : string.Empty)}: {string.Join(" -> ", chain)}");
            }

            return lines.Count == 0
                ? $"no root path to a {typeName} in the snapshot ({clock.Elapsed.TotalSeconds:0.0} s)"
                : $"{lines.Count} root path(s) to a {typeName} ({clock.Elapsed.TotalSeconds:0.0} s):\n" + string.Join('\n', lines);
        }
        catch (OperationCanceledException)
        {
            return $"no root path to a {typeName} in the snapshot's first {clock.Elapsed.TotalSeconds:0} s";
        }
        catch (Exception ex)
        {
            return $"no snapshot of the process ({ex.GetType().Name}: {ex.Message})";
        }
    }

    /// <summary>The object is a target; one whose fields can't be read counts as one (better a path too many than none).</summary>
    private static bool Picked(Func<ClrObject, bool> isTarget, ClrObject candidate)
    {
        try
        {
            return isTarget(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return true;
        }
    }
}
