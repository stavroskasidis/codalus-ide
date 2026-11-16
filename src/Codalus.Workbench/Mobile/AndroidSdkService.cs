using System.Runtime.InteropServices;
using System.Text;

namespace Codalus.Workbench.Mobile;

/// <summary>
/// The Android SDK's own tools, for the Android SDK Manager, the Android Device Manager, the adb Command Prompt and the
/// Device Log (ADR 0104): the package list (<c>sdkmanager --list</c>), the licenses an install needs, the install and
/// uninstall commands, the emulators on disk and the device profiles for new ones (<c>avdmanager</c>). Commands that
/// change the SDK are returned, not run: the shell runs them, elevated when the SDK's folder isn't writable.
/// </summary>
public sealed class AndroidSdkService(MobileService mobile, ToolProcessRunner run, Func<string, string?> environment, string home)
{
    /// <summary>How many answers an install's input holds: one per license prompt, more than any install asks.</summary>
    private const int Answers = 32;

    public MobileService Mobile => mobile;

    /// <summary>The SDK and JDK the IDE uses.</summary>
    public AndroidToolchain.Locations Locations() => mobile.Android();

    /// <summary><c>sdkmanager</c> and <c>avdmanager</c>, or null when the SDK has none (or there's no SDK).</summary>
    public (string SdkManager, string AvdManager)? Tools() => Locations().Sdk is { } sdk ? mobile.Toolchain.CommandLineTools(sdk) : null;

    /// <summary>Whether this process can change the SDK's folder (the classic IDE's, under Program Files, it can't).</summary>
    public bool IsSdkWritable() => Locations().Sdk is not { } sdk || Elevation.CanWrite(sdk);

    /// <summary>The folder of the emulators' <c>.ini</c> files.</summary>
    public string AvdHome => AndroidEmulators.AvdHome(environment, home);

    /// <summary>
    /// The environment of the SDK's tools: <c>ANDROID_HOME</c> and <c>ANDROID_SDK_ROOT</c> (the SDK), <c>JAVA_HOME</c>
    /// (sdkmanager and avdmanager are Java programs), and with <paramref name="path"/> the current <c>PATH</c> with the
    /// SDK's tool folders in front (the adb Command Prompt).
    /// </summary>
    public Dictionary<string, string> ToolEnvironment(bool path = false)
    {
        var locations = Locations();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (locations.Sdk is { } sdk)
        {
            result["ANDROID_HOME"] = sdk;
            result["ANDROID_SDK_ROOT"] = sdk;
        }

        if (locations.Jdk is { } jdk)
        {
            result["JAVA_HOME"] = jdk;
        }

        if (path)
        {
            var folders = new List<string>();
            if (locations.Adb is { } adb)
            {
                folders.Add(Path.GetDirectoryName(adb)!);
            }

            if (locations.Emulator is { } emulator)
            {
                folders.Add(Path.GetDirectoryName(emulator)!);
            }

            if (Tools() is { } tools)
            {
                folders.Add(Path.GetDirectoryName(tools.SdkManager)!);
            }

            if (locations.Jdk is { } java)
            {
                folders.Add(Path.Combine(java, "bin"));
            }

            var key = OperatingSystem.IsWindows() ? "Path" : "PATH";
            result[key] = string.Join(Path.PathSeparator, folders.Append(environment("PATH") ?? string.Empty).Where(f => f.Length > 0));
        }

        return result;
    }

    private ToolCommand SdkManager(IReadOnlyList<string> arguments, string? input = null)
    {
        var tools = Tools() ?? throw new InvalidOperationException("The Android SDK's command-line tools (sdkmanager) aren't installed.");
        return new ToolCommand(tools.SdkManager, arguments) { Environment = ToolEnvironment(), Input = input };
    }

    private string SdkRootArgument => "--sdk_root=" + (Locations().Sdk ?? throw new InvalidOperationException("No Android SDK was found."));

    // ------------------------------------------------------------------ packages

    /// <summary>Every package installed or available, with updates (<c>sdkmanager --list</c>).</summary>
    public async Task<IReadOnlyList<SdkPackage>> ListPackagesAsync(CancellationToken cancellationToken)
    {
        var (code, output) = await run.CaptureAsync(SdkManager([SdkRootArgument, "--list"]), cancellationToken).ConfigureAwait(false);
        var packages = AndroidSdkPackages.ParseList(output);
        if (code != 0 && packages.Count == 0)
        {
            throw new InvalidOperationException("sdkmanager --list failed: " + LastLines(output));
        }

        return packages;
    }

