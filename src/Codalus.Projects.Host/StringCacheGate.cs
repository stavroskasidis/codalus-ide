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
/// <para>
/// A cancelled build clears the cache sooner: <c>CancelAllSubmissions</c> aborts its nodes at once, and the in-process
/// node's cleanup clears it then. So a cancellation holds the gate alone from before the abort to the build's end
/// (<see cref="BeginExclusive"/>, <see cref="GatedCancellation"/>), and the build's own work that waits for the gate
/// meanwhile gives up (<see cref="EnterCancellable"/>).
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

    /// <summary>How many ends of builds wait for the gate now (for tests).</summary>
    internal static int WaitingCount
    {
        get
        {
            lock (s_sync)
            {
                return s_waiting;
            }
        }
    }

    /// <summary>Whether this flow holds the gate shared now.</summary>
    internal static bool IsHeld => s_held.Value > 0;

    /// <summary>Holds the gate shared until the result is disposed: around anything that loads, evaluates or edits projects.</summary>
    public static Scope Enter() => EnterCancellable(CancellationToken.None);

    /// <summary>
    /// <see cref="Enter"/>, giving up with <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/>
    /// is cancelled while it waits: a cancelled build's own work, which would otherwise wait for the gate that its
    /// cancellation holds until the build ends (<see cref="GatedCancellation"/>).
    /// </summary>
    public static Scope EnterCancellable(CancellationToken cancellationToken)
    {
        var nested = s_held.Value > 0;
        using var wake = cancellationToken.CanBeCanceled ? cancellationToken.UnsafeRegister(static _ =>
        {
            lock (s_sync)
            {
                Monitor.PulseAll(s_sync);
            }
        }, null) : default;
        lock (s_sync)
        {
            // A flow that holds the gate keeps the end of a build from starting, so it can't be running: it only waits.
            while (s_exclusive || (s_waiting > 0 && !nested))
            {
                cancellationToken.ThrowIfCancellationRequested();
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
        using (BeginExclusive())
        {
            action();
        }
    }

    /// <summary>
    /// Holds the gate alone, once no MSBuild work holds it, until the result is disposed: from a build's cancellation,
    /// whose in-process node clears the cache, to its end (<see cref="GatedCancellation"/>). Nothing in between may enter
    /// the gate.
    /// </summary>
    public static ExclusiveHold BeginExclusive()
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

        return new ExclusiveHold();
    }

    /// <summary>The gate held alone; leaves it once, however many times it is disposed.</summary>
    public sealed class ExclusiveHold : IDisposable
    {
        private int _left;

        internal ExclusiveHold()
        {
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) != 0)
            {
                return;
            }

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

/// <summary>
/// A build's cancellation and end through <see cref="StringCacheGate"/>. <c>BuildManager.CancelAllSubmissions</c> aborts the
/// build's nodes at once, on the thread pool, and the in-process node's cleanup clears MSBuild's string cache there, not
/// in <c>EndBuild</c>: an item edit evaluating meanwhile read a freed handle ("Handle is not initialized.", Linux CI,
/// run 41539988831, after a file change cancelled a design-time build). So the cancellation first takes the gate alone,
/// once the work in it has left, and holds it until the build has ended.
/// </summary>
/// <remarks>
/// <see cref="Cancel"/> returns at once: it runs as a token's callback, which runs where the token is cancelled, under
/// its owner's locks. A cancellation from a flow that holds the gate itself can't wait for it (that flow waits for the
/// build), so it cancels as before, without the gate. So does a build without an in-process node
/// (<paramref name="inProcess"/> false: a real build on MSBuild 17 and later), whose abort clears nothing here and
/// shouldn't wait for evaluations, or hold them off while its worker nodes shut down.
/// </remarks>
internal sealed class GatedCancellation(Action cancel, bool inProcess = true)
{
    private readonly object _sync = new();
    private Task<StringCacheGate.ExclusiveHold?>? _cancelling;

    /// <summary>Calls <see cref="Cancel"/> when <paramref name="cancellationToken"/> is cancelled, in the flow that cancels it.</summary>
    public CancellationTokenRegistration Register(CancellationToken cancellationToken) =>
        cancellationToken.UnsafeRegister(static state => ((GatedCancellation)state!).Cancel(), this);

    /// <summary>Cancels the build once it holds the gate alone (the first time only).</summary>
    public void Cancel()
    {
        lock (_sync)
        {
            if (_cancelling is not null)
            {
                return;
            }

            if (!inProcess || StringCacheGate.IsHeld)
            {
                cancel();
                _cancelling = Task.FromResult<StringCacheGate.ExclusiveHold?>(null);
                return;
            }

            _cancelling = Task.Run(() =>
            {
                var hold = StringCacheGate.BeginExclusive();
                try
                {
                    cancel();
                }
                catch
                {
                    hold.Dispose();
                    throw;
                }

                return (StringCacheGate.ExclusiveHold?)hold;
            });
        }
    }

    /// <summary>
    /// Runs <paramref name="end"/> (the build's <c>EndBuild</c>): in the cancellation's hold when the build was cancelled,
    /// which it then leaves, otherwise through <see cref="StringCacheGate.Exclusive"/>. Call it after the registration is
    /// disposed, so no cancellation starts after it.
    /// </summary>
    public async Task EndAsync(Action end)
    {
        Task<StringCacheGate.ExclusiveHold?>? cancelling;
        lock (_sync)
        {
            cancelling = _cancelling;
        }

        StringCacheGate.ExclusiveHold? hold = null;
        if (cancelling is not null)
        {
            try
            {
                hold = await cancelling.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The cancel failed and left the gate: the end takes it on its own below.
            }
        }

        if (hold is null)
        {
            StringCacheGate.Exclusive(end);
            return;
        }

        using (hold)
        {
            end();
        }
    }
}
