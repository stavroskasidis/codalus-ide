using Codalus.Platform;

namespace Codalus.Workbench.Mobile;

/// <summary>
/// The Android SDK and the JDK a .NET for Android build needs (M20a, ADR 0064), found where the tools put them:
/// <c>ANDROID_HOME</c>/<c>ANDROID_SDK_ROOT</c> and <c>JAVA_HOME</c> first, then the folders Android Studio, Visual Studio's
/// installer and the Android workload's <c>InstallAndroidDependencies</c> use on each OS.
/// </summary>
/// <param name="SdkPath">The Android SDK's folder (with <c>platform-tools/</c>), or null when none was found.</param>
/// <param name="JdkPath">The JDK's folder (with <c>bin/java</c>), or null.</param>
public sealed record AndroidSdk(string? SdkPath, string? JdkPath)
{
    private static string Exe(PlatformKind platform, string name) => platform == PlatformKind.Windows ? name + ".exe" : name;

    /// <summary><c>adb</c>, when the SDK has it.</summary>
    public string? Adb(PlatformKind platform, Func<string, bool> exists) =>
        SdkPath is { } sdk && Path.Combine(sdk, "platform-tools", Exe(platform, "adb")) is var adb && exists(adb) ? adb : null;

    /// <summary>The emulator, when the SDK has it.</summary>
    public string? Emulator(PlatformKind platform, Func<string, bool> exists) =>
        SdkPath is { } sdk && Path.Combine(sdk, "emulator", Exe(platform, "emulator")) is var emulator && exists(emulator) ? emulator : null;

    /// <summary>The properties a build passes so the workload finds the same SDK and JDK.</summary>
    public IReadOnlyDictionary<string, string> BuildProperties()
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (SdkPath is { } sdk)
        {
            properties["AndroidSdkDirectory"] = sdk;
        }

        if (JdkPath is { } jdk)
        {
            properties["JavaSdkDirectory"] = jdk;
        }

        return properties;
    }

    /// <summary>Finds the SDK and the JDK on this machine.</summary>
    public static AndroidSdk Locate(PlatformKind platform, Func<string, string?> environment, Func<string, bool>? directoryExists = null, Func<string, IEnumerable<string>>? subdirectories = null)
    {
        var exists = directoryExists ?? Directory.Exists;
        var children = subdirectories ?? (path => Directory.Exists(path) ? Directory.EnumerateDirectories(path) : []);
        var home = environment("HOME") ?? environment("USERPROFILE") ?? string.Empty;

        var sdkCandidates = new List<string?> { environment("ANDROID_HOME"), environment("ANDROID_SDK_ROOT") };
        var jdkCandidates = new List<string?> { environment("JAVA_HOME") };
        switch (platform)
        {
            case PlatformKind.Windows:
                var programFilesX86 = environment("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";
                var programFiles = environment("ProgramFiles") ?? @"C:\Program Files";
                var localAppData = environment("LOCALAPPDATA") ?? Path.Combine(home, "AppData", "Local");
                sdkCandidates.Add(Path.Combine(localAppData, "Android", "Sdk")); // Android Studio
                sdkCandidates.Add(Path.Combine(programFilesX86, "Android", "android-sdk")); // Visual Studio's installer
                jdkCandidates.AddRange(Newest(children, Path.Combine(programFilesX86, "Android", "openjdk"), "jdk-"));
                jdkCandidates.AddRange(Newest(children, Path.Combine(programFiles, "Microsoft"), "jdk-"));
                jdkCandidates.AddRange(Newest(children, Path.Combine(programFiles, "Eclipse Adoptium"), "jdk-"));
                jdkCandidates.Add(Path.Combine(programFiles, "Android", "Android Studio", "jbr"));
                break;
            case PlatformKind.MacOS:
                sdkCandidates.Add(Path.Combine(home, "Library", "Android", "sdk"));
                jdkCandidates.AddRange(Newest(children, "/Library/Java/JavaVirtualMachines", string.Empty).Select(j => Path.Combine(j, "Contents", "Home")));
                jdkCandidates.Add("/Applications/Android Studio.app/Contents/jbr/Contents/Home");
                break;
            default:
                sdkCandidates.Add(Path.Combine(home, "Android", "Sdk"));
                sdkCandidates.Add(Path.Combine(home, "android-sdk"));
                jdkCandidates.AddRange(Newest(children, "/usr/lib/jvm", string.Empty));
                break;
        }

        // The workload's InstallAndroidDependencies default (and Visual Studio Code's .NET MAUI extension).
        sdkCandidates.Add(Path.Combine(home, ".android", "sdk"));
        jdkCandidates.AddRange(Newest(children, Path.Combine(home, ".android", "jdk"), string.Empty));

        var sdk = sdkCandidates.FirstOrDefault(p => p is { Length: > 0 } && exists(Path.Combine(p, "platform-tools")));
        var jdk = jdkCandidates.FirstOrDefault(p => p is { Length: > 0 } && exists(Path.Combine(p, "bin")));
        return new AndroidSdk(sdk, jdk);
    }

    /// <summary>The folders under <paramref name="parent"/> starting with <paramref name="prefix"/>, newest version first.</summary>
    private static List<string> Newest(Func<string, IEnumerable<string>> children, string parent, string prefix) =>
        children(parent)
            .Where(d => Path.GetFileName(d).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => VersionOf(Path.GetFileName(d)[prefix.Length..]))
            .ToList();

    private static Version VersionOf(string text)
    {
        var digits = new string([.. text.SkipWhile(c => !char.IsAsciiDigit(c)).TakeWhile(c => char.IsAsciiDigit(c) || c == '.')]).Trim('.');
        return Version.TryParse(digits.Contains('.', StringComparison.Ordinal) ? digits : digits + ".0", out var version) ? version : new Version(0, 0);
    }
}
