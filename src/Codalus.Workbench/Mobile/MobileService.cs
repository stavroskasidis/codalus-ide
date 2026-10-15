using Codalus.Platform;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Build;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Settings;
using Codalus.Workbench.Setup;

namespace Codalus.Workbench.Mobile;

/// <summary>Starts a program without waiting for it (an emulator, the Simulator app); tests use a stand-in.</summary>
public delegate bool DetachedStarter(string program, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment);

/// <summary>
/// Devices, emulators and simulators for .NET MAUI apps (M20a, ADR 0064): the list (adb and the emulator on every OS,
/// <c>simctl</c> and <c>devicectl</c> on macOS, this Mac for Mac Catalyst), the device each platform runs on, starting
/// emulators and booting simulators, the installed workloads, and the run command (<c>dotnet build -t:Run</c>). Tools
/// run through a <see cref="ProcessRunner"/>, so tests replay recorded output.
/// </summary>
public sealed class MobileService(PlatformKind platform, AndroidToolchain android, ProcessRunner run, DetachedStarter start, SettingsService? settings = null)
{
    public const string SdkSetting = "android.sdkDirectory";
    public const string JdkSetting = "android.jdkDirectory";

    private readonly Dictionary<MobilePlatform, string> _selected = [];
    private readonly object _gate = new();
    private IReadOnlyList<MobileDevice> _devices = [];

    public PlatformKind Platform => platform;

    public AndroidToolchain Toolchain => android;

