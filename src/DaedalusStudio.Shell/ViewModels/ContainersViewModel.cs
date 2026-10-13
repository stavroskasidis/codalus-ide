using System.Collections.ObjectModel;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Containers;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>A container in the Containers window.</summary>
public sealed record ContainerRow(ContainerSummary Container, bool IsOurs)
{
    public string Name => Container.Name;

    public string Detail => string.Join(" · ", new[] { Container.Image, Container.Status, Container.Ports }.Where(s => s.Length > 0));

    public string StateGlyph => Container.IsRunning ? "●" : "○";

    public string AccessibleName => $"{Container.Name}, {(Container.IsRunning ? "running" : Container.State)}, {Container.Image}";
}

/// <summary>An image in the Containers window.</summary>
public sealed record ImageRow(ImageSummary Image)
{
    public string Name => Image.Reference;

    public string Detail => string.Join(" · ", new[] { Image.Size, Image.Created, Image.Id }.Where(s => s.Length > 0));
}

/// <summary>A name and a value (an environment variable, a label, a port, a mount).</summary>
public sealed record ContainerPropertyRow(string Name, string Value);

/// <summary>An entry of the Files tab.</summary>
public sealed record ContainerFileRow(string Path, string Name, bool IsDirectory)
{
    public string Label => IsDirectory ? Name + "/" : Name;
}

