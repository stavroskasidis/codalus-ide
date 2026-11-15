using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Mobile;

namespace Codalus.Shell.ViewModels;

/// <summary>An emulator in the Android Device Manager, with whether it's running now.</summary>
public sealed class EmulatorRow(AndroidEmulator emulator, MobileDevice? running)
{
    public AndroidEmulator Emulator { get; } = emulator;

    /// <summary>The running emulator (its adb serial), or null when it's stopped.</summary>
    public MobileDevice? Running { get; } = running;

    public bool IsRunning => Running is not null;

    public string Name => Emulator.DisplayName;

    public string Device => Emulator.Device ?? string.Empty;

    public string Api => Emulator.ApiLevel?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public string Abi => Emulator.Abi ?? string.Empty;

    public string Image => Emulator.Tag is { } tag ? AndroidSdkPackages.TagTitle(tag) : string.Empty;

    public string State => IsRunning
        ? Localizer.Format("msg.androidAvd.running", "Running ({0})", Running!.Id)
        : Localizer.Get("msg.androidAvd.stopped", "Stopped");

    public string AccessibleName => $"{Name}, {Device}, API {Api}, {Abi}, {State}";
}

/// <summary>
/// The Android Device Manager (ADR 0104): the emulators (AVDs) on this machine, from their files; New (a device profile
/// and a system image, downloaded when needed, through <c>avdmanager</c>), Start, Cold Boot, Stop, Wipe Data, Delete,
/// Open Folder; and the selected emulator's settings (memory, storage, SD card, GPU, keyboard, cores), saved to its
/// <c>config.ini</c> while it's stopped.
/// </summary>
public sealed class AndroidDeviceManagerViewModel : ObservableObject
{
    public const string ToolWindowId = "android.deviceManager";

    private readonly ShellHost _shell;
    private EmulatorRow? _selected;
    private string _status = string.Empty;
    private bool _busy;
    private string _ram = string.Empty;
    private string _storage = string.Empty;
    private string _sdCard = string.Empty;
    private string _cores = string.Empty;
    private int _gpu;
    private bool _keyboard;
    private string? _settingsError;
    private readonly List<RelayCommand> _commands = [];

