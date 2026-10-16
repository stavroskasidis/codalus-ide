using Codalus.Platform;
using Codalus.Workbench.Setup;

namespace Codalus.Workbench.Mobile;

/// <summary>
/// The Android SDK and the JDK on this machine (M20a, ADR 0064), found as the Android workload finds them: the
/// <c>android.sdkDirectory</c>/<c>android.jdkDirectory</c> settings, <c>ANDROID_HOME</c>/<c>ANDROID_SDK_ROOT</c> and
/// <c>JAVA_HOME</c>, then the usual install folders (Android Studio's, the Microsoft Build of OpenJDK's, the
/// distributions'). The tools come from the SDK: <c>adb</c> and the emulator.
/// </summary>
public sealed class AndroidToolchain(PlatformKind platform, Func<string, string?> environment, string home, Func<string, bool>? fileExists = null, Func<string, IEnumerable<string>>? subdirectories = null)
{
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;
    private readonly Func<string, IEnumerable<string>> _subdirectories = subdirectories ?? (folder => Directory.Exists(folder) ? Directory.EnumerateDirectories(folder) : []);

    /// <summary>Where the SDK and the JDK are (each null when not found), and the SDK's tools.</summary>
    public sealed record Locations(string? Sdk, string? Jdk, string? Adb, string? Emulator)
    {
        public bool IsComplete => Sdk is not null && Jdk is not null && Adb is not null;
    }

    private string Exe(string name) => platform == PlatformKind.Windows ? name + ".exe" : name;

    /// <summary>The SDK and JDK, with the settings' folders first when given.</summary>
    public Locations Find(string? sdkSetting = null, string? jdkSetting = null)
    {
        var sdk = SdkCandidates(sdkSetting).FirstOrDefault(IsSdk);
        var jdk = JdkCandidates(jdkSetting).FirstOrDefault(IsJdk);
        var adb = sdk is null ? null : Path.Combine(sdk, "platform-tools", Exe("adb"));
        var emulator = sdk is null ? null : Path.Combine(sdk, "emulator", Exe("emulator"));
        return new Locations(sdk, jdk, adb is not null && _fileExists(adb) ? adb : null, emulator is not null && _fileExists(emulator) ? emulator : null);
    }

    /// <summary>Where the IDE installs the SDK when there's none (Android Studio's default folder).</summary>
    public string DefaultSdkDirectory => platform switch
    {
        PlatformKind.Windows => Path.Combine(environment("LOCALAPPDATA") ?? Path.Combine(home, "AppData", "Local"), "Android", "Sdk"),
        PlatformKind.MacOS => Path.Combine(home, "Library", "Android", "sdk"),
        _ => Path.Combine(home, "Android", "Sdk"),
    };

    /// <summary>Where the IDE installs a JDK when there's none: next to the SDK.</summary>
    public string DefaultJdkDirectory => Path.Combine(Path.GetDirectoryName(DefaultSdkDirectory)!, "jdk");

