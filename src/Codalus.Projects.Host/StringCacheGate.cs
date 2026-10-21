namespace Codalus.Projects.Host;

/// <summary>
/// Keeps MSBuild's process-wide string cache from being cleared while this process evaluates or edits projects.
/// </summary>
/// <remarks>
/// <para>
/// MSBuild interns the strings it reads and expands (evaluation, project XML, globs) in one cache per process,
/// <c>Microsoft.NET.StringTools</c>' <c>WeakStringCache</c>: strings longer than 500 characters are held through weak
/// <see cref="System.Runtime.InteropServices.GCHandle"/>s. At the end of every build with an in-process node (all of
/// the design-time builds, and real builds on MSBuild before 17 or with <c>CODALUS_BUILD_IN_PROCESS</c>), the node's
/// <c>BuildRequestEngine.CleanupForBuild</c> calls <c>Strings.ClearCachedStrings()</c>, which frees those handles
/// without the lock that interning takes. An evaluation that interns a long string at that moment reads a freed
/// handle and fails with <see cref="InvalidOperationException"/> "Handle is not initialized." (the build host's
/// intermittent CI failure, first chased in PR #68).
/// </para>
/// <para>
/// So MSBuild work in this process holds the gate shared (<see cref="Enter"/>), and the end of a build holds it alone
/// (<see cref="Exclusive"/>). Shared holders never wait for each other. The end of a build waits for the work in
/// progress, and new work waits behind it (or a stream of overlapping evaluations would hold it off for good), except
/// work entering again from a flow that already holds the gate (an evaluation inside an edit, or a task it started),
/// which would otherwise deadlock. The end of a build holds work off only while it runs, which takes milliseconds.
/// Entering and leaving needn't be on the same thread.
/// </para>
/// </remarks>
internal static class StringCacheGate
{
    private static readonly object s_sync = new();
    private static readonly AsyncLocal<int> s_held = new();
    private static int s_shared;
    private static int s_waiting;
    private static bool s_exclusive;

    /// <summary>How many hold the gate shared now (for tests).</summary>
    internal static int SharedCount
    {
        get
        {
            lock (s_sync)
            {
                return s_shared;
            }
        }
    }

    /// <summary>Holds the gate shared until the result is disposed: around anything that loads, evaluates or edits projects.</summary>
    public static Scope Enter()
    {
        var nested = s_held.Value > 0;
        lock (s_sync)
        {
            // A flow that holds the gate keeps the end of a build from starting, so it can't be running: it only waits.
            while (s_exclusive || (s_waiting > 0 && !nested))
            {
                Monitor.Wait(s_sync);
            }

            s_shared++;
        }

        s_held.Value++;
        return new Scope();
    }

    /// <summary>
    /// Runs <paramref name="action"/> (a <c>BuildManager.EndBuild</c>, whose in-process node clears the cache) once no
    /// MSBuild work holds the gate, keeping new work out until it returns. It must not enter the gate itself.
    /// </summary>
    public static void Exclusive(Action action)
    {
        lock (s_sync)
        {
            s_waiting++;
            try
            {
                while (s_exclusive || s_shared > 0)
                {
                    Monitor.Wait(s_sync);
                }
            }
            catch
            {
                s_waiting--;
                Monitor.PulseAll(s_sync); // the work held back behind it
                throw;
            }

            s_waiting--;
            s_exclusive = true;
        }

        try
        {
            action();
        }
        finally
        {
            lock (s_sync)
            {
                s_exclusive = false;
                Monitor.PulseAll(s_sync);
            }
        }
    }

    /// <summary>A shared hold on the gate; leaves it once, however many times it is disposed.</summary>
    public sealed class Scope : IDisposable
    {
        private int _left;

        internal Scope()
        {
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) != 0)
            {
                return;
            }

            if (s_held.Value > 0)
            {
                s_held.Value--;
            }

            lock (s_sync)
            {
                if (--s_shared == 0)
                {
                    Monitor.PulseAll(s_sync);
                }
            }
        }
    }
}
