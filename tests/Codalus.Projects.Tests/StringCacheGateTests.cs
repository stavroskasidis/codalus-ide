using Codalus.Projects.Host;
using Microsoft.NET.StringTools;

namespace Codalus.Projects.Tests;

/// <summary>
/// The gate tests run alone, after the parallel ones: the gate is the process's, and the evaluations and builds of
/// the other classes hold it and wait for it too, which their counts and waits would see.
/// </summary>
[CollectionDefinition(nameof(StringCacheGateTests), DisableParallelization = true)]
public sealed class StringCacheGateTestsAlone;

/// <summary>
/// The build host's gate between MSBuild work and the end of a build (<see cref="StringCacheGate"/>), on its own and
/// with MSBuild's real string cache: clearing the cache while strings are interned fails with "Handle is not
/// initialized." (the build host's intermittent CI failure, PR #68), and the gate keeps the two apart.
/// </summary>
[Collection(nameof(StringCacheGateTests))]
public sealed class StringCacheGateTests
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(30);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Until the end of a build started on the thread pool waits for the gate: a fixed delay isn't enough on a busy
    /// machine (the Windows runner started it after the test's 100 ms, so new work got in first).
    /// </summary>
    private static async Task UntilAnEndOfBuildWaitsAsync()
    {
        var until = DateTime.UtcNow + s_wait;
        while (StringCacheGate.WaitingCount == 0)
        {
            Assert.True(DateTime.UtcNow < until, "The end of the build never waited for the gate.");
            await Task.Delay(10, Token);
        }
    }

    /// <summary>
    /// What the build host does, at full speed: threads interning strings over 500 characters (the ones the cache holds
    /// through GC handles) inside the gate, and a thread clearing the cache as the end of a build with an in-process node
    /// does (<c>BuildRequestEngine.CleanupForBuild</c>), through <see cref="StringCacheGate.Exclusive"/>. Without the gate
    /// this fails within about two seconds on a 4-core machine, every time (in the interning threads or the clearing one).
    /// </summary>
    [Fact]
    public async Task Interning_never_fails_while_the_cache_is_cleared_through_the_gate()
    {
        var pool = Enumerable.Range(0, 2000).Select(i => i + new string('x', 600)).ToArray();
        var stop = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        var interned = 0L;
        var cleared = 0;
        var interning = Enumerable.Range(0, Math.Max(2, Environment.ProcessorCount)).Select(worker => Task.Run(() =>
        {
            for (var i = worker; DateTime.UtcNow < stop; i++)
            {
                using (StringCacheGate.Enter())
                {
                    Assert.Equal(pool[i % pool.Length], Strings.WeakIntern(pool[i % pool.Length].AsSpan()));
                }

                Interlocked.Increment(ref interned);
            }
        }, Token)).ToList();
        var clearing = Task.Run(() =>
        {
            while (DateTime.UtcNow < stop)
            {
                StringCacheGate.Exclusive(Strings.ClearCachedStrings);
                cleared++;
                Thread.Sleep(1);
            }
        }, Token);

        await Task.WhenAll(interning.Append(clearing));
        Assert.True(interned > 0 && cleared > 0, $"{interned} strings interned, the cache cleared {cleared} times.");
        Assert.Equal(0, StringCacheGate.SharedCount);
    }

    [Fact]
    public async Task The_end_of_a_build_waits_for_msbuild_work_in_progress()
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task exclusive;
        using (StringCacheGate.Enter())
        {
            exclusive = Task.Run(() => StringCacheGate.Exclusive(ended.SetResult), Token);
            await UntilAnEndOfBuildWaitsAsync();
            await Task.Delay(100, Token);
            Assert.False(ended.Task.IsCompleted);
        }

        await ended.Task.WaitAsync(s_wait, Token);
        await exclusive.WaitAsync(s_wait, Token);
    }

    [Fact]
    public async Task Msbuild_work_waits_while_a_build_ends()
    {
        using var ending = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var exclusive = Task.Run(() => StringCacheGate.Exclusive(() =>
        {
            ending.Set();
            release.Wait(s_wait, Token);
        }), Token);
        Assert.True(ending.Wait(s_wait, Token));

        var entered = Task.Run(() => StringCacheGate.Enter().Dispose(), Token);
        await Task.Delay(200, Token);
        Assert.False(entered.IsCompleted);

        release.Set();
        await entered.WaitAsync(s_wait, Token);
        await exclusive.WaitAsync(s_wait, Token);
    }

    /// <summary>
    /// New work waits behind an end of build that waits, or overlapping evaluations (three readers in a loop) would
    /// hold it off for good: the first version of the gate kept a design-time build from ending for a minute. The gate is
    /// the process's: a test that fails holding it would hang every test after it, so the hold is left in any case.
    /// </summary>
    [Fact]
    public async Task New_work_waits_behind_a_build_waiting_to_end()
    {
        using var outer = StringCacheGate.Enter();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exclusive = Task.Run(() => StringCacheGate.Exclusive(ended.SetResult), Token);
        await UntilAnEndOfBuildWaitsAsync();

        Task<bool> entered;
        using (ExecutionContext.SuppressFlow()) // another flow: not work inside the outer one
        {
            entered = Task.Run(() =>
            {
                using (StringCacheGate.Enter())
                {
                    return ended.Task.IsCompleted;
                }
            }, Token);
        }

        await Task.Delay(200, Token);
        Assert.False(entered.IsCompleted);
        outer.Dispose();
        Assert.True(await entered.WaitAsync(s_wait, Token)); // after the end of the build
        await exclusive.WaitAsync(s_wait, Token);
    }

    /// <summary>
    /// Work already in the gate can enter again (an evaluation inside an edit) while the end of a build waits, on any
    /// thread, and leave on another: a writer-first lock would deadlock here.
    /// </summary>
    [Fact]
    public async Task Nested_work_gets_in_while_a_build_waits_to_end()
    {
        using var outer = StringCacheGate.Enter();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exclusive = Task.Run(() => StringCacheGate.Exclusive(ended.SetResult), Token);
        await UntilAnEndOfBuildWaitsAsync();

        using var inner = await Task.Run(StringCacheGate.Enter, Token).WaitAsync(s_wait, Token);
        Assert.Equal(2, StringCacheGate.SharedCount);
        inner.Dispose();
        inner.Dispose(); // leaves once
        Assert.False(ended.Task.IsCompleted);

        await Task.Run(outer.Dispose, Token);
        await ended.Task.WaitAsync(s_wait, Token);
        await exclusive.WaitAsync(s_wait, Token);
        Assert.Equal(0, StringCacheGate.SharedCount);
    }

    /// <summary>
    /// What a cancelled build does (Linux CI, run 41539988831): <c>CancelAllSubmissions</c> aborts the nodes at once, and
    /// the in-process node's cleanup clears the cache then, not in <c>EndBuild</c>, while an item edit interned strings.
    /// Here the cancellation itself clears the real cache, over and over, through <see cref="GatedCancellation"/>, while
    /// threads intern inside the gate: interning never fails.
    /// </summary>
    [Fact]
    public async Task Interning_never_fails_while_a_cancelled_build_clears_the_cache()
    {
        var pool = Enumerable.Range(0, 2000).Select(i => i + new string('y', 600)).ToArray();
        var stop = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        var interned = 0L;
        var cancelled = 0;
        var interning = Enumerable.Range(0, Math.Max(2, Environment.ProcessorCount)).Select(worker => Task.Run(() =>
        {
            for (var i = worker; DateTime.UtcNow < stop; i++)
            {
                using (StringCacheGate.Enter())
                {
                    Assert.Equal(pool[i % pool.Length], Strings.WeakIntern(pool[i % pool.Length].AsSpan()));
                }

                Interlocked.Increment(ref interned);
            }
        }, Token)).ToList();
        var cancelling = Task.Run(async () =>
        {
            while (DateTime.UtcNow < stop)
            {
                var cancellation = new GatedCancellation(Strings.ClearCachedStrings);
                cancellation.Cancel();
                await cancellation.EndAsync(() => { });
                cancelled++;
            }
        }, Token);

        await Task.WhenAll(interning.Append(cancelling));
        Assert.True(interned > 0 && cancelled > 0, $"{interned} strings interned, {cancelled} builds cancelled.");
        Assert.Equal(0, StringCacheGate.SharedCount);
    }

    /// <summary>
    /// A cancellation returns at once (it runs as a token's callback, under its owner's locks), aborts the build only once
    /// the work in the gate has left, and keeps new work out until the build has ended.
    /// </summary>
    [Fact]
    public async Task A_cancellation_waits_for_msbuild_work_then_holds_new_work_until_the_build_ends()
    {
        using var aborted = new ManualResetEventSlim();
        var cancellation = new GatedCancellation(aborted.Set);
        using var source = new CancellationTokenSource();
        using var registration = cancellation.Register(source.Token);

        var work = StringCacheGate.Enter();
        try
        {
            Task cancelling;
            using (ExecutionContext.SuppressFlow()) // the canceller is another flow, not the one in the gate
            {
                cancelling = Task.Run(source.Cancel, Token);
            }

            await cancelling.WaitAsync(s_wait, Token); // returned, though the abort waits
            await UntilAnEndOfBuildWaitsAsync();
            Assert.False(aborted.Wait(200, Token));
        }
        finally
        {
            work.Dispose();
        }

        Assert.True(aborted.Wait(s_wait, Token)); // once the work left

        Task<bool> entered;
        var ended = false;
        using (ExecutionContext.SuppressFlow())
        {
            entered = Task.Run(() =>
            {
                using (StringCacheGate.Enter())
                {
                    return Volatile.Read(ref ended);
                }
            }, Token);
        }

        await Task.Delay(200, Token);
        Assert.False(entered.IsCompleted); // held back between the abort and the end
        registration.Dispose();
        await cancellation.EndAsync(() => Volatile.Write(ref ended, true));
        Assert.True(await entered.WaitAsync(s_wait, Token)); // after the end
        Assert.Equal(0, StringCacheGate.SharedCount);
        Assert.Equal(0, StringCacheGate.WaitingCount);
    }

    /// <summary>
    /// A cancelled build's own work that waits for the gate (a project the batch loads itself) gives up, instead of waiting
    /// for the hold its cancellation keeps until the build ends, which would never come.
    /// </summary>
    [Fact]
    public async Task A_cancelled_builds_own_work_gives_up_waiting_for_the_gate()
    {
        using var source = new CancellationTokenSource();
        Task<Exception?> waiting;
        using (var hold = StringCacheGate.BeginExclusive())
        {
            using (ExecutionContext.SuppressFlow())
            {
                waiting = Task.Run(() =>
                {
                    try
                    {
                        StringCacheGate.EnterCancellable(source.Token).Dispose();
                        return null;
                    }
                    catch (OperationCanceledException ex)
                    {
                        return (Exception?)ex;
                    }
                }, Token);
            }

            await Task.Delay(200, Token);
            Assert.False(waiting.IsCompleted);
            await source.CancelAsync();
            Assert.IsType<OperationCanceledException>(await waiting.WaitAsync(s_wait, Token), exactMatch: false);
        }

        Assert.Equal(0, StringCacheGate.SharedCount);
        using (StringCacheGate.Enter())
        {
            Assert.Equal(1, StringCacheGate.SharedCount); // the gate is free again
        }
    }

    /// <summary>
    /// A flow in the gate that cancels a build can't wait for the gate (it waits for the build): it aborts at once, without
    /// the gate, as before, and the build ends through the gate on its own.
    /// </summary>
    [Fact]
    public async Task A_cancellation_from_inside_the_gate_aborts_at_once()
    {
        var aborted = false;
        var cancellation = new GatedCancellation(() => aborted = true);
        using var source = new CancellationTokenSource();
        using (cancellation.Register(source.Token))
        {
            using (StringCacheGate.Enter())
            {
                await source.CancelAsync();
                Assert.True(aborted);
            }
        }

        var ended = false;
        await cancellation.EndAsync(() => ended = true).WaitAsync(s_wait, Token);
        Assert.True(ended);
        Assert.Equal(0, StringCacheGate.SharedCount);
    }

    /// <summary>
    /// A build without an in-process node (a real build on MSBuild 17 and later) clears nothing here when cancelled: it
    /// aborts at once, while work holds the gate, and holds nothing off until it ends.
    /// </summary>
    [Fact]
    public async Task A_build_without_an_in_process_node_aborts_at_once()
    {
        var aborted = false;
        var cancellation = new GatedCancellation(() => aborted = true, inProcess: false);
        Task<bool> entered;
        using (StringCacheGate.Enter())
        {
            Task cancelling;
            using (ExecutionContext.SuppressFlow()) // another flow than the one in the gate
            {
                cancelling = Task.Run(cancellation.Cancel, Token);
            }

            await cancelling.WaitAsync(s_wait, Token);
            Assert.True(aborted);
            Assert.Equal(0, StringCacheGate.WaitingCount);
            using (ExecutionContext.SuppressFlow())
            {
                entered = Task.Run(() =>
                {
                    using (StringCacheGate.Enter())
                    {
                        return true;
                    }
                }, Token);
            }

            Assert.True(await entered.WaitAsync(s_wait, Token)); // nothing held off
        }

        var ended = false;
        await cancellation.EndAsync(() => ended = true).WaitAsync(s_wait, Token);
        Assert.True(ended);
    }
}
