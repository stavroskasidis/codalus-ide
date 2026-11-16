using System.Globalization;
using System.Text;

namespace Codalus.Workbench.Mobile;

/// <summary>
/// An Android Virtual Device (ADR 0104): its name (<c>Pixel_7_API_35</c>), the folder with its <c>config.ini</c>, and
/// what the Device Manager shows and edits from that file.
/// </summary>
public sealed record AndroidEmulator(string Name, string Folder, string IniFile, IReadOnlyDictionary<string, string> Config)
{
    public string DisplayName => Config.GetValueOrDefault("avd.ini.displayname") is { Length: > 0 } display ? display : MobileDevices.AvdTitle(Name);

    /// <summary>The device profile's name (<c>Pixel 7</c>), or its id.</summary>
    public string? Device => Config.GetValueOrDefault("hw.device.name");

    public string? Abi => Config.GetValueOrDefault("abi.type");

    public string? Tag => Config.GetValueOrDefault("tag.id");

    /// <summary>The API level, from the system image's folder (<c>system-images/android-35/google_apis/x86_64/</c>) or <c>target</c>.</summary>
    public int? ApiLevel => AndroidSdkPackages.ApiLevelOf((Config.GetValueOrDefault("image.sysdir.1") ?? string.Empty).Replace('/', ';').Replace('\\', ';'))
                            ?? AndroidSdkPackages.ApiLevelOf(Config.GetValueOrDefault("target") ?? string.Empty);

    /// <summary>The system image's package path (<c>system-images;android-35;google_apis;x86_64</c>), or null.</summary>
    public string? SystemImage => Config.GetValueOrDefault("image.sysdir.1") is { Length: > 0 } sysdir
        ? string.Join(';', sysdir.Replace('\\', '/').Trim('/').Split('/'))
        : null;
}

/// <summary>A hardware profile <c>avdmanager list device</c> offers for new emulators (<c>pixel_7</c>, "Pixel 7", Google).</summary>
public sealed record AndroidDeviceDefinition(string Id, string Name, string? Oem, string? Tag)
{
    /// <summary>Phones and tablets (no Wear OS, TV, automotive or desktop tag).</summary>
    public bool IsPhoneOrTablet => Tag is null or "" or "default";
}

/// <summary>The emulator settings the Device Manager edits, each a <c>config.ini</c> key.</summary>
public static class AndroidEmulatorSettings
{
    public const string Ram = "hw.ramSize";
    public const string InternalStorage = "disk.dataPartition.size";
    public const string SdCard = "sdcard.size";
    public const string Gpu = "hw.gpu.mode";
    public const string Keyboard = "hw.keyboard";
    public const string Cores = "hw.cpu.ncore";

    /// <summary>The GPU modes the emulator takes (<c>-gpu</c>).</summary>
    public static IReadOnlyList<string> GpuModes { get; } = ["auto", "host", "swiftshader_indirect", "angle_indirect", "guest"];
}

/// <summary>
/// Android Virtual Devices on disk (ADR 0104): where they are, reading and writing their <c>config.ini</c>, wiping their
/// data, deleting them, and the device profiles <c>avdmanager</c> lists. Creating one goes through <c>avdmanager</c>, which
/// copies the system image's hardware settings; the rest is the files themselves, as the classic IDE's manager does.
/// </summary>
public static class AndroidEmulators
{
    /// <summary>
    /// The folder of the AVDs' <c>.ini</c> files, as the emulator finds it: <c>ANDROID_AVD_HOME</c>, else
    /// <c>ANDROID_USER_HOME/avd</c>, else <c>ANDROID_SDK_HOME/.android/avd</c> (older tools), else <c>~/.android/avd</c>.
    /// </summary>
    public static string AvdHome(Func<string, string?> environment, string home)
    {
        if (environment("ANDROID_AVD_HOME") is { Length: > 0 } avdHome)
        {
            return avdHome;
        }

        if (environment("ANDROID_USER_HOME") is { Length: > 0 } userHome)
        {
            return Path.Combine(userHome, "avd");
        }

        if (environment("ANDROID_SDK_HOME") is { Length: > 0 } sdkHome)
        {
            return Path.Combine(sdkHome, ".android", "avd");
        }

        return Path.Combine(home, ".android", "avd");
    }

