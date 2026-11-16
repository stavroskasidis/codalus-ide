using Avalonia.Headless.XUnit;
using Codalus.Platform;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Mobile;

namespace Codalus.Shell.Tests;

/// <summary>
/// The platforms' native tools in the headless shell (ADR 0104): the toolbar's Android group, the Android SDK Manager
/// with its licenses, the Android Device Manager, the Device Log and the elevated SDK change, on stand-in tools and an
/// SDK in a temporary folder. Nothing downloads, installs, elevates or starts an emulator.
/// </summary>
public sealed class AndroidToolsShellTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("codalus-android-shell-").FullName;
    private readonly List<ToolCommand> _runs = [];

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string SdkList = """
        Installed packages:
          Path                 | Version | Description                    | Location
          -------              | ------- | -------                        | -------
          platform-tools       | 35.0.2  | Android SDK Platform-Tools     | platform-tools
          platforms;android-35 | 2       | Android SDK Platform 35        | platforms/android-35

        Available Packages:
          Path                                        | Version | Description
          -------                                     | ------- | -------
          platform-tools                              | 35.0.2  | Android SDK Platform-Tools
          platforms;android-35                        | 2       | Android SDK Platform 35
          platforms;android-36                        | 2       | Android SDK Platform 36
          system-images;android-36;google_apis;x86_64 | 5       | Google APIs Intel x86_64 Atom System Image
          system-images;android-36;google_apis;arm64-v8a | 5    | Google APIs ARM 64 v8a System Image
        """;

    private const string Licenses = """
        License android-sdk-license:
        ---------------------------------------
        Terms and Conditions
        ---------------------------------------
        Accept? (y/N): Skipping following packages as the license is not accepted:
        """;

    /// <summary>The shell on an SDK in a temporary folder, with one running emulator, and every tool a stand-in.</summary>
    private (ShellFixture Fixture, string Sdk, string AvdHome) Create(Func<ToolCommand, Action<string>?, CancellationToken, Task<int>>? tool = null)
    {
        var f = ShellFixture.Create();
        var home = Path.Combine(_dir, "home");
        var sdk = Path.Combine(home, "Android", "Sdk");
        var avdHome = Path.Combine(home, ".android", "avd");
        var exe = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var script = OperatingSystem.IsWindows() ? ".bat" : string.Empty;
        foreach (var file in new[] { Path.Combine("platform-tools", "adb" + exe), Path.Combine("emulator", "emulator" + exe), Path.Combine("cmdline-tools", "latest", "bin", "sdkmanager" + script), Path.Combine("cmdline-tools", "latest", "bin", "avdmanager" + script) })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(sdk, file))!);
            File.WriteAllText(Path.Combine(sdk, file), "");
        }

        // An emulator on disk, running as emulator-5554.
        var folder = Path.Combine(avdHome, "Pixel_7_API_35.avd");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(avdHome, "Pixel_7_API_35.ini"), $"path={folder}\ntarget=android-35\n");
        File.WriteAllText(Path.Combine(folder, "config.ini"), "abi.type=x86_64\nhw.device.name=pixel_7\nhw.ramSize=2048M\ndisk.dataPartition.size=6G\nhw.gpu.mode=auto\nimage.sysdir.1=system-images/android-35/google_apis/x86_64/\ntag.id=google_apis\nhw.keyboard=no\n");

        Task<(int, string)> Adb(string program, IReadOnlyList<string> arguments, string? directory, CancellationToken cancellationToken) =>
            Task.FromResult(arguments switch
            {
                ["devices", "-l"] => (0, "List of devices attached\nemulator-5554          device product:sdk_gphone64_x86_64 model:sdk_gphone64_x86_64 device:emu64xa transport_id:1\n"),
                [_, _, "emu", "avd", "name"] => (0, "Pixel_7_API_35\nOK\n"),
                [_, "-list-avds"] => (0, "Pixel_7_API_35\n"),
                ["-list-avds"] => (0, "Pixel_7_API_35\n"),
                _ => (1, ""),
            });

        var platform = OperatingSystem.IsWindows() ? PlatformKind.Windows : OperatingSystem.IsMacOS() ? PlatformKind.MacOS : PlatformKind.Linux;
        string? Environment(string name) => name switch { "ANDROID_HOME" => sdk, "ANDROID_AVD_HOME" => avdHome, _ => null };
        f.Shell.UseMobileService(new MobileService(platform, new AndroidToolchain(platform, Environment, home), Adb, (_, _, _) => true));
        f.Shell.RunTool = (command, onLine, cancellationToken) =>
        {
            lock (_runs)
            {
                _runs.Add(command);
            }

            if (tool is not null)
            {
                return tool(command, onLine, cancellationToken);
            }

            var output = command.Arguments.Contains("--list") ? SdkList : command.Arguments.Contains("--install") && command.Input?.StartsWith('n') == true ? Licenses : string.Empty;
            foreach (var line in output.Split('\n'))
            {
                onLine?.Invoke(line);
            }

            return Task.FromResult(0);
        };
        f.Shell.UseAndroidSdkService(new AndroidSdkService(f.Shell.Mobile, (c, l, t) => f.Shell.RunTool(c, l, t), Environment, home));
        return (f, sdk, avdHome);
    }

    [AvaloniaFact]
    public async Task The_toolbar_shows_the_android_group_while_the_solution_has_an_android_project()
    {
        await using var f = ShellFixture.Create();
        bool HasGroup() => f.Shell.Toolbar.Extra.Any(g => g.Items.Any(i => i.Command == "android.deviceManager"));
        Assert.False(HasGroup());

        f.Shell.Workbench.Context.Set("androidProjects", true);
        await ShellFixture.PumpUntil(HasGroup);
        var group = f.Shell.Toolbar.Extra.Single(g => g.Items.Any(i => i.Command == "android.deviceManager"));
        Assert.Equal(["android.deviceManager", "android.sdkManager", "android.adbPrompt", "mobile.deviceLog"], group.Items.Select(i => i.Command));

        // Each button can be removed in Customize, and comes back.
        Assert.Contains("android.sdkManager", f.Shell.CustomizeKeys(MenuCustomizations.StandardToolbar));
        await f.Shell.CustomizeRemoveAsync(MenuCustomizations.StandardToolbar, "android.sdkManager");
        await ShellFixture.PumpUntil(() => !f.Shell.Toolbar.Extra.SelectMany(g => g.Items).Any(i => i.Command == "android.sdkManager"));
        Assert.True(HasGroup());
        await f.Shell.CustomizeRemoveAsync(MenuCustomizations.StandardToolbar, "android.sdkManager");
        await ShellFixture.PumpUntil(() => f.Shell.Toolbar.Extra.SelectMany(g => g.Items).Any(i => i.Command == "android.sdkManager"));

        f.Shell.Workbench.Context.Set("androidProjects", false);
        await ShellFixture.PumpUntil(() => !HasGroup());

        // Tools → Android has them all, whatever the solution.
        Assert.Equal(
            ["android.deviceManager", "android.sdkManager", "android.adbPrompt", "mobile.deviceLog"],
            f.Shell.Workbench.Menus.GetItems(BuiltIns.ToolsAndroidMenu).Select(i => i.Command));
    }

    [AvaloniaFact]
    public async Task The_sdk_manager_lists_packages_and_installs_after_the_licenses_are_accepted()
    {
        var (f, sdk, _) = Create();
        await using var fixture = f;
        f.Shell.ShowAndroidSdkManager();
        var manager = f.Shell.AndroidSdkManager;
        await ShellFixture.PumpUntil(() => manager.Rows.Count == 5 && !manager.IsBusy);
        Assert.Contains(sdk, manager.Location, StringComparison.Ordinal);
        Assert.Equal("2 installed, 0 with updates.", manager.Status);

        manager.Category = 1; // platforms
        Assert.Equal(["platforms;android-36", "platforms;android-35"], manager.Rows.Select(r => r.Path));
        manager.Category = 0;
        manager.Search = "arm64";
        Assert.Single(manager.Rows);
        manager.Search = string.Empty;

        // Check one to install: Apply asks about its license (shown in full), and installs only after Accept.
        manager.Rows.Single(r => r.Path == "platforms;android-36").IsChecked = true;
        Assert.Equal(1, manager.PendingCount);
        Assert.Equal("Will be installed", manager.Rows.Single(r => r.Path == "platforms;android-36").Status);
        _ = manager.ApplyAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Equal("Android SDK - License Agreement", f.Shell.Dialogs.Title);
        Assert.Contains(f.Shell.Dialogs.Details, d => d.Text == "android-sdk-license" && d.IsHeader);
        Assert.Contains(f.Shell.Dialogs.Details, d => d.Text == "Terms and Conditions");
        Assert.DoesNotContain(_runs, r => r.Arguments.Contains("--sdk_root=" + sdk) && r.Arguments.Contains("--install"));
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => _runs.Any(r => r.Arguments.SequenceEqual(["--sdk_root=" + sdk, "--install", "platforms;android-36"])) && !manager.IsBusy);
        Assert.StartsWith("y\n", _runs.Single(r => r.Arguments.SequenceEqual(["--sdk_root=" + sdk, "--install", "platforms;android-36"])).Input, StringComparison.Ordinal);

        // A license already accepted in this SDK isn't asked again; unchecking an installed package uninstalls it.
        Directory.CreateDirectory(Path.Combine(sdk, "licenses"));
        File.WriteAllText(Path.Combine(sdk, "licenses", "android-sdk-license"), "hash");
        await ShellFixture.PumpUntil(() => manager.Rows.Count == 5);
        manager.Rows.Single(r => r.Path == "platforms;android-35").IsChecked = false;
        _ = manager.ApplyAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Equal("Uninstall Android SDK Packages", f.Shell.Dialogs.Title);
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => _runs.Any(r => r.Arguments.SequenceEqual(["--sdk_root=" + sdk, "--uninstall", "platforms;android-35"])) && !manager.IsBusy);
    }

    [AvaloniaFact]
    public async Task An_sdk_only_an_administrator_can_change_is_changed_elevated_or_with_sudo()
    {
        var (f, sdk, _) = Create();
        await using var fixture = f;
        f.Shell.CanWriteSdk = _ => false;
        var elevated = new List<ToolCommand>();
        f.Shell.RunElevated = (command, onLine, _) =>
        {
            elevated.Add(command);
            onLine("Installing Android SDK Build-Tools 36");
            return Task.FromResult<int?>(0);
        };

        var change = f.Shell.RunSdkChangeAsync(f.Shell.AndroidSdk.InstallCommand(["build-tools;36.0.0"]), sdk, "Installing build-tools;36.0.0");
        if (OperatingSystem.IsWindows())
        {
            // Windows: the same command, elevated (UAC), its output in the Android SDK channel.
            Assert.Equal(0, await change);
            Assert.Equal(["--sdk_root=" + sdk, "--install", "build-tools;36.0.0"], Assert.Single(elevated).Arguments);
            Assert.Contains("Installing Android SDK Build-Tools 36", f.Shell.AndroidSdkLog.GetText(), StringComparison.Ordinal);
            Assert.Contains("can only be changed by an administrator", f.Shell.AndroidSdkLog.GetText(), StringComparison.Ordinal);
        }
        else
        {
            // Elsewhere: the command under sudo, in the terminal, after the user agrees (here they don't).
            await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
            Assert.Equal("Run as Administrator", f.Shell.Dialogs.Title);
            Assert.Contains("sudo env ANDROID_HOME=", f.Shell.Dialogs.Message, StringComparison.Ordinal);
            Assert.Contains("--install 'build-tools;36.0.0'", f.Shell.Dialogs.Message, StringComparison.Ordinal);
            f.Shell.Dialogs.Cancel();
            Assert.Null(await change);
            Assert.Empty(elevated);
        }

        Assert.DoesNotContain(_runs, r => r.Arguments.Contains("--install"));
    }

    [AvaloniaFact]
    public async Task The_device_manager_lists_edits_and_deletes_emulators()
    {
        var (f, _, avdHome) = Create();
        await using var fixture = f;
        f.Shell.ShowAndroidDeviceManager();
        var manager = f.Shell.AndroidDeviceManager;
        await ShellFixture.PumpUntil(() => manager.Emulators.Count == 1 && manager.Selected is not null);
        var row = manager.Emulators[0];
        Assert.Equal(("Pixel 7 API 35", "pixel_7", "35", "x86_64", "Google APIs"), (row.Name, row.Device, row.Api, row.Abi, row.Image));
        Assert.True(row.IsRunning);
        Assert.Equal("Running (emulator-5554)", row.State);

        // While it runs: Stop, not Start, Wipe Data, Delete or editing.
        Assert.False(manager.CanEdit);
        Assert.True(manager.StopCommand.CanExecute(null));
        Assert.False(manager.StartCommand.CanExecute(null));
        Assert.False(manager.DeleteCommand.CanExecute(null));
        manager.StopCommand.Execute(null);
        await ShellFixture.PumpUntil(() => _runs.Any(r => r.Arguments.SequenceEqual(["-s", "emulator-5554", "emu", "kill"])), 30);

        // Stopped (adb lists it no more): its settings are edited, checked and written to its config.ini.
        f.Shell.UseMobileService(new MobileService(f.Shell.Mobile.Platform, f.Shell.Mobile.Toolchain, (_, _, _, _) => Task.FromResult((0, "List of devices attached\n")), (_, _, _) => true));
        f.Shell.UseAndroidSdkService(new AndroidSdkService(f.Shell.Mobile, (c, l, t) => f.Shell.RunTool(c, l, t), n => n == "ANDROID_AVD_HOME" ? avdHome : n == "ANDROID_HOME" ? Path.Combine(_dir, "home", "Android", "Sdk") : null, Path.Combine(_dir, "home")));
        await manager.RefreshAsync();
        await ShellFixture.PumpUntil(() => manager.Selected is { IsRunning: false });
        Assert.True(manager.CanEdit);
        Assert.Equal(("2048", "6144", "", 0, false), (manager.Ram, manager.Storage, manager.SdCard, manager.Gpu, manager.Keyboard));
        manager.Ram = "lots";
        manager.SaveCommand.Execute(null);
        await ShellFixture.PumpUntil(() => manager.HasSettingsError);
        manager.Ram = "4096";
        manager.SdCard = "512";
        manager.Gpu = AndroidEmulatorSettings.GpuModes.ToList().IndexOf("host");
        manager.Keyboard = true;
        manager.SaveCommand.Execute(null);
        var config = Path.Combine(avdHome, "Pixel_7_API_35.avd", "config.ini");
        await ShellFixture.PumpUntil(() => File.ReadAllText(config).Contains("hw.ramSize=4096M", StringComparison.Ordinal));
        Assert.Equal(
            "abi.type=x86_64\nhw.device.name=pixel_7\nhw.ramSize=4096M\ndisk.dataPartition.size=6144M\nhw.gpu.mode=host\nimage.sysdir.1=system-images/android-35/google_apis/x86_64/\ntag.id=google_apis\nhw.keyboard=yes\nsdcard.size=512M\n",
            File.ReadAllText(config));

        // Delete asks first.
        manager.DeleteCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Equal("Delete Emulator", f.Shell.Dialogs.Title);
        f.Shell.Dialogs.Accept();
        await ShellFixture.PumpUntil(() => manager.Emulators.Count == 0);
        Assert.False(File.Exists(Path.Combine(avdHome, "Pixel_7_API_35.ini")));
        Assert.Contains("New creates one", manager.Status, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task The_device_log_follows_the_running_device_and_filters()
    {
        var following = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (f, _, _) = Create(async (command, onLine, cancellationToken) =>
        {
            if (command.Arguments is [_, _, "logcat", ..])
            {
                onLine?.Invoke("--------- beginning of main");
                onLine?.Invoke("11-15 10:23:45.123  1234  1234 I ActivityManager: Start proc 4321:com.contoso.app/u0a123 for activity");
                onLine?.Invoke("11-15 10:23:45.200  4321  4321 D Contoso : debug detail");
                onLine?.Invoke("11-15 10:23:46.000  4321  4330 E mono-rt : [ERROR] FATAL UNHANDLED EXCEPTION: System.Exception: boom");
                following.TrySetResult(cancellationToken);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return 0;
        });
        await using var fixture = f;
        f.Shell.ShowDeviceLog();
        var log = f.Shell.DeviceLogWindow;
        await ShellFixture.PumpUntil(() => log.Rows.Count == 3);
        Assert.Equal("emulator-5554", log.Source?.Device.Id);
        Assert.Equal(["-s", "emulator-5554", "logcat", "-v", "threadtime", "-T", "2000"], _runs.Single(r => r.Arguments.Contains("logcat")).Arguments);
        Assert.Equal(("E", "mono-rt", true), (log.Rows[2].Level, log.Rows[2].Tag, log.Rows[2].IsError));

        log.Level = (int)DeviceLogLevel.Info;
        Assert.Equal(["ActivityManager", "mono-rt"], log.Rows.Select(r => r.Tag));
        log.Search = "boom";
        Assert.Equal(["mono-rt"], log.Rows.Select(r => r.Tag));
        log.Search = string.Empty;
        log.Level = 0;
        log.ClearCommand.Execute(null);
        Assert.Empty(log.Rows);

        // Closing the window stops following.
        var token = await following.Task;
        f.Shell.Layout.CloseToolWindow(DeviceLogViewModel.ToolWindowId);
        log.StopFollowing();
        await ShellFixture.PumpUntil(() => token.IsCancellationRequested);
    }
}
