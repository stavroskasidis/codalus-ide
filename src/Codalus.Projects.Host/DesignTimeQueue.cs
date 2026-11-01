namespace Codalus.Projects.Host;

/// <summary>
/// The projects waiting for a design-time build, and the one worker that builds them in batches through
/// <paramref name="buildBatch"/>: projects queued while a batch builds go in the next one. Here, without MSBuild types,
/// so it is tested on its own.
/// </summary>
internal sealed class DesignTimeQueue(IEqualityComparer<string> comparer, Func<string[], CancellationToken, Task> buildBatch, CancellationToken lifetime) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _pending = new(comparer);
    private CancellationTokenSource _batch = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private Task _worker = Task.CompletedTask;
    private bool _running;
    private string[] _inFlight = [];

    /// <summary>How long a new worker lets a burst of changes (watcher events, many projects evaluated) settle into one batch.</summary>
    internal TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>For tests: runs on a worker as it ends, once it has found nothing left to build (or was cancelled).</summary>
    internal Action? Stopping { get; set; }

    /// <summary>Queues <paramref name="projects"/>. False when nothing is queued or building: no progress will come.</summary>
    public bool Add(IEnumerable<string> projects)
    {
        lock (_gate)
        {
            _pending.UnionWith(projects);
            if (_running)
            {
                return true;
            }

            if (_pending.Count == 0)
            {
                return false;
            }

            Start();
            return true;
        }
    }

    /// <summary>From a batch: queues <paramref name="projects"/> for the next one, and says whether one will run.</summary>
    public bool Requeue(IEnumerable<string> projects)
    {
        lock (_gate)
        {
            _pending.UnionWith(projects);
            return _pending.Count > 0;
        }
    }

    /// <summary>For tests: runs in <see cref="PauseAsync"/> once it has cancelled, before it waits for the worker.</summary>
    internal Action? Paused { get; set; }

    /// <summary>Cancels the running batch and forgets the queued projects.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            CancelLocked();
        }
    }

    private void CancelLocked()
    {
        _pending.Clear();
        var previous = _batch;
        _batch = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        previous.Cancel();
        previous.Dispose();
    }

    /// <summary>Cancels the running batch and the queued projects, waits for the worker to end, and returns their projects, to queue them again later.</summary>
    public async Task<string[]> PauseAsync()
    {
        string[] interrupted;
        Task worker;
        lock (_gate)
        {
            // Taken and cleared at once: a project queued between the two (a framework switch while a reload paused) was
            // cleared without being returned, and never built. One queued after goes to the next worker.
            interrupted = [.. _pending, .. _inFlight];
            worker = _worker;
            CancelLocked();
        }

        Paused?.Invoke();
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        return interrupted;
    }

    private void Start()
    {
        _running = true;
        var token = _batch.Token;
        _worker = Task.Run(() => RunAsync(token), CancellationToken.None);
    }

    /// <summary>Drains the queue in batches, so changes made during a batch are built right after it.</summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Settle, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                string[] batch;
                lock (_gate)
                {
                    if (cancellationToken.IsCancellationRequested || _pending.Count == 0)
                    {
                        return;
                    }

                    batch = [.. _pending];
                    _pending.Clear();
                    _inFlight = batch;
                }

                try
                {
                    await buildBatch(batch, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    lock (_gate)
                    {
                        _inFlight = [];
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // Under the lock Add takes, so a project queued while this worker ends (a cancelled batch can take a while to)
            // starts the next one: decided here and ended after, Add saw this one still running and started none, and the
            // project waited for the next change to queue another.
            lock (_gate)
            {
                _running = false;
                if (_pending.Count > 0 && !_batch.IsCancellationRequested)
                {
                    Start();
                }
            }

            Stopping?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Cancel();
        Task worker;
        lock (_gate)
        {
            worker = _worker;
        }

        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _batch.Dispose();
    }
}
