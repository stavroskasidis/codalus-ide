using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Codalus.Workbench.Mobile;

/// <summary>
/// Reads the tools' device lists (M20a, ADR 0064): <c>adb devices -l</c>, <c>emulator -list-avds</c>,
/// <c>xcrun simctl list devices available --json</c> and <c>xcrun devicectl list devices</c>' JSON. Parsing only, so
/// tests feed it recorded output.
/// </summary>
public static class MobileDevices
{
    /// <summary>The devices and running emulators <c>adb devices -l</c> lists.</summary>
    public static IReadOnlyList<MobileDevice> ParseAdbDevices(string output)
    {
        var devices = new List<MobileDevice>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.Ordinal) || line.StartsWith('*'))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var serial = parts[0];
            var state = parts[1];
            var properties = parts.Skip(2).Select(p => p.Split(':', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
            var emulator = serial.StartsWith("emulator-", StringComparison.Ordinal);
            var name = properties.GetValueOrDefault("model")?.Replace('_', ' ') ?? serial;
            devices.Add(new MobileDevice(serial, name, MobilePlatform.Android, emulator ? MobileDeviceKind.Emulator : MobileDeviceKind.Device, IsRunning: true)
            {
                Problem = state switch
                {
                    "device" => null,
                    "unauthorized" => "Allow USB debugging on the device: it asks when it's connected.",
                    "offline" => "The device is offline: reconnect it, or wait for the emulator to finish starting.",
                    "no permissions" or "no" => "adb has no permission to use this USB device (udev rules on Linux).",
                    _ => $"The device is {state}.",
                },
            });
        }

        return devices;
    }

    /// <summary>The virtual devices <c>emulator -list-avds</c> lists (it may print INFO lines before them).</summary>
    public static IReadOnlyList<string> ParseAvds(string output) =>
        [.. output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.'))];

    /// <summary>An AVD's name as it is shown: <c>Pixel_8_API_35</c> → <c>Pixel 8 API 35</c>.</summary>
    public static string AvdTitle(string avd) => avd.Replace('_', ' ');

    /// <summary>
    /// The emulators that aren't running yet: the AVDs no running emulator is using (<paramref name="running"/> maps
    /// an emulator's serial to its AVD, from <c>adb -s &lt;serial&gt; emu avd name</c>).
    /// </summary>
    public static IReadOnlyList<MobileDevice> StoppedEmulators(IReadOnlyList<string> avds, IReadOnlyDictionary<string, string> running) =>
        [.. avds.Where(a => !running.Values.Contains(a, StringComparer.Ordinal))
            .Select(a => new MobileDevice("avd:" + a, AvdTitle(a), MobilePlatform.Android, MobileDeviceKind.Emulator, IsRunning: false) { Avd = a })];

    /// <summary>The AVD name in <c>adb emu avd name</c>'s output (the first line; <c>OK</c> follows).</summary>
    public static string? ParseAvdName(string output) =>
        output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && l != "OK");

    /// <summary>
    /// iOS simulators from <c>xcrun simctl list devices available --json</c> (not watchOS, tvOS or visionOS). The tool's
    /// output comes with what it wrote on standard error after it (<c>EnvironmentCheck.RunProcessAsync</c>), so only the
    /// first JSON value is read and a warning after it doesn't lose the simulators.
    /// </summary>
    public static IReadOnlyList<MobileDevice> ParseSimulators(string json)
    {
        var result = new List<MobileDevice>();
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
            using var document = JsonDocument.ParseValue(ref reader);
            if (!document.RootElement.TryGetProperty("devices", out var runtimes) || runtimes.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var runtime in runtimes.EnumerateObject())
            {
                // com.apple.CoreSimulator.SimRuntime.iOS-18-0 → iOS 18.0
                var key = runtime.Name[(runtime.Name.LastIndexOf('.') + 1)..];
                if (!key.StartsWith("iOS-", StringComparison.Ordinal) || runtime.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var version = key[4..].Replace('-', '.');
                foreach (var device in runtime.Value.EnumerateArray())
                {
                    if (device.TryGetProperty("isAvailable", out var available) && available.ValueKind == JsonValueKind.False)
                    {
                        continue;
                    }

                    var udid = device.TryGetProperty("udid", out var u) ? u.GetString() : null;
                    var name = device.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var state = device.TryGetProperty("state", out var s) ? s.GetString() : null;
                    if (udid is { Length: > 0 } && name is { Length: > 0 })
                    {
                        result.Add(new MobileDevice(udid, name, MobilePlatform.iOS, MobileDeviceKind.Simulator, state == "Booted") { OsVersion = version });
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return [.. result.OrderByDescending(d => Version.TryParse(d.OsVersion, out var v) ? v : new Version(0, 0)).ThenBy(d => d.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The iOS targets of a Mac: the connected iPhones and iPads first, then the simulators. A device whose identifier
    /// is a listed simulator's is that simulator, and is left out: a booted "iPhone 17" simulator showed a second time as
    /// a device with the same identifier, both rows were where runs go, and a run resolved to the first, the "device",
    /// which can't be debugged ("Debugging on an iPhone or iPad isn't available yet", the maintainer's Mac, 2026-11-12).
    /// </summary>
    public static IReadOnlyList<MobileDevice> MergeApple(IReadOnlyList<MobileDevice> simulators, IReadOnlyList<MobileDevice> devices)
    {
        var simulated = simulators.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. devices.Where(d => !simulated.Contains(d.Id)), .. simulators];
    }

    /// <summary>Connected iPhones and iPads from <c>xcrun devicectl list devices --json-output</c> (Xcode 15 and later).</summary>
    public static IReadOnlyList<MobileDevice> ParseAppleDevices(string json)
    {
        var result = new List<MobileDevice>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("result", out var root) || !root.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var device in devices.EnumerateArray())
            {
                var hardware = device.TryGetProperty("hardwareProperties", out var h) ? h : default;
                var properties = device.TryGetProperty("deviceProperties", out var p) ? p : default;
                var connection = device.TryGetProperty("connectionProperties", out var c) ? c : default;
                string? Get(JsonElement element, string name) =>
                    element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

                if (Get(hardware, "platform") != "iOS" || Get(hardware, "udid") is not { Length: > 0 } udid)
                {
                    continue;
                }

                // A simulator isn't a connected device, where the tool lists one (its "reality" is "virtual").
                if (string.Equals(Get(hardware, "reality"), "virtual", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var paired = Get(connection, "pairingState") is null or "paired";
                result.Add(new MobileDevice(udid, Get(properties, "name") ?? Get(hardware, "marketingName") ?? udid, MobilePlatform.iOS, MobileDeviceKind.Device, IsRunning: true)
                {
                    OsVersion = Get(properties, "osVersionNumber"),
                    Problem = paired ? null : "Pair the device with this Mac: trust it when it asks, then open it in Xcode once.",
                });
            }
        }
        catch (JsonException)
        {
        }

        return result;
    }
}

/// <summary>A code signing identity (<c>security find-identity -v -p codesigning</c>).</summary>
public sealed record SigningIdentity(string Hash, string Name)
{
    /// <summary>The team id in the name's parentheses (<c>Apple Development: Jane Doe (ABCDE12345)</c>), or null.</summary>
    public string? Team => Name.LastIndexOf('(') is var open and >= 0 && Name.EndsWith(')') ? Name[(open + 1)..^1] : null;
}

/// <summary>What a provisioning profile signs for (ADR 0114): read from the devices it lists and its entitlements.</summary>
public enum ProvisioningKind
{
    /// <summary>Lists devices and allows a debugger (<c>get-task-allow</c>).</summary>
    Development,

    /// <summary>Lists devices without a debugger: ad hoc (iOS's "Release Testing").</summary>
    AdHoc,

    /// <summary>Lists no devices: the App Store (and TestFlight).</summary>
    AppStore,

    /// <summary>Provisions all devices: an enterprise's in-house distribution.</summary>
    Enterprise,

    /// <summary>A Mac's Developer ID distribution outside the Mac App Store.</summary>
    DeveloperId,
}

/// <summary>An installed provisioning profile.</summary>
public sealed record ProvisioningProfile(string Uuid, string Name, string? Team, string? ApplicationId, DateTimeOffset? Expires, string Path)
{
    public ProvisioningKind Kind { get; init; } = ProvisioningKind.Development;

    /// <summary>The platforms it lists (<c>iOS</c>, <c>OSX</c>); empty when it lists none.</summary>
    public IReadOnlyList<string> Platforms { get; init; } = [];

    /// <summary>Whether it signs apps for <paramref name="platform"/> (Mac Catalyst apps take the Mac's profiles; one listing no platform signs any).</summary>
    public bool IsFor(MobilePlatform platform) =>
        Platforms.Count == 0
        || (platform == MobilePlatform.iOS && Platforms.Contains("iOS", StringComparer.OrdinalIgnoreCase))
        || (platform == MobilePlatform.MacCatalyst && Platforms.Contains("OSX", StringComparer.OrdinalIgnoreCase));

    public bool IsExpired(DateTimeOffset now) => Expires is { } expires && expires < now;

    /// <summary>Whether it covers <paramref name="bundleId"/> (its application identifier, after the team prefix, may end with <c>*</c>).</summary>
    public bool Covers(string bundleId)
    {
        if (ApplicationId is not { } id)
        {
            return false;
        }

        var dot = id.IndexOf('.', StringComparison.Ordinal);
        var pattern = dot < 0 ? id : id[(dot + 1)..];
        return pattern == "*" || pattern == bundleId || (pattern.EndsWith(".*", StringComparison.Ordinal) && bundleId.StartsWith(pattern[..^1], StringComparison.Ordinal));
    }
}

/// <summary>What Apple signing a build will use (M20a): identities from the keychain and the installed provisioning profiles.</summary>
public static class AppleSigning
{
    /// <summary>The valid identities <c>security find-identity -v -p codesigning</c> lists.</summary>
    public static IReadOnlyList<SigningIdentity> ParseIdentities(string output)
    {
        var result = new List<SigningIdentity>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var close = line.IndexOf(')', StringComparison.Ordinal);
            var quote = line.IndexOf('"', StringComparison.Ordinal);
            if (close < 0 || quote < 0 || !char.IsDigit(line[0]) || !line.EndsWith('"'))
            {
                continue;
            }

            var hash = line[(close + 1)..quote].Trim();
            var name = line[(quote + 1)..^1];
            if (hash.Length > 0 && name.Length > 0)
            {
                result.Add(new SigningIdentity(hash, name));
            }
        }

        return result;
    }

    /// <summary>The folders provisioning profiles are installed in (Xcode 16's and the older one).</summary>
    public static IReadOnlyList<string> ProfileFolders(string home) =>
    [
        Path.Combine(home, "Library", "Developer", "Xcode", "UserData", "Provisioning Profiles"),
        Path.Combine(home, "Library", "MobileDevice", "Provisioning Profiles"),
    ];

    /// <summary>
    /// A <c>.mobileprovision</c> file's profile. The file is a signed (CMS) property list; the plist is read from the
    /// signed content without checking the signature, which the build's tools do.
    /// </summary>
    public static ProvisioningProfile? ParseProfile(byte[] contents, string path)
    {
        var text = Encoding.Latin1.GetString(contents);
        var start = text.IndexOf("<?xml", StringComparison.Ordinal);
        var end = text.IndexOf("</plist>", StringComparison.Ordinal);
        if (start < 0 || end < start
            || PropertyList.ParseDictionary(Encoding.UTF8.GetString(contents, start, end + "</plist>".Length - start)) is not { } values
            || values.GetValueOrDefault("UUID") is not string uuid)
        {
            return null;
        }

        var entitlements = values.GetValueOrDefault("Entitlements") as IReadOnlyDictionary<string, object>;
        var applicationId = entitlements?.GetValueOrDefault("application-identifier") as string
            ?? entitlements?.GetValueOrDefault("com.apple.application-identifier") as string;
        var team = (values.GetValueOrDefault("TeamIdentifier") as IReadOnlyList<object>)?.OfType<string>().FirstOrDefault();
        var platforms = (values.GetValueOrDefault("Platform") as IReadOnlyList<object>)?.OfType<string>().ToList() ?? [];
        var kind = values.GetValueOrDefault("ProvisionsAllDevices") is true
                ? (platforms.Contains("OSX", StringComparer.OrdinalIgnoreCase) ? ProvisioningKind.DeveloperId : ProvisioningKind.Enterprise)
            : values.ContainsKey("ProvisionedDevices")
                ? (entitlements?.GetValueOrDefault("get-task-allow") is true || entitlements?.GetValueOrDefault("com.apple.security.get-task-allow") is true ? ProvisioningKind.Development : ProvisioningKind.AdHoc)
            : ProvisioningKind.AppStore;
        return new ProvisioningProfile(uuid, values.GetValueOrDefault("Name") as string ?? uuid, team, applicationId, values.GetValueOrDefault("ExpirationDate") as DateTimeOffset?, path)
        {
            Kind = kind,
            Platforms = platforms,
        };
    }

    /// <summary>The profiles in the usual folders.</summary>
    public static IReadOnlyList<ProvisioningProfile> InstalledProfiles(string home)
    {
        var result = new List<ProvisioningProfile>();
        foreach (var folder in ProfileFolders(home).Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.mobileprovision").Concat(Directory.EnumerateFiles(folder, "*.provisionprofile")))
            {
                try
                {
                    if (ParseProfile(File.ReadAllBytes(file), file) is { } profile && result.All(p => p.Uuid != profile.Uuid))
                    {
                        result.Add(profile);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        return result;
    }
}
