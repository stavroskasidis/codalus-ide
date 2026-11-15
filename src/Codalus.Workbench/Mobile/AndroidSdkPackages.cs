using System.Globalization;
using System.Runtime.InteropServices;

namespace Codalus.Workbench.Mobile;

/// <summary>What an Android SDK package is, for the SDK Manager's groups.</summary>
public enum SdkPackageCategory
{
    Platform,
    SystemImage,
    BuildTools,
    Tools,
    Sources,
    Other,
}

/// <summary>
/// An Android SDK package as <c>sdkmanager --list</c> reports it (ADR 0104): its path (<c>platforms;android-35</c>), the
/// version installed or available, whether it's installed, and the newer version when there is one.
/// </summary>
public sealed record SdkPackage(string Path, string Version, string Description, bool Installed)
{
    /// <summary>The newer version <c>sdkmanager</c> offers, when installed and out of date.</summary>
    public string? Update { get; init; }

    /// <summary>The installed package's folder, relative to the SDK.</summary>
    public string? Location { get; init; }

    public SdkPackageCategory Category => AndroidSdkPackages.CategoryOf(Path);

    /// <summary>The API level a platform, a system image or sources are for (<c>android-35</c>: 35), or null.</summary>
    public int? ApiLevel => AndroidSdkPackages.ApiLevelOf(Path);
}

/// <summary>A license <c>sdkmanager</c> asks the user to accept before installing (<c>android-sdk-license</c>), and its text.</summary>
public sealed record SdkLicense(string Id, string Text);

/// <summary>
/// Reads <c>sdkmanager</c>'s output (ADR 0104): the package tables of <c>--list</c> and the license prompts of
/// <c>--install</c>. Parsing only, so tests feed it recorded output.
/// </summary>
public static class AndroidSdkPackages
{
    /// <summary>
    /// The packages in <c>sdkmanager --list</c>'s three tables: installed packages, available packages (the latest version
    /// of each path), and available updates. An installed package is listed once, with its update when there is one.
    /// </summary>
    public static IReadOnlyList<SdkPackage> ParseList(string output)
    {
        var installed = new Dictionary<string, SdkPackage>(StringComparer.Ordinal);
        var available = new Dictionary<string, SdkPackage>(StringComparer.Ordinal);
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        string? section = null;
        foreach (var raw in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            // Progress bars rewrite the line with \r: only the last part counts.
            var line = raw.Contains('\r', StringComparison.Ordinal) ? raw[(raw.LastIndexOf('\r') + 1)..] : raw;
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!trimmed.Contains('|', StringComparison.Ordinal))
            {
                section = trimmed switch
                {
                    "Installed packages:" => "installed",
                    "Available Packages:" => "available",
                    "Available Updates:" => "updates",
                    _ => trimmed.EndsWith(':') && !trimmed.StartsWith("Warning", StringComparison.Ordinal) ? null : section,
                };
                continue;
            }

            var cells = trimmed.Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 2 || cells[0] is "Path" or "ID" || cells[0].StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }

