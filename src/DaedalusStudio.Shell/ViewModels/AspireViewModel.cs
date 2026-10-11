using System.Collections.ObjectModel;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Aspire;

namespace DaedalusStudio.Shell.ViewModels;

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
        if (change.Initial is { } initial)
        {
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
                }
            }
        }
        else if (change.Deleted is { } deleted && IndexOf(deleted) is >= 0 and var at)
        {
            Resources.RemoveAt(at);
        }

        UpdateStatus();
    }

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

    private async Task WatchLogsAsync(AspireResourceClient client, string resource, CancellationToken cancellationToken)
    {
        var text = new System.Text.StringBuilder();
        try
        {
            await foreach (var line in client.WatchLogsAsync(resource, cancellationToken).ConfigureAwait(false))
            {
                lock (text)
                {
                    text.Append(line.Text).Append('\n');
                    if (text.Length > 200_000)
                    {
                        text.Remove(0, text.Length - 150_000);
                    }
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
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or InvalidDataException)
        {
        }
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
