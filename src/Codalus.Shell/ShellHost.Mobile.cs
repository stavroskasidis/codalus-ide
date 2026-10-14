using System.Text.Json;
using Avalonia.Controls;
using Codalus.Platform;
using Codalus.Plugins.Protocol;
using Codalus.Projects.Protocol;
using Codalus.Shell.Controls;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Mobile;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Shell;

/// <summary>
/// .NET MAUI and mobile apps (M20a, ADR 0064): the toolbar's device list for a mobile startup project, the Devices
/// window (devices and emulators, starting an emulator), and Start Without Debugging on an Android device or emulator.
/// </summary>
public sealed partial class ShellHost
{
    public const string DevicesWindow = "mobile.devices";

    /// <summary>Set to 1, F5 on a mobile app debugs it with the Mono debugger (M20b, experimental).</summary>
    public const string MobileDebugVariable = "CODALUS_MOBILE_DEBUG";

    private AndroidDevices? _android;
    private IReadOnlyList<MobileDevice> _devices = [];
    private string? _deviceId;
    private string? _devicesStatus;
    private bool _devicesRefreshing;
    private Action? _devicesChanged;

    /// <summary>The Android SDK's tools, found once (<see cref="AndroidSdk.Locate"/>).</summary>
    internal AndroidDevices Android => _android ??= new AndroidDevices(AndroidSdk.Locate(PlatformInfo.Current, Environment.GetEnvironmentVariable), PlatformInfo.Current);

    /// <summary>The known devices (after <see cref="RefreshDevicesAsync"/>).</summary>
    internal IReadOnlyList<MobileDevice> Devices => _devices;

    /// <summary>The device the next start uses.</summary>
    internal MobileDevice? SelectedDevice => _devices.FirstOrDefault(d => d.Id == _deviceId) ?? _devices.FirstOrDefault(d => d.IsRunning && d.Problem is null) ?? (_devices.Count > 0 ? _devices[0] : null);

    public sealed class DevicesContent
    {
        internal DevicesContent(ShellHost shell) => Shell = shell;

        internal ShellHost Shell { get; }
    }

    private DevicesContent? _devicesContent;

    private DevicesContent DevicesPage() => _devicesContent ??= new DevicesContent(this);

    private void InitializeMobile()
    {
        Workbench.ToolWindows.Register(new ToolWindowDescriptor(DevicesWindow, "Devices", ToolWindowLocation.Right, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "phone" });
        Workbench.Commands.Register(new CommandDescriptor("mobile.devices", "Devices", ContributionSource.Shell) { Category = "Mobile" }, (_, _) =>
        {
            Ui.Post(() =>
            {
                Layout.ShowToolWindow(DevicesWindow, focus: true);
                _ = RefreshDevicesAsync();
            });
            return Task.FromResult<JsonElement?>(null);
        });
        Workbench.Commands.Register(new CommandDescriptor("mobile.refreshDevices", "Refresh Devices", ContributionSource.Shell) { Category = "Mobile" }, async (_, _) =>
        {
            await RefreshDevicesAsync().ConfigureAwait(true);
            return null;
        });
        Workbench.Menus.Add([new MenuItemRule(MenuLocations.MenuBar("view"), "mobile.devices", ContributionSource.Shell) { Group = "otherWindows", Order = 90 }]);
        Toolbar.SelectDevice = id =>
        {
            _deviceId = id;
            _devicesChanged?.Invoke();
        };
    }

    /// <summary>Whether <paramref name="project"/> is an app for Android, iOS or Mac Catalyst.</summary>
    internal bool IsMobileApp(string project) => Solutions.Projects.TryGetValue(project, out var info) && MobileProjects.IsMobileApp(info);

    private Task? _devicesRefresh;

    /// <summary>Lists the devices again and updates the toolbar and the Devices window (one listing at a time: callers share it).</summary>
    internal Task RefreshDevicesAsync()
    {
        if (_devicesRefresh is { IsCompleted: false } running)
        {
            return running;
        }

        return _devicesRefresh = RefreshDevicesCoreAsync();
    }

