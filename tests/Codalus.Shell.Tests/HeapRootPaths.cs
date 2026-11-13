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
    /// <summary>
    /// Up to <paramref name="maxPaths"/> root paths to the objects of <paramref name="typeName"/> that <paramref name="isTarget"/>
    /// picks, one per line. With none, why there may be none: the heap and the threads as the snapshot has them, and every
    /// thread whose stack runs <paramref name="frame"/> (a method's name), where a test holds its object.
    /// </summary>
    public static string Describe(string typeName, Func<ClrObject, bool> isTarget, int maxPaths = 3, string? frame = null)
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
                    chain.Add(Name(heap.GetObject(link.Object), link.Object));
                }

                lines.Add($"{root.RootKind}{(root.IsPinned ? " (pinned)" : string.Empty)}{Frame(root)}: {string.Join(" -> ", chain)}");
            }

            return lines.Count == 0
                ? $"no root path to a {typeName} in the snapshot ({clock.Elapsed.TotalSeconds:0.0} s); {Why(runtime, typeName, isTarget, frame)}"
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

    /// <summary>
    /// An object's type and, for a delegate, the method it calls: a shell held 30 s through "System.Action ->
    /// System.Delegate+Wrapper[] -> System.Action -> ShellHost" from a stack root said neither whose event that was nor
    /// which handler (Linux CI, run 43200651803).
    /// </summary>
    private static string Name(ClrObject item, ulong address)
    {
        var name = item.Type?.Name ?? $"0x{address:x}";
        try
        {
            if (item.IsDelegate && item.AsDelegate().GetDelegateTarget() is { Method: { } method })
            {
                name += $" ({method.Type?.Name}.{method.Name})";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
        }

        return name;
    }

    /// <summary>For a root on a thread's stack, the frame that holds it and where that thread is: which code keeps the object, and what it waits for.</summary>
    private static string Frame(ClrRoot root)
    {
        if (root is not ClrStackRoot { StackFrame: { } frame })
        {
            return string.Empty;
        }

        try
        {
            var thread = frame.Thread;
            var stack = thread is null ? [] : thread.EnumerateStackTrace().Select(f => f.Method is { } m ? $"{m.Type?.Name}.{m.Name}" : null).OfType<string>().Take(12).ToArray();
            return $" in {(frame.Method is { } method ? $"{method.Type?.Name}.{method.Name}" : frame.FrameName ?? "a frame")} (thread {thread?.ManagedThreadId}, at {string.Join(" < ", stack)})";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return " in a frame that can't be read";
        }
    }

    /// <summary>
    /// What the snapshot says when it has no path: whether the object is on the heap at all, whether the heap could be
    /// walked, and the threads running managed code (cooperative), whose stacks the runtime may not report where the
    /// snapshot caught them. A frame's object had no path on Windows and Linux CI with nothing more said (runs
    /// 43368524423 and 43422861486, docs/ci-flakes.md).
    /// </summary>
    private static string Why(ClrRuntime runtime, string typeName, Func<ClrObject, bool> isTarget, string? frame)
    {
        var heap = runtime.Heap;
        var parts = new List<string>();
        try
        {
            int all = 0, picked = 0;
            foreach (var candidate in heap.EnumerateObjects())
            {
                if (candidate.Type?.Name == typeName)
                {
                    all++;
                    picked += Picked(isTarget, candidate) ? 1 : 0;
                }
            }

            parts.Add($"{all} {typeName} on the heap, {picked} of them picked");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            parts.Add($"the heap's objects can't be read ({ex.GetType().Name}: {ex.Message})");
        }

        parts.Add(heap.CanWalkHeap ? "the heap can be walked" : "the heap can't be walked (a GC was under way)");
        var threads = runtime.Threads.Where(t => t.IsAlive).ToArray();
        parts.Add($"{threads.Length} managed thread(s), {threads.Count(t => t.GCMode == GCMode.Cooperative)} running managed code{(threads.Any(t => t.IsGc) ? ", one of them the GC's" : string.Empty)}");
        if (frame is not null)
        {
            foreach (var thread in threads)
            {
                try
                {
                    var stack = thread.EnumerateStackTrace().Select(f => f.Method is { } m ? $"{m.Type?.Name}.{m.Name}" : f.FrameName).OfType<string>().Take(16).ToArray();
                    if (stack.Any(f => f.EndsWith("." + frame, StringComparison.Ordinal)))
                    {
                        parts.Add($"thread {thread.ManagedThreadId} ({thread.GCMode}, state 0x{(int)thread.State:x}) has {thread.EnumerateStackRoots().Count()} stack root(s), at {string.Join(" < ", stack)}");
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    parts.Add($"thread {thread.ManagedThreadId}'s stack can't be read ({ex.Message})");
                }
            }
        }

        return string.Join("; ", parts);
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
