using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Mobile;

namespace Codalus.Shell.ViewModels;

/// <summary>A device the Device Log can follow: a running Android device or emulator, or a booted iOS simulator.</summary>
public sealed record DeviceLogSource(MobileDevice Device)
{
    public override string ToString() => Device.Label;
}

/// <summary>An entry in the Device Log.</summary>
public sealed class DeviceLogRow(DeviceLogEntry entry)
{
    public DeviceLogEntry Entry { get; } = entry;

    public string Time => Entry.Time;

    public string Level => "VDIWEF"[(int)Entry.Level].ToString();

    public string Tag => Entry.Tag;

    public string Process => Entry.Process ?? Entry.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string Message => Entry.Message;

    public bool IsError => Entry.Level >= DeviceLogLevel.Error;

    public bool IsWarning => Entry.Level == DeviceLogLevel.Warning;

    public bool IsQuiet => Entry.Level <= DeviceLogLevel.Debug;

    public string AccessibleName => $"{Entry.Level} {Tag}: {Message}";
}

/// <summary>
/// The Device Log (ADR 0104): a device's log as it comes (<c>adb logcat</c>, or a simulator's unified log), in columns,
/// filtered by level, by text and to the startup project's app (its processes, found with <c>pidof</c> on Android; its
/// process name in a simulator); Pause, Clear and Save. It keeps the last <see cref="DeviceLogs.Capacity"/> entries and
/// stops following when its window closes.
/// </summary>
public sealed class DeviceLogViewModel : ObservableObject
{
    public const string ToolWindowId = "mobile.deviceLog";

    private readonly ShellHost _shell;
    private readonly List<DeviceLogEntry> _entries = [];
    private readonly List<DeviceLogEntry> _pending = [];
    private readonly object _gate = new();
    private readonly HashSet<int> _appProcesses = [];
    private DeviceLogSource? _source;
    private CancellationTokenSource? _stream;
    private bool _flushPosted;
    private int _level;
    private string _search = string.Empty;
    private bool _onlyMyApp;
    private bool _paused;
    private string _status = string.Empty;
    private string? _appPackage;
    private string? _appProcessName;

    public DeviceLogViewModel(ShellHost shell)
    {
        _shell = shell;
        ClearCommand = new RelayCommand(Clear);
        SaveCommand = new RelayCommand(() => _ = SaveAsync());
        RefreshCommand = new RelayCommand(() => _ = RefreshSourcesAsync());
    }

    public ObservableCollection<DeviceLogSource> Sources { get; } = [];

    public ObservableCollection<DeviceLogRow> Rows { get; } = [];

    /// <summary>Rows were added at the end (the view keeps the last one in sight while not paused).</summary>
    public event EventHandler? RowsAppended;

    public DeviceLogSource? Source
    {
        get => _source;
        set
        {
            if (SetField(ref _source, value))
            {
                Clear();
                Follow();
            }
        }
    }

    public IReadOnlyList<string> Levels { get; } =
    [
        Localizer.Get("msg.deviceLog.verbose", "Verbose"),
        Localizer.Get("msg.deviceLog.debug", "Debug"),
        Localizer.Get("msg.deviceLog.info", "Info"),
        Localizer.Get("msg.deviceLog.warning", "Warning"),
        Localizer.Get("msg.deviceLog.error", "Error"),
        Localizer.Get("msg.deviceLog.fatal", "Fatal"),
    ];

    /// <summary>The lowest level shown (an index of <see cref="Levels"/>, a <see cref="DeviceLogLevel"/>).</summary>
    public int Level
    {
        get => _level;
        set
        {
            if (SetField(ref _level, value))
            {
                Refilter();
            }
        }
    }

    public string Search
    {
        get => _search;
        set
        {
            if (SetField(ref _search, value ?? string.Empty))
            {
                Refilter();
            }
        }
    }

    /// <summary>Only the startup project's app.</summary>
    public bool OnlyMyApp
    {
        get => _onlyMyApp;
        set
        {
            if (SetField(ref _onlyMyApp, value))
            {
                if (value)
                {
                    _ = FindAppAsync();
                }

                Refilter();
            }
        }
    }

