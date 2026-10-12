using System.Collections.ObjectModel;
using System.Globalization;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Remote;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>A port of the Ports window: forwarded (with its local address) or only listening on the remote machine.</summary>
public sealed class RemotePortRow(int remotePort, ForwardedPort? forwarded, string? process, string address)
{
    public int RemotePort { get; } = remotePort;

    public ForwardedPort? Forwarded { get; } = forwarded;

    public bool IsForwarded => Forwarded is not null;

    public string Port => RemotePort.ToString(CultureInfo.InvariantCulture);

    public string LocalAddress => Forwarded?.LocalAddress ?? "not forwarded";

    public string Detail => string.Join(" · ", new[] { process, address, Forwarded?.Origin switch { "auto" => "detected", "user" => "added", { } other => other, null => null } }.Where(s => !string.IsNullOrEmpty(s)));

    public string AccessibleName => $"Port {Port}, {LocalAddress}" + (Detail.Length > 0 ? ", " + Detail : string.Empty);
}

/// <summary>
/// The Ports window of a remote window (M21, ADR 0049): forwarded ports and the remote machine's other listening
/// ports; forward one, stop forwarding, or open it in the browser.
/// </summary>
public sealed class RemotePortsViewModel(ShellHost shell) : ObservableObject
{
    public const string ToolWindowId = "daedalus.remotePorts";

    private RemotePortRow? _selected;
    private string _status = "Not connected to a remote machine.";

    public ObservableCollection<RemotePortRow> Ports { get; } = [];

    public RemotePortRow? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(CanStop));
            }
        }
    }

    public bool HasSelection => Selected is not null;

    public bool CanStop => Selected is { IsForwarded: true };

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    /// <summary>Rebuilds the rows from the session (UI thread).</summary>
    public void Refresh()
    {
        var selected = Selected?.RemotePort;
        Ports.Clear();
        if (shell.Remote is not { } remote)
        {
            Status = "Not connected to a remote machine.";
            return;
        }

        var forwarded = remote.Ports.Forwarded.ToDictionary(p => p.RemotePort);
        var listening = remote.Ports.Listening.GroupBy(p => p.Port).ToDictionary(g => g.Key, g => g.First());
        foreach (var port in forwarded.Keys.Union(listening.Keys).Order())
        {
            var listen = listening.GetValueOrDefault(port);
            Ports.Add(new RemotePortRow(port, forwarded.GetValueOrDefault(port), listen?.ProcessName ?? forwarded.GetValueOrDefault(port)?.ProcessName, listen?.Address ?? "not listening"));
        }

        Selected = Ports.FirstOrDefault(p => p.RemotePort == selected);
        Status = $"{remote.Display}: {forwarded.Count.ToString(CultureInfo.InvariantCulture)} forwarded, {listening.Count.ToString(CultureInfo.InvariantCulture)} listening";
    }

    public Task ForwardAsync() => shell.ForwardPortAsync(Selected is { IsForwarded: false } row ? row.Port : null);

    public void Stop()
    {
        if (Selected is { IsForwarded: true } row)
        {
            shell.Remote?.Ports.Stop(row.RemotePort);
        }
    }

    public void OpenInBrowser()
    {
        if (Selected is { Forwarded: { } forwarded })
        {
            shell.RemoteBrowser(new Uri("http://localhost:" + forwarded.LocalPort.ToString(CultureInfo.InvariantCulture) + "/"));
        }
    }
}