            switch (section)
            {
                case "installed":
                    installed[cells[0]] = new SdkPackage(cells[0], cells[1], cells.ElementAtOrDefault(2) ?? string.Empty, Installed: true) { Location = cells.ElementAtOrDefault(3) };
                    break;
                case "available":
                    // Several versions of a path can be listed (a preview channel): the last one is the newest.
                    available[cells[0]] = new SdkPackage(cells[0], cells[1], cells.ElementAtOrDefault(2) ?? string.Empty, Installed: false);
                    break;
                case "updates":
                    if (cells.Length >= 3)
                    {
                        updates[cells[0]] = cells[2];
                    }

                    break;
            }
        }

        var result = new List<SdkPackage>();
        foreach (var package in installed.Values)
        {
            var description = package.Description.Length > 0 ? package.Description : available.GetValueOrDefault(package.Path)?.Description ?? string.Empty;
            result.Add(package with { Description = description, Update = updates.GetValueOrDefault(package.Path) });
        }

        result.AddRange(available.Values.Where(p => !installed.ContainsKey(p.Path)));
        return [.. result.OrderBy(p => p.Category).ThenByDescending(p => p.ApiLevel ?? 0).ThenBy(p => p.Path, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The licenses an <c>sdkmanager --install</c> asked about, in order: each <c>License id:</c> (or
    /// <c>1/3: License id:</c>), its text between two dashed rules, then <c>Accept? (y/N):</c>.
    /// </summary>
    public static IReadOnlyList<SdkLicense> ParseLicenses(string output)
    {
        var licenses = new List<SdkLicense>();
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            var at = line.IndexOf("License ", StringComparison.Ordinal);
            if (at < 0 || !line.EndsWith(':') || (at > 0 && !line[..at].TrimEnd().EndsWith(':')))
            {
                continue;
            }

            var id = line[(at + "License ".Length)..^1].Trim();
            if (id.Length == 0 || id.Contains(' ', StringComparison.Ordinal))
            {
                continue;
            }

            var start = i + 1;
            if (start < lines.Length && lines[start].Trim().StartsWith("---", StringComparison.Ordinal))
            {
                start++;
            }

            var end = start;
            while (end < lines.Length && !lines[end].Trim().StartsWith("---", StringComparison.Ordinal) && !lines[end].Contains("Accept? (y/N)", StringComparison.Ordinal))
            {
                end++;
            }

            if (licenses.All(l => l.Id != id))
            {
                licenses.Add(new SdkLicense(id, string.Join('\n', lines[start..end].Select(l => l.TrimEnd())).Trim()));
            }

            i = end;
        }

        return licenses;
    }

    /// <summary>The ids of the licenses accepted in <paramref name="sdk"/> (a file per license in its <c>licenses</c> folder).</summary>
    public static IReadOnlySet<string> AcceptedLicenses(string sdk)
    {
        var folder = System.IO.Path.Combine(sdk, "licenses");
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).Select(System.IO.Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    public static SdkPackageCategory CategoryOf(string path) => (path.Split(';')[0]) switch
    {
        "platforms" => SdkPackageCategory.Platform,
        "system-images" => SdkPackageCategory.SystemImage,
        "build-tools" => SdkPackageCategory.BuildTools,
        "platform-tools" or "emulator" or "cmdline-tools" or "tools" or "cmake" or "ndk" or "ndk-bundle" => SdkPackageCategory.Tools,
        "sources" => SdkPackageCategory.Sources,
        _ => SdkPackageCategory.Other,
    };

    /// <summary>The API level in a path's <c>android-NN</c> part (<c>android-35</c>, <c>android-35-ext14</c>), or null.</summary>
    public static int? ApiLevelOf(string path)
    {
        foreach (var part in path.Split(';'))
        {
            if (part.StartsWith("android-", StringComparison.Ordinal))
            {
                var digits = new string([.. part["android-".Length..].TakeWhile(char.IsAsciiDigit)]);
                if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var level))
                {
                    return level;
                }
            }
        }

        return null;
    }

    /// <summary>A system image's parts: <c>system-images;android-35;google_apis_playstore;x86_64</c>.</summary>
    public static (int? ApiLevel, string Tag, string Abi)? SystemImageOf(string path) =>
        path.Split(';') is ["system-images", var platform, var tag, var abi] ? (ApiLevelOf(platform), tag, abi) : null;

    /// <summary>The system images' ABI that runs fast on this machine: <c>arm64-v8a</c> on Arm, <c>x86_64</c> elsewhere.</summary>
    public static string HostAbi(Architecture architecture) => architecture == Architecture.Arm64 ? "arm64-v8a" : "x86_64";

    /// <summary>A system image's tag as people read it (<c>google_apis_playstore</c>: Google Play).</summary>
    public static string TagTitle(string tag) => tag switch
    {
        "google_apis_playstore" => "Google Play",
        "google_apis" => "Google APIs",
        "default" => "AOSP",
        "google_atd" => "Google ATD",
        "aosp_atd" => "AOSP ATD",
        "google_apis_playstore_ps16k" => "Google Play, 16 KB pages",
        "google_apis_ps16k" => "Google APIs, 16 KB pages",
        _ => tag.Replace('_', ' '),
    };
}
