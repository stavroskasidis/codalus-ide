using System.Diagnostics;
using Codalus.Platform;
using Codalus.Workbench.Setup;

namespace Codalus.Workbench.Mobile;

/// <summary>Where a mobile app can run (M20, ADR 0064): an Android device or emulator, an iOS simulator or device, this Mac.</summary>
/// <param name="Id">The choice's key: <c>android:&lt;serial&gt;</c> for a running device, <c>avd:&lt;name&gt;</c> for an emulator not running yet.</param>
/// <param name="Label">What the device list shows.</param>
/// <param name="Platform"><c>android</c>, <c>ios</c> or <c>maccatalyst</c>: the target framework's platform.</param>
/// <param name="Kind">Emulator (or simulator), device, or this machine.</param>
/// <param name="Serial">adb's serial (or the simulator's UDID) while it runs.</param>
/// <param name="Avd">The emulator's virtual device.</param>
public sealed record MobileDevice(string Id, string Label, string Platform, MobileDeviceKind Kind, string? Serial, string? Avd)
{
    /// <summary>Whether it runs, so an app can be installed now.</summary>
    public bool IsRunning => Serial is not null;

    /// <summary>A state adb gives (<c>unauthorized</c>, <c>offline</c>), when the device can't be used yet.</summary>
    public string? Problem { get; init; }
}

public enum MobileDeviceKind
{
    Emulator,
    Device,
    Local,
}

/// <summary>
/// Android devices and emulators through the SDK's own tools (ADR 0064): <c>adb devices -l</c>, <c>emulator
/// -list-avds</c>, and <c>adb emu avd name</c> to tell which emulator is which. Nothing here needs a project.
/// </summary>
public sealed class AndroidDevices(AndroidSdk sdk, PlatformKind platform, ProcessRunner? run = null, Func<string, bool>? fileExists = null, Func<string, string?>? environment = null)
{
    private readonly ProcessRunner _run = run ?? EnvironmentCheck.RunAsync;
    private readonly Func<string, bool> _exists = fileExists ?? File.Exists;
    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;

    public AndroidSdk Sdk => sdk;

    /// <summary><c>adb</c>, or null without the SDK's platform tools.</summary>
    public string? Adb => sdk.Adb(platform, _exists);

    /// <summary>The emulator, or null without it.</summary>
    public string? Emulator => sdk.Emulator(platform, _exists);

    /// <summary>The devices and emulators: those that run first, then the virtual devices that could be started.</summary>
    public async Task<IReadOnlyList<MobileDevice>> ListAsync(CancellationToken cancellationToken)
    {
        var devices = new List<MobileDevice>();
        if (Adb is { } adb)
        {
            var (code, output) = await _run(adb, ["devices", "-l"], null, cancellationToken).ConfigureAwait(false);
            if (code == 0)
            {
                foreach (var (serial, state, model) in ParseDevices(output))
                {
                    var emulator = serial.StartsWith("emulator-", StringComparison.Ordinal);
                    string? avd = null;
                    if (emulator && state == "device")
                    {
                        var (avdCode, avdOutput) = await _run(adb, ["-s", serial, "emu", "avd", "name"], null, cancellationToken).ConfigureAwait(false);
                        avd = avdCode == 0 ? avdOutput.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && l != "OK") : null;
                    }

                    var label = avd is not null ? DisplayName(avd) : model?.Replace('_', ' ') ?? serial;
                    devices.Add(new MobileDevice("android:" + serial, label, "android", emulator ? MobileDeviceKind.Emulator : MobileDeviceKind.Device, serial, avd)
                    {
                        Problem = state == "device" ? null : state,
                    });
                }
            }
        }

        if (Emulator is { } emulatorTool)
        {
            var (code, output) = await _run(emulatorTool, ["-list-avds"], null, cancellationToken).ConfigureAwait(false);
            if (code == 0)
            {
                foreach (var avd in ParseAvds(output).Where(a => !devices.Any(d => d.Avd == a)))
                {
                    devices.Add(new MobileDevice("avd:" + avd, DisplayName(avd), "android", MobileDeviceKind.Emulator, null, avd));
                }
            }
        }