    /// <summary>The AVDs in <paramref name="avdHome"/>: each <c>name.ini</c> whose <c>path</c> (or <c>name.avd</c> next to it) has a <c>config.ini</c>.</summary>
    public static IReadOnlyList<AndroidEmulator> Read(string avdHome)
    {
        if (!Directory.Exists(avdHome))
        {
            return [];
        }

        var result = new List<AndroidEmulator>();
        foreach (var ini in Directory.EnumerateFiles(avdHome, "*.ini"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(ini);
                var pointer = ReadIni(File.ReadAllText(ini));
                var folder = pointer.GetValueOrDefault("path") is { Length: > 0 } path && Directory.Exists(path) ? path
                    : pointer.GetValueOrDefault("path.rel") is { Length: > 0 } relative && Directory.Exists(Path.Combine(Path.GetDirectoryName(avdHome)!, relative)) ? Path.Combine(Path.GetDirectoryName(avdHome)!, relative)
                    : Path.Combine(avdHome, name + ".avd");
                var config = Path.Combine(folder, "config.ini");
                if (File.Exists(config))
                {
                    result.Add(new AndroidEmulator(name, folder, ini, ReadIni(File.ReadAllText(config))));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return [.. result.OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>An <c>.ini</c> file's <c>key=value</c> lines (keys as written; later ones win).</summary>
    public static IReadOnlyDictionary<string, string> ReadIni(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var equals = line.IndexOf('=');
            if (equals <= 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }

        return values;
    }

    /// <summary>
    /// <paramref name="text"/> with <paramref name="changes"/> applied: a key's line changed in place, a new key added at
    /// the end, a null value's line removed; every other line (and the file's line ends) as it was.
    /// </summary>
    public static string UpdateIni(string text, IReadOnlyDictionary<string, string?> changes)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var done = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Count; i++)
        {
            var equals = lines[i].IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = lines[i][..equals].Trim();
            if (!changes.TryGetValue(key, out var value))
            {
                continue;
            }

            if (value is null || done.Contains(key))
            {
                lines.RemoveAt(i--);
                continue;
            }

            lines[i] = key + "=" + value;
            done.Add(key);
        }

        foreach (var (key, value) in changes)
        {
            if (value is not null && !done.Contains(key))
            {
                lines.Add(key + "=" + value);
            }
        }

        return string.Join(newline, lines) + newline;
    }

    /// <summary>Writes <paramref name="changes"/> to the emulator's <c>config.ini</c>.</summary>
    public static void Save(AndroidEmulator emulator, IReadOnlyDictionary<string, string?> changes)
    {
        var path = Path.Combine(emulator.Folder, "config.ini");
        File.WriteAllText(path, UpdateIni(File.ReadAllText(path), changes), new UTF8Encoding(false));
    }

    /// <summary>
    /// Wipes the emulator's data, as <c>emulator -wipe-data</c> does: its user data, cache and snapshots go, and the next
    /// start begins from the system image's. The emulator must be stopped.
    /// </summary>
    public static void WipeData(AndroidEmulator emulator)
    {
        foreach (var file in Directory.EnumerateFiles(emulator.Folder))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("userdata-qemu.img", StringComparison.Ordinal) || name.StartsWith("cache.img", StringComparison.Ordinal)
                || name.StartsWith("encryptionkey.img", StringComparison.Ordinal) || name is "multiinstance.lock" or "hardware-qemu.ini.lock")
            {
                File.Delete(file);
            }
        }

        var snapshots = Path.Combine(emulator.Folder, "snapshots");
        if (Directory.Exists(snapshots))
        {
            Directory.Delete(snapshots, recursive: true);
        }
    }

    /// <summary>Deletes the emulator: its folder and its <c>.ini</c>. It must be stopped.</summary>
    public static void Delete(AndroidEmulator emulator)
    {
        if (Directory.Exists(emulator.Folder))
        {
            Directory.Delete(emulator.Folder, recursive: true);
        }

        if (File.Exists(emulator.IniFile))
        {
            File.Delete(emulator.IniFile);
        }
    }

    /// <summary>Whether a running emulator holds the AVD (its lock files; the emulator removes them when it ends).</summary>
    public static bool IsLocked(AndroidEmulator emulator) =>
        File.Exists(Path.Combine(emulator.Folder, "hardware-qemu.ini.lock")) || Directory.Exists(Path.Combine(emulator.Folder, "hardware-qemu.ini.lock"));

    /// <summary>A name <c>avdmanager</c> takes: letters, digits and <c>. _ -</c> (spaces become underscores).</summary>
    public static string? ValidateName(string name, IEnumerable<AndroidEmulator> existing)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Enter a name.";
        }

