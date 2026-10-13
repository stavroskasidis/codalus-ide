namespace Codalus.Workbench.Notifications;

/// <summary>
/// A long operation the user can see (and, when <see cref="Cancellable"/>, stop): a plugin's (API 1.0,
/// <c>IWindow.WithProgressAsync</c>) or the IDE's own. The status bar shows the running ones.
/// </summary>
public sealed class ProgressTask
{
    private readonly ProgressService _service;
    private readonly Action? _cancel;

    internal ProgressTask(ProgressService service, long id, string title, string source, Action? cancel)
    {
        _service = service;
        Id = id;
        Title = title;
        Source = source;
        _cancel = cancel;
    }

    public long Id { get; }

    public string Title { get; }

    /// <summary>Who runs it: a plugin's name, or the IDE.</summary>
    public string Source { get; }

    public string? Message { get; private set; }

    /// <summary>0 to 100, or null while it can't say.</summary>
    public double? Percent { get; private set; }

    public bool Cancellable => _cancel is not null;

    public bool IsCancellationRequested { get; private set; }

    /// <summary>Updates what the task says; a null keeps the previous value.</summary>
    public void Report(string? message, double? percent)
    {
        Message = message ?? Message;
        Percent = percent is { } p ? Math.Clamp(p, 0, 100) : Percent;
        _service.OnChanged(this);
    }

    /// <summary>Asks the task's owner to stop it.</summary>
    public void Cancel()
    {
        if (_cancel is not null && !IsCancellationRequested)
        {
            IsCancellationRequested = true;
            _cancel();
            _service.OnChanged(this);
        }
    }

    /// <summary>The task is over.</summary>
    public void End() => _service.OnEnded(this);
}

/// <summary>The running <see cref="ProgressTask"/>s. Events arrive on any thread.</summary>
public sealed class ProgressService
{
    private readonly Lock _gate = new();
    private readonly List<ProgressTask> _tasks = [];
    private long _nextId;

    public event EventHandler? Changed;

    public IReadOnlyList<ProgressTask> Running
    {
        get
        {
            lock (_gate)
            {
                return [.. _tasks];
            }
        }
    }

    /// <summary>Starts a task; <paramref name="cancel"/> (when given) is how the user stops it.</summary>
    public ProgressTask Begin(string title, string source, Action? cancel = null)
    {
        var task = new ProgressTask(this, Interlocked.Increment(ref _nextId), title, source, cancel);
        lock (_gate)
        {
            _tasks.Add(task);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return task;
    }

    internal void OnChanged(ProgressTask task) => Changed?.Invoke(this, EventArgs.Empty);

    internal void OnEnded(ProgressTask task)
    {
        bool removed;
        lock (_gate)
        {
            removed = _tasks.Remove(task);
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
