using Avalonia.Headless.XUnit;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Mobile;

namespace Codalus.Shell.Tests;

/// <summary>
/// M20a end to end: samples/Maui runs on an Android emulator through Start Without Debugging. It boots an emulator and
/// builds for Android, so it runs only with <c>CODALUS_TEST_ANDROID=1</c> (and the SDK, an emulator image and the
/// workloads); <c>CODALUS_TEST_ANDROID_AVD</c> picks the virtual device.
/// </summary>
public sealed class MobileShellTests
{
    /// <summary>Opens samples/Maui with an emulator chosen in the toolbar; skips without the opt-in, the SDK or a virtual device.</summary>
    private static async Task<(string Folder, string Project, MobileDevice Device, bool StartedHere)> OpenAsync(ShellFixture f)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("CODALUS_TEST_ANDROID") == "1", "Set CODALUS_TEST_ANDROID=1 to run a MAUI app on an emulator.");
        var shell = f.Shell;
        Assert.SkipWhen(shell.Android.Adb is null || shell.Android.Emulator is null, "No Android SDK with an emulator.");

        var folder = f.CopySample("Maui");
        shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await shell.OpenSolutionAsync(Path.Combine(folder, "Maui.slnx"));
        var project = Path.Combine(folder, "MauiCounter", "MauiCounter.csproj");
        await ShellFixture.PumpUntil(() => shell.Solutions.Projects.TryGetValue(project, out var p) && p.State == "loaded", seconds: 300);
        Assert.True(shell.IsMobileApp(project));
        shell.SetStartupProject(project);

        await shell.RefreshDevicesAsync();
        var wanted = Environment.GetEnvironmentVariable("CODALUS_TEST_ANDROID_AVD");
        var device = shell.Devices.FirstOrDefault(d => d.Avd is not null && (wanted is null || d.Avd == wanted));
        Assert.SkipWhen(device is null, "No Android virtual device.");
        Assert.True(shell.Toolbar.HasDevices); // the toolbar shows the devices for a mobile startup project
        shell.Toolbar.Device = shell.Toolbar.Devices.Single(d => d.Id == device!.Id);
        return (folder, project, device!, !device!.IsRunning);
    }

    [AvaloniaFact]
    public async Task A_maui_app_runs_on_an_android_emulator_and_stops()
    {
        await using var f = ShellFixture.Create();
        var shell = f.Shell;
        var (_, project, device, startedHere) = await OpenAsync(f);

        string? serial = null;
        try
        {
            await shell.StartWithoutDebuggingAsync();
            await ShellFixture.PumpUntil(() => shell.Runs.Running.Any(r => r.Project == project), seconds: 600);
            serial = shell.Devices.First(d => d.Avd == device.Avd && d.IsRunning).Serial!;
            var pid = await shell.Android.ProcessIdAsync(serial, "com.companyname.mauicounter", TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.NotNull(pid);

            // Stop ends the log reader here and the app on the emulator.
            shell.Runs.Running.Single(r => r.Project == project).Stop();
            await ShellFixture.PumpUntil(() => shell.Runs.Running.All(r => r.Project != project), seconds: 60);
            Assert.Null(await shell.Android.ProcessIdAsync(serial, "com.companyname.mauicounter", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        finally
        {
            if (startedHere && serial is not null)
            {
                await shell.Android.AdbAsync(serial, ["emu", "kill"], CancellationToken.None);
            }
        }
    }

    [AvaloniaFact]
    public async Task A_maui_app_is_debugged_on_an_android_emulator()
    {
        // Experimental (M20b's checkpoint): the runtime's agent stalls or drops the session; run it while working on that.
        Assert.SkipUnless(Environment.GetEnvironmentVariable(ShellHost.MobileDebugVariable) == "1", "Set CODALUS_MOBILE_DEBUG=1 to try debugging on an emulator.");
        await using var f = ShellFixture.Create();
        var shell = f.Shell;
        var (folder, project, device, startedHere) = await OpenAsync(f);
        var page = Path.Combine(folder, "MauiCounter", "MainPage.xaml.cs");
        var line = Array.FindIndex(File.ReadAllLines(page), l => l.Contains("BindingContext = new CounterViewModel()", StringComparison.Ordinal)) + 1;
        shell.Debugger.Breakpoints.Toggle(page, line);

        string? serial = null;
        try
        {
            await shell.StartDebuggingAsync();
            await ShellFixture.PumpUntil(() => shell.Debugger.State == DebugState.Stopped, seconds: 600);
            serial = shell.Devices.First(d => d.Avd == device.Avd && d.IsRunning).Serial;
            await ShellFixture.PumpUntil(() => shell.CallStack.Frames.Count > 0, seconds: 60);
            Assert.Contains("MainPage", shell.CallStack.Frames[0].Name, StringComparison.Ordinal);
            var evaluated = await shell.Debugger.EvaluateAsync("GetType().Name", "watch", TestContext.Current.CancellationToken);
            Assert.Contains("MainPage", evaluated.Result, StringComparison.Ordinal);

            await shell.Debugger.ContinueAsync();
            await ShellFixture.PumpUntil(() => shell.Debugger.State == DebugState.Running, seconds: 30);
            await shell.Debugger.StopAsync();
            await ShellFixture.PumpUntil(() => shell.Debugger.State == DebugState.Idle, seconds: 60);
            Assert.Null(await shell.Android.ProcessIdAsync(serial!, "com.companyname.mauicounter", TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)); // the session's end stops the app
        }
        finally
        {
            if (startedHere && serial is not null)
            {
                await shell.Android.AdbAsync(serial, ["emu", "kill"], CancellationToken.None);
            }
        }
    }
}
