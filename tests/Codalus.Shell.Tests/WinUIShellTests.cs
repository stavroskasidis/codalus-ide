using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Codalus.Debugging;
using Codalus.Platform;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Mobile;
using Codalus.Workbench.Notifications;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.Tests;

/// <summary>
/// M44 (ADR 0074, D31) in the headless shell on samples/WindowsDesktop's WinUI 3 app (WinUIApp.slnx): it opens on every
/// OS with WinUI's XAML served by the C# host; off Windows, building and running it say Windows only; the device picker
/// lists MAUI's "Windows Machine" (Windows only elsewhere); a packaged app explains Developer Mode before anything
/// else. On Windows the unpackaged app runs, Hot Reloads and debugs (netcoredbg), and with Developer Mode the packaged
/// one is registered, started by Windows, followed and attached to.
/// </summary>
public sealed class WinUIShellTests
{
    private static async Task<(ShellFixture Fixture, string Project)> OpenAsync(bool packaged = false)
    {
        var f = ShellFixture.Create();
        var folder = f.CopySample("WindowsDesktop");
        var project = Path.Combine(folder, "WinUIApp", "WinUIApp.csproj");
        if (packaged)
        {
            File.WriteAllText(project, File.ReadAllText(project).Replace("<OutputType>WinExe</OutputType>", "<OutputType>WinExe</OutputType>\n    <WinUIAppPackaged>true</WinUIAppPackaged>", StringComparison.Ordinal));
        }

        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "WinUIApp.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 300);
        return (f, project);
    }

    private static ProjectInfo MauiApp(params string[] frameworks) =>
        new("/src/TodoApp/TodoApp.csproj", "TodoApp", ProjectStates.Loaded, null, frameworks, "Exe", [], [], [], true) { UsesMaui = true };