        return devices;
    }

    /// <summary>The lines of <c>adb devices -l</c>: serial, state (<c>device</c>, <c>unauthorized</c>, <c>offline</c>) and model.</summary>
    public static IReadOnlyList<(string Serial, string State, string? Model)> ParseDevices(string output)
    {
        var devices = new List<(string, string, string?)>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.Ordinal) || line.StartsWith('*'))
            {
                continue;
            }

            var fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2)
            {
                continue;
            }

            var model = fields.Skip(2).FirstOrDefault(f => f.StartsWith("model:", StringComparison.Ordinal))?["model:".Length..];
            devices.Add((fields[0], fields[1], model));
        }

        return devices;
    }

    /// <summary>The virtual devices of <c>emulator -list-avds</c> (its log lines left out).</summary>
    public static IReadOnlyList<string> ParseAvds(string output) =>
        [.. output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.Contains(' ', StringComparison.Ordinal) && !l.StartsWith("INFO", StringComparison.Ordinal))];

    /// <summary>The virtual device's display name from its <c>config.ini</c> ("Pixel 7 - API 35"), or its name.</summary>
    public string DisplayName(string avd)
    {
        var home = _environment("ANDROID_AVD_HOME") is { Length: > 0 } avdHome
            ? avdHome
            : Path.Combine(_environment("ANDROID_USER_HOME") ?? Path.Combine(_environment("HOME") ?? _environment("USERPROFILE") ?? string.Empty, ".android"), "avd");
        var config = Path.Combine(home, avd + ".avd", "config.ini");
        try
        {
            if (_exists(config))
            {
                foreach (var line in File.ReadLines(config))
                {
                    if (line.StartsWith("avd.ini.displayname=", StringComparison.Ordinal))
                    {
                        return line["avd.ini.displayname=".Length..].Trim();
                    }
                }
            }
        }
        catch (IOException)
        {
        }

        return avd.Replace('_', ' ');
    }

    /// <summary>
    /// Starts an emulator (its own window, left running when the IDE closes, as Android Studio does) and waits until
    /// it has booted; returns its serial.
    /// </summary>
    public async Task<string> StartEmulatorAsync(string avd, Action<string>? log, CancellationToken cancellationToken)
    {
        var emulator = Emulator ?? throw new InvalidOperationException("The Android SDK has no emulator: install it with the SDK Manager (the \"emulator\" package).");
        var start = new ProcessStartInfo(emulator) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-avd");
        start.ArgumentList.Add(avd);
        Process.Start(start)?.Dispose();
        log?.Invoke($"Starting the emulator {DisplayName(avd)}…");

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromMinutes(5));
        string? serial = null;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), limit.Token).ConfigureAwait(false);
            serial ??= (await ListAsync(limit.Token).ConfigureAwait(false)).FirstOrDefault(d => d.Avd == avd && d.IsRunning && d.Problem is null)?.Serial;
            if (serial is not null && (await ShellAsync(serial, ["getprop", "sys.boot_completed"], limit.Token).ConfigureAwait(false)).Trim() == "1")
            {
                log?.Invoke($"The emulator {DisplayName(avd)} is ready ({serial}).");
                return serial;
            }
        }
    }

    /// <summary>Runs <c>adb -s serial shell …</c>; returns its output.</summary>
    public async Task<string> ShellAsync(string serial, IReadOnlyList<string> command, CancellationToken cancellationToken)
    {
        var (_, output) = await AdbAsync(serial, ["shell", .. command], cancellationToken).ConfigureAwait(false);
        return output;
    }

    /// <summary>Runs <c>adb -s serial …</c>.</summary>
    public Task<(int ExitCode, string Output)> AdbAsync(string serial, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        _run(Adb ?? throw new InvalidOperationException("adb wasn't found: the Android SDK's platform tools aren't installed."), ["-s", serial, .. arguments], null, cancellationToken);

    /// <summary>Starts the app's launcher activity.</summary>
    public async Task LaunchAsync(string serial, string applicationId, CancellationToken cancellationToken)
    {
        var (code, output) = await AdbAsync(serial, ["shell", "monkey", "-p", applicationId, "-c", "android.intent.category.LAUNCHER", "1"], cancellationToken).ConfigureAwait(false);
        if (code != 0 || output.Contains("No activities found", StringComparison.Ordinal) || output.Contains("aborted", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{applicationId} couldn't be started on {serial}: {output.Trim()}");
        }
    }

    /// <summary>The app's process id once it runs, or null after <paramref name="wait"/>.</summary>
    public async Task<int?> ProcessIdAsync(string serial, string applicationId, TimeSpan wait, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + wait;
        do
        {
            var output = await ShellAsync(serial, ["pidof", applicationId], cancellationToken).ConfigureAwait(false);
            if (int.TryParse(output.Trim().Split(' ')[0], out var pid))
            {
                return pid;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < until);
        return null;
    }

    /// <summary>Stops the app.</summary>
    public Task StopAsync(string serial, string applicationId, CancellationToken cancellationToken) =>
        AdbAsync(serial, ["shell", "am", "force-stop", applicationId], cancellationToken);

    /// <summary>The start of <c>adb logcat</c> for one process (the app's output for the Output window).</summary>
    public ProcessStartInfo Logcat(string serial, int processId)
    {
        var start = new ProcessStartInfo(Adb ?? throw new InvalidOperationException("adb wasn't found."))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        foreach (var argument in new[] { "-s", serial, "logcat", "-v", "tag", "--pid=" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }
}
