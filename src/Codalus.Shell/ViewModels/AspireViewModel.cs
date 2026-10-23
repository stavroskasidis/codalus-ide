using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Aspire;

namespace Codalus.Shell.ViewModels;

/// <summary>A resource of the running AppHost, as the Aspire window lists it.</summary>
public sealed record AspireResourceRow(AspireResource Resource)
{
    public string Name => Resource.DisplayName;

    public string Detail => string.Join(" · ", new[] { Resource.Type, Resource.State ?? "Unknown", string.Join(", ", Resource.Urls.Where(u => !u.IsInternal).Select(u => u.Url)) }.Where(s => s.Length > 0));

    /// <summary>● running, ◐ starting or waiting, ✕ failed, ○ otherwise.</summary>
    public string StateGlyph => Resource.State switch
    {
        "Running" => Resource.StateStyle == "error" ? "✕" : "●",
        "Starting" or "Waiting" or "Building" or "RuntimeUnhealthy" => "◐",
        "FailedToStart" or "Exited" when Resource.StateStyle == "error" => "✕",
        _ => "○",
    };

    public string AccessibleName => $"{Resource.DisplayName}, {Resource.Type}, {Resource.State ?? "state unknown"}";
}

/// <summary>
/// The Aspire window (M17b, ADR 0043): the running AppHost's resources (projects, containers, executables) with their
/// state and endpoints, the selected one's console output, environment and properties, its commands (start, stop,
/// restart), and the dashboard. The data comes from the AppHost's resource service, the one the dashboard reads.
/// </summary>
public sealed class AspireViewModel(Action<Uri> browse) : ObservableObject, IDisposable
{
    public const string ToolWindowId = "aspire";

    private AspireResourceClient? _client;
    private AspireResourceRow? _selected;
    private string? _status = "No AppHost is running. Start an Aspire AppHost project (F5 or Ctrl+F5) to see its resources here.";
    private string _logs = string.Empty;
    private string? _dashboard;
    private CancellationTokenSource? _logWatch;

    /// <summary>Each resource's state, by name, as the log watch reads it off the UI thread.</summary>
    private readonly ConcurrentDictionary<string, string?> _states = new(StringComparer.Ordinal);