    /// <summary>The view stops changing (entries still come in, and show on resume).</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (SetField(ref _paused, value) && !value)
            {
                Refilter();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    /// <summary>What "only my app" means now: the startup project's package or process, for the tooltip.</summary>
    public string AppDescription => _appPackage ?? _appProcessName ?? Localizer.Get("msg.deviceLog.noApp", "the startup project's app (none is set)");

    public ICommand ClearCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand RefreshCommand { get; }

    /// <summary>The devices whose logs can be followed now.</summary>
    internal IEnumerable<MobileDevice> Followable() =>
        _shell.Mobile.Devices.Where(d => d.IsRunning && d.IsUsable && (d.Platform == MobilePlatform.Android || (d.Platform == MobilePlatform.iOS && d.Kind == MobileDeviceKind.Simulator)));

    /// <summary>Reads the devices again and follows the chosen one (the window has shown).</summary>
    public async Task RefreshSourcesAsync()
    {
        await _shell.RefreshDevicesAsync().ConfigureAwait(true);
        UpdateSources();
        if (_stream is null)
        {
            Follow();
        }
    }

    /// <summary>The device list changed: the picker follows; a device that went away stops being followed.</summary>
    public void OnDevicesChanged()
    {
        if (_stream is not null || Sources.Count > 0)
        {
            UpdateSources();
        }
    }

    private void UpdateSources()
    {
        var devices = Followable().ToList();
        var current = _source?.Device.Id;
        Sources.Clear();
        foreach (var device in devices)
        {
            Sources.Add(new DeviceLogSource(device));
        }

        var keep = Sources.FirstOrDefault(s => s.Device.Id == current);
        if (keep is not null)
        {
            _source = keep;
            OnPropertyChanged(nameof(Source));
            return;
        }

        // The startup project's device first, else the first one.
        var preferred = _shell.Mobile.DeviceFor(MobilePlatform.Android) is { IsRunning: true } android ? android.Id : null;
        Source = Sources.FirstOrDefault(s => s.Device.Id == preferred) ?? Sources.FirstOrDefault();
        if (Sources.Count == 0)
        {
            Status = Localizer.Get("msg.deviceLog.noDevices", "No running device. Start an emulator (Android Device Manager) or connect a phone with USB debugging on.");
        }
    }

    /// <summary>Follows the chosen device's log, ending the previous one.</summary>
    private void Follow()
    {
        StopFollowing();
        if (_source is not { } source)
        {
            return;
        }

        ToolCommand command;
        Func<string, DeviceLogEntry?> parse;
        if (source.Device.Platform == MobilePlatform.Android)
        {
            if (_shell.Mobile.Android().Adb is not { } adb)
            {
                Status = Localizer.Get("msg.deviceLog.noAdb", "adb wasn't found in the Android SDK.");
                return;
            }

            command = new ToolCommand(adb, DeviceLogs.LogcatArguments(source.Device.Id)) { Environment = _shell.AndroidSdk.ToolEnvironment() };
            parse = DeviceLogs.ParseLogcat;
        }
        else
        {
            command = new ToolCommand("xcrun", DeviceLogs.SimulatorLogArguments(source.Device.Id));
            parse = DeviceLogs.ParseUnifiedLog;
        }

        var stream = new CancellationTokenSource();
        _stream = stream;
        Status = Localizer.Format("msg.deviceLog.following", "Following {0}.", source.Device.Label);
        if (_onlyMyApp)
        {
            _ = FindAppAsync();
        }

        _ = Task.Run(async () =>
        {
            string? ended = null;
            try
            {
                var code = await _shell.RunTool(command, line =>
                {
                    if (parse(line) is { } entry)
                    {
                        Enqueue(entry);
                    }
                }, stream.Token).ConfigureAwait(false);
                ended = Localizer.Format("msg.deviceLog.ended", "The log ended (exit code {0}): the device went away, or Refresh follows it again.", code);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                ended = Localizer.Format("msg.deviceLog.failed", "The log couldn't be read: {0}", ex.Message);
            }

            if (ended is not null)
            {
                Ui.Post(() =>
                {
                    if (ReferenceEquals(_stream, stream))
                    {
                        _stream = null;
                        Status = ended;
                    }
                });
            }
        });
    }

    /// <summary>Stops following (the window closed, or another device).</summary>
    public void StopFollowing()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        stream?.Cancel();
        stream?.Dispose();
    }

    private void Enqueue(DeviceLogEntry entry)
    {
        lock (_gate)
        {
            _pending.Add(entry);
            if (_flushPosted)
            {
                return;
            }

            _flushPosted = true;
        }

        _ = Task.Delay(150).ContinueWith(_ => Ui.Post(Flush), TaskScheduler.Default);
    }