    private IEnumerable<string> SdkCandidates(string? setting)
    {
        if (!string.IsNullOrWhiteSpace(setting))
        {
            yield return setting.Trim();
        }

        foreach (var variable in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            if (environment(variable) is { Length: > 0 } value)
            {
                yield return value;
            }
        }

        yield return DefaultSdkDirectory;
        if (platform == PlatformKind.Windows)
        {
            var programFilesX86 = environment("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";
            yield return Path.Combine(programFilesX86, "Android", "android-sdk");
        }
        else if (platform == PlatformKind.Linux)
        {
            yield return Path.Combine(home, "android-sdk");
            yield return "/usr/lib/android-sdk";
            yield return "/opt/android-sdk";
        }
    }

    private IEnumerable<string> JdkCandidates(string? setting)
    {
        if (!string.IsNullOrWhiteSpace(setting))
        {
            yield return setting.Trim();
        }

        if (environment("JAVA_HOME") is { Length: > 0 } javaHome)
        {
            yield return javaHome;
        }

        yield return DefaultJdkDirectory;

        // Newest first among the usual folders: the workload supports JDK 17 and later.
        IEnumerable<string> folders = platform switch
        {
            PlatformKind.Windows => Under(Path.Combine(environment("ProgramFiles") ?? @"C:\Program Files", "Microsoft"), "jdk-")
                .Concat(Under(Path.Combine(environment("ProgramFiles") ?? @"C:\Program Files", "Eclipse Adoptium"), "jdk-"))
                .Concat(Under(Path.Combine(environment("ProgramFiles") ?? @"C:\Program Files", "Java"), "jdk")),
            PlatformKind.MacOS => Under("/Library/Java/JavaVirtualMachines", "").Select(j => Path.Combine(j, "Contents", "Home"))
                .Concat(["/opt/homebrew/opt/openjdk/libexec/openjdk.jdk/Contents/Home"]),
            _ => Under("/usr/lib/jvm", "").Concat(Under("/usr/lib64/jvm", "")),
        };
        foreach (var folder in folders)
        {
            yield return folder;
        }

        // Android Studio's own runtime.
        yield return platform switch
        {
            PlatformKind.Windows => Path.Combine(environment("ProgramFiles") ?? @"C:\Program Files", "Android", "Android Studio", "jbr"),
            PlatformKind.MacOS => "/Applications/Android Studio.app/Contents/jbr/Contents/Home",
            _ => "/opt/android-studio/jbr",
        };
    }

    private IEnumerable<string> Under(string folder, string prefix) =>
        _subdirectories(folder)
            .Where(d => Path.GetFileName(d).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => JdkMajor(Path.GetFileName(d)));

    /// <summary>The major version in a JDK folder's name (<c>jdk-21.0.4+7</c>, <c>java-17-openjdk-amd64</c>), or 0.</summary>
    internal static int JdkMajor(string name)
    {
        foreach (var part in name.Split('-', '.', '+', '_'))
        {
            if (int.TryParse(part.TrimStart('j', 'd', 'k'), out var number) && number is >= 8 and < 100)
            {
                return number;
            }
        }

        return 0;
    }

    /// <summary>The folder name that carries a JDK's version (macOS's <c>jdk-21.jdk/Contents/Home</c>: <c>jdk-21.jdk</c>).</summary>
    private static string JdkFolderName(string jdk)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(jdk);
        return Path.GetFileName(trimmed) == "Home" && Path.GetDirectoryName(Path.GetDirectoryName(trimmed)) is { } bundle ? Path.GetFileName(bundle) : Path.GetFileName(trimmed);
    }

    private bool IsSdk(string folder) =>
        _fileExists(Path.Combine(folder, "platform-tools", Exe("adb"))) || _subdirectories(Path.Combine(folder, "platforms")).Any() || _subdirectories(Path.Combine(folder, "build-tools")).Any();

    private bool IsJdk(string folder) =>
        _fileExists(Path.Combine(folder, "bin", Exe("java"))) && _fileExists(Path.Combine(folder, "bin", Exe("javac")));

    /// <summary>The SDK platforms installed (<c>android-35</c>…), for Check Environment.</summary>
    public IReadOnlyList<string> Platforms(string sdk) =>
        [.. _subdirectories(Path.Combine(sdk, "platforms")).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

    /// <summary>How the SDK and JDK stand, as Check Environment rows.</summary>
    public IReadOnlyList<ToolStatus> Status(Locations locations)
    {
        const string NeededFor = "Building, running and debugging .NET MAUI and .NET for Android apps";
        var sdk = locations.Sdk is { } s
            ? new ToolStatus("android-sdk", "Android SDK", false, locations.Adb is null ? ToolState.Mismatch : ToolState.Found, NeededFor)
            {
                Path = s,
                Version = Platforms(s) is { Count: > 0 } platforms ? platforms[^1] : null,
                Detail = locations.Adb is null ? "The SDK has no platform-tools (adb)." : null,
            }
            : new ToolStatus("android-sdk", "Android SDK", false, ToolState.Missing, NeededFor) { Detail = "No Android SDK was found (ANDROID_HOME, or " + DefaultSdkDirectory + ")." };
        var jdk = locations.Jdk is { } j
            ? new ToolStatus("jdk", "Java Development Kit", false, ToolState.Found, NeededFor) { Path = j, Version = JdkMajor(JdkFolderName(j)) is > 0 and var major ? major.ToString(System.Globalization.CultureInfo.InvariantCulture) : null }
            : new ToolStatus("jdk", "Java Development Kit", false, ToolState.Missing, NeededFor) { Detail = "No JDK was found (JAVA_HOME, or the usual folders)." };
        return [sdk, jdk];
    }
}