    public AndroidDeviceManagerViewModel(ShellHost shell)
    {
        _shell = shell;
        RelayCommand Command(Func<Task> run, Func<bool> can)
        {
            var command = new RelayCommand(() => _ = RunAsync(run), () => !_busy && can());
            _commands.Add(command);
            return command;
        }

        NewCommand = Command(NewAsync, () => true);
        StartCommand = Command(() => StartAsync(coldBoot: false), () => _selected is { IsRunning: false });
        ColdBootCommand = Command(() => StartAsync(coldBoot: true), () => _selected is { IsRunning: false });
        StopCommand = Command(StopAsync, () => _selected is { IsRunning: true });
        WipeDataCommand = Command(WipeDataAsync, () => _selected is { IsRunning: false });
        DeleteCommand = Command(DeleteAsync, () => _selected is { IsRunning: false });
        SaveCommand = Command(SaveAsync, () => _selected is { IsRunning: false });
        RefreshCommand = Command(RefreshAsync, () => true);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => _selected is not null);
        _commands.Add((RelayCommand)OpenFolderCommand);
    }

    public ObservableCollection<EmulatorRow> Emulators { get; } = [];

    public EmulatorRow? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                LoadSettings();
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(CanEdit));
                OnPropertyChanged(nameof(Details));
                Raise();
            }
        }
    }

    public bool HasSelection => _selected is not null;

    /// <summary>Settings change only while the emulator is stopped (it rereads them when it starts).</summary>
    public bool CanEdit => _selected is { IsRunning: false };

    /// <summary>The selected emulator's system image and folder.</summary>
    public string Details => _selected is not { } row ? string.Empty
        : string.Join(" · ", new[] { row.Emulator.SystemImage, row.Emulator.Folder }.OfType<string>());

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public string Ram
    {
        get => _ram;
        set => SetField(ref _ram, value ?? string.Empty);
    }

    public string Storage
    {
        get => _storage;
        set => SetField(ref _storage, value ?? string.Empty);
    }

    public string SdCard
    {
        get => _sdCard;
        set => SetField(ref _sdCard, value ?? string.Empty);
    }

    public string Cores
    {
        get => _cores;
        set => SetField(ref _cores, value ?? string.Empty);
    }

    public IReadOnlyList<string> GpuModes => AndroidEmulatorSettings.GpuModes;

    public int Gpu
    {
        get => _gpu;
        set => SetField(ref _gpu, value);
    }

    public bool Keyboard
    {
        get => _keyboard;
        set => SetField(ref _keyboard, value);
    }

    public string? SettingsError
    {
        get => _settingsError;
        private set
        {
            if (SetField(ref _settingsError, value))
            {
                OnPropertyChanged(nameof(HasSettingsError));
            }
        }
    }

    public bool HasSettingsError => _settingsError is not null;

    public ICommand NewCommand { get; }

    public ICommand StartCommand { get; }

    public ICommand ColdBootCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand WipeDataCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand OpenFolderCommand { get; }

    private void Raise()
    {
        foreach (var command in _commands)
        {
            command.RaiseCanExecuteChanged();
        }
    }

    private async Task RunAsync(Func<Task> run)
    {
        _busy = true;
        Raise();
        try
        {
            await run().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _shell.Workbench.Notifications.Error(ex.Message);
        }
        finally
        {
            _busy = false;
            Raise();
        }
    }

    /// <summary>Reads the emulators from their files, and which are running from adb.</summary>
    public async Task RefreshAsync()
    {
        await _shell.RefreshDevicesAsync().ConfigureAwait(true);
        Reload();
    }

    /// <summary>The device list changed (an emulator started or stopped): the rows follow, once the window has shown.</summary>
    public void OnDevicesChanged()
    {
        if (_shown && !_busy)
        {
            Reload();
        }
    }

    private bool _shown;

    /// <summary>Rebuilds the rows from the files and the last device list (UI thread).</summary>
    public void Reload()
    {
        _shown = true;
        var selected = _selected?.Emulator.Name;
        Emulators.Clear();
        foreach (var emulator in _shell.AndroidSdk.Emulators())
        {
            Emulators.Add(new EmulatorRow(emulator, _shell.AndroidSdk.RunningDevice(emulator)));
        }

        Selected = Emulators.FirstOrDefault(e => e.Emulator.Name == selected) ?? Emulators.FirstOrDefault();
        LoadSettings();
        OnPropertyChanged(nameof(CanEdit));
        Raise();
        var locations = _shell.AndroidSdk.Locations();
        Status = Emulators.Count > 0 ? string.Empty
            : locations.Sdk is null ? Localizer.Get("msg.androidAvd.noSdk", "No emulators: the Android SDK isn't installed.")
            : Localizer.Format("msg.androidAvd.none", "No emulators in {0}. New creates one.", _shell.AndroidSdk.AvdHome);
        if (locations.Sdk is not null && locations.Emulator is null)
        {
            Status = (Status.Length > 0 ? Status + " " : string.Empty) + Localizer.Get("msg.androidAvd.noEmulator", "The emulator isn't installed: install it in the Android SDK Manager (Tools).");
        }
    }

    private void LoadSettings()
    {
        SettingsError = null;
        var config = _selected?.Emulator.Config;
        string Size(string key) => AndroidEmulators.SizeInMegabytes(config?.GetValueOrDefault(key))?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        Ram = Size(AndroidEmulatorSettings.Ram);
        Storage = Size(AndroidEmulatorSettings.InternalStorage);
        SdCard = Size(AndroidEmulatorSettings.SdCard);
        Cores = config?.GetValueOrDefault(AndroidEmulatorSettings.Cores) ?? string.Empty;
        var gpu = config?.GetValueOrDefault(AndroidEmulatorSettings.Gpu) ?? "auto";
        Gpu = Math.Max(0, GpuModes.ToList().IndexOf(gpu));
        Keyboard = config?.GetValueOrDefault(AndroidEmulatorSettings.Keyboard) == "yes";
    }

    private async Task NewAsync()
    {
        if (await _shell.NewEmulatorAsync().ConfigureAwait(true) is { } name)
        {
            Reload();
            Selected = Emulators.FirstOrDefault(e => e.Emulator.Name == name) ?? Selected;
        }
    }

    private async Task StartAsync(bool coldBoot)
    {
        if (_selected is not { } row)
        {
            return;
        }

        Status = Localizer.Format("msg.androidAvd.starting", "Starting {0}…", row.Name);
        var device = new MobileDevice("avd:" + row.Emulator.Name, row.Name, MobilePlatform.Android, MobileDeviceKind.Emulator, IsRunning: false) { Avd = row.Emulator.Name };
        await _shell.StartDeviceAsync(device, coldBoot).ConfigureAwait(true);
        Reload();
    }

    private async Task StopAsync()
    {
        if (_selected is { Running: { } running } row)
        {
            Status = Localizer.Format("msg.androidAvd.stopping", "Stopping {0}…", row.Name);
            await _shell.AndroidSdk.StopAsync(running, CancellationToken.None).ConfigureAwait(true);

            // The emulator takes a moment to leave adb's list.
            for (var i = 0; i < 20 && _shell.AndroidSdk.RunningDevice(row.Emulator) is not null; i++)
            {
                await Task.Delay(500).ConfigureAwait(true);
                await _shell.RefreshDevicesAsync().ConfigureAwait(true);
            }

            Reload();
        }
    }

    private async Task WipeDataAsync()
    {
        if (_selected is not { } row || !await _shell.Dialogs.ConfirmAsync(
                Localizer.Get("msg.androidAvd.wipeTitle", "Wipe Data"),
                Localizer.Format("msg.androidAvd.wipeMessage", "This erases everything installed and saved on {0}, as a factory reset does. Its next start is a cold boot.", row.Name),
                Localizer.Get("msg.androidAvd.wipe", "Wipe Data"),
                danger: true).ConfigureAwait(true))
        {
            return;
        }

        AndroidEmulators.WipeData(row.Emulator);
        Status = Localizer.Format("msg.androidAvd.wiped", "{0}'s data is wiped.", row.Name);
    }

    private async Task DeleteAsync()
    {
        if (_selected is not { } row || !await _shell.Dialogs.ConfirmAsync(
                Localizer.Get("msg.androidAvd.deleteTitle", "Delete Emulator"),
                Localizer.Format("msg.androidAvd.deleteMessage", "This deletes {0} and its data ({1}).", row.Name, row.Emulator.Folder),
                Localizer.Get("msg.androidAvd.delete", "Delete"),
                danger: true).ConfigureAwait(true))
        {
            return;
        }

        AndroidEmulators.Delete(row.Emulator);
        await _shell.RefreshDevicesAsync().ConfigureAwait(true);
        Reload();
    }

    /// <summary>Writes the settings to the emulator's <c>config.ini</c>, after checking them.</summary>
    private Task SaveAsync()
    {
        if (_selected is not { IsRunning: false } row)
        {
            return Task.CompletedTask;
        }

        if (Validate() is { } error)
        {
            SettingsError = error;
            return Task.CompletedTask;
        }

        var changes = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [AndroidEmulatorSettings.Ram] = Ram.Trim() + "M",
            [AndroidEmulatorSettings.InternalStorage] = Storage.Trim() + "M",
            [AndroidEmulatorSettings.SdCard] = SdCard.Trim().Length == 0 || SdCard.Trim() == "0" ? null : SdCard.Trim() + "M",
            [AndroidEmulatorSettings.Cores] = Cores.Trim().Length == 0 ? null : Cores.Trim(),
            [AndroidEmulatorSettings.Gpu] = GpuModes[Math.Clamp(Gpu, 0, GpuModes.Count - 1)],
            [AndroidEmulatorSettings.Keyboard] = Keyboard ? "yes" : "no",
        };
        AndroidEmulators.Save(row.Emulator, changes);
        Reload();
        Status = Localizer.Format("msg.androidAvd.saved", "{0}'s settings are saved; they apply at its next start.", row.Name);
        return Task.CompletedTask;
    }

    /// <summary>Why the settings can't be saved, or null: sizes in whole megabytes in the emulator's ranges.</summary>
    internal string? Validate()
    {
        static bool Number(string text, int min, int max, bool optional) =>
            (optional && text.Trim().Length == 0) || (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max);

        return !Number(Ram, 512, 65536, optional: false) ? Localizer.Get("msg.androidAvd.badRam", "Memory: a size in MB, from 512 to 65536.")
            : !Number(Storage, 512, 1048576, optional: false) ? Localizer.Get("msg.androidAvd.badStorage", "Internal storage: a size in MB, from 512.")
            : !(SdCard.Trim() is "" or "0" || Number(SdCard, 10, 1048576, optional: false)) ? Localizer.Get("msg.androidAvd.badSdCard", "SD card: a size in MB, at least 10 (or empty for none).")
            : !Number(Cores, 1, 64, optional: true) ? Localizer.Get("msg.androidAvd.badCores", "Processor cores: from 1 to 64.")
            : null;
    }

    private void OpenFolder()
    {
        if (_selected is { } row)
        {
            try
            {
                _shell.RevealPath(row.Emulator.Folder);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _shell.Workbench.Notifications.Error(Localizer.Format("msg.shellHost.couldNotOpen", "Could not open {0}: {1}", row.Emulator.Folder, ex.Message));
            }
        }
    }
}
