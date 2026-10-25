namespace Codalus.Benchmarks.Harness.Platform;

/// <summary>
/// The CPU time a process tree uses (M50: CPU use reported on Linux and macOS while a solution first opens). The tree's
/// processes are sampled every 250 ms, so a helper that exits (a design-time build's worker node) still counts with
/// what it had used at its last sample.
/// </summary>
internal sealed class CpuSampler : IAsyncDisposable
{
    private readonly int _rootPid;
    private readonly Dictionary<(int Pid, DateTime Started), TimeSpan> _seen = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public CpuSampler(int rootPid)
    {
        _rootPid = rootPid;
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>The CPU the tree has used since it started, every process it had at a sample included.</summary>
    public TimeSpan Total
    {
        get
        {
            Sample();
            lock (_seen)
            {
                return _seen.Values.Aggregate(TimeSpan.Zero, (sum, t) => sum + t);
            }
        }
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Sample();
            try
            {
                await Task.Delay(250, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Sample()
    {
        var now = ProcessTree.CpuTimes(_rootPid);
        lock (_seen)
        {
            foreach (var (key, time) in now)
            {
                if (!_seen.TryGetValue(key, out var before) || time > before)
                {
                    _seen[key] = time;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