    /// <summary>
    /// The waits before the selected resource's console is followed again when its stream ended while the resource is
    /// live: one per stream in a row that brought no new line, after which it gives up until the resource starts again.
    /// </summary>
    internal IReadOnlyList<TimeSpan> LogRetryDelays { get; init; } =
        [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)];

    public ObservableCollection<AspireResourceRow> Resources { get; } = [];

    public ObservableCollection<ContainerPropertyRow> Environment { get; } = [];

    public ObservableCollection<ContainerPropertyRow> Properties { get; } = [];

    public string? AppHost { get; private set; }

    public bool IsRunning => _client is not null;

    public AspireResourceRow? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                ShowDetails();
            }
        }
    }

    public bool HasSelection => _selected is not null;

    public string Logs
    {
        get => _logs;
        private set => SetField(ref _logs, value);
    }

    /// <summary>The dashboard's login URL, once the AppHost printed it.</summary>
    public string? DashboardUrl
    {
        get => _dashboard;
        set
        {
            if (SetField(ref _dashboard, value))
            {
                OnPropertyChanged(nameof(HasDashboard));
                UpdateStatus();
            }
        }
    }

    public bool HasDashboard => _dashboard is not null;

    public string? Status
    {
        get => _status;
        private set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => _status is not null;

    /// <summary>An AppHost started: its resources come from <paramref name="client"/>.</summary>
    public void Attach(string appHost, AspireResourceClient client)
    {
        _client = client;
        AppHost = appHost;
        DashboardUrl = null;
        Resources.Clear();
        Selected = null;
        OnPropertyChanged(nameof(IsRunning));
        UpdateStatus();
    }

    /// <summary>The AppHost ended: the last resources stay listed, greyed by their state.</summary>
    public void Detach()
    {
        _logWatch?.Cancel();
        _client = null;
        OnPropertyChanged(nameof(IsRunning));
        Status = AppHost is null ? Status : $"{AppHost} has stopped.";
    }

    private void UpdateStatus() =>
        Status = _client is null ? Status : Resources.Count == 0 ? $"{AppHost} is starting…" : null;

    public void Dispose()
    {
        _logWatch?.Cancel();
        _logWatch?.Dispose();
    }

    /// <summary>Applies a change of the resource tree (the dashboard's own resource and hidden ones aren't listed).</summary>
    public void Apply(AspireResourceChange change)
    {
        var restarted = false;
        if (change.Initial is { } initial)
        {
            _states.Clear();
            foreach (var resource in initial)
            {
                _states[resource.Name] = resource.State;
            }

            var selected = _selected?.Resource.Name;
            Resources.Clear();
            foreach (var resource in initial.Where(Visible))
            {
                Resources.Add(new AspireResourceRow(resource));
            }

            _selected = Resources.FirstOrDefault(r => r.Resource.Name == selected);
            OnPropertyChanged(nameof(Selected));
        }
        else if (change.Upsert is { } upsert && Visible(upsert))
        {
            restarted = !IsLive(upsert.Name) && IsLiveState(upsert.State);
            _states[upsert.Name] = upsert.State;
            var index = IndexOf(upsert.Name);
            var row = new AspireResourceRow(upsert);
            if (index < 0)
            {
                Resources.Add(row);
            }
            else
            {
                Resources[index] = row;
                if (_selected?.Resource.Name == upsert.Name)
                {
                    _selected = row;
                    OnPropertyChanged(nameof(Selected));
                    ShowProperties();

                    // Started again (its restart command): its console is followed again, from its start.
                    if (restarted)
                    {
                        WatchLogs();
                    }
                }
            }
        }
        else if (change.Upsert is { } hidden)
        {
            _states[hidden.Name] = hidden.State;
        }
        else if (change.Deleted is { } deleted)
        {
            _states.TryRemove(deleted, out _);
            if (IndexOf(deleted) is >= 0 and var at)
            {
                Resources.RemoveAt(at);
            }
        }

        UpdateStatus();
    }

    /// <summary>Whether a resource in <paramref name="state"/> runs or is on its way to (its console can still grow).</summary>
    private static bool IsLiveState(string? state) => state is "Running" or "Starting" or "Waiting" or "Building" or "RuntimeUnhealthy";

    private bool IsLive(string resource) => _states.TryGetValue(resource, out var state) && IsLiveState(state);

    private static bool Visible(AspireResource resource) => !resource.IsHidden && !resource.Type.Equals("Parameter", StringComparison.OrdinalIgnoreCase);

    private int IndexOf(string name)
    {
        for (var i = 0; i < Resources.Count; i++)
        {
            if (Resources[i].Resource.Name == name)
            {
                return i;
            }
        }

        return -1;
    }

    private void ShowProperties()
    {
        Environment.Clear();
        Properties.Clear();
        if (_selected is not { } row)
        {
            return;
        }

        foreach (var (name, value) in row.Resource.Environment)
        {
            Environment.Add(new ContainerPropertyRow(name, value));
        }

        foreach (var url in row.Resource.Urls)
        {
            Properties.Add(new ContainerPropertyRow("url " + url.Name, url.Url + (url.IsInternal ? " (internal)" : string.Empty)));
        }

        foreach (var (name, value) in row.Resource.Properties.Where(p => p.Value.Length > 0))
        {
            Properties.Add(new ContainerPropertyRow(name, value));
        }
    }

    private void ShowDetails()
    {
        ShowProperties();
        WatchLogs();
    }

    /// <summary>Follows the selected resource's console from its start, in place of the last one followed.</summary>
    private void WatchLogs()
    {
        _logWatch?.Cancel();
        _logWatch = null;
        Logs = string.Empty;
        if (_selected is not { } row || _client is not { } client)
        {
            return;
        }

        var watch = _logWatch = new CancellationTokenSource();
        _ = WatchLogsAsync(client, row.Resource.Name, watch.Token);
    }

    /// <summary>
    /// A resource's console, from the resource service. Its stream can end while the resource runs (a project just
    /// started: the stream ended after 64 ms with nothing, and the window stayed empty), so while the resource is live
    /// it's followed again, after <see cref="LogRetryDelays"/> (longer each time), until a stream brings new lines or
    /// the delays run out; not once the resource stops or goes, nor after another is selected or the AppHost ends (the
    /// token). Each stream replays the console from its start: lines already shown (by their numbers) are skipped.
    /// </summary>
    private async Task WatchLogsAsync(AspireResourceClient client, string resource, CancellationToken cancellationToken)
    {
        var text = new System.Text.StringBuilder();
        var shown = 0;
        var empty = 0; // streams in a row that brought no new line
        while (true)
        {
            var fresh = false;
            try
            {
                await foreach (var line in client.WatchLogsAsync(resource, cancellationToken).ConfigureAwait(false))
                {
                    if (line.Number > 0 && line.Number <= shown)
                    {
                        continue;
                    }

                    fresh = true;
                    shown = Math.Max(shown, line.Number);
                    text.Append(PlainText(line.Text)).Append('\n');
                    if (text.Length > 200_000)
                    {
                        text.Remove(0, text.Length - 150_000);
                    }

                    var snapshot = text.ToString();
                    Ui.Post(() =>
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            Logs = snapshot;
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                // The service went away or broke the stream: the same as one that ended.
            }

            empty = fresh ? 0 : empty + 1;
            if (cancellationToken.IsCancellationRequested || LogRetryDelays.Count == 0 || empty > LogRetryDelays.Count
                || !await WaitWhileLiveAsync(resource, LogRetryDelays[Math.Max(empty - 1, 0)], cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>Waits <paramref name="delay"/>; false when the resource stopped (or went) meanwhile, or the wait was cancelled.</summary>
    private async Task<bool> WaitWhileLiveAsync(string resource, TimeSpan delay, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + delay;
        while (IsLive(resource))
        {
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                return true;
            }

            try
            {
                await Task.Delay(left < TimeSpan.FromMilliseconds(100) ? left : TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>A console line without its ANSI escape sequences (colors), as the dashboard shows it.</summary>
    public static string PlainText(string text)
    {
        if (!text.Contains('\u001b', StringComparison.Ordinal))
        {
            return text;
        }

        var plain = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\u001b' && i + 1 < text.Length && text[i + 1] == '[')
            {
                i += 2;
                while (i < text.Length && !char.IsAsciiLetter(text[i]))
                {
                    i++;
                }

                continue;
            }

            plain.Append(text[i]);
        }

        return plain.ToString();
    }

    /// <summary>Runs a command of the selected resource (<c>resource-start</c>, <c>resource-stop</c>, <c>resource-restart</c>).</summary>
    public async Task<string?> ExecuteAsync(string command)
    {
        if (_selected is not { } row || _client is not { } client)
        {
            return "No resource is selected.";
        }

        try
        {
            var error = await client.ExecuteAsync(command, row.Resource, CancellationToken.None).ConfigureAwait(true);
            Status = error is null ? null : $"{row.Name}: {error}";
            return error;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            Status = $"{row.Name}: {ex.Message}";
            return ex.Message;
        }
    }

    public Task<string?> StartAsync() => ExecuteAsync("resource-start");

    public Task<string?> StopAsync() => ExecuteAsync("resource-stop");

    public Task<string?> RestartAsync() => ExecuteAsync("resource-restart");

    public void OpenDashboard()
    {
        if (_dashboard is not null && Uri.TryCreate(_dashboard, UriKind.Absolute, out var uri))
        {
            browse(uri);
        }
    }

    /// <summary>Opens the selected resource's first public endpoint.</summary>
    public void OpenEndpoint()
    {
        if (_selected?.Resource.Urls.FirstOrDefault(u => !u.IsInternal) is { } url && Uri.TryCreate(url.Url, UriKind.Absolute, out var uri))
        {
            browse(uri);
        }
    }
}