    private async Task RefreshDevicesCoreAsync()
    {
        _devicesRefreshing = true;
        _devicesStatus = "Looking for devices…";
        _devicesChanged?.Invoke();
        try
        {
            _devices = await Android.ListAsync(CancellationToken.None).ConfigureAwait(true);
            _devicesStatus = Android.Adb is null
                ? "The Android SDK wasn't found: install it with Android Studio or Help → Check Environment, or set ANDROID_HOME."
                : _devices.Count == 0 ? "No devices or emulators: create an emulator with Android Studio's Device Manager, or connect a device with USB debugging on." : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            _devicesStatus = "The devices couldn't be listed: " + ex.Message;
        }
        finally
        {
            _devicesRefreshing = false;
        }

        UpdateToolbarDevices();
        _devicesChanged?.Invoke();
    }

    /// <summary>The toolbar's device list: shown while the startup project is a mobile app.</summary>
    private void UpdateToolbarDevices()
    {
        if (Toolbar is null)
        {
            return;
        }

        if (Runs.StartupProject is not { } startup || !IsMobileApp(startup))
        {
            Toolbar.SetDevices([], null);
            return;
        }

        if (_devices.Count == 0 && !_devicesRefreshing)
        {
            _ = RefreshDevicesAsync();
        }

        Toolbar.SetDevices([.. _devices.Select(d => new DeviceChoice(ChoiceLabel(d), d.Id))], SelectedDevice?.Id);
    }

    private static string ChoiceLabel(MobileDevice device) =>
        device.Kind == MobileDeviceKind.Emulator ? $"{device.Label}{(device.IsRunning ? string.Empty : " (not running)")}" : device.Label;

