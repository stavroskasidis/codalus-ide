using Codalus.Platform;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Mobile;

namespace Codalus.Workbench.Tests;

/// <summary>M20a: the Android SDK and JDK found where the tools put them, devices from adb's own output, mobile projects.</summary>
public sealed class MobileTests
{
    [Fact]
    public void Android_home_wins_then_the_installers_folders()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine("C:", "sdk", "platform-tools"),
            Path.Combine(@"C:\Program Files (x86)", "Android", "android-sdk", "platform-tools"),
            Path.Combine(@"C:\Program Files (x86)", "Android", "openjdk", "jdk-17.0.14", "bin"),
            Path.Combine(@"C:\Program Files (x86)", "Android", "openjdk", "jdk-21.0.8", "bin"),
        };
        IEnumerable<string> Children(string parent) => dirs
            .Select(d => Path.GetDirectoryName(d)!)
            .Where(d => string.Equals(Path.GetDirectoryName(d), parent, StringComparison.OrdinalIgnoreCase)).Distinct();
        var env = new Dictionary<string, string> { ["ProgramFiles(x86)"] = @"C:\Program Files (x86)", ["USERPROFILE"] = @"C:\Users\me" };

        var found = AndroidSdk.Locate(PlatformKind.Windows, k => env.GetValueOrDefault(k), dirs.Contains, Children);
        Assert.Equal(Path.Combine(@"C:\Program Files (x86)", "Android", "android-sdk"), found.SdkPath); // Visual Studio's installer
        Assert.Equal(Path.Combine(@"C:\Program Files (x86)", "Android", "openjdk", "jdk-21.0.8"), found.JdkPath); // the newest JDK

        env["ANDROID_HOME"] = Path.Combine("C:", "sdk");
        Assert.Equal(Path.Combine("C:", "sdk"), AndroidSdk.Locate(PlatformKind.Windows, k => env.GetValueOrDefault(k), dirs.Contains, Children).SdkPath);
        Assert.Equal(new AndroidSdk(null, null), AndroidSdk.Locate(PlatformKind.Linux, _ => null, _ => false, _ => []));
        Assert.Equal(Path.Combine("C:", "sdk"), new AndroidSdk(Path.Combine("C:", "sdk"), "jdk").BuildProperties()["AndroidSdkDirectory"]);
    }

    [Fact]
    public void Adb_and_the_emulator_say_which_devices_there_are()
    {
        var devices = AndroidDevices.ParseDevices("""
            * daemon not running; starting now at tcp:5037
            List of devices attached
            emulator-5554          device product:sdk_gphone64_x86_64 model:sdk_gphone64_x86_64 device:emu64xa transport_id:1
            R58M12ABCDE            unauthorized usb:1-1 transport_id:2

            """);
        Assert.Equal([("emulator-5554", "device", "sdk_gphone64_x86_64"), ("R58M12ABCDE", "unauthorized", null)], devices);
        Assert.Equal(["pixel_7_-_api_35", "Pixel_9"], AndroidDevices.ParseAvds("INFO    | Storing crashdata in: /tmp\npixel_7_-_api_35\r\nPixel_9\n"));
    }

    [Fact]
    public async Task Running_emulators_are_matched_to_their_virtual_devices()
    {
        var calls = new List<string>();
        Task<(int, string)> Run(string program, IReadOnlyList<string> arguments, string? directory, CancellationToken cancellationToken)
        {
            var line = string.Join(' ', arguments);
            calls.Add(Path.GetFileNameWithoutExtension(program) + " " + line);
            return Task.FromResult(line switch
            {
                "devices -l" => (0, "List of devices attached\nemulator-5554 device model:sdk_gphone64\nR58M unauthorized usb:1-1\n"),
                "-s emulator-5554 emu avd name" => (0, "pixel_7_-_api_35\r\nOK\r\n"),
                "-list-avds" => (0, "pixel_7_-_api_35\npixel_9_pro_xl_-_api_36\n"),
                _ => (1, string.Empty),
            });
        }

        var sdk = new AndroidSdk(Path.Combine("sdk"), null);
        var android = new AndroidDevices(sdk, PlatformKind.Linux, Run, _ => true, _ => null);
        var devices = await android.ListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, devices.Count);
        Assert.Equal(("android:emulator-5554", MobileDeviceKind.Emulator, "pixel_7_-_api_35", true), (devices[0].Id, devices[0].Kind, devices[0].Avd, devices[0].IsRunning));
        Assert.Equal(("android:R58M", MobileDeviceKind.Device, "unauthorized"), (devices[1].Id, devices[1].Kind, devices[1].Problem));
        Assert.Equal(("avd:pixel_9_pro_xl_-_api_36", false, "pixel 9 pro xl - api 36"), (devices[2].Id, devices[2].IsRunning, devices[2].Label)); // the running one isn't listed twice
        Assert.DoesNotContain(calls, c => c.Contains("R58M emu", StringComparison.Ordinal)); // only emulators are asked their name
    }

    [Fact]
    public void Mobile_apps_are_executables_with_a_mobile_framework()
    {
        static ProjectInfo Project(string outputType, params string[] frameworks) =>
            new("/a/App.csproj", "App", "loaded", null, frameworks, outputType, [], [], [], Restored: true);

        Assert.Equal("android", MobileProjects.PlatformOf("net10.0-android"));
        Assert.Equal("ios", MobileProjects.PlatformOf("net10.0-ios26.0"));
        Assert.Equal("maccatalyst", MobileProjects.PlatformOf("net10.0-maccatalyst"));
        Assert.Null(MobileProjects.PlatformOf("net10.0-windows10.0.19041.0"));
        Assert.Null(MobileProjects.PlatformOf("net10.0"));

        var app = Project("Exe", "net10.0-android", "net10.0-ios", "net10.0-maccatalyst");
        Assert.True(MobileProjects.IsMobileApp(app));
        Assert.Equal("net10.0-ios", MobileProjects.FrameworkFor(app, "ios"));
        Assert.False(MobileProjects.IsMobileApp(Project("Library", "net10.0-android")));
        Assert.False(MobileProjects.IsMobileApp(Project("Exe", "net10.0")));
    }
}