    [AvaloniaFact]
    public async Task A_winui_app_opens_with_its_xaml_and_off_windows_says_where_it_builds_and_runs()
    {
        var (f, project) = await OpenAsync();
        await using var _ = f;
        var info = f.Shell.Solutions.Projects[project];
        Assert.True(info.UsesWinUI);
        Assert.Equal("None", info.WindowsPackageType);
        Assert.True(WindowsDesktopProjects.RunsOnlyOnWindows(info));
        Assert.False(f.Shell.RunsAsPackage(info));

        // .xaml is XAML for the C# host, with x:Bind paths from the page's class.
        var editor = await f.Shell.OpenFileAsync(Path.Combine(Path.GetDirectoryName(project)!, "MainPage.xaml"), preview: false, null);
        await ShellFixture.PumpUntil(() => editor?.Controller is not null);
        Assert.Equal(ShellHost.XamlLanguageId, editor!.Model.LanguageId);
        var position = editor.Model.PositionAt(editor.Model.Text.Text.IndexOf("{x:Bind ViewModel.NewTitle,", StringComparison.Ordinal) + "{x:Bind ViewModel.".Length);
        await ShellFixture.RetryUntil(
            "the x:Bind path {x:Bind ViewModel.} in MainPage.xaml to complete the view model's IsTyping",
            async token => await f.Shell.Languages.ClientAsync(ShellHost.XamlLanguageId) is { } client ? await client.CompletionAsync(editor.Model.Uri, position, null, token) : null,
            completion => completion?.Items.Any(i => i.Label == "IsTyping") == true,
            ShellFixture.Describe);

        // Off Windows, Start, Start Without Debugging and Build say it's Windows only, and build nothing.
        f.Shell.SetStartupProject(project);
        if (!OperatingSystem.IsWindows())
        {
            var builds = f.Shell.Builds.LastBuild;
            await f.Shell.StartDebuggingAsync();
            await f.Shell.StartWithoutDebuggingAsync();
            Assert.False(await f.Shell.BuildProjectAsync(BuildTargets.Build, project));
            Assert.Equal(3, f.Shell.Workbench.Notifications.Items.Count(n => n.Message.Contains("WinUIApp is a WinUI 3 project: it builds, runs and debugs only on Windows", StringComparison.Ordinal)));
            Assert.Same(builds, f.Shell.Builds.LastBuild);
            Assert.Empty(f.Shell.Runs.Running);
        }

        // Add New Item: WinUI's items (built in), a page with its code-behind.
        var folder = Path.GetDirectoryName(project)!;
        var add = f.Shell.OpenAddNewItem(project, folder)!;
        await ShellFixture.PumpUntil(() => add.Templates.Any(t => t.Template.ShortName == "winuipage"), seconds: 60);
        Assert.Contains(add.Templates, t => t.Template.ShortName == "winuiwindow");
        Assert.DoesNotContain(add.Templates, t => t.Template.ShortName == "wpfwindow");
        add.Selected = add.Templates.First(t => t.Template.ShortName == "winuipage");
        add.Name = "SettingsPage";
        var created = await add.AddAsync();
        Assert.Equal(Path.Combine(folder, "SettingsPage.xaml"), created);
        Assert.Contains("x:Class=\"WinUIApp.SettingsPage\"", File.ReadAllText(created!), StringComparison.Ordinal);
        Assert.Contains("public sealed partial class SettingsPage : Page", File.ReadAllText(created + ".cs"), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task The_device_picker_lists_windows_machine_and_says_windows_only_elsewhere()
    {
        await using var f = ShellFixture.Create();
        var app = MauiApp("net10.0-android", "net10.0-windows10.0.19041.0");

        // Off Windows: listed, marked Windows only, not a choice.
        var items = f.Shell.DevicePickerItems(app, PlatformKind.Linux);
        var windows = Assert.Single(items, i => i.Label == "Windows Machine");
        Assert.StartsWith("Windows only", windows.Detail, StringComparison.Ordinal);
        Assert.False(Assert.IsType<MobileDevice>(windows.Tag).IsUsable);

        // On Windows (stand-in tools: no Android SDK): this machine, the current target when nothing is chosen.
        static Task<(int, string)> NoTools(string program, IReadOnlyList<string> arguments, string? directory, CancellationToken ct) => Task.FromResult((1, string.Empty));
        f.Shell.UseMobileService(new MobileService(PlatformKind.Windows, new AndroidToolchain(PlatformKind.Windows, _ => null, f.UserData, _ => false), NoTools, (_, _, _) => false, f.Shell.Workbench.Settings));
        await f.Shell.RefreshDevicesAsync();
        items = f.Shell.DevicePickerItems(app, PlatformKind.Windows);
        Assert.Equal("Windows Machine", items[0].Label);
        Assert.Equal("current", items[0].Detail);
        Assert.True(Assert.IsType<MobileDevice>(items[0].Tag).IsUsable);
        Assert.DoesNotContain(items, i => i.Detail?.StartsWith("Windows only", StringComparison.Ordinal) == true);

        // A MAUI app without a Windows framework has no Windows target anywhere.
        Assert.DoesNotContain(f.Shell.DevicePickerItems(MauiApp("net10.0-android"), PlatformKind.Linux), i => i.Label == "Windows Machine");
    }

    [AvaloniaFact]
    public async Task A_packaged_app_explains_developer_mode_first_and_has_no_hot_reload()
    {
        var (f, project) = await OpenAsync(packaged: true);
        await using var _ = f;
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects[project].WindowsPackageType == "MSIX", seconds: 120);
        var info = f.Shell.Solutions.Projects[project];
        Assert.True(f.Shell.RunsAsPackage(info));
        Assert.Contains("it's a packaged (MSIX) app", f.Shell.HotReloadUnsupportedReason(project), StringComparison.Ordinal);

        // Developer Mode off: said, with the Settings page one click away; nothing builds.
        var opened = new List<string>();
        f.Shell.DeveloperModeProbe = () => false;
        f.Shell.OpenWindowsSettings = opened.Add;
        var builds = f.Shell.Builds.LastBuild;
        var start = f.Shell.StartPackagedAppAsync(info, framework: null, debug: false);
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(n => n.Message.Contains("needs Developer Mode", StringComparison.Ordinal)));
        var notification = f.Shell.Workbench.Notifications.Items.Single(n => n.Message.Contains("needs Developer Mode", StringComparison.Ordinal));
        Assert.Contains("WinUIApp is a packaged (MSIX) app", notification.Message, StringComparison.Ordinal);
        f.Shell.Workbench.Notifications.Choose(notification, "Open Settings");
        await start;
        Assert.Equal([WindowsApps.DeveloperSettingsPage], opened);
        Assert.Same(builds, f.Shell.Builds.LastBuild);
        Assert.Empty(f.Shell.Runs.Running);
    }

    [AvaloniaFact]
    public async Task Windows_runs_hot_reloads_and_debugs_the_unpackaged_winui_app()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WinUI 3 apps build and run on Windows only (D31).");
        var (f, project) = await OpenAsync();
        await using var _ = f;
        f.Shell.SetStartupProject(project);

