namespace Codalus.Platform;

/// <summary>
/// Watches one file for external changes (including delete-and-recreate saves used by many editors) and raises
/// <see cref="Changed"/> once per burst of events, after <see cref="Debounce"/>. The event is raised on a
/// thread-pool thread.
/// </summary>
public sealed class FileWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private readonly string _fileName;

    public FileWatcher(string path, TimeSpan? debounce = null)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        _fileName = Path.GetFileName(full);
        Debounce = debounce ?? TimeSpan.FromMilliseconds(150);
        _timer = new Timer(_ => Changed?.Invoke(this, EventArgs.Empty));
        _watcher = new FileSystemWatcher(directory, _fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        _watcher.Changed += OnEvent;
        _watcher.Created += OnEvent;
        _watcher.Deleted += OnEvent;
        _watcher.Renamed += (s, e) =>
        {
            if (string.Equals(e.Name, _fileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.OldName, _fileName, StringComparison.OrdinalIgnoreCase))
            {
                OnEvent(s, e);
            }
        };
        _watcher.EnableRaisingEvents = true;
    }

    public TimeSpan Debounce { get; }

    public event EventHandler? Changed;

    private void OnEvent(object sender, FileSystemEventArgs e) => _timer.Change(Debounce, Timeout.InfiniteTimeSpan);

    public void Dispose()
    {
        _watcher.Dispose();
        _timer.Dispose();
    }
}
