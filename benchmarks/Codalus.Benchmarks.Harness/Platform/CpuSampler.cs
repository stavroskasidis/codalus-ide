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
    private readonly Dictionary<(int Pid, DateTime Started), string> _names = [];
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

    /// <summary>The CPU each process name has used so far (an MSBuild node's several processes summed), to tell where it goes.</summary>
    public IReadOnlyDictionary<string, TimeSpan> ByProcess
    {
        get
        {
            Sample();
            lock (_seen)
            {
                return _seen.GroupBy(e => _names.GetValueOrDefault(e.Key, "?"), StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Aggregate(TimeSpan.Zero, (sum, e) => sum + e.Value), StringComparer.Ordinal);
            }
        }
    }

    /// <summary>The processes whose CPU grew most from <paramref name="before"/> to now, as "name 1.2 s", largest first.</summary>
    public string Since(IReadOnlyDictionary<string, TimeSpan> before, int top = 5) =>
        string.Join(", ", ByProcess.Select(e => (e.Key, Delta: e.Value - before.GetValueOrDefault(e.Key)))
            .Where(e => e.Delta > TimeSpan.FromMilliseconds(50)).OrderByDescending(e => e.Delta).Take(top)
            .Select(e => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{e.Key} {e.Delta.TotalSeconds:0.0} s")));

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

                if (!_names.ContainsKey(key))
                {
                    _names[key] = Name(key.Pid);
                }
            }
        }
    }

    private static string Name(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "?";
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