        // Start Without Debugging: built, then run as a program until it's stopped.
        await f.Shell.StartWithoutDebuggingAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Runs.Running.Any(p => p.Name == "WinUIApp" && !p.HasExited), seconds: 600);
        await Task.Delay(3000, TestContext.Current.CancellationToken);
        Assert.Contains(f.Shell.Runs.Running, p => p.Name == "WinUIApp" && !p.HasExited); // a window, still up
        f.Shell.StopRunning();
        await ShellFixture.PumpUntil(() => f.Shell.Runs.Running.All(p => p.HasExited), seconds: 60);

        // Hot Reload: dotnet watch runs it.
        await f.Shell.StartWithHotReloadAsync();
        await ShellFixture.PumpUntil(() => f.Shell.HotReloadProgram?.Channel?.GetText().Contains("dotnet watch", StringComparison.OrdinalIgnoreCase) == true, seconds: 600);
        f.Shell.StopRunning();
        await ShellFixture.PumpUntil(() => f.Shell.Runs.Running.All(p => p.HasExited), seconds: 60);

        // F5 (netcoredbg: WinUI apps are CoreCLR apps): a breakpoint in the view model's constructor.
        if (DebugAdapterLocator.Resolve() is not null || Environment.GetEnvironmentVariable("CI") == "true")
        {
            var model = Path.Combine(Path.GetDirectoryName(project)!, "ViewModels", "MainViewModel.cs");
            var line = Array.FindIndex(File.ReadAllLines(model), l => l.Contains("Items.Add(new TodoItem(\"Open the WinUI fixture\"))", StringComparison.Ordinal)) + 1;
            f.Shell.Debugger.Breakpoints.Set(new BreakpointSpec(model, line));
            f.Shell.Run("debug.start");
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped, seconds: 300);
            await ShellFixture.PumpUntil(() => f.Shell.CallStack.Frames.Count > 0);
            Assert.Contains("MainViewModel", f.Shell.CallStack.Frames[0].Name, StringComparison.Ordinal);
            f.Shell.Debugger.Breakpoints.Clear();
            f.Shell.Run("debug.stop");
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);
        }
    }

    [AvaloniaFact]
    public async Task Windows_registers_starts_and_attaches_to_the_packaged_winui_app()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Packaged (MSIX) apps register and start on Windows only (D31).");
        // It attaches the debugger: without netcoredbg it failed instead of skipping, as the other debugger tests do.
        Assert.SkipWhen(DebugAdapterLocator.Resolve() is null && Environment.GetEnvironmentVariable("CI") != "true", "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        var (f, project) = await OpenAsync(packaged: true);
        await using var _ = f;
        Assert.SkipUnless(f.Shell.DeveloperModeProbe() == true, "Developer Mode is off on this machine (CI's windows runner may not have it): registering a build's layout needs it. The explanation is A_packaged_app_explains_developer_mode_first_and_has_no_hot_reload.");
        try
        {
            await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects[project].WindowsPackageType == "MSIX", seconds: 120);
            f.Shell.SetStartupProject(project);

            // Start Without Debugging: built, registered from the build's layout, started by Windows, followed.
            await f.Shell.StartWithoutDebuggingAsync();
            var channel = f.Shell.Workbench.Output.Channels.Single(c => c.Id == "codalus.run.WinUIApp");
            await ShellFixture.PumpUntil(() => f.Shell.Runs.Running.Any(p => p.Name == "WinUIApp" && !p.HasExited)
                                              || f.Shell.Workbench.Notifications.Items.Any(n => n.Message.Contains("WinUIApp", StringComparison.Ordinal) && n.Message.Contains("failed", StringComparison.Ordinal)), seconds: 600);
            Assert.True(f.Shell.Runs.Running.Any(p => p.Name == "WinUIApp" && !p.HasExited), channel.GetText() + string.Join(" | ", f.Shell.Workbench.Notifications.Items.Select(n => n.Message)));
            Assert.Contains("Add-AppxPackage -Register", channel.GetText(), StringComparison.Ordinal);
            Assert.Contains("Codalus.Samples.WinUIApp_b1n3b429t8b64!App", channel.GetText(), StringComparison.Ordinal);
            await Task.Delay(3000, TestContext.Current.CancellationToken);
            f.Shell.StopRunning();
            await ShellFixture.PumpUntil(() => f.Shell.Runs.Running.All(p => p.HasExited), seconds: 60);

            // F5: started again, and netcoredbg attaches to its process; Break All stops it with a stack.
            // A start that fails says so in a notification (the build, the registration, the activation, the attach):
            // the wait ends there, and the failure shows the Run output, the Debug Console and the notifications.
            // The attach comes right after Windows starts the app, so it can be running before the app's Main: a Break
            // All then finds no thread with managed frames (PR #29's CI, the Call Stack's [External Code]). The pause
            // comes once the app runs its own code, its window shown (OnLaunched ran; its UI thread is in Main).
            var notified = f.Shell.Workbench.Notifications.Items.Select(n => n.Id).DefaultIfEmpty().Max();
            string Diagnostics() =>
                $"Debugger: {f.Shell.Debugger.State}\nRun output:\n{channel.GetText()}\nDebug Console:\n{f.Shell.DebugLog.GetText()}\nNotifications: {string.Join(" | ", f.Shell.Workbench.Notifications.Items.Select(n => n.Message))}";
            f.Shell.Run("debug.start");
            await WaitAsync(() => f.Shell.Debugger.State == DebugState.Running || f.Shell.Workbench.Notifications.Items.Any(n => n.Id > notified && n.Severity == NotificationSeverity.Error), seconds: 600);
            Assert.True(f.Shell.Debugger.State == DebugState.Running, Diagnostics());
            await ShellFixture.PumpUntil(() => HasWindow("WinUIApp") || f.Shell.Debugger.State != DebugState.Running, seconds: 120, describe: () => "the app's window didn't show.\n" + Diagnostics());
            Assert.True(f.Shell.Debugger.State == DebugState.Running, Diagnostics());
            f.Shell.Run("debug.pause");
            await WaitAsync(() => f.Shell.Debugger.State == DebugState.Stopped || f.Shell.Debugger.State == DebugState.Idle, seconds: 120);
            Assert.True(f.Shell.Debugger.State == DebugState.Stopped, Diagnostics());
            await ShellFixture.PumpUntil(() => f.Shell.CallStack.Frames.Any(r => !r.IsExternalCode), describe: () => $"the Call Stack shows {string.Join(", ", f.Shell.CallStack.Frames.Select(r => r.Name))}.\n{Stacks(f)}\n{Diagnostics()}");
            f.Shell.Run("debug.stop");
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);
        }
        finally
        {
            // What the test registered goes again (the framework package it depends on stays, as after a deployment).
            using var remove = Process.Start(new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "Get-Process -Name WinUIApp -ErrorAction SilentlyContinue | Stop-Process -Force; Get-AppxPackage -Name 'Codalus.Samples.WinUIApp' | Remove-AppxPackage"]) { UseShellExecute = false, CreateNoWindow = true });
            remove?.WaitForExit(60_000);
        }
    }

    /// <summary>Whether a process of that name shows a window (a fresh look each time: <see cref="Process.MainWindowHandle"/> is cached).</summary>
    private static bool HasWindow(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try
        {
            return processes.Any(p => p.MainWindowHandle != IntPtr.Zero);
        }
        catch (InvalidOperationException)
        {
            return false; // one exited meanwhile
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>The stop, for a failure: the thread shown, and each thread with its frame count (0: none, or unreadable).</summary>
    private static string Stacks(ShellFixture f)
    {
        var debugger = f.Shell.Debugger;
        IReadOnlyDictionary<int, IReadOnlyList<Codalus.Debugging.StackFrame>> stacks;
        try
        {
            // Off the UI thread: the debug service's requests don't need it.
            var read = Task.Run(() => debugger.AllStacksAsync(200, CancellationToken.None));
            stacks = read.Wait(TimeSpan.FromSeconds(30)) ? read.Result : new Dictionary<int, IReadOnlyList<Codalus.Debugging.StackFrame>>();
        }
        catch (AggregateException ex)
        {
            return $"Stop: {debugger.CurrentStop}. The threads' stacks couldn't be read: {ex.InnerException?.Message}";
        }

        return $"Stop: {debugger.CurrentStop}, thread {debugger.SelectedThreadId} shown with {debugger.Frames.Count} frames.\nThreads:\n"
            + string.Join("\n", debugger.Threads.Select(t => $"  {t.Id} {t.Name}: {(stacks.TryGetValue(t.Id, out var frames) ? $"{frames.Count} frames, top {frames[0].Name}" : "0 frames")}"));
    }

    /// <summary>A wait that ends at its timeout instead of failing there, so the assertion after it shows <c>Diagnostics()</c>.</summary>
    private static async Task WaitAsync(Func<bool> condition, int seconds)
    {
        try
        {
            await ShellFixture.PumpUntil(condition, seconds);
        }
        catch (Xunit.Sdk.FailException)
        {
        }
    }
}