    /// <summary>Moves the entries that came in to the list (UI thread, a few times a second).</summary>
    private void Flush()
    {
        List<DeviceLogEntry> batch;
        lock (_gate)
        {
            batch = [.. _pending];
            _pending.Clear();
            _flushPosted = false;
        }

        // Closed: nothing is shown, so nothing is read.
        if (!_shell.Layout.IsVisible(ToolWindowId))
        {
            StopFollowing();
            Status = Localizer.Get("msg.deviceLog.closed", "Stopped while the window was closed: Refresh follows the device again.");
            return;
        }

        _entries.AddRange(batch);
        if (_entries.Count > DeviceLogs.Capacity)
        {
            _entries.RemoveRange(0, _entries.Count - DeviceLogs.Capacity + (DeviceLogs.Capacity / 10));
            if (!_paused)
            {
                Refilter();
            }

            return;
        }

        if (_paused)
        {
            return;
        }

        var added = false;
        foreach (var entry in batch.Where(Matches))
        {
            Rows.Add(new DeviceLogRow(entry));
            added = true;
        }

        if (added)
        {
            RowsAppended?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool Matches(DeviceLogEntry entry) =>
        DeviceLogs.Matches(
            entry,
            (DeviceLogLevel)Math.Clamp(_level, 0, Levels.Count - 1),
            _search,
            _onlyMyApp && _source?.Device.Platform == MobilePlatform.Android ? _appProcesses : null,
            _onlyMyApp && _source?.Device.Platform == MobilePlatform.iOS ? _appProcessName ?? string.Empty : null);

    private void Refilter()
    {
        Rows.Clear();
        foreach (var entry in _entries.Where(Matches))
        {
            Rows.Add(new DeviceLogRow(entry));
        }

        RowsAppended?.Invoke(this, EventArgs.Empty);
    }

    private void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
        }

        _entries.Clear();
        _appProcesses.Clear();
        Rows.Clear();
    }

    /// <summary>
    /// The startup project's app: its package (<c>ApplicationId</c>) on Android, whose processes <c>pidof</c> finds every
    /// two seconds while following (each run of the app is a new process; earlier ones stay in); its executable
    /// (<c>AssemblyName</c>) in a simulator.
    /// </summary>
    private async Task FindAppAsync()
    {
        if (_shell.Runs.StartupProject is not { } project || !_shell.IsMobileProject(project))
        {
            _appPackage = _appProcessName = null;
            OnPropertyChanged(nameof(AppDescription));
            Refilter();
            return;
        }

        try
        {
            var properties = await _shell.ProjectManagement.GetPropertiesAsync(project, ["ApplicationId", "AssemblyName"], null, CancellationToken.None).ConfigureAwait(true);
            _appPackage = properties.Properties.FirstOrDefault(p => p.Name == "ApplicationId")?.Value is { Length: > 0 } id ? id : null;
            _appProcessName = properties.Properties.FirstOrDefault(p => p.Name == "AssemblyName")?.Value is { Length: > 0 } name ? name : Path.GetFileNameWithoutExtension(project);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Codalus.Rpc.JsonRpcRemoteException or Codalus.Rpc.JsonRpcConnectionLostException)
        {
            _appPackage = null;
            _appProcessName = Path.GetFileNameWithoutExtension(project);
        }

        OnPropertyChanged(nameof(AppDescription));
        Refilter();
        var stream = _stream;
        while (_onlyMyApp && stream is not null && ReferenceEquals(stream, _stream) && _source?.Device.Platform == MobilePlatform.Android && _appPackage is { } package
               && _shell.Mobile.Android().Adb is { } adb)
        {
            var source = _source.Device.Id;
            try
            {
                var (_, output) = await _shell.RunTool.CaptureAsync(new ToolCommand(adb, ["-s", source, "shell", "pidof", package]) { Environment = _shell.AndroidSdk.ToolEnvironment() }, stream.Token).ConfigureAwait(true);
                var before = _appProcesses.Count;
                _appProcesses.UnionWith(DeviceLogs.ParsePidof(output));
                if (_appProcesses.Count != before)
                {
                    Refilter();
                }

                await Task.Delay(TimeSpan.FromSeconds(2), stream.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                return;
            }
        }
    }

    /// <summary>Saves the entries shown, in logcat's layout.</summary>
    public async Task SaveAsync()
    {
        if (_shell.Window is not { } window
            || await window.SaveFileAsync(Localizer.Get("msg.deviceLog.saveTitle", "Save Device Log"), "device.log", Localizer.Get("msg.deviceLog.logFiles", "Log files"), ["*.log", "*.txt"]).ConfigureAwait(true) is not { } path)
        {
            return;
        }

        try
        {
            await File.WriteAllLinesAsync(path, Rows.Select(r => DeviceLogs.Format(r.Entry))).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _shell.Workbench.Notifications.Error(Localizer.Format("msg.deviceLog.saveFailed", "Couldn't save the log: {0}", ex.Message));
        }
    }
}
