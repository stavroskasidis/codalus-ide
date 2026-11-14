using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Notifications;

namespace Codalus.Shell.ViewModels;

public sealed class NotificationActionViewModel(string label, ICommand command)
{
    public string Label { get; } = label;

    public ICommand Command { get; } = command;
}

public sealed class NotificationViewModel
{
    public NotificationViewModel(Notification model, NotificationService service)
    {
        Model = model;
        Actions = [.. model.Actions.Select(a => new NotificationActionViewModel(a, new RelayCommand(() => service.Choose(model, a))))];
        DismissCommand = new RelayCommand(() => service.Dismiss(model));
    }

    public Notification Model { get; }

    public string Message => Model.Message;

    public string Source => Model.Source;

    public string Time => Model.Created.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    public string Icon => Model.Severity switch
    {
        NotificationSeverity.Error => "error",
        NotificationSeverity.Warning => "warning",
        _ => "info",
    };

    public bool IsError => Model.Severity == NotificationSeverity.Error;

    public bool IsWarning => Model.Severity == NotificationSeverity.Warning;

    public bool IsInfo => Model.Severity == NotificationSeverity.Info;

    public string AccessibleName => $"{Model.Severity}: {Model.Message} ({Model.Source})";

    public IReadOnlyList<NotificationActionViewModel> Actions { get; }

    public bool HasActions => Actions.Count > 0;

    public ICommand DismissCommand { get; }

    /// <summary>The pointer is over the toast: it doesn't hide while it is, nor for a moment after (set by its card).</summary>
    public bool IsHovered
    {
        get => _isHovered;
        set
        {
            if (_isHovered && !value)
            {
                HoverEnded = DateTime.UtcNow;
            }

            _isHovered = value;
        }
    }

    private bool _isHovered;

    /// <summary>When the pointer last left the toast.</summary>
    public DateTime HoverEnded { get; private set; }

    /// <summary>The user clicked in the toast or gave it the focus: it stays until dismissed (set by its card).</summary>
    public bool IsPinned { get; set; }
}

/// <summary>
/// Notifications as toasts (bottom right) and in the Notifications tool window. Informational toasts hide
/// themselves after <c>workbench.notifications.autoHideSeconds</c> but stay in the center until dismissed; not while
/// the pointer is over one, and not at all once the user clicked in it or gave it the focus. Warnings, errors and
/// toasts with buttons (they ask for a choice) stay until dismissed or hidden with Escape.
/// </summary>
public sealed class NotificationsViewModel : ObservableObject, IDisposable
{
    private const int MaxToasts = 3;

    /// <summary>How long a toast stays after the pointer leaves it, when its time is up.</summary>
    internal static readonly TimeSpan HoverLinger = TimeSpan.FromSeconds(3);
    private readonly NotificationService _service;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Notification, byte> _leftToasts = new();
    private readonly Func<int> _autoHideSeconds;
    private readonly HashSet<IDisposable> _hideTimers = [];

    public NotificationsViewModel(NotificationService service, Func<int> autoHideSeconds)
    {
        _service = service;
        _autoHideSeconds = autoHideSeconds;
        ClearAllCommand = new RelayCommand(service.DismissAll);
        HideToastsCommand = new RelayCommand(HideToasts);
        foreach (var notification in service.Items)
        {
            Items.Insert(0, new NotificationViewModel(notification, service));
        }

        service.Added += (_, n) => Ui.Post(() => OnAdded(n));
        service.Removed += (_, n) => Ui.Post(() => OnRemoved(n));
    }

    /// <summary>All notifications, newest first.</summary>
    public ObservableCollection<NotificationViewModel> Items { get; } = [];

    public ObservableCollection<NotificationViewModel> Toasts { get; } = [];

    public int Count => Items.Count;

    public bool IsEmpty => Items.Count == 0;

    public bool HasToasts => Toasts.Count > 0;

    public ICommand ClearAllCommand { get; }

    public ICommand HideToastsCommand { get; }

    /// <summary>Raised when the number of notifications or visible toasts changes.</summary>
    public event EventHandler? Changed;

    /// <summary>When true (the Notifications window is focused), new notifications don't pop up as toasts.</summary>
    public bool SuppressToasts { get; set; }

    /// <summary>
    /// Whether a notification was a toast and isn't any more, though it's still in the center: hidden with Escape,
    /// pushed out by newer toasts or hidden after a while. Whoever keeps one notification about a problem shows it again
    /// (M49). Any thread.
    /// </summary>
    public bool HasLeftToasts(Notification notification) => _leftToasts.ContainsKey(notification);

    public void HideToasts()
    {
        foreach (var toast in Toasts)
        {
            _leftToasts.TryAdd(toast.Model, 0);
        }

        Toasts.Clear();
        RaiseChanged();
    }

    private void OnAdded(Notification notification)
    {
        var vm = new NotificationViewModel(notification, _service);
        Items.Insert(0, vm);
        if (!SuppressToasts)
        {
            Toasts.Add(vm);
            while (Toasts.Count > MaxToasts)
            {
                _leftToasts.TryAdd(Toasts[0].Model, 0);
                Toasts.RemoveAt(0);
            }

            if (notification.Severity == NotificationSeverity.Info && !vm.HasActions && _autoHideSeconds() is > 0 and var seconds)
            {
                HideLater(vm, TimeSpan.FromSeconds(seconds));
            }
        }

        RaiseChanged();
    }

    /// <summary>Stops the toasts' timers: a pending one-shot timer holds the toast and this list (and the shell) until it ticks.</summary>
    public void Dispose()
    {
        foreach (var timer in _hideTimers)
        {
            timer.Dispose();
        }

        _hideTimers.Clear();
    }

    /// <summary>Hides a toast once its time is up, unless the pointer is over it (then a moment after it leaves) or it's pinned.</summary>
    private void HideLater(NotificationViewModel vm, TimeSpan after)
    {
        IDisposable? timer = null;
        timer = Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            _hideTimers.Remove(timer!);
            if (ToastExpiry(vm, DateTime.UtcNow) is { } remaining)
            {
                HideLater(vm, remaining);
            }
            else if (!vm.IsPinned)
            {
                HideToast(vm);
            }
        }, after);
        _hideTimers.Add(timer);
    }

    /// <summary>How much longer a toast whose time is up stays for the pointer over it, or that left it just now; null: none.</summary>
    internal static TimeSpan? ToastExpiry(NotificationViewModel vm, DateTime now)
    {
        if (vm.IsPinned)
        {
            return null;
        }

        if (vm.IsHovered)
        {
            return HoverLinger;
        }

        var left = vm.HoverEnded + HoverLinger - now;
        return left > TimeSpan.Zero ? left : null;
    }

    private void HideToast(NotificationViewModel vm)
    {
        if (Toasts.Remove(vm))
        {
            _leftToasts.TryAdd(vm.Model, 0);
            RaiseChanged();
        }
    }

    private void OnRemoved(Notification notification)
    {
        _leftToasts.TryRemove(notification, out _);
        foreach (var list in new[] { Items, Toasts })
        {
            if (list.FirstOrDefault(v => v.Model == notification) is { } vm)
            {
                list.Remove(vm);
            }
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasToasts));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
