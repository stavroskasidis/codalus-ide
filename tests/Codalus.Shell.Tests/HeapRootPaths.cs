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
    /// <summary>The snapshots one call takes at most, when they catch a GC under way or the holder thread running.</summary>
    private const int MaxSnapshots = 10;

    /// <summary>
    /// Up to <paramref name="maxPaths"/> root paths to the objects of <paramref name="typeName"/> that <paramref name="isTarget"/>
    /// picks, one per line. With none, why there may be none: the heap and the threads as the snapshot has them, and the
    /// thread <paramref name="holder"/> (a managed thread id), where a test holds its object on the stack.
    /// </summary>
    /// <remarks>
    /// A snapshot can't show every root. One taken while a GC is under way has a heap that can't be walked, whose objects
    /// may be half moved; and a thread caught running managed code (cooperative), rather than stopped in a wait, has a
    /// stack the runtime may not walk, so none of its frames' roots are reported. Both gave "no root path" on CI for an
    /// object a waiting thread's frame held (Linux and Windows, runs 43368524423 and 43422861486; docs/ci-flakes.md):
    /// locally, 25 of 25 snapshots of a thread spinning in managed code had none, and 1 of 25 taken while another thread
    /// allocated caught a GC. A third, on Windows: the holder thread stopped in its wait, but its stack walked no further
    /// than the wait's P/Invoke frames, so it had no roots either (run 43583064438). Such a snapshot isn't the process's
    /// state at a moment its roots can be read, so another is taken, up to <see cref="MaxSnapshots"/>, and the answer says
    /// how many were set aside and why.
    /// </remarks>
    public static string Describe(string typeName, Func<ClrObject, bool> isTarget, int maxPaths = 3, int? holder = null)
    {
        if (OperatingSystem.IsMacOS())
        {
            // ClrMD's macOS snapshot is a core file of the whole process: dumping the Shell tests' process lost the macOS
            // runner (eng/ci-test.sh). Windows takes the snapshot in memory; Linux writes the core, as ClrMD does.
            return "no snapshot of the process on macOS (a core file of the test host risks the runner)";
        }

        var clock = Stopwatch.StartNew();
        var setAside = new List<string>();
        try
        {
            while (true)
            {
                // No symbol server: the runtime is read with the DAC beside this process's own runtime.
                using var target = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId, new DataTargetOptions { SymbolPaths = [] });
                var clr = target.ClrVersions.Single();
                var dac = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), OperatingSystem.IsWindows() ? "mscordaccore.dll" : OperatingSystem.IsMacOS() ? "libmscordaccore.dylib" : "libmscordaccore.so");
                using var runtime = clr.CreateRuntime(dac, ignoreMismatch: clr.Version.Major == 0); // ELF and Mach-O runtimes have no version resource
                if (Unreadable(runtime, holder) is { } why && setAside.Count < MaxSnapshots - 1)
                {
                    setAside.Add(why);
                    continue;
                }

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

                var snapshots = setAside.Count == 0 ? string.Empty : $"; {setAside.Count} earlier snapshot(s) set aside: {string.Join(", ", setAside)}";
                return lines.Count == 0
                    ? $"no root path to a {typeName} in the snapshot ({clock.Elapsed.TotalSeconds:0.0} s{snapshots}); {Why(runtime, typeName, isTarget, holder)}"
                    : $"{lines.Count} root path(s) to a {typeName} ({clock.Elapsed.TotalSeconds:0.0} s{snapshots}):\n" + string.Join('\n', lines);
            }
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

    /// <summary>Why a snapshot can't show the roots (a GC under way, the holder thread running), or null when it can.</summary>
    private static string? Unreadable(ClrRuntime runtime, int? holder)
    {
        if (!runtime.Heap.CanWalkHeap)
        {
            return "a GC was under way";
        }

        if (holder is { } id)
        {
            var thread = runtime.Threads.FirstOrDefault(t => t.IsAlive && t.ManagedThreadId == id);
            if (thread is null)
            {
                return $"thread {id} wasn't in it";
            }

            if (thread.GCMode == GCMode.Cooperative)
            {
                return $"thread {id} was running managed code";
            }

            // Stopped in a wait, but its stack walked no further than the wait's own frames: on Windows the snapshot
            // caught the thread blocked in WaitForMultipleObjectsEx and its walk ended at the two P/Invoke frames, with
            // none of its callers' frames and so none of their roots (Windows CI, PR #368's run 43583064438). The
            // holder's own frame holds the object, so a holder stack with no roots isn't one the snapshot could read.
            try
            {
                if (!thread.EnumerateStackRoots().Any())
                {
                    var frames = thread.EnumerateStackTrace().Select(f => f.Method is { } m ? $"{m.Type?.Name}.{m.Name}" : f.FrameName).OfType<string>().Take(3);
                    return $"thread {id}'s stack had no roots (it ended at {string.Join(" < ", frames)})";
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return $"thread {id}'s stack couldn't be read ({ex.Message})";
            }
        }

        return null;
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
    private static string Why(ClrRuntime runtime, string typeName, Func<ClrObject, bool> isTarget, int? holder)
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
        if (holder is { } id && threads.FirstOrDefault(t => t.ManagedThreadId == id) is { } thread)
        {
            try
            {
                var stack = thread.EnumerateStackTrace().Select(f => f.Method is { } m ? $"{m.Type?.Name}.{m.Name}" : f.FrameName).OfType<string>().Take(16).ToArray();
                parts.Add($"thread {id} ({thread.GCMode}, state 0x{(int)thread.State:x}) has {thread.EnumerateStackRoots().Count()} stack root(s), at {string.Join(" < ", stack)}");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                parts.Add($"thread {id}'s stack can't be read ({ex.Message})");
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
