using System.Collections.ObjectModel;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Mobile;

namespace Codalus.Shell.ViewModels;

/// <summary>A device, emulator or simulator in the Devices window.</summary>
public sealed class DeviceRow(MobileDevice device, bool isTarget)
{
    public MobileDevice Device { get; } = device;

    public bool IsTarget { get; } = isTarget;

    public string Name => Device.Name + (IsTarget ? "  ✓" : string.Empty);

    public string Detail => string.Join(" · ", new[]
    {
        MobileTargets.Title(Device.Platform) + (Device.OsVersion is { } os ? " " + os : string.Empty),
        Device.Kind switch
        {
            MobileDeviceKind.Emulator => Device.IsRunning ? "emulator, running" : "emulator",
            MobileDeviceKind.Simulator => Device.IsRunning ? "simulator, booted" : "simulator",
            MobileDeviceKind.ThisMac => "this Mac",
            MobileDeviceKind.ThisPc => "this PC",
            _ => "device",
        },
        Device.Problem,
    }.Where(s => !string.IsNullOrEmpty(s)));

    public bool CanStart => !Device.IsRunning && Device.Kind is MobileDeviceKind.Emulator or MobileDeviceKind.Simulator;

    public string AccessibleName => Device.Name + ", " + Detail + (IsTarget ? ", runs go here" : string.Empty);
}

/// <summary>
/// The Devices window (M20a, ADR 0064): Android devices and emulators, iOS simulators and devices and this Mac, with
/// the one each platform's runs go to; start an emulator, boot a simulator, choose where runs go; the Android SDK and
/// JDK with an install when they're missing.
/// </summary>
public sealed class DevicesViewModel(ShellHost shell) : ObservableObject
{
    public const string ToolWindowId = "codalus.devices";

    private DeviceRow? _selected;
    private string _status = string.Empty;
    private string _toolchain = string.Empty;
    private bool _canInstallAndroid;
    private bool _busy;

    public ObservableCollection<DeviceRow> Devices { get; } = [];

    public DeviceRow? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanUse));
            }
        }
    }

    public bool CanStart => Selected is { CanStart: true } && !_busy;

    public bool CanUse => Selected is { IsTarget: false, Device.IsUsable: true };

    /// <summary>The empty state, or what's going on (an emulator starting).</summary>
    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    /// <summary>Where the Android SDK and JDK are, or what's missing.</summary>
    public string Toolchain
    {
        get => _toolchain;
        private set => SetField(ref _toolchain, value);
    }

    public bool CanInstallAndroid
    {
        get => _canInstallAndroid;
        private set => SetField(ref _canInstallAndroid, value);
    }

    /// <summary>Rebuilds the rows from the service (UI thread).</summary>
    public void Refresh()
    {
        var selected = Selected?.Device.Id;
        Devices.Clear();
        var targets = Enum.GetValues<MobilePlatform>().Select(shell.Mobile.DeviceFor).OfType<MobileDevice>().Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var device in shell.Mobile.Devices.OrderBy(d => d.Platform).ThenByDescending(d => d.IsRunning).ThenBy(d => d.Name, StringComparer.CurrentCulture))
        {
            Devices.Add(new DeviceRow(device, targets.Contains(device.Id)));
        }

        Selected = Devices.FirstOrDefault(d => d.Device.Id == selected);
        var android = shell.Mobile.Android();
        Toolchain = (android.Sdk is { } sdk ? "Android SDK: " + sdk : "No Android SDK found")
            + " · " + (android.Jdk is { } jdk ? "JDK: " + jdk : "no JDK found")
            + (android.Sdk is not null && android.Emulator is null ? " · the emulator isn't installed" : string.Empty);
        CanInstallAndroid = !android.IsComplete && shell.MobileProjects().Any(p => MobileTargets.FrameworkFor(p, MobilePlatform.Android) is not null);
        if (!_busy)
        {
            Status = Devices.Count > 0 ? string.Empty
                : android.Adb is null ? "No devices: the Android SDK isn't installed."
                : "No devices. Connect a phone with USB debugging on, or create an emulator with the Android SDK's Device Manager.";
        }
    }

    public Task RefreshAsync() => shell.RefreshDevicesAsync();

    public async Task StartAsync()
    {
        if (Selected is not { CanStart: true } row)
        {
            return;
        }

        _busy = true;
        OnPropertyChanged(nameof(CanStart));
        Status = $"Starting {row.Device.Name}…";
        try
        {
            await shell.StartDeviceAsync(row.Device).ConfigureAwait(true);
        }
        finally
        {
            _busy = false;
            Status = string.Empty;
            Refresh();
        }
    }

    public void Use()
    {
        if (Selected is { Device.IsUsable: true } row)
        {
            shell.Mobile.Select(row.Device);
        }
    }

    public Task InstallAndroidAsync() => shell.InstallAndroidDependenciesAsync();
}
