using Codalus.Platform;
using Codalus.Projects.Protocol;

namespace Codalus.Workbench.Mobile;

/// <summary>The platform a target framework builds for (<c>net10.0-android</c> → Android).</summary>
public enum MobilePlatform
{
    None,
    Android,
    iOS,
    MacCatalyst,

    /// <summary>WinUI, the Windows App SDK (M44, ADR 0074): runs on Windows only (D31).</summary>
    Windows,
    Tizen,
}

/// <summary>Where a MAUI app runs.</summary>
public enum MobileDeviceKind
{
    Emulator,
    Simulator,

    /// <summary>A physical phone or tablet.</summary>
    Device,

    /// <summary>This Mac (Mac Catalyst).</summary>
    ThisMac,

    /// <summary>This Windows machine (MAUI's Windows target, M44): "Windows Machine", as other IDEs call it.</summary>
    ThisPc,
}

/// <summary>
/// A device, emulator or simulator a MAUI app can run on (M20a, ADR 0064). <see cref="Id"/> is what the build's run
/// target is given: an adb serial, a simulator's or a device's UDID; an emulator that isn't running is
/// <c>avd:&lt;name&gt;</c> until it starts.
/// </summary>
public sealed record MobileDevice(string Id, string Name, MobilePlatform Platform, MobileDeviceKind Kind, bool IsRunning)
{
    public string? OsVersion { get; init; }

    /// <summary>Why it can't be used yet (an unauthorized or offline Android device), or null.</summary>
    public string? Problem { get; init; }

    /// <summary>An Android emulator's virtual device name.</summary>
    public string? Avd { get; init; }

    public bool IsUsable => Problem is null;

    /// <summary>The name with what it is, for pickers: "Pixel 8 (Android 15, emulator)".</summary>
    public string Label
    {
        get
        {
            var kind = Kind switch
            {
                MobileDeviceKind.Emulator => IsRunning ? "emulator" : "emulator, not running",
                MobileDeviceKind.Simulator => IsRunning ? "simulator" : "simulator, shut down",
                MobileDeviceKind.ThisMac => "Mac Catalyst",
                MobileDeviceKind.ThisPc => "Windows",
                _ => "device",
            };
            var os = OsVersion is null ? null : $"{MobileTargets.Title(Platform)} {OsVersion}";
            return Kind is MobileDeviceKind.ThisMac or MobileDeviceKind.ThisPc ? Name : $"{Name} ({(os is null ? kind : os + ", " + kind)})";
        }
    }
}

/// <summary>.NET MAUI's platform frameworks, the workloads they need and the commands that run them (M20a, ADR 0064).</summary>
public static class MobileTargets
{
    /// <summary>The id of this Mac as a Mac Catalyst target.</summary>
    public const string ThisMacId = "maccatalyst";

    /// <summary>The id of this Windows machine as the Windows target (M44).</summary>
    public const string WindowsMachineId = "windows";

    /// <summary>This Windows machine as a target: MAUI's Windows framework runs here as a desktop app (M44).</summary>
    public static MobileDevice WindowsMachine { get; } = new(WindowsMachineId, "Windows Machine", MobilePlatform.Windows, MobileDeviceKind.ThisPc, IsRunning: true);