    /// <summary>
    /// The licenses installing <paramref name="packages"/> needs that this SDK hasn't accepted: <c>sdkmanager</c> asked in
    /// an empty SDK folder (so it asks about every license the packages and their dependencies need, and installs
    /// nothing, every answer being no), less the ones in the SDK's <c>licenses</c> folder.
    /// </summary>
    public async Task<IReadOnlyList<SdkLicense>> PendingLicensesAsync(IReadOnlyList<string> packages, CancellationToken cancellationToken)
    {
        var probe = Path.Combine(Path.GetTempPath(), "codalus-sdk-licenses-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probe);
        try
        {
            var command = SdkManager(["--sdk_root=" + probe, "--install", .. packages], Repeat("n"));
            var (_, output) = await run.CaptureAsync(command, cancellationToken).ConfigureAwait(false);
            var accepted = Locations().Sdk is { } sdk ? AndroidSdkPackages.AcceptedLicenses(sdk) : new HashSet<string>();
            return [.. AndroidSdkPackages.ParseLicenses(output).Where(l => !accepted.Contains(l.Id))];
        }
        finally
        {
            try
            {
                Directory.Delete(probe, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// <c>sdkmanager --install</c> for <paramref name="packages"/> (the latest version of each: an installed one is
    /// updated), answering yes to its license prompts: the caller shows them and asks first.
    /// </summary>
    public ToolCommand InstallCommand(IReadOnlyList<string> packages) => SdkManager([SdkRootArgument, "--install", .. packages], Repeat("y"));

    /// <summary><c>sdkmanager --uninstall</c> for <paramref name="packages"/>.</summary>
    public ToolCommand UninstallCommand(IReadOnlyList<string> packages) => SdkManager([SdkRootArgument, "--uninstall", .. packages]);

    /// <summary><c>sdkmanager --update</c>: every installed package to its latest version, answering yes to its licenses.</summary>
    public ToolCommand UpdateAllCommand() => SdkManager([SdkRootArgument, "--update"], Repeat("y"));

    private static string Repeat(string answer)
    {
        var input = new StringBuilder();
        for (var i = 0; i < Answers; i++)
        {
            input.Append(answer).Append('\n');
        }

        return input.ToString();
    }

    // ------------------------------------------------------------------ emulators

    public IReadOnlyList<AndroidEmulator> Emulators() => AndroidEmulators.Read(AvdHome);

    /// <summary>The device profiles for new emulators (<c>avdmanager list device</c>), phones and tablets first.</summary>
    public async Task<IReadOnlyList<AndroidDeviceDefinition>> DeviceDefinitionsAsync(CancellationToken cancellationToken)
    {
        var tools = Tools() ?? throw new InvalidOperationException("The Android SDK's command-line tools (avdmanager) aren't installed.");
        var (code, output) = await run.CaptureAsync(new ToolCommand(tools.AvdManager, ["list", "device"]) { Environment = ToolEnvironment() }, cancellationToken).ConfigureAwait(false);
        var devices = AndroidEmulators.ParseDevices(output);
        if (code != 0 && devices.Count == 0)
        {
            throw new InvalidOperationException("avdmanager list device failed: " + LastLines(output));
        }

        return [.. devices.OrderByDescending(d => d.IsPhoneOrTablet).ThenByDescending(d => d.Oem == "Google").ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// Creates an emulator (<c>avdmanager create avd</c>, answering no to its custom hardware profile question); its exit
    /// code and output.
    /// </summary>
    public Task<(int ExitCode, string Output)> CreateEmulatorAsync(string name, string systemImage, string deviceId, CancellationToken cancellationToken)
    {
        var tools = Tools() ?? throw new InvalidOperationException("The Android SDK's command-line tools (avdmanager) aren't installed.");
        return run.CaptureAsync(new ToolCommand(tools.AvdManager, AndroidEmulators.CreateArguments(name, systemImage, deviceId)) { Environment = ToolEnvironment(), Input = "no\n" }, cancellationToken);
    }

    /// <summary>The running device (a serial) that is <paramref name="emulator"/>, from the device list, or null.</summary>
    public MobileDevice? RunningDevice(AndroidEmulator emulator) =>
        mobile.Devices.FirstOrDefault(d => d.Kind == MobileDeviceKind.Emulator && d.IsRunning && d.Avd == emulator.Name);

    /// <summary>Ends a running emulator (<c>adb -s serial emu kill</c>).</summary>
    public async Task StopAsync(MobileDevice running, CancellationToken cancellationToken)
    {
        var adb = Locations().Adb ?? throw new InvalidOperationException("adb wasn't found in the Android SDK.");
        await run.CaptureAsync(new ToolCommand(adb, ["-s", running.Id, "emu", "kill"]) { Environment = ToolEnvironment() }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The system images installed or available for this machine's ABI, newest first, Google Play before Google APIs.</summary>
    public static IReadOnlyList<SdkPackage> SystemImages(IReadOnlyList<SdkPackage> packages, Architecture architecture)
    {
        var abi = AndroidSdkPackages.HostAbi(architecture);
        return [.. packages
            .Where(p => AndroidSdkPackages.SystemImageOf(p.Path) is { } image && image.Abi == abi && !image.Tag.Contains("wear", StringComparison.Ordinal)
                        && !image.Tag.Contains("tv", StringComparison.Ordinal) && !image.Tag.Contains("automotive", StringComparison.Ordinal) && !image.Tag.Contains("desktop", StringComparison.Ordinal))
            .OrderByDescending(p => p.Installed)
            .ThenByDescending(p => p.ApiLevel ?? 0)
            .ThenBy(p => AndroidSdkPackages.SystemImageOf(p.Path)!.Value.Tag switch { "google_apis_playstore" => 0, "google_apis" => 1, "default" => 2, _ => 3 })];
    }

    private static string LastLines(string output) =>
        string.Join(' ', output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('[')).TakeLast(3));
}
