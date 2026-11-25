using System.Runtime.InteropServices;
using Codalus.Platform;
using Codalus.Workbench.Mobile;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The Android SDK's own tools behind the Android SDK Manager, the Android Device Manager and the Device Log (ADR 0104),
/// with the tools' output as they print it and stand-in processes: nothing downloads, installs or starts an emulator.
/// </summary>
public sealed class AndroidToolsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("codalus-android-tools-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // sdkmanager --list (cmdline-tools 16), its progress bar rewriting the first line.
    private const string SdkList = """
        [=======                                ] 17% Fetch remote repository...       \r[=======================================] 100% Computing updates...
        Installed packages:
          Path                 | Version | Description                    | Location
          -------              | ------- | -------                        | -------
          build-tools;34.0.0   | 34.0.0  | Android SDK Build-Tools 34     | build-tools/34.0.0
          emulator             | 34.1.19 | Android Emulator               | emulator
          platform-tools       | 35.0.2  | Android SDK Platform-Tools     | platform-tools
          platforms;android-34 | 3       | Android SDK Platform 34        | platforms/android-34

        Available Packages:
          Path                                                | Version  | Description
          -------                                             | -------  | -------
          build-tools;34.0.0                                  | 34.0.0   | Android SDK Build-Tools 34
          build-tools;36.0.0                                  | 36.0.0   | Android SDK Build-Tools 36
          cmdline-tools;latest                                | 19.0     | Android SDK Command-line Tools (latest)
          emulator                                            | 35.5.10  | Android Emulator
          platforms;android-34                                | 3        | Android SDK Platform 34
          platforms;android-36                                | 2        | Android SDK Platform 36
          system-images;android-36;google_apis;arm64-v8a      | 5        | Google APIs ARM 64 v8a System Image
          system-images;android-36;google_apis;x86_64         | 5        | Google APIs Intel x86_64 Atom System Image
          system-images;android-36;google_apis_playstore;x86_64 | 5      | Google Play Intel x86_64 Atom System Image
          system-images;android-35;google_apis_playstore;x86_64 | 9      | Google Play Intel x86_64 Atom System Image
          system-images;android-36;android-wear;x86_64        | 4        | Wear OS 6 Intel x86_64 Atom System Image

        Available Updates:
          ID       | Installed | Available
          -------  | -------   | -------
          emulator | 34.1.19   | 35.5.10
        """;

    [Fact]
    public void Sdkmanager_lists_installed_available_and_updatable_packages()
    {
        var packages = AndroidSdkPackages.ParseList(SdkList.Replace("\\r", "\r", StringComparison.Ordinal));
        var emulator = Assert.Single(packages, p => p.Path == "emulator");
        Assert.True(emulator.Installed);
        Assert.Equal(("34.1.19", "35.5.10", "emulator"), (emulator.Version, emulator.Update, emulator.Location));
        Assert.Equal(SdkPackageCategory.Tools, emulator.Category);

        // Installed once (not again as available), the rest available; platforms by API level, newest first.
        Assert.Single(packages, p => p.Path == "platforms;android-34");
        Assert.False(Assert.Single(packages, p => p.Path == "platforms;android-36").Installed);
        Assert.Equal(["platforms;android-36", "platforms;android-34"], packages.Where(p => p.Category == SdkPackageCategory.Platform).Select(p => p.Path));
        Assert.Null(packages.Single(p => p.Path == "platform-tools").Update);
        Assert.Equal(SdkPackageCategory.BuildTools, packages.Single(p => p.Path == "build-tools;36.0.0").Category);
        Assert.Equal("Android SDK Command-line Tools (latest)", packages.Single(p => p.Path == "cmdline-tools;latest").Description);

        // The system images this machine runs fast, Google Play first, the newest first; no Wear OS.
        var images = AndroidSdkService.SystemImages(packages, Architecture.X64);
        Assert.Equal(
            ["system-images;android-36;google_apis_playstore;x86_64", "system-images;android-36;google_apis;x86_64", "system-images;android-35;google_apis_playstore;x86_64"],
            images.Select(i => i.Path));
        Assert.Equal(["system-images;android-36;google_apis;arm64-v8a"], AndroidSdkService.SystemImages(packages, Architecture.Arm64).Select(i => i.Path));
        Assert.Equal((36, "google_apis_playstore", "x86_64"), AndroidSdkPackages.SystemImageOf(images[0].Path));
        Assert.Equal("Google Play", AndroidSdkPackages.TagTitle("google_apis_playstore"));
        Assert.Equal(35, AndroidSdkPackages.ApiLevelOf("platforms;android-35-ext14"));
    }

    // sdkmanager --install answered "n" to each prompt: the prompt has no line end, so the next license follows it.
    private const string LicensePrompts = """
        License android-sdk-license:
        ---------------------------------------
        Terms and Conditions

        This is the Android Software Development Kit License Agreement
        ---------------------------------------
        Accept? (y/N): License android-sdk-arm-dbt-license:
        ---------------------------------------
        Terms and Conditions (ARM)
        ---------------------------------------
        Accept? (y/N): Skipping following packages as the license is not accepted:
        Google APIs ARM 64 v8a System Image
        The following packages can not be installed since their licenses or those of the packages they depend on were not accepted:
          system-images;android-36;google_apis;arm64-v8a
        """;

    [Fact]
    public void Sdkmanager_license_prompts_give_each_license_once_with_its_text()
    {
        var licenses = AndroidSdkPackages.ParseLicenses(LicensePrompts + "\n1/2: License android-sdk-license:\n---\nagain\n---\n");
        Assert.Equal(["android-sdk-license", "android-sdk-arm-dbt-license"], licenses.Select(l => l.Id));
        Assert.Equal("Terms and Conditions\n\nThis is the Android Software Development Kit License Agreement", licenses[0].Text);
        Assert.Equal("Terms and Conditions (ARM)", licenses[1].Text);
        Assert.Empty(AndroidSdkPackages.ParseLicenses("All SDK package licenses accepted.\n[====] 100% Unzipping..."));
    }

    private (AndroidSdkService Service, string Sdk, List<ToolCommand> Runs) Service(Func<ToolCommand, string> output, PlatformKind platform = PlatformKind.Linux)
    {
        var home = Path.Combine(_dir, "home");
        var sdk = Path.Combine(home, "Android", "Sdk");
        var bin = Path.Combine(sdk, "cmdline-tools", "latest", "bin");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(Path.Combine(sdk, "platform-tools"));
        File.WriteAllText(Path.Combine(sdk, "platform-tools", "adb"), "");
        File.WriteAllText(Path.Combine(bin, "sdkmanager"), "");
        File.WriteAllText(Path.Combine(bin, "avdmanager"), "");
        var runs = new List<ToolCommand>();
        Task<int> Run(ToolCommand command, Action<string>? onLine, CancellationToken cancellationToken)
        {
            lock (runs)
            {
                runs.Add(command);
            }

            foreach (var line in output(command).Split('\n'))
            {
                onLine?.Invoke(line);
            }

            return Task.FromResult(0);
        }

        Task<(int, string)> NoProcess(string program, IReadOnlyList<string> arguments, string? directory, CancellationToken cancellationToken) => Task.FromResult((1, ""));
        var environment = new Dictionary<string, string> { ["ANDROID_AVD_HOME"] = Path.Combine(home, ".android", "avd"), ["PATH"] = "/usr/bin" };
        var mobile = new MobileService(platform, new AndroidToolchain(platform, n => environment.GetValueOrDefault(n), home), NoProcess, (_, _, _) => false);
        return (new AndroidSdkService(mobile, Run, n => environment.GetValueOrDefault(n), home), sdk, runs);
    }

    [Fact]
    public async Task Licenses_are_asked_in_an_empty_sdk_and_the_ones_accepted_here_are_left_out()
    {
        var (service, sdk, runs) = Service(_ => LicensePrompts);
        Assert.NotNull(service.Tools());
        Directory.CreateDirectory(Path.Combine(sdk, "licenses"));
        File.WriteAllText(Path.Combine(sdk, "licenses", "android-sdk-license"), "\n24333f8a63b6825ea9c5514f83c2829b004d1fee");

        var pending = await service.PendingLicensesAsync(["system-images;android-36;google_apis;arm64-v8a"], Ct);
        Assert.Equal(["android-sdk-arm-dbt-license"], pending.Select(l => l.Id));

        // The probe ran in another (empty, now gone) SDK folder, answering no to everything: nothing was installed.
        var probe = Assert.Single(runs);
        Assert.StartsWith("--sdk_root=", probe.Arguments[0], StringComparison.Ordinal);
        Assert.NotEqual("--sdk_root=" + sdk, probe.Arguments[0]);
        Assert.False(Directory.Exists(probe.Arguments[0]["--sdk_root=".Length..]));
        Assert.Equal(["--install", "system-images;android-36;google_apis;arm64-v8a"], probe.Arguments.Skip(1));
        Assert.DoesNotContain("y", probe.Input!.Split('\n'));
        Assert.Equal(Path.Combine(sdk, "cmdline-tools", "latest", "bin", "sdkmanager"), probe.Program);

        // The install itself answers yes (the user has accepted), in the real SDK, with the SDK and JDK in its environment.
        var install = service.InstallCommand(["platforms;android-36"]);
        Assert.Equal(["--sdk_root=" + sdk, "--install", "platforms;android-36"], install.Arguments);
        Assert.All(install.Input!.Split('\n', StringSplitOptions.RemoveEmptyEntries), a => Assert.Equal("y", a));
        Assert.Equal(sdk, install.Environment!["ANDROID_HOME"]);
        Assert.Equal(["--sdk_root=" + sdk, "--uninstall", "emulator"], service.UninstallCommand(["emulator"]).Arguments);
        Assert.Null(service.UninstallCommand(["emulator"]).Input);

        // The adb Command Prompt: the SDK's tool folders in front of PATH.
        var path = service.ToolEnvironment(path: true)[OperatingSystem.IsWindows() ? "Path" : "PATH"];
        Assert.StartsWith(Path.Combine(sdk, "platform-tools") + Path.PathSeparator + Path.Combine(sdk, "cmdline-tools", "latest", "bin"), path, StringComparison.Ordinal);
        Assert.EndsWith("/usr/bin", path, StringComparison.Ordinal);
    }

    [Fact]
    public void Command_line_tools_are_latest_then_the_newest_version_then_the_old_tools()
    {
        var sdk = Path.Combine(_dir, "sdk");
        void Tools(string folder, string suffix = "")
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "sdkmanager" + suffix), "");
            File.WriteAllText(Path.Combine(folder, "avdmanager" + suffix), "");
        }

        var linux = new AndroidToolchain(PlatformKind.Linux, _ => null, _dir);
        Assert.Null(linux.CommandLineTools(sdk));
        Tools(Path.Combine(sdk, "tools", "bin"));
        Assert.Equal(Path.Combine(sdk, "tools", "bin", "sdkmanager"), linux.CommandLineTools(sdk)?.SdkManager);
        Tools(Path.Combine(sdk, "cmdline-tools", "9.0", "bin"));
        Tools(Path.Combine(sdk, "cmdline-tools", "16.0", "bin"));
        Assert.Equal(Path.Combine(sdk, "cmdline-tools", "16.0", "bin", "avdmanager"), linux.CommandLineTools(sdk)?.AvdManager);
        Tools(Path.Combine(sdk, "cmdline-tools", "latest", "bin"));
        Assert.Equal(Path.Combine(sdk, "cmdline-tools", "latest", "bin", "sdkmanager"), linux.CommandLineTools(sdk)?.SdkManager);

        // On Windows they're batch files.
        Tools(Path.Combine(sdk, "cmdline-tools", "latest", "bin"), ".bat");
        Assert.Equal(Path.Combine(sdk, "cmdline-tools", "latest", "bin", "sdkmanager.bat"), new AndroidToolchain(PlatformKind.Windows, _ => null, _dir).CommandLineTools(sdk)?.SdkManager);
    }

    [Fact]
    public void An_elevated_run_is_a_cmd_script_with_its_input_output_and_exit_code_in_files()
    {
        var command = new ToolCommand(@"C:\Program Files (x86)\Android\android-sdk\cmdline-tools\latest\bin\sdkmanager.bat", [@"--sdk_root=C:\Program Files (x86)\Android\android-sdk", "--install", "build-tools;36.0.0"])
        {
            Environment = new Dictionary<string, string> { ["JAVA_HOME"] = @"C:\Program Files\Microsoft\jdk-17.0.12.7-hotspot" },
            Input = "y\ny\n",
        };
        var script = Elevation.WindowsScript(command, @"C:\Temp\a 100%.log", @"C:\Temp\exit.txt", @"C:\Temp\in.txt");
        Assert.Equal(
            "@echo off\r\n"
            + "chcp 65001 >nul\r\n"
            + "set \"JAVA_HOME=C:\\Program Files\\Microsoft\\jdk-17.0.12.7-hotspot\"\r\n"
            + "call \"C:\\Program Files (x86)\\Android\\android-sdk\\cmdline-tools\\latest\\bin\\sdkmanager.bat\" \"--sdk_root=C:\\Program Files (x86)\\Android\\android-sdk\" \"--install\" \"build-tools;36.0.0\""
            + " < \"C:\\Temp\\in.txt\" > \"C:\\Temp\\a 100%%.log\" 2>&1\r\n"
            + "> \"C:\\Temp\\exit.txt\" echo %ERRORLEVEL%\r\n",
            script);

        // A program (not a batch file) without input, in its folder.
        var dotnet = Elevation.WindowsScript(new ToolCommand(@"C:\Program Files\dotnet\dotnet.exe", ["build", @"C:\src\App\App.csproj"]) { WorkingDirectory = @"C:\src\App" }, "l", "e", "i");
        Assert.Contains("cd /d \"C:\\src\\App\"\r\n\"C:\\Program Files\\dotnet\\dotnet.exe\" \"build\"", dotnet, StringComparison.Ordinal);
        Assert.DoesNotContain(" < ", dotnet, StringComparison.Ordinal);

        // Elsewhere: the command under sudo, for the terminal.
        Assert.Equal(
            "yes y | sudo env ANDROID_HOME=/opt/android-sdk JAVA_HOME='/usr/lib/jvm/my jdk' /opt/android-sdk/cmdline-tools/latest/bin/sdkmanager --sdk_root=/opt/android-sdk --install 'platforms;android-36'",
            Elevation.SudoCommand(new ToolCommand("/opt/android-sdk/cmdline-tools/latest/bin/sdkmanager", ["--sdk_root=/opt/android-sdk", "--install", "platforms;android-36"])
            {
                Environment = new Dictionary<string, string> { ["ANDROID_HOME"] = "/opt/android-sdk", ["JAVA_HOME"] = "/usr/lib/jvm/my jdk" },
                Input = "y\ny\n",
            }));

        // dotnet workload doesn't ask for permission itself outside Windows: under sudo, by dotnet's full path, in a
        // root the user can't write.
        Assert.Equal("sudo /usr/local/share/dotnet/dotnet workload restore '/Users/me/My App/App.csproj'", Elevation.WorkloadCommand(PlatformKind.MacOS, "/usr/local/share/dotnet/dotnet", writable: false, ["workload", "restore", "/Users/me/My App/App.csproj"]));
        Assert.Equal("sudo /usr/share/dotnet/dotnet workload install maui-android", Elevation.WorkloadCommand(PlatformKind.Linux, "/usr/share/dotnet/dotnet", writable: false, ["workload", "install", "maui-android"]));
        Assert.Equal("dotnet workload restore /home/me/src/App/App.csproj", Elevation.WorkloadCommand(PlatformKind.Linux, "/home/me/.dotnet/dotnet", writable: true, ["workload", "restore", "/home/me/src/App/App.csproj"]));
        Assert.Equal("dotnet workload install maui", Elevation.WorkloadCommand(PlatformKind.MacOS, null, writable: false, ["workload", "install", "maui"]));
        Assert.Equal("dotnet workload restore \"D:\\My App\\App.csproj\"", Elevation.WorkloadCommand(PlatformKind.Windows, @"C:\Program Files\dotnet\dotnet.exe", writable: false, ["workload", "restore", @"D:\My App\App.csproj"]));

        Assert.True(Elevation.CanWrite(_dir));
        Assert.True(Elevation.CanWrite(Path.Combine(_dir, "not", "yet")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir));
        if (!OperatingSystem.IsWindows() && Environment.UserName != "root")
        {
            var locked = Directory.CreateDirectory(Path.Combine(_dir, "locked")).FullName;
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                Assert.False(Elevation.CanWrite(locked));
            }
            finally
            {
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    // avdmanager list device (cmdline-tools 16).
    private const string DeviceList = """
        Available devices definitions:
        id: 0 or "automotive_1024p_landscape"
            Name: Automotive (1024p landscape)
            OEM : Google
            Tag : android-automotive-playstore
        ---------
        id: 26 or "pixel_7"
            Name: Pixel 7
            OEM : Google
        ---------
        id: 40 or "Nexus One"
            Name: Nexus One
            OEM : Google
        ---------
        id: 61 or "wearos_large_round"
            Name: Wear OS Large Round
            OEM : Google
            Tag : android-wear
        """;

    [Fact]
    public async Task Emulators_are_read_created_edited_wiped_and_deleted_on_disk()
    {
        var (service, _, runs) = Service(c => c.Arguments is ["list", "device"] ? DeviceList : "");
        var devices = await service.DeviceDefinitionsAsync(Ct);
        Assert.Equal(["Nexus One", "Pixel 7", "Automotive (1024p landscape)", "Wear OS Large Round"], devices.Select(d => d.Name));
        Assert.Equal("pixel_7", devices[1].Id);
        Assert.Equal("android-wear", devices[3].Tag);
        Assert.False(devices[3].IsPhoneOrTablet);

        // Creating goes through avdmanager, answering no to a custom hardware profile.
        Assert.Empty(service.Emulators());
        Assert.Equal("Pixel_7_API_36", AndroidEmulators.SuggestName(devices[1], 36, []));
        await service.CreateEmulatorAsync("Pixel_7_API_36", "system-images;android-36;google_apis;x86_64", "pixel_7", Ct);
        Assert.Equal(["create", "avd", "--name", "Pixel_7_API_36", "--package", "system-images;android-36;google_apis;x86_64", "--device", "pixel_7"], runs[^1].Arguments);
        Assert.Equal("no\n", runs[^1].Input);

        // What avdmanager writes: the .ini pointing at the folder, and the folder's config.ini.
        var avdHome = service.AvdHome;
        var folder = Path.Combine(avdHome, "Pixel_7_API_36.avd");
        Directory.CreateDirectory(Path.Combine(folder, "snapshots", "default_boot"));
        File.WriteAllText(Path.Combine(avdHome, "Pixel_7_API_36.ini"), $"avd.ini.encoding=UTF-8\npath={folder}\npath.rel=avd/Pixel_7_API_36.avd\ntarget=android-36\n");
        File.WriteAllText(Path.Combine(folder, "config.ini"), "AvdId=Pixel_7_API_36\r\nabi.type=x86_64\r\nhw.device.name=pixel_7\r\nhw.ramSize=2048M\r\nimage.sysdir.1=system-images/android-36/google_apis/x86_64/\r\ntag.id=google_apis\r\nhw.keyboard=no\r\n");
        File.WriteAllText(Path.Combine(folder, "userdata-qemu.img"), "data");
        File.WriteAllText(Path.Combine(folder, "userdata.img"), "image");

        var emulator = Assert.Single(service.Emulators());
        Assert.Equal(("Pixel_7_API_36", "Pixel 7 API 36", "pixel_7", 36), (emulator.Name, emulator.DisplayName, emulator.Device, emulator.ApiLevel));
        Assert.Equal(("x86_64", "google_apis"), (emulator.Abi, emulator.Tag));
        Assert.Equal("system-images;android-36;google_apis;x86_64", emulator.SystemImage);
        Assert.Equal("Pixel_7_API_36_2", AndroidEmulators.SuggestName(devices[1], 36, [emulator]));
        Assert.Equal("There's already an emulator named pixel_7_api_36.", AndroidEmulators.ValidateName("pixel_7_api_36", [emulator]));
        Assert.NotNull(AndroidEmulators.ValidateName("my phone", []));
        Assert.Null(AndroidEmulators.ValidateName("My-Phone_2.1", [emulator]));

        // Editing changes its lines in place, adds new keys and keeps the file's line ends.
        AndroidEmulators.Save(emulator, new Dictionary<string, string?> { [AndroidEmulatorSettings.Ram] = "4096M", [AndroidEmulatorSettings.Keyboard] = "yes", [AndroidEmulatorSettings.SdCard] = "512M" });
        Assert.Equal(
            "AvdId=Pixel_7_API_36\r\nabi.type=x86_64\r\nhw.device.name=pixel_7\r\nhw.ramSize=4096M\r\nimage.sysdir.1=system-images/android-36/google_apis/x86_64/\r\ntag.id=google_apis\r\nhw.keyboard=yes\r\nsdcard.size=512M\r\n",
            File.ReadAllText(Path.Combine(folder, "config.ini")));
        Assert.Equal("a=1\n", AndroidEmulators.UpdateIni("a=1\nb=2\n", new Dictionary<string, string?> { ["b"] = null }));
        Assert.Equal(4096, AndroidEmulators.SizeInMegabytes(service.Emulators()[0].Config[AndroidEmulatorSettings.Ram]));
        Assert.Equal(6144, AndroidEmulators.SizeInMegabytes("6G"));
        Assert.Equal(6144, AndroidEmulators.SizeInMegabytes("6442450944"));
        Assert.Equal(2048, AndroidEmulators.SizeInMegabytes("2048MB"));
        Assert.Null(AndroidEmulators.SizeInMegabytes("lots"));

        // Wiping its data keeps the system image's data and the settings; deleting removes it all.
        AndroidEmulators.WipeData(emulator);
        Assert.False(File.Exists(Path.Combine(folder, "userdata-qemu.img")));
        Assert.False(Directory.Exists(Path.Combine(folder, "snapshots")));
        Assert.True(File.Exists(Path.Combine(folder, "userdata.img")));
        Assert.True(File.Exists(Path.Combine(folder, "config.ini")));
        AndroidEmulators.Delete(emulator);
        Assert.Empty(service.Emulators());
        Assert.Empty(Directory.EnumerateFileSystemEntries(avdHome));

        // Where AVDs are: ANDROID_AVD_HOME, ANDROID_USER_HOME, ANDROID_SDK_HOME, then ~/.android.
        Assert.Equal(Path.Combine("/u", "avd"), AndroidEmulators.AvdHome(n => n == "ANDROID_USER_HOME" ? "/u" : null, "/home/me"));
        Assert.Equal(Path.Combine("/s", ".android", "avd"), AndroidEmulators.AvdHome(n => n == "ANDROID_SDK_HOME" ? "/s" : null, "/home/me"));
        Assert.Equal(Path.Combine("/home/me", ".android", "avd"), AndroidEmulators.AvdHome(_ => null, "/home/me"));
    }

    [Fact]
    public void Logcat_and_the_simulator_log_become_entries_and_filter()
    {
        var entry = DeviceLogs.ParseLogcat("11-15 10:23:45.123  1234  5678 I ActivityManager: Start proc 4321:com.contoso.app/u0a123 for activity");
        Assert.Equal(("11-15 10:23:45.123", DeviceLogLevel.Info, "ActivityManager", 1234, 5678), (entry!.Time, entry.Level, entry.Tag, entry.ProcessId, entry.ThreadId));
        Assert.Equal("Start proc 4321:com.contoso.app/u0a123 for activity", entry.Message);

        var padded = DeviceLogs.ParseLogcat("11-15 10:23:46.000 21001 21001 E mono-rt : [ERROR] FATAL UNHANDLED EXCEPTION: System.Exception: boom");
        Assert.Equal(("mono-rt", DeviceLogLevel.Error, "[ERROR] FATAL UNHANDLED EXCEPTION: System.Exception: boom"), (padded!.Tag, padded.Level, padded.Message));
        Assert.Equal(DeviceLogLevel.Fatal, DeviceLogs.ParseLogcat("11-15 10:23:46.000   500   500 F libc    : Fatal signal 6")!.Level);
        Assert.Equal("", DeviceLogs.ParseLogcat("11-15 10:23:46.000   500   500 D Tag: ")!.Message);
        Assert.Null(DeviceLogs.ParseLogcat("--------- beginning of main"));
        Assert.Null(DeviceLogs.ParseLogcat("garbage"));
        Assert.Equal("11-15 10:23:46.000 21001 21001 E mono-rt: [ERROR] FATAL UNHANDLED EXCEPTION: System.Exception: boom", DeviceLogs.Format(padded));

        var simulator = DeviceLogs.ParseUnifiedLog("""{"traceID":1,"eventMessage":"Loaded the page","messageType":"Error","timestamp":"2026-11-15 10:23:45.123456+0100","subsystem":"com.contoso.app","category":"ui","processImagePath":"/Users/me/Library/Developer/CoreSimulator/Devices/X/data/Containers/Bundle/Application/Y/App.app/App","processID":812,"threadID":9001}""");
        Assert.Equal(("11-15 10:23:45.123", DeviceLogLevel.Error, "com.contoso.app:ui", 812, "App"), (simulator!.Time, simulator.Level, simulator.Tag, simulator.ProcessId, simulator.Process));
        Assert.Null(DeviceLogs.ParseUnifiedLog("Filtering the log data using \"composedMessage CONTAINS x\""));
        Assert.Null(DeviceLogs.ParseUnifiedLog("{\"finished\":1}"));

        // Filters: the level, text in the tag or message, and the app's processes (Android) or name (simulators).
        Assert.True(DeviceLogs.Matches(padded, DeviceLogLevel.Warning, "boom", null, null));
        Assert.False(DeviceLogs.Matches(entry, DeviceLogLevel.Warning, null, null, null));
        Assert.True(DeviceLogs.Matches(entry, DeviceLogLevel.Verbose, "activitymanager", null, null));
        Assert.False(DeviceLogs.Matches(entry, DeviceLogLevel.Verbose, null, DeviceLogs.ParsePidof("21001 21002\n"), null));
        Assert.True(DeviceLogs.Matches(padded, DeviceLogLevel.Verbose, null, DeviceLogs.ParsePidof("21001\n"), null));
        Assert.True(DeviceLogs.Matches(simulator, DeviceLogLevel.Verbose, null, null, "App"));
        Assert.False(DeviceLogs.Matches(simulator, DeviceLogLevel.Verbose, null, null, "Other"));
        Assert.Empty(DeviceLogs.ParsePidof(""));
        Assert.Equal(["-s", "emulator-5554", "logcat", "-v", "threadtime", "-T", "2000"], DeviceLogs.LogcatArguments("emulator-5554"));
    }
}
