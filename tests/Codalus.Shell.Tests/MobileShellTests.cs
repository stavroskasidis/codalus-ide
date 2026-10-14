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

    /// <summary>
    /// M20c: on a Mac with the MAUI workloads, samples/Maui runs on this Mac (Mac Catalyst) and on an iOS simulator, and
    /// stops. Runs with <c>CODALUS_TEST_APPLE=1</c>; <c>CODALUS_TEST_IOS_SIMULATOR</c> picks the simulator by name.
    /// </summary>
    [AvaloniaFact]
    public async Task A_maui_app_runs_on_this_mac_and_an_ios_simulator()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("CODALUS_TEST_APPLE") == "1", "Set CODALUS_TEST_APPLE=1 on a Mac with the MAUI workloads.");
        await using var f = ShellFixture.Create();
        var shell = f.Shell;
        var folder = f.CopySample("Maui");
        shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await shell.OpenSolutionAsync(Path.Combine(folder, "Maui.slnx"));
        var project = Path.Combine(folder, "MauiCounter", "MauiCounter.csproj");
        await ShellFixture.PumpUntil(() => shell.Solutions.Projects.TryGetValue(project, out var p) && p.State == "loaded", seconds: 300);
        shell.SetStartupProject(project);
        await shell.RefreshDevicesAsync();

        // This Mac: the bundle's executable is the program.
        var mac = Assert.Single(shell.Devices, d => d.Id == AppleDevices.ThisMacId);
        shell.Toolbar.Device = shell.Toolbar.Devices.Single(d => d.Id == mac.Id);
        await shell.StartWithoutDebuggingAsync();
        await ShellFixture.PumpUntil(() => shell.Runs.Running.Any(r => r.Project == project), seconds: 900);
        await Task.Delay(3000, TestContext.Current.CancellationToken);
        Assert.Contains(shell.Runs.Running, r => r.Project == project); // still running: it started
        shell.Runs.Running.Single(r => r.Project == project).Stop();
        await ShellFixture.PumpUntil(() => shell.Runs.Running.All(r => r.Project != project), seconds: 60);

        // An iOS simulator: booted, installed, launched attached to its console, terminated by Stop.
        var wanted = Environment.GetEnvironmentVariable("CODALUS_TEST_IOS_SIMULATOR");
        var simulator = shell.Devices.FirstOrDefault(d => d.Platform == "ios" && (wanted is null || d.Label.StartsWith(wanted, StringComparison.Ordinal)));
        Assert.SkipWhen(simulator is null, "No iOS simulator.");
        shell.Toolbar.Device = shell.Toolbar.Devices.Single(d => d.Id == simulator!.Id);
        await shell.StartWithoutDebuggingAsync();
        await ShellFixture.PumpUntil(() => shell.Runs.Running.Any(r => r.Project == project), seconds: 900);
        shell.Runs.Running.Single(r => r.Project == project).Stop();
        await ShellFixture.PumpUntil(() => shell.Runs.Running.All(r => r.Project != project), seconds: 60);
    }

    /// <summary>
    /// M20d: MainPage.xaml of samples/Maui is MAUI XAML, and completion comes from the C# host's XAML engine with MAUI's
    /// types. Needs the MAUI workload (not on CI): runs with <c>CODALUS_TEST_MAUI=1</c>.
    /// </summary>
    [AvaloniaFact]
    public async Task Maui_xaml_completes_from_the_projects_types()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("CODALUS_TEST_MAUI") == "1", "Set CODALUS_TEST_MAUI=1 (with the MAUI workload) to open samples/Maui.");
        await using var f = ShellFixture.Create();
        var shell = f.Shell;
        var folder = f.CopySample("Maui");
        shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await shell.OpenSolutionAsync(Path.Combine(folder, "Maui.slnx"));
        var project = Path.Combine(folder, "MauiCounter", "MauiCounter.csproj");
        await ShellFixture.PumpUntil(() => shell.Solutions.Projects.TryGetValue(project, out var p) && p.State == "loaded", seconds: 300);
        await ShellFixture.PumpUntil(() => shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: Codalus.LanguageServices.Protocol.WorkspaceStates.Ready }, seconds: 300);

        var page = Path.Combine(folder, "MauiCounter", "MainPage.xaml");
        var document = await shell.OpenFileAsync(page, preview: false, null);
        await ShellFixture.PumpUntil(() => document?.Controller is not null);
        Assert.Equal(ShellHost.MauiXamlLanguageId, document!.Model.LanguageId);

        var model = document.Model;
        var editor = document.Controller!;
        var at = model.Text.Text.IndexOf("<Label", StringComparison.Ordinal);
        model.Text.Insert(at, "<Bu />" + Environment.NewLine + "            ");
        var asked = System.Diagnostics.Stopwatch.StartNew();
        editor.Editor.CaretOffset = at + 3;
        editor.ShowCompletion();
        string Log() => string.Join('\n', shell.Workbench.Output.GetOrCreate("codalus.languages", "Language Services").GetText()
            .Split('\n').Distinct().TakeLast(80))
            + "\nDesign-time inputs: " + string.Join("; ", shell.Solutions.CompilerInputs.Values.Select(i =>
                $"{Path.GetFileName(i.Path)} {i.TargetFramework} {i.State} {i.Error} ({i.Arguments.Length} arguments, {i.Arguments.Count(a => a.StartsWith("/additionalfile:", StringComparison.OrdinalIgnoreCase))} additional files)"));
        await PumpOrExplainAsync(() =>
        {
            if (editor.CompletionWindow is { } w && w.CompletionList.CompletionData.Any(d => d.Text == "Button"))
            {
                return true;
            }

            if (asked.Elapsed > TimeSpan.FromSeconds(10))
            {
                editor.CompletionWindow?.Close();
                editor.Editor.CaretOffset = at + 3;
                editor.ShowCompletion();
                asked.Restart();
            }

            return false;
        }, 360, Log);
        editor.CompletionWindow?.Close();
    }

    private static async Task PumpOrExplainAsync(Func<bool> condition, int seconds, Func<string> explain)
    {
        try
        {
            await ShellFixture.PumpUntil(condition, seconds);
        }
        catch (Exception e) when (e.Message.Contains("Timed out", StringComparison.Ordinal))
        {
            Assert.Fail("Timed out. The language service's log:\n" + explain());
        }
    }
}
