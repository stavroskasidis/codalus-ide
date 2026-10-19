using Codalus.Projects.Host;
using Microsoft.NET.StringTools;

namespace Codalus.Projects.Tests;

/// <summary>
/// The build host's gate between MSBuild work and the end of a build (<see cref="StringCacheGate"/>), on its own and
/// with MSBuild's real string cache: clearing the cache while strings are interned fails with "Handle is not
/// initialized." (the build host's intermittent CI failure, PR #68), and the gate keeps the two apart.
/// </summary>
public sealed class StringCacheGateTests
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(30);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

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
            await Task.Delay(200, Token);
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
    /// hold it off for good: the first version of the gate kept a design-time build from ending for a minute.
    /// </summary>
    [Fact]
    public async Task New_work_waits_behind_a_build_waiting_to_end()
    {
        var outer = StringCacheGate.Enter();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exclusive = Task.Run(() => StringCacheGate.Exclusive(ended.SetResult), Token);
        await Task.Delay(100, Token);

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
        var outer = StringCacheGate.Enter();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exclusive = Task.Run(() => StringCacheGate.Exclusive(ended.SetResult), Token);
        await Task.Delay(100, Token);

        var inner = await Task.Run(StringCacheGate.Enter, Token).WaitAsync(s_wait, Token);
        Assert.Equal(2, StringCacheGate.SharedCount);
        inner.Dispose();
        inner.Dispose(); // leaves once
        Assert.False(ended.Task.IsCompleted);

        await Task.Run(outer.Dispose, Token);
        await ended.Task.WaitAsync(s_wait, Token);
        await exclusive.WaitAsync(s_wait, Token);
        Assert.Equal(0, StringCacheGate.SharedCount);
    }
}