    /// <summary>The devices as last refreshed.</summary>
    public IReadOnlyList<MobileDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                return _devices;
            }
        }
    }

    /// <summary>The list or a selection changed (on any thread).</summary>
    public event EventHandler? Changed;

    public static IReadOnlyList<SettingDescriptor> Descriptors(ContributionSource source) =>
    [
        SettingDescriptor.Create(SdkSetting, "\"\"", SettingType.String, source) with
        {
            Title = "Android SDK Location",
            Category = "Mobile",
            Description = "The Android SDK's folder. Empty: ANDROID_HOME, then the usual folders (Android Studio's).",
            Scope = SettingScope.User,
            Order = 1,
        },
        SettingDescriptor.Create(JdkSetting, "\"\"", SettingType.String, source) with
        {
            Title = "Java Development Kit Location",
            Category = "Mobile",
            Description = "The JDK's folder for Android builds (JDK 17 or later). Empty: JAVA_HOME, then the usual folders.",
            Scope = SettingScope.User,
            Order = 2,
        },
    ];

    /// <summary>The Android SDK and JDK, with the settings' folders first.</summary>
    public AndroidToolchain.Locations Android() =>
        android.Find(settings?.GetString(SdkSetting, string.Empty), settings?.GetString(JdkSetting, string.Empty));

    // ------------------------------------------------------------------ the list

    /// <summary>Reads every device, emulator and simulator this machine has.</summary>
    public async Task<IReadOnlyList<MobileDevice>> RefreshAsync(CancellationToken cancellationToken)
    {
        var devices = new List<MobileDevice>();
        devices.AddRange(await AndroidDevicesAsync(Android(), cancellationToken).ConfigureAwait(false));
        if (platform == PlatformKind.MacOS)
        {
            devices.AddRange(await AppleDevicesAsync(cancellationToken).ConfigureAwait(false));
            devices.Add(new MobileDevice(MobileTargets.ThisMacId, "This Mac", MobilePlatform.MacCatalyst, MobileDeviceKind.ThisMac, IsRunning: true));
        }

        lock (_gate)
        {
            _devices = devices;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return devices;
    }

    private async Task<IReadOnlyList<MobileDevice>> AndroidDevicesAsync(AndroidToolchain.Locations locations, CancellationToken cancellationToken)
    {
        var result = new List<MobileDevice>();
        var running = new Dictionary<string, string>(StringComparer.Ordinal);
        if (locations.Adb is { } adb)
        {
            var (code, output) = await TryRunAsync(adb, ["devices", "-l"], cancellationToken).ConfigureAwait(false);
            if (code == 0)
            {
                foreach (var device in MobileDevices.ParseAdbDevices(output))
                {
                    if (device.Kind == MobileDeviceKind.Emulator && device.IsUsable)
                    {
                        var (avdCode, avdOutput) = await TryRunAsync(adb, ["-s", device.Id, "emu", "avd", "name"], cancellationToken).ConfigureAwait(false);
                        if (avdCode == 0 && MobileDevices.ParseAvdName(avdOutput) is { } avd)
                        {
                            running[device.Id] = avd;
                            result.Add(device with { Name = MobileDevices.AvdTitle(avd), Avd = avd });
                            continue;
                        }
                    }

                    result.Add(device);
                }
            }
        }

        if (locations.Emulator is { } emulator)
        {
            var (code, output) = await TryRunAsync(emulator, ["-list-avds"], cancellationToken).ConfigureAwait(false);
            if (code == 0)
            {
                result.AddRange(MobileDevices.StoppedEmulators(MobileDevices.ParseAvds(output), running));
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<MobileDevice>> AppleDevicesAsync(CancellationToken cancellationToken)
    {
        var result = new List<MobileDevice>();
        var (code, output) = await TryRunAsync("xcrun", ["simctl", "list", "devices", "available", "--json"], cancellationToken).ConfigureAwait(false);
        if (code == 0)
        {
            result.AddRange(MobileDevices.ParseSimulators(output));
        }

        var file = Path.Combine(Path.GetTempPath(), $"codalus-devicectl-{Environment.ProcessId}.json");
        try
        {
            var (deviceCode, _) = await TryRunAsync("xcrun", ["devicectl", "list", "devices", "--quiet", "--json-output", file], cancellationToken).ConfigureAwait(false);
            if (deviceCode == 0 && File.Exists(file))
            {
                result.InsertRange(0, MobileDevices.ParseAppleDevices(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false)));
            }
        }
        catch (IOException)
        {
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }

        return result;
    }

    private async Task<(int, string)> TryRunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            return await run(program, arguments, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }

    // ------------------------------------------------------------------ the device each platform runs on

    /// <summary>The device runs of <paramref name="mobilePlatform"/> go to: the chosen one while it's listed, else the first running and usable one, else the first.</summary>
    public MobileDevice? DeviceFor(MobilePlatform mobilePlatform)
    {
        var candidates = Devices.Where(d => d.Platform == mobilePlatform).ToList();
        lock (_gate)
        {
            if (_selected.TryGetValue(mobilePlatform, out var id) && candidates.FirstOrDefault(d => d.Id == id || (d.Avd is not null && "avd:" + d.Avd == id)) is { } chosen)
            {
                return chosen;
            }
        }

        return candidates.FirstOrDefault(d => d.IsRunning && d.IsUsable) ?? candidates.FirstOrDefault(d => d.IsUsable);
    }

    /// <summary>Chooses the device runs of its platform go to.</summary>
    public void Select(MobileDevice device)
    {
        lock (_gate)
        {
            // An emulator is remembered by its AVD, so the choice holds when it starts and gets a serial.
            _selected[device.Platform] = device.Avd is { } avd ? "avd:" + avd : device.Id;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The choices, for the solution's state.</summary>
    public IReadOnlyDictionary<string, string> Selections
    {
        get
        {
            lock (_gate)
            {
                return _selected.ToDictionary(p => p.Key.ToString(), p => p.Value, StringComparer.Ordinal);
            }
        }
    }

    public void LoadSelections(IReadOnlyDictionary<string, string>? selections)
    {
        lock (_gate)
        {
            _selected.Clear();
            foreach (var (key, value) in selections ?? new Dictionary<string, string>())
            {
                if (Enum.TryParse<MobilePlatform>(key, out var parsed))
                {
                    _selected[parsed] = value;
                }
            }
        }
    }

    // ------------------------------------------------------------------ starting

    /// <summary>
    /// Starts an emulator and waits until Android has booted in it (at most <paramref name="timeout"/>, 5 minutes by
    /// default); the running emulator, or null when it didn't come up.
    /// </summary>
    public async Task<MobileDevice?> StartEmulatorAsync(MobileDevice emulator, CancellationToken cancellationToken, TimeSpan? timeout = null, TimeSpan? poll = null)
    {
        if (emulator.IsRunning)
        {
            return emulator;
        }

        var locations = Android();
        if (locations.Emulator is not { } program || locations.Adb is not { } adb || emulator.Avd is not { } avd)
        {
            throw new InvalidOperationException("The Android emulator isn't installed: install it with the SDK's emulator package (Tools → Android SDK Manager, or sdkmanager \"emulator\").");
        }

        if (!start(program, ["-avd", avd], AndroidEnvironment(locations)))
        {
            throw new InvalidOperationException($"The emulator didn't start ({program}).");
        }

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(5));
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(poll ?? TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            var devices = await AndroidDevicesAsync(locations, cancellationToken).ConfigureAwait(false);
            if (devices.FirstOrDefault(d => d.IsRunning && d.Avd == avd && d.IsUsable) is { } started)
            {
                var (code, booted) = await TryRunAsync(adb, ["-s", started.Id, "shell", "getprop", "sys.boot_completed"], cancellationToken).ConfigureAwait(false);
                if (code == 0 && booted.Trim() == "1")
                {
                    await RefreshAsync(cancellationToken).ConfigureAwait(false);
                    return started;
                }
            }
        }

        return null;
    }

    /// <summary>Boots an iOS simulator and opens the Simulator app on it.</summary>
    public async Task BootSimulatorAsync(MobileDevice simulator, CancellationToken cancellationToken)
    {
        if (!simulator.IsRunning)
        {
            var (code, output) = await TryRunAsync("xcrun", ["simctl", "boot", simulator.Id], cancellationToken).ConfigureAwait(false);
            if (code != 0 && !output.Contains("current state: Booted", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The simulator didn't boot: {output.Trim()}");
            }
        }

        start("open", ["-a", "Simulator", "--args", "-CurrentDeviceUDID", simulator.Id], null);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ workloads

    /// <summary>The workloads <c>dotnet workload list</c> lists, in <paramref name="directory"/> (its <c>global.json</c> picks the SDK).</summary>
    public async Task<IReadOnlyList<string>?> InstalledWorkloadsAsync(string dotnet, string? directory, CancellationToken cancellationToken)
    {
        try
        {
            var (code, output) = await run(dotnet, ["workload", "list"], directory, cancellationToken).ConfigureAwait(false);
            return code == 0 ? ParseWorkloadList(output) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The ids in <c>dotnet workload list</c>'s table.</summary>
    public static IReadOnlyList<string> ParseWorkloadList(string output)
    {
        var result = new List<string>();
        var inTable = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("---", StringComparison.Ordinal))
            {
                inTable = true;
                continue;
            }

            if (!inTable)
            {
                continue;
            }

            if (line.Length == 0 || line.StartsWith("Use `dotnet workload", StringComparison.Ordinal))
            {
                break;
            }

            var id = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
            if (id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
            {
                result.Add(id);
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ running

    /// <summary>
    /// The plan that builds, deploys and starts <paramref name="project"/>'s <paramref name="framework"/> on
    /// <paramref name="device"/>: <c>dotnet build -t:Run</c>, with the Android SDK and JDK the IDE found.
    /// </summary>
    public LaunchPlan RunPlan(string dotnet, ProjectInfo project, string framework, MobileDevice? device, string? configuration)
    {
        var locations = MobileTargets.PlatformOf(framework) == MobilePlatform.Android ? Android() : null;
        var arguments = MobileTargets.RunArguments(project.Path, framework, device, configuration, locations);
        return new LaunchPlan(dotnet, string.Join(' ', arguments.Select(Quote)), Path.GetDirectoryName(project.Path)!, AndroidEnvironment(locations), null)
        {
            ArgumentList = arguments,
        };
    }

    /// <summary>The plan that installs the Android SDK (and a JDK when none is found) through the workload's own target.</summary>
    public LaunchPlan InstallAndroidDependenciesPlan(string dotnet, ProjectInfo project)
    {
        var framework = MobileTargets.FrameworkFor(project, MobilePlatform.Android)
                        ?? throw new InvalidOperationException($"{project.Name} has no Android target framework.");
        var locations = Android();
        var arguments = MobileTargets.InstallAndroidDependenciesArguments(project.Path, framework, locations.Sdk ?? android.DefaultSdkDirectory, locations.Jdk ?? android.DefaultJdkDirectory);
        return new LaunchPlan(dotnet, string.Join(' ', arguments.Select(Quote)), Path.GetDirectoryName(project.Path)!, AndroidEnvironment(locations), null)
        {
            ArgumentList = arguments,
        };
    }

    /// <summary>
    /// The environment of Android tools and builds: the SDK and JDK the IDE found, and <c>USER</c> (the workload's SDK
    /// installer fails without it, as in some containers).
    /// </summary>
    private static Dictionary<string, string> AndroidEnvironment(AndroidToolchain.Locations? locations)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (locations?.Sdk is { } sdk)
        {
            environment["ANDROID_HOME"] = sdk;
        }

        if (locations?.Jdk is { } jdk)
        {
            environment["JAVA_HOME"] = jdk;
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("USER")) && !OperatingSystem.IsWindows())
        {
            environment["USER"] = Environment.UserName;
        }

        return environment;
    }

    private static string Quote(string argument) => argument.Contains(' ', StringComparison.Ordinal) ? "\"" + argument + "\"" : argument;
}
