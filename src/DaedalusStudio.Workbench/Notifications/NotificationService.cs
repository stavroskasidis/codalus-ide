namespace DaedalusStudio.Workbench.Notifications;

public enum NotificationSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>A notification shown as a toast and kept in the notification center until dismissed.</summary>
public sealed class Notification
{
    private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Notification(long id, NotificationSeverity severity, string message, IReadOnlyList<string> actions, string source)
    {
        Id = id;
        Severity = severity;
        Message = message;
        Actions = actions;
        Source = source;
        Created = DateTimeOffset.Now;
    }

    public long Id { get; }

    public NotificationSeverity Severity { get; }

    public string Message { get; }

    public IReadOnlyList<string> Actions { get; }

    /// <summary>"Daedalus Studio" or the contributing plugin's display name.</summary>
    public string Source { get; }

    public DateTimeOffset Created { get; }

    /// <summary>Completes with the chosen action, or null when dismissed.</summary>
    public Task<string?> Result => _result.Task;

    internal bool Complete(string? action) => _result.TrySetResult(action);
}

/// <summary>Owns active notifications. Views subscribe to <see cref="Added"/> and <see cref="Removed"/>.</summary>
public sealed class NotificationService
{
    private const int MaxHistory = 100;
    private readonly Lock _gate = new();
    private readonly List<Notification> _items = [];
    private long _nextId;

    public event EventHandler<Notification>? Added;

    public event EventHandler<Notification>? Removed;

    public IReadOnlyList<Notification> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    public Notification Show(NotificationSeverity severity, string message, IReadOnlyList<string>? actions = null, string source = "Daedalus Studio")
    {
        var notification = new Notification(Interlocked.Increment(ref _nextId), severity, message, actions ?? [], source);
        Notification? evicted = null;
        lock (_gate)
        {
            _items.Add(notification);
            if (_items.Count > MaxHistory)
            {
                evicted = _items[0];
                _items.RemoveAt(0);
            }
        }

        if (evicted is not null)
        {
            evicted.Complete(null);
            Removed?.Invoke(this, evicted);
        }

        Added?.Invoke(this, notification);
        return notification;
    }

    public Notification Info(string message, params string[] actions) => Show(NotificationSeverity.Info, message, actions);

    public Notification Warning(string message, params string[] actions) => Show(NotificationSeverity.Warning, message, actions);

    public Notification Error(string message, params string[] actions) => Show(NotificationSeverity.Error, message, actions);

    /// <summary>The user picked an action (which also dismisses the notification).</summary>
    public void Choose(Notification notification, string action)
    {
        notification.Complete(action);
        Dismiss(notification);
    }

    public void Dismiss(Notification notification)
    {
        notification.Complete(null);
        bool removed;
        lock (_gate)
        {
            removed = _items.Remove(notification);
        }

        if (removed)
        {
            Removed?.Invoke(this, notification);
        }
    }

    public void DismissAll()
    {
        foreach (var notification in Items)
        {
            Dismiss(notification);
        }
    }
}
