using System.Text;
using Codalus.Platform;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Mobile;
using Codalus.Workbench.Setup;

namespace Codalus.Workbench.Tests;

/// <summary>
/// .NET MAUI's devices, workloads and runs (M20a, ADR 0064) with recorded tool output and stand-in processes: nothing
/// starts an emulator or installs anything.
/// </summary>
public sealed class MobileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("codalus-mobile-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ProjectInfo Project(string path, bool maui, params string[] frameworks) =>
        new(path, Path.GetFileNameWithoutExtension(path), ProjectStates.Loaded, null, frameworks, "Exe", [], [], [], true) { UsesMaui = maui };

    [Fact]
    public void Frameworks_map_to_platforms_and_runs_go_where_the_host_can_run_them()
    {
        Assert.Equal(MobilePlatform.Android, MobileTargets.PlatformOf("net10.0-android"));
        Assert.Equal(MobilePlatform.Android, MobileTargets.PlatformOf("net10.0-android36.0"));
        Assert.Equal(MobilePlatform.iOS, MobileTargets.PlatformOf("net10.0-ios18.0"));
        Assert.Equal(MobilePlatform.MacCatalyst, MobileTargets.PlatformOf("net10.0-maccatalyst"));
        Assert.Equal(MobilePlatform.Windows, MobileTargets.PlatformOf("net10.0-windows10.0.19041.0"));
        Assert.Equal(MobilePlatform.None, MobileTargets.PlatformOf("net10.0"));

        var app = Project("/src/TodoApp/TodoApp.csproj", maui: true, "net10.0-android", "net10.0-ios", "net10.0-maccatalyst");
        Assert.True(MobileTargets.IsMobile(app));
        Assert.False(MobileTargets.IsMobile(Project("/src/Lib/Lib.csproj", maui: false, "net10.0")));

        // Linux runs only Android; the Mac runs the active framework when it can.
        Assert.Equal("net10.0-android", MobileTargets.RunFramework(app, "net10.0-ios", PlatformKind.Linux));
        Assert.Equal("net10.0-ios", MobileTargets.RunFramework(app, "net10.0-ios", PlatformKind.MacOS));
        Assert.Null(MobileTargets.RunFramework(Project("/w/W.csproj", maui: true, "net10.0-windows10.0.19041.0"), null, PlatformKind.Windows));

        // Workloads: MAUI's for a MAUI project, only those of frameworks this machine builds; "maui" covers them all.
        Assert.Equal(["maui-android"], MobileTargets.MissingWorkloads(app, [], PlatformKind.Windows));
        Assert.Equal(["maui-android", "maui-ios", "maui-maccatalyst"], MobileTargets.MissingWorkloads(app, [], PlatformKind.MacOS));
        Assert.Equal(["maui-ios"], MobileTargets.MissingWorkloads(app, ["maui-android", "maui-maccatalyst"], PlatformKind.MacOS));
        Assert.Empty(MobileTargets.MissingWorkloads(app, ["maui"], PlatformKind.MacOS));
        Assert.Equal(["android"], MobileTargets.MissingWorkloads(Project("/a/A.csproj", maui: false, "net10.0-android"), [], PlatformKind.Linux));
    }

    [Fact]
    public void The_run_command_is_the_platform_sdks_run_target_with_the_device()
    {
        var emulator = new MobileDevice("emulator-5554", "Pixel 8", MobilePlatform.Android, MobileDeviceKind.Emulator, true);
        var android = MobileTargets.RunArguments("/src/App.csproj", "net10.0-android", emulator, "Debug", new AndroidToolchain.Locations("/sdk", "/jdk", "/sdk/platform-tools/adb", null));
        Assert.Equal(["build", "/src/App.csproj", "-t:Run", "-f", "net10.0-android", "-c", "Debug", "-p:AdbTarget=-s emulator-5554", "-p:AndroidSdkDirectory=/sdk", "-p:JavaSdkDirectory=/jdk"], android);

        var simulator = new MobileDevice("8A1F", "iPhone 16", MobilePlatform.iOS, MobileDeviceKind.Simulator, false);
        Assert.Contains("-p:_DeviceName=:v2:udid=8A1F", MobileTargets.RunArguments("/src/App.csproj", "net10.0-ios", simulator, null));
        var phone = new MobileDevice("00008110-001", "Jane's iPhone", MobilePlatform.iOS, MobileDeviceKind.Device, true);
        var device = MobileTargets.RunArguments("/src/App.csproj", "net10.0-ios", phone, "Debug");
        Assert.Contains("-p:RuntimeIdentifier=ios-arm64", device);
        Assert.Contains("-p:_DeviceName=00008110-001", device);
        Assert.Equal(["build", "/src/App.csproj", "-t:Run", "-f", "net10.0-maccatalyst"], MobileTargets.RunArguments("/src/App.csproj", "net10.0-maccatalyst", null, null));

        Assert.Equal(
            ["build", "/src/App.csproj", "-t:InstallAndroidDependencies", "-f", "net10.0-android", "-p:AndroidSdkDirectory=/home/u/Android/Sdk", "-p:JavaSdkDirectory=/home/u/Android/jdk", "-p:AcceptAndroidSDKLicenses=True"],
            MobileTargets.InstallAndroidDependenciesArguments("/src/App.csproj", "net10.0-android", "/home/u/Android/Sdk", "/home/u/Android/jdk"));
    }

    [Fact]
    public void Debugging_adds_the_platforms_debugger_properties_and_the_adapters_mode()
    {
        var emulator = new MobileDevice("emulator-5554", "Pixel 8", MobilePlatform.Android, MobileDeviceKind.Emulator, true);
        var android = MobileTargets.DebugArguments("net10.0-android", emulator, 50123)!.Value;
        Assert.Equal(["-p:AndroidAttachDebugger=true", "-p:AndroidSdbTargetPort=50123", "-p:AndroidSdbHostPort=50123"], android.RunArguments);
        Assert.Equal((Codalus.Debugging.MonoDebugArguments.Connect, "127.0.0.1", 50123), (android.Adapter.Mode, android.Adapter.Address, android.Adapter.Port));

        var simulator = MobileTargets.DebugArguments("net10.0-ios", new MobileDevice("8A1F", "iPhone 16", MobilePlatform.iOS, MobileDeviceKind.Simulator, true), 50124)!.Value;
        Assert.Equal(Codalus.Debugging.MonoDebugArguments.Listen, simulator.Adapter.Mode);
        Assert.Contains(simulator.RunArguments, a => a.Contains("__XAMARIN_DEBUG_PORT__=50124", StringComparison.Ordinal));
        Assert.Equal(Codalus.Debugging.MonoDebugArguments.Listen, MobileTargets.DebugArguments("net10.0-maccatalyst", null, 50125)!.Value.Adapter.Mode);

        // An iPhone needs mlaunch's tunnel: not yet.
        Assert.Null(MobileTargets.DebugArguments("net10.0-ios", new MobileDevice("0000", "Phone", MobilePlatform.iOS, MobileDeviceKind.Device, true), 50126));
    }

    [Fact]
    public void Adb_emulator_simctl_and_devicectl_output_becomes_devices()
    {
        var adb = MobileDevices.ParseAdbDevices("""
            * daemon started successfully
            List of devices attached
            emulator-5554          device product:sdk_gphone64_x86_64 model:sdk_gphone64_x86_64 device:emu64xa transport_id:1
            R58M123ABC             unauthorized usb:1-1 transport_id:2
            0a1b2c3d               device usb:1-2 product:husky model:Pixel_8_Pro device:husky transport_id:3

            """);
        Assert.Equal(3, adb.Count);
        Assert.Equal((MobileDeviceKind.Emulator, true), (adb[0].Kind, adb[0].IsUsable));
        Assert.Contains("Allow USB debugging", adb[1].Problem, StringComparison.Ordinal);
        Assert.Equal(("Pixel 8 Pro", MobileDeviceKind.Device), (adb[2].Name, adb[2].Kind));

        var avds = MobileDevices.ParseAvds("INFO    | Storing crashdata in: /tmp/android-u/emu-crash.db\nPixel_8_API_35\nTablet_API_34\n");
        Assert.Equal(["Pixel_8_API_35", "Tablet_API_34"], avds);
        Assert.Equal("Pixel_8_API_35", MobileDevices.ParseAvdName("Pixel_8_API_35\r\nOK\r\n"));
        var stopped = MobileDevices.StoppedEmulators(avds, new Dictionary<string, string> { ["emulator-5554"] = "Pixel_8_API_35" });
        Assert.Equal(("avd:Tablet_API_34", "Tablet API 34", false), (stopped.Single().Id, stopped.Single().Name, stopped.Single().IsRunning));

        var simulators = MobileDevices.ParseSimulators("""
            {"devices":{
              "com.apple.CoreSimulator.SimRuntime.iOS-17-5":[{"udid":"A1","isAvailable":true,"state":"Shutdown","name":"iPhone 15"}],
              "com.apple.CoreSimulator.SimRuntime.iOS-18-2":[{"udid":"B2","isAvailable":true,"state":"Booted","name":"iPhone 16 Pro"},{"udid":"C3","isAvailable":false,"state":"Shutdown","name":"Broken"}],
              "com.apple.CoreSimulator.SimRuntime.watchOS-11-0":[{"udid":"W1","isAvailable":true,"state":"Shutdown","name":"Apple Watch"}]}}
            """);
        Assert.Equal(["iPhone 16 Pro", "iPhone 15"], simulators.Select(s => s.Name));
        Assert.Equal(("18.2", true), (simulators[0].OsVersion, simulators[0].IsRunning));
        Assert.Equal("iPhone 16 Pro (iOS 18.2, simulator)", simulators[0].Label);

        var phones = MobileDevices.ParseAppleDevices("""
            {"result":{"devices":[
              {"identifier":"X","deviceProperties":{"name":"Jane's iPhone","osVersionNumber":"18.1"},"hardwareProperties":{"udid":"00008110-001","platform":"iOS","deviceType":"iPhone"},"connectionProperties":{"pairingState":"paired"}},
              {"identifier":"Y","deviceProperties":{"name":"Living Room"},"hardwareProperties":{"udid":"TV1","platform":"tvOS"}},
              {"identifier":"Z","deviceProperties":{"name":"New iPad"},"hardwareProperties":{"udid":"00008027-002","platform":"iOS"},"connectionProperties":{"pairingState":"unpaired"}}]}}
            """);
        Assert.Equal(["Jane's iPhone", "New iPad"], phones.Select(p => p.Name));
        Assert.True(phones[0].IsUsable);
        Assert.Contains("Pair the device", phones[1].Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Signing_identities_and_provisioning_profiles_are_read()
    {
        var identities = AppleSigning.ParseIdentities("""
              1) 0123456789ABCDEF0123456789ABCDEF01234567 "Apple Development: Jane Doe (ABCDE12345)"
              2) FEDCBA9876543210FEDCBA9876543210FEDCBA98 "Apple Distribution: Example Ltd (ABCDE12345)"
                 2 valid identities found
            """);
        Assert.Equal(2, identities.Count);
        Assert.Equal(("0123456789ABCDEF0123456789ABCDEF01234567", "ABCDE12345"), (identities[0].Hash, identities[0].Team));

        // A .mobileprovision is a CMS envelope around a plist: bytes before and after it are skipped.
        var plist = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>Name</key><string>Todo Development</string>
              <key>TeamIdentifier</key><array><string>ABCDE12345</string></array>
              <key>ExpirationDate</key><date>2027-01-31T10:00:00Z</date>
              <key>Entitlements</key><dict><key>application-identifier</key><string>ABCDE12345.dev.codalus.*</string></dict>
              <key>UUID</key><string>5D2C-UUID</string>
            </dict></plist>
            """;
        var bytes = new byte[] { 0x30, 0x82, 0x10, 0x00 }.Concat(Encoding.UTF8.GetBytes(plist)).Concat(new byte[] { 0xA0, 0x82, 0x00 }).ToArray();
        var profile = AppleSigning.ParseProfile(bytes, "/p/x.mobileprovision")!;
        Assert.Equal(("5D2C-UUID", "Todo Development", "ABCDE12345"), (profile.Uuid, profile.Name, profile.Team));
        Assert.True(profile.Covers("dev.codalus.samples.todoapp"));
        Assert.False(profile.Covers("com.example.other"));
        Assert.False(profile.IsExpired(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero)));
        Assert.True(profile.IsExpired(new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Null(AppleSigning.ParseProfile(Encoding.UTF8.GetBytes("not a profile"), "/p/y"));
    }

    [Fact]
    public void The_android_sdk_and_jdk_are_found_from_settings_the_environment_and_the_usual_folders()
    {
        var home = Path.Combine(_dir, "home");
        var sdk = Path.Combine(home, "Android", "Sdk");
        Directory.CreateDirectory(Path.Combine(sdk, "platform-tools"));
        Directory.CreateDirectory(Path.Combine(sdk, "platforms", "android-35"));
        Directory.CreateDirectory(Path.Combine(sdk, "platforms", "android-36"));
        File.WriteAllText(Path.Combine(sdk, "platform-tools", "adb"), "");
        var jvm = Path.Combine(_dir, "jvm");
        foreach (var jdk in new[] { "java-17-openjdk-amd64", "java-21-openjdk-amd64" })
        {
            Directory.CreateDirectory(Path.Combine(jvm, jdk, "bin"));
            File.WriteAllText(Path.Combine(jvm, jdk, "bin", "java"), "");
            File.WriteAllText(Path.Combine(jvm, jdk, "bin", "javac"), "");
        }

        // No settings, no ANDROID_HOME: Android Studio's folder. JAVA_HOME wins over the newest JDK.
        var toolchain = new AndroidToolchain(PlatformKind.Linux, _ => null, home, subdirectories: folder => folder == "/usr/lib/jvm" ? Directory.EnumerateDirectories(jvm) : Directory.Exists(folder) ? Directory.EnumerateDirectories(folder) : []);
        var found = toolchain.Find();
        Assert.Equal(sdk, found.Sdk);
        Assert.Equal(Path.Combine(jvm, "java-21-openjdk-amd64"), found.Jdk);
        Assert.Equal(Path.Combine(sdk, "platform-tools", "adb"), found.Adb);
        Assert.Null(found.Emulator);
        Assert.True(found.IsComplete);

        var status = toolchain.Status(found);
        Assert.Equal(("android-36", ToolState.Found), (status[0].Version, status[0].State));
        Assert.Equal("21", status[1].Version);

        var withJavaHome = new AndroidToolchain(PlatformKind.Linux, n => n == "JAVA_HOME" ? Path.Combine(jvm, "java-17-openjdk-amd64") : null, home);
        Assert.Equal(Path.Combine(jvm, "java-17-openjdk-amd64"), withJavaHome.Find().Jdk);

        // A setting that isn't an SDK is skipped; nothing at all is missing, with where it would be installed.
        var none = new AndroidToolchain(PlatformKind.Linux, _ => null, Path.Combine(_dir, "nobody"), subdirectories: _ => []);
        var missing = none.Find(sdkSetting: Path.Combine(_dir, "not-an-sdk"));
        Assert.Equal((null, null), (missing.Sdk, missing.Jdk));
        Assert.Equal(ToolState.Missing, none.Status(missing)[0].State);
        Assert.Equal(Path.Combine(_dir, "nobody", "Android", "Sdk"), none.DefaultSdkDirectory);
        Assert.Equal(Path.Combine(_dir, "nobody", "Android", "jdk"), none.DefaultJdkDirectory);

        Assert.Equal(Path.Combine(@"C:\Users\u\AppData\Local", "Android", "Sdk"), new AndroidToolchain(PlatformKind.Windows, n => n == "LOCALAPPDATA" ? @"C:\Users\u\AppData\Local" : null, @"C:\Users\u").DefaultSdkDirectory);
        Assert.Equal(Path.Combine("/Users/u", "Library", "Android", "sdk"), new AndroidToolchain(PlatformKind.MacOS, _ => null, "/Users/u").DefaultSdkDirectory);
        Assert.Equal(21, AndroidToolchain.JdkMajor("jdk-21.0.4+7"));
        Assert.Equal(17, AndroidToolchain.JdkMajor("microsoft-17.jdk"));
    }

    [Fact]
    public async Task The_service_lists_devices_keeps_choices_and_starts_an_emulator_until_it_has_booted()
    {
        var home = Path.Combine(_dir, "home");
        var sdk = Path.Combine(home, "Android", "Sdk");
        var adb = Path.Combine(sdk, "platform-tools", "adb");
        var emulatorExe = Path.Combine(sdk, "emulator", "emulator");
        Directory.CreateDirectory(Path.GetDirectoryName(adb)!);
        Directory.CreateDirectory(Path.GetDirectoryName(emulatorExe)!);
        File.WriteAllText(adb, "");
        File.WriteAllText(emulatorExe, "");

        var started = false;
        var polls = 0;
        var calls = new List<string>();
        Task<(int, string)> Run(string program, IReadOnlyList<string> arguments, string? directory, CancellationToken _)
        {
            var line = Path.GetFileName(program) + " " + string.Join(' ', arguments);
            calls.Add(line);
            return Task.FromResult<(int, string)>(line switch
            {
                "adb devices -l" when started && ++polls >= 2 => (0, "List of devices attached\nemulator-5554 device model:sdk_gphone64 transport_id:1\n"),
                "adb devices -l" when started => (0, "List of devices attached\nemulator-5554 offline transport_id:1\n"),
                "adb devices -l" => (0, "List of devices attached\n\n"),
                "adb -s emulator-5554 emu avd name" => (0, "Pixel_8_API_35\nOK\n"),
                "adb -s emulator-5554 shell getprop sys.boot_completed" => (0, "1\n"),
                "emulator -list-avds" => (0, "Pixel_8_API_35\n"),
                "dotnet workload list" => (0, "\nInstalled Workload Id      Manifest Version       Installation Source\n---------------------------------------------------------------------\nmaui-android               10.0.110/10.0.100      SDK 10.0.400\n\nUse `dotnet workload search` to find additional workloads to install.\n"),
                _ => (1, "unexpected"),
            });
        }

        var launched = new List<string>();
        var service = new MobileService(PlatformKind.Linux, new AndroidToolchain(PlatformKind.Linux, _ => null, home), Run, (program, arguments, environment) =>
        {
            launched.Add(Path.GetFileName(program) + " " + string.Join(' ', arguments));
            Assert.Equal(sdk, environment!["ANDROID_HOME"]);
            started = true;
            return true;
        });

        var changed = 0;
        service.Changed += (_, _) => changed++;
        var devices = await service.RefreshAsync(Ct);
        var avd = Assert.Single(devices);
        Assert.Equal(("avd:Pixel_8_API_35", false), (avd.Id, avd.IsRunning));
        Assert.Same(avd, service.DeviceFor(MobilePlatform.Android));
        service.Select(avd);
        Assert.Equal(2, changed);

        var running = await service.StartEmulatorAsync(avd, Ct, poll: TimeSpan.FromMilliseconds(10));
        Assert.Equal(["emulator -avd Pixel_8_API_35"], launched);
        Assert.Equal(("emulator-5554", "Pixel 8 API 35", true), (running!.Id, running.Name, running.IsRunning));

        // The choice was the AVD, so it holds now that the emulator has a serial.
        Assert.Equal("emulator-5554", service.DeviceFor(MobilePlatform.Android)!.Id);
        Assert.Equal("avd:Pixel_8_API_35", service.Selections["Android"]);

        Assert.Equal(["maui-android"], await service.InstalledWorkloadsAsync("dotnet", null, Ct));

        var project = Project(Path.Combine(_dir, "TodoApp", "TodoApp.csproj"), maui: true, "net10.0-android");
        var plan = service.RunPlan("dotnet", project, "net10.0-android", running, "Debug");
        Assert.Equal("dotnet", plan.FileName);
        Assert.Contains("-p:AdbTarget=-s emulator-5554", plan.ArgumentList!);
        Assert.Equal(Path.Combine(_dir, "TodoApp"), plan.WorkingDirectory);
        Assert.Equal(sdk, plan.Environment["ANDROID_HOME"]);

        var debugPlan = service.DebugPlan("dotnet", project, "net10.0-android", running, "Debug", 50200)!.Value;
        Assert.Contains("-p:AdbTarget=-s emulator-5554", debugPlan.Run.ArgumentList!);
        Assert.Contains("-p:AndroidAttachDebugger=true", debugPlan.Run.ArgumentList!);
        Assert.Equal(50200, debugPlan.Adapter.Port);

        var install = service.InstallAndroidDependenciesPlan("dotnet", project);
        Assert.Contains("-t:InstallAndroidDependencies", install.ArgumentList!);
        Assert.Contains("-p:AndroidSdkDirectory=" + sdk, install.ArgumentList!);
    }
}
