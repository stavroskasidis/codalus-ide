using Avalonia.Headless.XUnit;
using Codalus.Platform;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Mobile;
using Codalus.Workbench.Setup;

namespace Codalus.Shell.Tests;

/// <summary>
/// M20a (ADR 0064) in the headless shell on samples/Maui: the MAUI app is a mobile project with a device picker, its
/// <c>.xaml</c> files are XAML for the C# host, Check Environment lists its workloads and the Android SDK, and a run
/// goes to the chosen device through <c>dotnet build -t:Run</c> (with stand-in tools: nothing deploys), or says what's
/// missing first.
/// </summary>
public sealed class MobileShellTests
{
    private static async Task<(ShellFixture Fixture, string Folder)> OpenAsync()
    {
        var f = ShellFixture.Create();
        var folder = f.CopySample("Maui");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Maui.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 240);
        return (f, folder);
    }

    [AvaloniaFact]
    public async Task The_maui_app_has_a_device_picker_its_xaml_and_its_environment()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var app = Path.Combine(folder, "TodoApp", "TodoApp.csproj");
        var views = Path.Combine(folder, "MauiViews", "MauiViews.csproj");
        var info = f.Shell.Solutions.Projects[app];
        Assert.True(info.UsesMaui);
        Assert.False(f.Shell.Solutions.Projects[views].UsesMaui);
        Assert.True(f.Shell.IsMobileProject(app));
        Assert.False(f.Shell.IsMobileProject(views));
        Assert.Equal("net10.0-android", f.Shell.MobileRunFramework(info));

        // The project says which workload packs are missing; it agrees with dotnet workload list.
        var installed = await f.Shell.Mobile.InstalledWorkloadsAsync("dotnet", folder, TestContext.Current.CancellationToken) ?? [];
        Assert.Equal(installed.Contains("maui-android") || installed.Contains("maui"), info.MissingWorkloadPacks.Length == 0 || !info.MissingWorkloadPacks.Any(p => p.Contains("Android", StringComparison.OrdinalIgnoreCase)));

        // The startup project gets the device picker.
        f.Shell.SetStartupProject(app);
        await ShellFixture.PumpUntil(() => f.Shell.Toolbar.HasDevice);
        Assert.True(f.Shell.Workbench.Context.GetValue("mobileStartup") is true);
        f.Shell.SetStartupProject(views);
        await ShellFixture.PumpUntil(() => !f.Shell.Toolbar.HasDevice);
        f.Shell.SetStartupProject(app);
        await ShellFixture.PumpUntil(() => f.Shell.Toolbar.HasDevice);

        // .xaml is XAML, served by the C# host.
        var page = await f.Shell.OpenFileAsync(Path.Combine(folder, "MauiViews", "Views", "TodoPage.xaml"), preview: false, null);
        await ShellFixture.PumpUntil(() => page?.Controller is not null);
        Assert.Equal(ShellHost.XamlLanguageId, page!.Model.LanguageId);

        // Check Environment: the workload, the Android SDK and the JDK, each with a fix.
        var tools = await f.Shell.CheckEnvironmentAsync(offer: false);
        var workload = Assert.Single(tools, t => t.Id == "workload:maui-android");
        Assert.Equal(installed.Contains("maui-android") || installed.Contains("maui"), workload.State == ToolState.Found);
        Assert.Equal("dotnet workload install maui-android", f.Shell.PlanFor(workload)?.Command);
        var sdk = Assert.Single(tools, t => t.Id == "android-sdk");
        Assert.Single(tools, t => t.Id == "jdk");
        Assert.NotNull(f.Shell.PlanFor(sdk));

        // The Devices window.
        f.Shell.ShowDevices();
        await ShellFixture.PumpUntil(() => f.Shell.DevicesWindow.Toolchain.Length > 0);
        Assert.Contains("Android SDK", f.Shell.DevicesWindow.Toolchain, StringComparison.Ordinal);
        Assert.True(f.Shell.Layout.IsVisible(DevicesViewModel.ToolWindowId));
    }

    [AvaloniaFact]
    public async Task A_run_goes_to_the_chosen_emulator_through_the_run_target_or_says_what_is_missing()
    {
        var (f, folder) = await OpenAsync();
        await using var _ = f;
        var app = Path.Combine(folder, "TodoApp", "TodoApp.csproj");
        var info = f.Shell.Solutions.Projects[app];
        f.Shell.SetStartupProject(app);

        // Stand-in tools: an SDK with adb and an emulator, one emulator running; nothing is installed or started.
        var sdk = Path.Combine(f.UserData, "android-sdk");
        string[] files = [Path.Combine(sdk, "platform-tools", "adb"), Path.Combine(sdk, "emulator", "emulator"), Path.Combine(f.UserData, "jdk", "bin", "java"), Path.Combine(f.UserData, "jdk", "bin", "javac")];
        Task<(int, string)> Run(string program, IReadOnlyList<string> arguments, string? directory, CancellationToken ct) =>
            Task.FromResult<(int, string)>((Path.GetFileName(program) + " " + string.Join(' ', arguments)) switch
            {
                "adb devices -l" => (0, "List of devices attached\nemulator-5554 device model:sdk_gphone64 transport_id:1\n"),
                "adb -s emulator-5554 emu avd name" => (0, "Pixel_8_API_35\nOK\n"),
                "emulator -list-avds" => (0, "Pixel_8_API_35\nTablet_API_34\n"),
                _ => (1, string.Empty),
            });
        var toolchain = new AndroidToolchain(PlatformInfo.Current, n => n switch { "ANDROID_HOME" => sdk, "JAVA_HOME" => Path.Combine(f.UserData, "jdk"), _ => null }, f.UserData, files.Contains);
        f.Shell.UseMobileService(new MobileService(PlatformInfo.Current, toolchain, Run, (_, _, _) => false, f.Shell.Workbench.Settings));
        await f.Shell.RefreshDevicesAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Toolbar.DeviceLabel == "Pixel 8 API 35");
        Assert.Equal(2, f.Shell.DevicesWindow.Devices.Count);
        Assert.Contains(f.Shell.DevicesWindow.Devices, d => d.IsTarget && d.Device.Id == "emulator-5554");

        if (info.MissingWorkloadPacks.Length > 0)
        {
            // Without the workload, the run offers to install it first.
            var run = f.Shell.StartWithoutDebuggingAsync();
            await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(n => n.Message.Contains("needs .NET workloads", StringComparison.Ordinal)));
            f.Shell.Workbench.Notifications.DismissAll();
            await run;
            return;
        }

        // With it: the run target, the emulator by its serial, the SDK and the JDK.
        await f.Shell.StartWithoutDebuggingAsync();
        var channel = f.Shell.Workbench.Output.Channels.Single(c => c.Id == "codalus.run.TodoApp");
        await ShellFixture.PumpUntil(() => channel.GetText().Contains("-t:Run", StringComparison.Ordinal));
        var command = channel.GetText().Split('\n')[0];
        Assert.Contains("build " + app, command, StringComparison.Ordinal);
        Assert.Contains("-f net10.0-android", command, StringComparison.Ordinal);
        Assert.Contains("\"-p:AdbTarget=-s emulator-5554\"", command, StringComparison.Ordinal);
        Assert.Contains("-p:AndroidSdkDirectory=" + sdk, command, StringComparison.Ordinal);
        Assert.DoesNotContain(f.Shell.Workbench.Notifications.Items, n => n.Message.StartsWith("Could not start", StringComparison.Ordinal));
        f.Shell.StopRunning();
    }
}