    /// <summary>
    /// Start (or Start Without Debugging) of a mobile app (M20a): on the device chosen in the toolbar, an emulator
    /// started when needed. Debugging on a device comes with the Mono debugger adapter (M20b).
    /// </summary>
    internal async Task StartMobileAsync(string project, bool debug)
    {
        if (!Solutions.Projects.TryGetValue(project, out var info))
        {
            return;
        }

        if (debug && Environment.GetEnvironmentVariable(MobileDebugVariable) != "1")
        {
            // The Mono debugger connects to the app and hits breakpoints, but the runtime's agent isn't dependable yet
            // (M20b's checkpoint): it stays behind this variable until it is.
            Workbench.Notifications.Info("Debugging .NET MAUI apps on a device isn't available yet: Start Without Debugging (Ctrl+F5) runs it.");
            return;
        }

        if (MobileProjects.FrameworkFor(info, "android") is null)
        {
            Workbench.Notifications.Warning($"{info.Name} has no Android target framework, and its other platforms (iOS, Mac Catalyst) need a Mac.");
            return;
        }

        if (_devices.Count == 0)
        {
            await RefreshDevicesAsync().ConfigureAwait(true);
        }

        if (SelectedDevice is not { } device)
        {
            Workbench.Notifications.Warning(_devicesStatus ?? "There is no device or emulator to run on.");
            return;
        }

        var channel = Workbench.Output.GetOrCreate("codalus.run." + info.Name, "Run: " + info.Name);
        channel.Clear();
        var runner = new AndroidRunner(Android, Builds, Runs, ProjectManagement, Solutions);
        var task = Workbench.Progress.Begin($"Starting {info.Name} on {device.Label}", "mobile");
        try
        {
            void Log(string line)
            {
                channel.AppendLine(line);
                Ui.Post(() => StatusBar.ShowMessage(line, TimeSpan.FromSeconds(5)));
            }

            bool started;
            if (debug)
            {
                // The Mono adapter on the device's runtime (M20b, ADR 0065).
                Debugger.JustMyCode = Workbench.Settings.GetBoolean(JustMyCodeSetting, true);
                Debugger.StepFiltering = Workbench.Settings.GetBoolean(StepFilteringSetting, true);
                Layout.ShowToolWindow(DebugConsoleViewModel.ToolWindowId, focus: false);
                started = await runner.DebugAsync(info, device, Debugger, Log, CancellationToken.None).ConfigureAwait(true);
            }
            else
            {
                started = await runner.RunAsync(info, device, Log, CancellationToken.None).ConfigureAwait(true) is not null;
            }

            if (!started)
            {
                StatusBar.ShowMessage(Localizer.Get("msg.build.theBuildFailedSoTheProgram", "The build failed, so the program was not started."), TimeSpan.FromSeconds(6));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or Codalus.Rpc.JsonRpcRemoteException or Codalus.Rpc.JsonRpcConnectionLostException)
        {
            channel.AppendLine(ex.Message);
            Workbench.Notifications.Error(ex.Message);
        }
        finally
        {
            task.End();
        }

        _ = RefreshDevicesAsync(); // an emulator may have started
    }

    // ------------------------------------------------------------------ the Devices window

    internal ViewNodeDto DevicesRoot()
    {
        var children = new List<ViewNodeDto>
        {
            new("toolbar") { Children = [new ViewNodeDto("button") { Id = "refresh", Text = "Refresh", Disabled = _devicesRefreshing }] },
        };
        if (_devicesStatus is { } status)
        {
            children.Add(new ViewNodeDto("text") { Text = status, Style = "muted" });
        }

        if (_devices.Count > 0)
        {
            children.Add(new ViewNodeDto("table")
            {
                Id = "devices",
                Columns = ["Device", "Kind", "State", "Serial"],
                Rows = [.. _devices.Select(d => new[]
                {
                    (d.Id == SelectedDevice?.Id ? "● " : string.Empty) + d.Label,
                    d.Kind == MobileDeviceKind.Emulator ? "Android emulator" : "Android device",
                    d.Problem ?? (d.IsRunning ? "Running" : "Not running"),
                    d.Serial ?? string.Empty,
                })],
            });
            children.Add(new ViewNodeDto("text") { Text = "Double-click a device to run on it; an emulator that isn't running starts.", Style = "muted" });
        }

        if (Android.Sdk is { SdkPath: { } sdk } found)
        {
            children.Add(new ViewNodeDto("text") { Text = $"Android SDK: {sdk}" + (found.JdkPath is { } jdk ? $"\nJDK: {jdk}" : "\nNo JDK was found: set JAVA_HOME."), Style = "muted" });
        }

        return new ViewNodeDto("stack") { Children = [.. children] };
    }

    internal async Task OnDevicesEventAsync(string id, string kind, int? row)
    {
        switch (id, kind)
        {
            case ("refresh", "click"):
                await RefreshDevicesAsync().ConfigureAwait(true);
                return;
            case ("devices", "activate" or "doubleClick" or "select") when row is { } index && index < _devices.Count:
                var device = _devices[index];
                _deviceId = device.Id;
                UpdateToolbarDevices();
                _devicesChanged?.Invoke();
                if (kind != "select" && !device.IsRunning && device.Avd is { } avd)
                {
                    _devicesStatus = $"Starting {device.Label}…";
                    _devicesChanged?.Invoke();
                    try
                    {
                        await Android.StartEmulatorAsync(avd, line => Ui.Post(() => StatusBar.ShowMessage(line, TimeSpan.FromSeconds(5))), CancellationToken.None).ConfigureAwait(true);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
                    {
                        Workbench.Notifications.Error($"{device.Label} didn't start: {ex.Message}");
                    }

                    await RefreshDevicesAsync().ConfigureAwait(true);
                    _deviceId = _devices.FirstOrDefault(d => d.Avd == avd)?.Id ?? _deviceId;
                    UpdateToolbarDevices();
                }

                return;
        }
    }

    internal sealed class DevicesView : UserControl
    {
        private readonly DeclarativeView _view = new();

        public DevicesView(ShellHost shell)
        {
            Content = new ScrollViewer { Content = _view };
            shell._devicesChanged = () => Ui.Post(() => _view.Root = shell.DevicesRoot());
            _view.Raised += (id, kind, value, row) => _ = shell.OnDevicesEventAsync(id, kind, row);
            _view.Root = shell.DevicesRoot();
        }
    }
}