/// <summary>
/// The Containers window (M17a, ADR 0042): the engine's containers and images, and the selected container's logs,
/// environment, ports, mounts, labels and files. Actions run through the user's docker or podman, never blocking the
/// UI; the shell supplies attaching the debugger, a terminal in the container, the browser and opening files.
/// </summary>
public sealed class ContainersViewModel(
    Func<ContainerEngine> engine,
    Func<ContainerSummary, Task> attach,
    Func<ContainerSummary, Task> terminal,
    Action<Uri> browse,
    Func<string, string, Task> openFile,
    Func<ContainerSummary, Task>? openRemoteWindow = null) : ObservableObject
{
    public const string ToolWindowId = "containers";

    private ContainerRow? _selected;
    private ImageRow? _selectedImage;
    private string? _status = "Refresh to list the containers.";
    private string _logs = string.Empty;
    private string _filesPath = "/";
    private int _tab;
    private bool _isBusy;
    private int _refreshes;
    private int _detailLoads;

    public ObservableCollection<ContainerRow> Containers { get; } = [];

    public ObservableCollection<ImageRow> Images { get; } = [];

    public ObservableCollection<ContainerPropertyRow> Environment { get; } = [];

    public ObservableCollection<ContainerPropertyRow> Ports { get; } = [];

    public ObservableCollection<ContainerPropertyRow> Labels { get; } = [];

    public ObservableCollection<ContainerFileRow> Files { get; } = [];

    /// <summary>The selected container's details, as <c>inspect</c> said them last.</summary>
    public ContainerDetails? Details { get; private set; }

    public ContainerRow? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                _filesPath = "/";
                OnPropertyChanged(nameof(FilesPath));
                _ = LoadDetailsAsync();
            }
        }
    }

    public bool HasSelection => _selected is not null;

    public ImageRow? SelectedImage
    {
        get => _selectedImage;
        set => SetField(ref _selectedImage, value);
    }

    /// <summary>The details tab: 0 Logs, 1 Environment, 2 Ports, 3 Labels, 4 Files.</summary>
    public int Tab
    {
        get => _tab;
        set
        {
            if (SetField(ref _tab, value) && value == 4)
            {
                _ = LoadFilesAsync(_filesPath);
            }
        }
    }

    public string Logs
    {
        get => _logs;
        private set => SetField(ref _logs, value);
    }

    public string FilesPath
    {
        get => _filesPath;
        set => SetField(ref _filesPath, string.IsNullOrWhiteSpace(value) ? "/" : value);
    }

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

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    /// <summary>The engine's name in the window's caption, once known.</summary>
    public string EngineName => engine().Name;

    /// <summary>Lists the containers and images again, keeping the selection.</summary>
    public async Task RefreshAsync()
    {
        var refresh = ++_refreshes;
        var current = engine();
        IsBusy = true;
        try
        {
            var (os, error) = await current.ServerOsAsync(CancellationToken.None).ConfigureAwait(true);
            if (refresh != _refreshes)
            {
                return;
            }

            if (os is null)
            {
                Containers.Clear();
                Images.Clear();
                Status = $"{current.Name} doesn't answer: {error} Start Docker or Podman (or 'podman machine start'), or choose the engine in the '{ContainerEngine.ToolSetting}' setting.";
                return;
            }

            var containers = await current.ContainersAsync(CancellationToken.None).ConfigureAwait(true);
            var images = await current.ImagesAsync(CancellationToken.None).ConfigureAwait(true);
            var ours = (await current.ContainersAsync(CancellationToken.None, ContainerEngine.ProjectLabel).ConfigureAwait(true)).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            if (refresh != _refreshes)
            {
                return;
            }

            var selected = _selected?.Container.Name;
            Containers.Clear();
            foreach (var container in containers.OrderBy(c => !ours.Contains(c.Id)).ThenBy(c => !c.IsRunning).ThenBy(c => c.Name, StringComparer.Ordinal))
            {
                Containers.Add(new ContainerRow(container, ours.Contains(container.Id)));
            }

            Images.Clear();
            foreach (var image in images)
            {
                Images.Add(new ImageRow(image));
            }

            Status = containers.Count == 0 ? $"No containers ({current.Name}, {os})." : null;
            var again = Containers.FirstOrDefault(c => c.Container.Name == selected);
            if (again is not null)
            {
                _selected = again;
                OnPropertyChanged(nameof(Selected));
                await LoadDetailsAsync().ConfigureAwait(true);
            }
            else if (_selected is not null)
            {
                Selected = null;
            }
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
        }
        finally
        {
            if (refresh == _refreshes)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>Reads the selected container's inspect data, logs and (on the Files tab) files.</summary>
    public async Task LoadDetailsAsync()
    {
        var load = ++_detailLoads;
        Environment.Clear();
        Ports.Clear();
        Labels.Clear();
        Files.Clear();
        Details = null;
        if (_selected is not { } row)
        {
            Logs = string.Empty;
            return;
        }

        try
        {
            var current = engine();
            var details = await current.InspectAsync(row.Container.Name, CancellationToken.None).ConfigureAwait(true);
            var logs = await current.LogsAsync(row.Container.Name, 500, CancellationToken.None).ConfigureAwait(true);
            if (load != _detailLoads)
            {
                return;
            }

            Details = details;
            Logs = logs;
            foreach (var (name, value) in details?.Environment ?? [])
            {
                Environment.Add(new ContainerPropertyRow(name, value));
            }

            foreach (var port in details?.Ports ?? [])
            {
                Ports.Add(new ContainerPropertyRow(port.ContainerPort, port.HostPort is null ? "not published" : $"{(string.IsNullOrEmpty(port.HostIp) ? "0.0.0.0" : port.HostIp)}:{port.HostPort}"));
            }

            foreach (var mount in details?.Mounts ?? [])
            {
                Ports.Add(new ContainerPropertyRow("mount " + mount.Destination, $"{mount.Source} ({mount.Type}{(mount.ReadOnly ? ", read-only" : string.Empty)})"));
            }

            foreach (var (name, value) in details?.Labels ?? [])
            {
                Labels.Add(new ContainerPropertyRow(name, value));
            }

            if (_tab == 4)
            {
                await LoadFilesAsync(_filesPath).ConfigureAwait(true);
            }
        }
        catch (InvalidOperationException ex)
        {
            Logs = ex.Message;
        }
    }

    /// <summary>Lists a folder of the selected (running) container.</summary>
    public async Task LoadFilesAsync(string path)
    {
        Files.Clear();
        if (_selected is not { } row)
        {
            return;
        }

        FilesPath = path;
        if (!row.Container.IsRunning)
        {
            Files.Add(new ContainerFileRow(path, "(the container isn't running)", false));
            return;
        }

        try
        {
            var entries = await engine().ListFilesAsync(row.Container.Name, path, CancellationToken.None).ConfigureAwait(true);
            if (path != "/")
            {
                Files.Add(new ContainerFileRow(ParentOf(path), "..", true));
            }

            foreach (var entry in entries)
            {
                Files.Add(new ContainerFileRow(path.TrimEnd('/') + "/" + entry.Name, entry.Name, entry.IsDirectory));
            }
        }
        catch (InvalidOperationException ex)
        {
            Files.Add(new ContainerFileRow(path, ex.Message, false));
        }
    }

    private static string ParentOf(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? "/" : trimmed[..slash];
    }

    /// <summary>Opens a folder (lists it) or a file (copied out, opened read-only).</summary>
    public Task OpenFileAsync(ContainerFileRow file) =>
        file.IsDirectory ? LoadFilesAsync(file.Path) : _selected is { } row ? openFile(row.Container.Name, file.Path) : Task.CompletedTask;

    // ------------------------------------------------------------------ actions

    private async Task ActAsync(Func<ContainerEngine, string, Task> action, string verb)
    {
        if (_selected is not { } row)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await action(engine(), row.Container.Name).ConfigureAwait(true);
            Status = null;
        }
        catch (InvalidOperationException ex)
        {
            Status = $"Could not {verb} {row.Container.Name}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    public Task StartAsync() => ActAsync((e, c) => e.StartAsync(c, CancellationToken.None), "start");

    public Task StopAsync() => ActAsync((e, c) => e.StopAsync(c, CancellationToken.None), "stop");

    public Task RestartAsync() => ActAsync((e, c) => e.RestartAsync(c, CancellationToken.None), "restart");

    public Task RemoveAsync() => ActAsync((e, c) => e.RemoveAsync(c, CancellationToken.None), "remove");

    public async Task RemoveImageAsync()
    {
        if (_selectedImage is not { } image)
        {
            return;
        }

        try
        {
            await engine().RemoveImageAsync(image.Image.Reference, CancellationToken.None).ConfigureAwait(true);
            Status = null;
        }
        catch (InvalidOperationException ex)
        {
            Status = $"Could not remove the image {image.Name}: {ex.Message}";
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>Opens the browser on the selected container's first published port.</summary>
    public void OpenInBrowser()
    {
        if (Details?.Ports.FirstOrDefault(p => p.HostPort is not null) is { } port)
        {
            browse(new Uri($"http://localhost:{port.HostPort}"));
        }
        else
        {
            Status = "The container publishes no port.";
        }
    }

    public Task AttachAsync() => _selected is { } row ? attach(row.Container) : Task.CompletedTask;

    public Task OpenTerminalAsync() => _selected is { } row ? terminal(row.Container) : Task.CompletedTask;

    /// <summary>A remote window in the selected container (M21b): its files, builds, terminals and debugging there.</summary>
    public Task OpenRemoteWindowAsync() => _selected is { } row && openRemoteWindow is not null ? openRemoteWindow(row.Container) : Task.CompletedTask;
}