    /// <summary>The platform of a target framework: its platform part (<c>net10.0-ios18.0</c> → iOS).</summary>
    public static MobilePlatform PlatformOf(string? framework)
    {
        if (framework is null || framework.IndexOf('-', StringComparison.Ordinal) is var dash && dash < 0)
        {
            return MobilePlatform.None;
        }

        var platform = framework[(dash + 1)..].TrimEnd('.', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9').ToLowerInvariant();
        return platform switch
        {
            "android" => MobilePlatform.Android,
            "ios" => MobilePlatform.iOS,
            "maccatalyst" => MobilePlatform.MacCatalyst,
            "windows" => MobilePlatform.Windows,
            "tizen" => MobilePlatform.Tizen,
            _ => MobilePlatform.None,
        };
    }

    public static string Title(MobilePlatform platform) => platform switch
    {
        MobilePlatform.Android => "Android",
        MobilePlatform.iOS => "iOS",
        MobilePlatform.MacCatalyst => "Mac Catalyst",
        MobilePlatform.Windows => "Windows",
        MobilePlatform.Tizen => "Tizen",
        _ => "",
    };

    /// <summary>
    /// Whether Codalus IDE runs and deploys this platform's apps on <paramref name="host"/>: Android everywhere, iOS and
    /// Mac Catalyst on macOS, Windows (WinUI, M44) on Windows (D31: no Windows remote target yet). Tizen isn't
    /// supported.
    /// </summary>
    public static bool RunsOn(MobilePlatform platform, PlatformKind host) => platform switch
    {
        MobilePlatform.Android => true,
        MobilePlatform.iOS or MobilePlatform.MacCatalyst => host == PlatformKind.MacOS,
        MobilePlatform.Windows => host == PlatformKind.Windows,
        _ => false,
    };

    /// <summary>Whether the project runs on devices: a MAUI project, or any project with a mobile framework.</summary>
    public static bool IsMobile(ProjectInfo project) =>
        project.UsesMaui || project.TargetFrameworks.Any(f => PlatformOf(f) is MobilePlatform.Android or MobilePlatform.iOS or MobilePlatform.MacCatalyst);

    /// <summary>
    /// The framework a run uses: the chosen one (<paramref name="active"/>) when this machine runs it; else, on Windows,
    /// the Windows framework (the "Windows Machine" other IDEs start with, M44); else the first that runs here (Android
    /// before Apple's on macOS, as the templates list them); or null when none does.
    /// </summary>
    public static string? RunFramework(ProjectInfo project, string? active, PlatformKind host)
    {
        if (active is not null && RunsOn(PlatformOf(active), host) && project.TargetFrameworks.Contains(active, StringComparer.OrdinalIgnoreCase))
        {
            return active;
        }

        if (host == PlatformKind.Windows && FrameworkFor(project, MobilePlatform.Windows) is { } windows)
        {
            return windows;
        }

        return project.TargetFrameworks.FirstOrDefault(f => RunsOn(PlatformOf(f), host));
    }

    /// <summary>The framework of <paramref name="project"/> for <paramref name="platform"/>, or null.</summary>
    public static string? FrameworkFor(ProjectInfo project, MobilePlatform platform) =>
        project.TargetFrameworks.FirstOrDefault(f => PlatformOf(f) == platform);

    /// <summary>The workload a platform needs (MAUI's, for a MAUI project; the bare platform's otherwise).</summary>
    public static string? Workload(MobilePlatform platform, bool maui) => platform switch
    {
        MobilePlatform.Android => maui ? "maui-android" : "android",
        MobilePlatform.iOS => maui ? "maui-ios" : "ios",
        MobilePlatform.MacCatalyst => maui ? "maui-maccatalyst" : "maccatalyst",
        MobilePlatform.Tizen => maui ? "maui-tizen" : null,
        MobilePlatform.Windows => maui ? "maui-windows" : null,
        _ => null,
    };

    /// <summary>
    /// The workloads <paramref name="project"/> needs on <paramref name="host"/> that <paramref name="installed"/>
    /// doesn't list: those of its frameworks this machine builds.
    /// </summary>
    public static IReadOnlyList<string> MissingWorkloads(ProjectInfo project, IReadOnlyCollection<string> installed, PlatformKind host)
    {
        var installedSet = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
        if (project.UsesMaui && installedSet.Contains("maui"))
        {
            return [];
        }

        return [.. project.TargetFrameworks
            .Select(PlatformOf)
            .Where(p => RunsOn(p, host))
            .Select(p => Workload(p, project.UsesMaui))
            .OfType<string>()
            .Where(w => !installedSet.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The arguments of <c>dotnet</c> that build, deploy and start the app on <paramref name="device"/>: the platform
    /// SDK's <c>Run</c> target (<c>dotnet build -t:Run</c>), with the device as the target expects it.
    /// </summary>
    public static IReadOnlyList<string> RunArguments(string projectPath, string framework, MobileDevice? device, string? configuration, AndroidToolchain.Locations? android = null)
    {
        var arguments = new List<string> { "build", projectPath, "-t:Run", "-f", framework };
        if (!string.IsNullOrEmpty(configuration))
        {
            arguments.Add("-c");
            arguments.Add(configuration);
        }

        switch (PlatformOf(framework))
        {
            case MobilePlatform.Android:
                if (device is { IsRunning: true })
                {
                    arguments.Add("-p:AdbTarget=-s " + device.Id);
                }

                if (android?.Sdk is { } sdk)
                {
                    arguments.Add("-p:AndroidSdkDirectory=" + sdk);
                }

                if (android?.Jdk is { } jdk)
                {
                    arguments.Add("-p:JavaSdkDirectory=" + jdk);
                }

                break;
            case MobilePlatform.iOS when device is { Kind: MobileDeviceKind.Simulator }:
                arguments.Add("-p:_DeviceName=:v2:udid=" + device.Id);
                break;
            case MobilePlatform.iOS when device is { Kind: MobileDeviceKind.Device }:
                arguments.Add("-p:RuntimeIdentifier=ios-arm64");
                arguments.Add("-p:_DeviceName=" + device.Id);
                break;
        }

        return arguments;
    }

    /// <summary>
    /// The run's extra arguments and the Mono debugger adapter's launch arguments for debugging on
    /// <paramref name="device"/> through local port <paramref name="port"/> (M20b, ADR 0065), or null when the platform
    /// can't be debugged yet (an iPhone).
    /// <list type="bullet">
    /// <item>Android: the SDK's own debugger properties: its <c>Run</c> target forwards the port with adb and starts the
    /// app with its agent listening there (<c>debug.mono.extra</c>); the adapter connects, retrying until it answers.</item>
    /// <item>iOS simulators and Mac Catalyst: the app connects to the adapter, which listens
    /// (<c>__XAMARIN_DEBUG_PORT__</c>, <c>__XAMARIN_DEBUG_HOSTS__</c>).</item>
    /// </list>
    /// </summary>
    public static (IReadOnlyList<string> RunArguments, Codalus.Debugging.MonoDebugArguments Adapter)? DebugArguments(string framework, MobileDevice? device, int port, bool justMyCode = true)
    {
        var p = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return PlatformOf(framework) switch
        {
            MobilePlatform.Android => (
                ["-p:AndroidAttachDebugger=true", "-p:AndroidSdbTargetPort=" + p, "-p:AndroidSdbHostPort=" + p],
                new Codalus.Debugging.MonoDebugArguments(Codalus.Debugging.MonoDebugArguments.Connect, "127.0.0.1", port, TimeoutSeconds: 900, JustMyCode: justMyCode)),
            MobilePlatform.iOS when device is not { Kind: MobileDeviceKind.Device } => (
                ["-p:MtouchDebug=true", $"-p:MlaunchAdditionalArguments=--argument=-monodevelop-port --argument={p} --setenv=__XAMARIN_DEBUG_PORT__={p} --setenv=__XAMARIN_DEBUG_HOSTS__=127.0.0.1"],
                new Codalus.Debugging.MonoDebugArguments(Codalus.Debugging.MonoDebugArguments.Listen, "127.0.0.1", port, TimeoutSeconds: 900, JustMyCode: justMyCode)),
            MobilePlatform.MacCatalyst => (
                ["-p:_BundlerDebug=true", $"-p:OpenArguments=--env __XAMARIN_DEBUG_PORT__={p} --env __XAMARIN_DEBUG_HOSTS__=127.0.0.1"],
                new Codalus.Debugging.MonoDebugArguments(Codalus.Debugging.MonoDebugArguments.Listen, "127.0.0.1", port, TimeoutSeconds: 900, JustMyCode: justMyCode)),
            _ => null,
        };
    }

    /// <summary>
    /// The arguments of <c>dotnet</c> that install the Android SDK (and a JDK when none is found) through the Android
    /// workload's own <c>InstallAndroidDependencies</c> target. Running it accepts the Android SDK's licenses, so the
    /// IDE shows them first.
    /// </summary>
    public static IReadOnlyList<string> InstallAndroidDependenciesArguments(string projectPath, string framework, string sdkDirectory, string jdkDirectory) =>
        ["build", projectPath, "-t:InstallAndroidDependencies", "-f", framework, "-p:AndroidSdkDirectory=" + sdkDirectory, "-p:JavaSdkDirectory=" + jdkDirectory, "-p:AcceptAndroidSDKLicenses=True"];
}