        if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            return "Use only letters, digits, '.', '_' and '-'.";
        }

        return existing.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) ? $"There's already an emulator named {name}." : null;
    }

    /// <summary>A name for a new emulator of <paramref name="device"/> on <paramref name="apiLevel"/>: <c>Pixel_7_API_35</c>, then <c>_2</c>….</summary>
    public static string SuggestName(AndroidDeviceDefinition device, int? apiLevel, IEnumerable<AndroidEmulator> existing)
    {
        var stem = new string([.. device.Name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_')]).Trim('_');
        while (stem.Contains("__", StringComparison.Ordinal))
        {
            stem = stem.Replace("__", "_", StringComparison.Ordinal);
        }

        if (apiLevel is { } level)
        {
            stem += "_API_" + level.ToString(CultureInfo.InvariantCulture);
        }

        var names = existing.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = stem;
        for (var i = 2; names.Contains(name); i++)
        {
            name = stem + "_" + i.ToString(CultureInfo.InvariantCulture);
        }

        return name;
    }

    /// <summary><c>avdmanager create avd</c>'s arguments: the name, the system image and the device profile.</summary>
    public static IReadOnlyList<string> CreateArguments(string name, string systemImage, string deviceId) =>
        ["create", "avd", "--name", name, "--package", systemImage, "--device", deviceId];

    /// <summary>
    /// The device profiles in <c>avdmanager list device</c>'s output: blocks of <c>id: 12 or "pixel_7"</c>, <c>Name:</c>,
    /// <c>OEM :</c> and <c>Tag :</c>, separated by dashed lines.
    /// </summary>
    public static IReadOnlyList<AndroidDeviceDefinition> ParseDevices(string output)
    {
        var devices = new List<AndroidDeviceDefinition>();
        string? id = null, name = null, oem = null, tag = null;
        void Flush()
        {
            if (id is not null)
            {
                devices.Add(new AndroidDeviceDefinition(id, name ?? id, oem, tag));
            }

            id = name = oem = tag = null;
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("id:", StringComparison.Ordinal))
            {
                Flush();
                var quote = line.IndexOf('"');
                var last = line.LastIndexOf('"');
                id = quote >= 0 && last > quote ? line[(quote + 1)..last] : line["id:".Length..].Trim();
            }
            else if (line.StartsWith("---", StringComparison.Ordinal))
            {
                Flush();
            }
            else if (id is not null && line.IndexOf(':') is > 0 and var colon)
            {
                var value = line[(colon + 1)..].Trim();
                switch (line[..colon].Trim())
                {
                    case "Name":
                        name = value;
                        break;
                    case "OEM":
                        oem = value;
                        break;
                    case "Tag":
                        tag = value;
                        break;
                }
            }
        }

        Flush();
        return devices;
    }

    /// <summary>A size as <c>config.ini</c> writes it (<c>2048M</c>, <c>6G</c>, <c>6442450944</c>), in megabytes, or null.</summary>
    public static long? SizeInMegabytes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim().ToUpperInvariant();
        if (text.Length > 1 && text[^1] == 'B' && char.IsAsciiLetter(text[^2]))
        {
            text = text[..^1]; // 2048MB
        }

        var unit = char.IsAsciiLetter(text[^1]) ? text[^1] : ' ';
        if (!long.TryParse(unit == ' ' ? text : text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        return unit switch
        {
            'K' => number / 1024,
            'M' => number,
            'G' => number * 1024,
            'T' => number * 1024 * 1024,
            ' ' => number / (1024 * 1024),
            _ => null,
        };
    }
}
