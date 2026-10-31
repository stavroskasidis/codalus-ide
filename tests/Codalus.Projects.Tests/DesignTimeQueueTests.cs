using Codalus.Projects.Host;

namespace Codalus.Projects.Tests;

/// <summary>
/// The build host's queue of design-time builds on its own: a project queued as its worker ends must still be built.
/// Lost, it kept the inputs of its last build until the next change queued another (docs/ci-flakes.md, Web21).
/// </summary>
public sealed class DesignTimeQueueTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);
    private readonly List<string> _built = [];
    private readonly DesignTimeQueue _queue;
    private Func<string[], CancellationToken, Task> _build = (_, _) => Task.CompletedTask;

    public DesignTimeQueueTests() => _queue = new DesignTimeQueue(StringComparer.Ordinal, BuildAsync, CancellationToken.None) { Settle = TimeSpan.Zero };

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _queue.DisposeAsync();

    private async Task BuildAsync(string[] batch, CancellationToken cancellationToken)
    {
        await _build(batch, cancellationToken);
        lock (_built)
        {
            _built.AddRange(batch);
        }
    }

    private bool Built(string project)
    {
        lock (_built)
        {
            return _built.Contains(project);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(started.Elapsed < s_timeout, because);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_project_queued_after_the_worker_found_nothing_left_is_built()
    {
        Assert.False(_queue.Add([]));

        // The worker builds A, finds the queue empty, and is held there, before it ends.
        using var stopping = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var held = 0;
        _queue.Stopping = () =>
        {
            if (Interlocked.Exchange(ref held, 1) == 0)
            {
                stopping.Set();
                release.Wait(s_timeout);
            }
        };
        Assert.True(_queue.Add(["A"]));
        Assert.True(stopping.Wait(s_timeout, TestContext.Current.CancellationToken), "The worker never stopped.");
        Assert.True(Built("A"));

        // B, queued then: a worker builds it (the old queue saw the ending one still running, and started none).
        Assert.True(_queue.Add(["B"]));
        release.Set();
        await WaitUntilAsync(() => Built("B"), "B was queued as the worker ended, and never built.");
    }

    [Fact]
    public async Task A_project_queued_while_a_cancelled_batch_winds_down_is_built()
    {
        // A batch that takes a while to end once cancelled (MSBuild's submissions do), as a configuration switch cancels it.
        var winding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _build = async (batch, _) =>
        {
            if (batch is ["A"])
            {
                started.SetResult();
                await winding.Task;
            }
        };
        Assert.True(_queue.Add(["A"]));
        await started.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        _queue.Cancel();

        // The new configuration's evaluation queues B before the cancelled batch has ended.
        Assert.True(_queue.Add(["B"]));
        winding.SetResult();
        await WaitUntilAsync(() => Built("B"), "B was queued while the cancelled batch wound down, and never built.");
    }

    [Fact]
    public async Task Pausing_returns_the_building_and_queued_projects_once_the_worker_ended()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _build = async (batch, cancellationToken) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        };
        _queue.Add(["A"]);
        await started.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        _queue.Add(["B"]);

        var interrupted = await _queue.PauseAsync().WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        Assert.Equal(["A", "B"], interrupted.Order(StringComparer.Ordinal));
        Assert.False(Built("A"));

        // Queued again, they build.
        _build = (_, _) => Task.CompletedTask;
        Assert.True(_queue.Add(interrupted));
        await WaitUntilAsync(() => Built("A") && Built("B"), "The paused projects never built after they were queued again.");
    }
}
