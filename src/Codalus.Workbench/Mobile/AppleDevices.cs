using System.Text.Json;
using Codalus.Platform;
using Codalus.Workbench.Setup;

namespace Codalus.Workbench.Mobile;

/// <summary>
/// iOS simulators and this Mac (M20c, ADR 0064) through Xcode's own tools: <c>xcrun simctl list devices available
/// --json</c> for the simulators, booted with <c>simctl boot</c> and shown with the Simulator app. On macOS only:
/// iOS and Mac Catalyst apps build and run only there. Physical iOS devices (signing, provisioning) are a gap.
/// </summary>
public sealed class AppleDevices(PlatformKind platform, ProcessRunner? run = null)
{
    /// <summary>The device id of this Mac, where Mac Catalyst apps run.</summary>
    public const string ThisMacId = "mac:local";

    private readonly ProcessRunner _run = run ?? EnvironmentCheck.RunAsync;

    /// <summary>Whether this machine can build and run Apple apps.</summary>
    public bool Available => platform == PlatformKind.MacOS;

    /// <summary>This Mac (for Mac Catalyst), then the booted simulators, then the others newest runtime first.</summary>
    public async Task<IReadOnlyList<MobileDevice>> ListAsync(CancellationToken cancellationToken)
    {
        if (!Available)
        {
            return [];
        }

        var devices = new List<MobileDevice> { new(ThisMacId, "This Mac (Mac Catalyst)", "maccatalyst", MobileDeviceKind.Local, "local", null) };
        var (code, output) = await _run("xcrun", ["simctl", "list", "devices", "available", "--json"], null, cancellationToken).ConfigureAwait(false);
        if (code == 0)
        {
            devices.AddRange(ParseSimulators(output));
        }

        return devices;
    }

    /// <summary>
    /// The iOS simulators of <c>simctl list devices --json</c>: <c>{"devices": {"com.apple.CoreSimulator.SimRuntime.iOS-18-2": [{"udid", "name", "state", "isAvailable"}]}}</c>.
    /// Other platforms' simulators (watchOS, tvOS, visionOS) are left out.
    /// </summary>
    public static IReadOnlyList<MobileDevice> ParseSimulators(string json)
    {
        var simulators = new List<(MobileDevice Device, Version Runtime)>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("devices", out var runtimes) || runtimes.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            foreach (var runtime in runtimes.EnumerateObject())
            {
                if (RuntimeVersion(runtime.Name) is not { } version || runtime.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var device in runtime.Value.EnumerateArray())
                {
                    var udid = device.TryGetProperty("udid", out var u) ? u.GetString() : null;
                    var name = device.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var available = !device.TryGetProperty("isAvailable", out var a) || a.ValueKind != JsonValueKind.False;
                    if (udid is null || name is null || !available)
                    {
                        continue;
                    }

                    var booted = device.TryGetProperty("state", out var s) && s.GetString() == "Booted";
                    var label = $"{name} (iOS {version.ToString(version.Build >= 0 ? 3 : 2)})";
                    simulators.Add((new MobileDevice("sim:" + udid, label, "ios", MobileDeviceKind.Emulator, booted ? udid : null, udid), version));
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return [.. simulators
            .OrderByDescending(s => s.Device.IsRunning)
            .ThenByDescending(s => s.Runtime)
            .ThenBy(s => s.Device.Label, StringComparer.Ordinal)
            .Select(s => s.Device)];
    }

    /// <summary><c>com.apple.CoreSimulator.SimRuntime.iOS-18-2</c> → 18.2; null for other platforms.</summary>
    internal static Version? RuntimeVersion(string runtime)
    {
        const string prefix = "com.apple.CoreSimulator.SimRuntime.iOS-";
        if (!runtime.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = runtime[prefix.Length..].Split('-');
        return parts.Length >= 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor)
            ? parts.Length >= 3 && int.TryParse(parts[2], out var patch) ? new Version(major, minor, patch) : new Version(major, minor)
            : null;
    }

    /// <summary>Boots the simulator (an already booted one is fine) and brings the Simulator app up to show it.</summary>
    public async Task BootAsync(string udid, CancellationToken cancellationToken)
    {
        var (code, output) = await _run("xcrun", ["simctl", "boot", udid], null, cancellationToken).ConfigureAwait(false);
        if (code != 0 && !output.Contains("current state: Booted", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The simulator didn't boot: {output.Trim()}");
        }

        await _run("open", ["-a", "Simulator"], null, cancellationToken).ConfigureAwait(false);
        await _run("xcrun", ["simctl", "bootstatus", udid], null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Installs an app bundle (<c>X.app</c>) on a booted simulator.</summary>
    public async Task InstallAsync(string udid, string appBundle, CancellationToken cancellationToken)
    {
        var (code, output) = await _run("xcrun", ["simctl", "install", udid, appBundle], null, cancellationToken).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(appBundle)} wasn't installed on the simulator: {output.Trim()}");
        }
    }

    /// <summary>Ends the app on the simulator (nothing when it isn't running).</summary>
    public Task TerminateAsync(string udid, string bundleId, CancellationToken cancellationToken) =>
        _run("xcrun", ["simctl", "terminate", udid, bundleId], null, cancellationToken);

    /// <summary>
    /// The command that launches the app and stays attached to its output (<c>simctl launch --console-pty</c>), as
    /// the program the Run output shows: it ends when the app does.
    /// </summary>
    public static (string FileName, IReadOnlyList<string> ArgumentList) LaunchCommand(string udid, string bundleId) =>
        ("xcrun", ["simctl", "launch", "--console-pty", "--terminate-running-process", udid, bundleId]);
}
