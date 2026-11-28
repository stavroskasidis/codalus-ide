using System.Diagnostics;
using Codalus.Projects.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Build;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Mobile;
using Codalus.Workbench.Setup;

namespace Codalus.Shell;

/// <summary>
/// .NET MAUI (M20a, ADR 0064): the device a mobile startup project runs on (the toolbar's picker), running it there
/// with the platform SDK's <c>Run</c> target, the Devices window, and the offers to install what's missing: the
/// project's workloads (<c>dotnet workload install</c> in the terminal) and the Android SDK and JDK (the Android
/// workload's own <c>InstallAndroidDependencies</c> target, after showing that it accepts the SDK's licenses).
/// </summary>
public sealed partial class ShellHost
{
    private readonly HashSet<string> _workloadsOffered = new(StringComparer.OrdinalIgnoreCase);
    private bool _devicesRead;

    public MobileService Mobile { get; private set; } = null!;

    public DevicesViewModel DevicesWindow { get; private set; } = null!;

    private void InitializeMobile()
    {
        Workbench.Settings.Register(MobileService.Descriptors(ContributionSource.Shell));
        DevicesWindow = new DevicesViewModel(this);
        InitializeAndroidTools();
        UseMobileService(new MobileService(
            Workbench.Platform,
            new AndroidToolchain(Workbench.Platform, Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            EnvironmentCheck.RunProcessAsync,
            StartDetached,
            Workbench.Settings));
        Solutions.ProjectChanged += (_, project) => Ui.Post(() => OnMobileProjectChanged(project));
        Solutions.SolutionChanged += (_, _) => Ui.Post(() =>
        {
            _workloadsOffered.Clear();
            UpdateToolbarDevice();
        });
    }

    /// <summary>Devices and runs go through <paramref name="service"/> (tests give one on stand-in tools).</summary>
    internal void UseMobileService(MobileService service)
    {
        Mobile = service;
        InitializeAndroidSdk();
        service.Changed += (_, _) => Ui.Post(() =>
        {
            if (ReferenceEquals(Mobile, service))
            {
                DevicesWindow.Refresh();
                AndroidDeviceManager?.OnDevicesChanged();
                DeviceLogWindow?.OnDevicesChanged();
                UpdateToolbarDevice();
            }
        });
    }

    /// <summary>The solution's loaded mobile projects (MAUI, or with an Android, iOS or Mac Catalyst framework).</summary>
    public IReadOnlyList<ProjectInfo> MobileProjects() =>
        [.. Solutions.Projects.Values.Where(p => p.State == ProjectStates.Loaded && MobileTargets.IsMobile(p))];

    /// <summary>Whether runs of <paramref name="project"/> go to a device (and the IDE's mobile path).</summary>
    public bool IsMobileProject(string project) => Solutions.Projects.TryGetValue(project, out var info) && MobileTargets.IsMobile(info);

    /// <summary>The framework a run of <paramref name="project"/> uses on this machine, or null.</summary>
    public string? MobileRunFramework(ProjectInfo project) =>
        MobileTargets.RunFramework(project, Solutions.ActiveFrameworks.GetValueOrDefault(project.Path), Workbench.Platform); // the chosen framework; none chosen: Windows first on Windows (M44)

    private void OnMobileProjectChanged(ProjectInfo project)
    {
        if (!MobileTargets.IsMobile(project) || project.State != ProjectStates.Loaded)
        {
            return;
        }

        if (!_devicesRead)
        {
            _devicesRead = true;
            _ = RefreshDevicesAsync();
        }

        UpdateToolbarDevice();
        if (project.MissingWorkloadPacks.Length > 0 && _workloadsOffered.Add(project.Path))
        {
            _ = OfferWorkloadsAsync(project);
        }
    }

    // ------------------------------------------------------------------ the device

    private void UpdateToolbarDevice()
    {
        if (Toolbar is null || Mobile is null)
        {
            return;
        }

        var mobileProjects = MobileProjects();
        Workbench.Context.Set("mobileProjects", mobileProjects.Count > 0);

        // The toolbar's Android and iOS groups (ADR 0104): while the solution has a project for that platform.
        Workbench.Context.Set("androidProjects", mobileProjects.Any(p => MobileTargets.FrameworkFor(p, MobilePlatform.Android) is not null));
        Workbench.Context.Set("iosProjects", Workbench.Platform == Codalus.Platform.PlatformKind.MacOS && mobileProjects.Any(p => MobileTargets.FrameworkFor(p, MobilePlatform.iOS) is not null));
        if (Runs.StartupProject is not { } startup || !Solutions.Projects.TryGetValue(startup, out var project) || !MobileTargets.IsMobile(project))
        {
            Workbench.Context.Set("mobileStartup", false);
            Toolbar.SetDevice(null);
            return;
        }

        Workbench.Context.Set("mobileStartup", true);
        var framework = MobileRunFramework(project);
        var platform = MobileTargets.PlatformOf(framework);
        Toolbar.SetDevice(framework is null ? Localizer.Get("msg.mobile.noTarget", "No target here")
            : Mobile.DeviceFor(platform) is { } device ? device.Name
            : platform == MobilePlatform.Android ? Localizer.Get("msg.mobile.noAndroidDevice", "No Android device") : MobileTargets.Title(platform));
    }

    public async Task RefreshDevicesAsync()
    {
        try
        {
            await Mobile.RefreshAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Workbench.Log.AppendLine($"[mobile] Reading the devices failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The toolbar's picker: the devices of the startup project's platforms this machine runs, and the frameworks to
    /// switch to; choosing a device of another platform makes that platform's framework the active one.
    /// </summary>
    public async Task SelectDeviceAsync()
    {
        if (Runs.StartupProject is not { } startup || !Solutions.Projects.TryGetValue(startup, out var project) || !MobileTargets.IsMobile(project))
        {
            Workbench.Notifications.Info(Localizer.Get("msg.mobile.notMobile", "The startup project isn't a .NET MAUI or mobile project."));
            return;
        }

        await RefreshDevicesAsync().ConfigureAwait(true);
        var current = MobileTargets.PlatformOf(MobileRunFramework(project));
        var items = DevicePickerItems(project, Workbench.Platform);
        if (await Palette.ShowAsync(items, Localizer.Format("msg.mobile.pickDevice", "Run {0} on", project.Name)).ConfigureAwait(true) is not { } picked)
        {
            return;
        }

        if (picked.Tag is MobileDevice { IsUsable: false } unusable)
        {
            Workbench.Notifications.Info(Localizer.Format("msg.mobile.unusable", "{0}: {1}", unusable.Name, unusable.Problem));
            return;
        }

        if (picked.Tag is MobileDevice selected)
        {
            Mobile.Select(selected);
            if (selected.Platform != current && MobileTargets.FrameworkFor(project, selected.Platform) is { } framework)
            {
                await SetActiveFrameworkAsync(project.Path, framework).ConfigureAwait(true);
            }

            UpdateToolbarDevice();
        }
        else
        {
            ShowDevices();
        }
    }

    /// <summary>
    /// The device picker's items for <paramref name="project"/> on <paramref name="host"/>: the devices of the platforms
    /// this machine runs, the current platform's first. A Windows framework off Windows lists "Windows Machine" as
    /// Windows only (D31, M44), so the target is visible but not chosen.
    /// </summary>
    internal List<QuickPickItem> DevicePickerItems(ProjectInfo project, Codalus.Platform.PlatformKind host)
    {
        var platforms = project.TargetFrameworks.Select(MobileTargets.PlatformOf).Where(p => MobileTargets.RunsOn(p, host)).Distinct().ToList();
        var current = MobileTargets.PlatformOf(MobileTargets.RunFramework(project, Solutions.ActiveFrameworks.GetValueOrDefault(project.Path), host));
        var items = new List<QuickPickItem>();
        foreach (var device in Mobile.Devices.Where(d => platforms.Contains(d.Platform)).OrderBy(d => d.Platform != current).ThenBy(d => d.Platform).ThenByDescending(d => d.IsRunning))
        {
            var chosen = Mobile.DeviceFor(device.Platform)?.Id == device.Id && device.Platform == current;
            items.Add(new QuickPickItem(device.Label) { Tag = device, Icon = device.Kind == MobileDeviceKind.ThisPc ? "window" : "phone", Detail = device.Problem ?? (chosen ? Localizer.Get("msg.mobile.current", "current") : null) });
        }

        if (host != Codalus.Platform.PlatformKind.Windows && MobileTargets.FrameworkFor(project, MobilePlatform.Windows) is { } windows)
        {
            var problem = Localizer.Format("msg.mobile.windowsOnly", "Windows only: {0} runs on Windows (from this OS, a Windows machine as a target comes later).", windows);
            items.Add(new QuickPickItem(MobileTargets.WindowsMachine.Label) { Tag = MobileTargets.WindowsMachine with { Problem = problem }, Icon = "window", Detail = problem });
        }

        items.Add(new QuickPickItem(Localizer.Get("msg.mobile.openDevices", "Open the Devices window")) { Tag = "devices" });
        return items;
    }

    public void ShowDevices()
    {
        DevicesWindow.Refresh();
        Layout.ShowToolWindow(DevicesViewModel.ToolWindowId, focus: true);
        _ = RefreshDevicesAsync();
    }

    /// <summary>Starts an emulator (from its cold state with <paramref name="coldBoot"/>) or boots a simulator.</summary>
    public async Task<MobileDevice?> StartDeviceAsync(MobileDevice device, bool coldBoot = false)
    {
        try
        {
            if (device.Kind == MobileDeviceKind.Simulator)
            {
                await Mobile.BootSimulatorAsync(device, CancellationToken.None).ConfigureAwait(true);
                return Mobile.Devices.FirstOrDefault(d => d.Id == device.Id) ?? device;
            }

            StatusBar.ShowMessage(Localizer.Format("msg.mobile.starting", "Starting {0}…", device.Name), TimeSpan.FromSeconds(10));
            var started = await Mobile.StartEmulatorAsync(device, CancellationToken.None, coldBoot: coldBoot).ConfigureAwait(true);
            if (started is null)
            {
                Workbench.Notifications.Warning(Localizer.Format("msg.mobile.didNotStart", "{0} didn't finish starting within 5 minutes.", device.Name));
            }

            return started;
        }
        catch (InvalidOperationException ex)
        {
            Workbench.Notifications.Error(ex.Message);
            return null;
        }
    }

    private static bool StartDetached(string program, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = false, RedirectStandardError = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[key] = value;
        }

        try
        {
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ running

    /// <summary>
    /// Runs a mobile project on its device: the workloads, the Android SDK and the device first (starting an emulator
    /// that isn't running), then <c>dotnet build -t:Run</c>, its output in the project's Run channel. The platform's
    /// SDK builds what's needed, so there's no separate build first. The Windows target ("Windows Machine", M44) runs as
    /// a desktop app instead: <see cref="StartWindowsTargetAsync"/>, with <paramref name="hotReload"/> when unpackaged.
    /// </summary>
    public async Task StartOnDeviceAsync(string projectPath, bool debug, bool hotReload = false)
    {
        if (!Solutions.Projects.TryGetValue(projectPath, out var project))
        {
            return;
        }

        if (MobileRunFramework(project) is not { } framework)
        {
            Workbench.Notifications.Warning(Localizer.Format("msg.mobile.noFrameworkHere", "{0} has no target framework this machine runs: Android runs everywhere, iOS and Mac Catalyst need a Mac, Windows needs Windows.", project.Name));
            return;
        }

        if (project.MissingWorkloadPacks.Length > 0)
        {
            await OfferWorkloadsAsync(project).ConfigureAwait(true);
            return;
        }

        var platform = MobileTargets.PlatformOf(framework);
        if (platform == MobilePlatform.Windows)
        {
            await StartWindowsTargetAsync(project, framework, debug, hotReload).ConfigureAwait(true);
            return;
        }

        if (platform == MobilePlatform.Android && !Mobile.Android().IsComplete)
        {
            var install = Localizer.Get("msg.mobile.installAndroid", "Install…");
            if (await Workbench.Notifications.Warning(Localizer.Format("msg.mobile.androidMissing", "{0} needs the Android SDK and a JDK to run on Android.", project.Name), install).Result.ConfigureAwait(true) == install)
            {
                await InstallAndroidDependenciesAsync().ConfigureAwait(true);
            }

            return;
        }

        if (Mobile.Devices.Count == 0)
        {
            await RefreshDevicesAsync().ConfigureAwait(true);
        }

        var device = Mobile.DeviceFor(platform);
        if (device is null && platform != MobilePlatform.MacCatalyst)
        {
            Workbench.Notifications.Warning(platform == MobilePlatform.Android
                ? Localizer.Get("msg.mobile.noDevices", "There's no Android device or emulator. Connect a phone with USB debugging on, or create an emulator in the Android Device Manager (Tools → Android).")
                : Localizer.Get("msg.mobile.noSimulators", "There's no iOS simulator or device. Add a simulator in Xcode (Window → Devices and Simulators)."));
            ShowDevices();
            return;
        }

        if (device is { IsUsable: false })
        {
            Workbench.Notifications.Warning(Localizer.Format("msg.mobile.unusable", "{0}: {1}", device.Name, device.Problem));
            return;
        }

        if (device is { IsRunning: false })
        {
            // An emulator that isn't running, or a shut-down simulator: started first, then chosen again by its new id.
            if (await StartDeviceAsync(device).ConfigureAwait(true) is null)
            {
                return;
            }

            device = Mobile.DeviceFor(platform) ?? device;
        }

        if (debug)
        {
            await DebugOnDeviceAsync(project, framework, device).ConfigureAwait(true);
            return;
        }

        var plan = Mobile.RunPlan(EnvironmentCheck.FindDotnet() ?? "dotnet", project, framework, device, Solutions.Solution?.Active?.Configuration);
        try
        {
            var program = Runs.StartPlan(project.Path, project.Name, plan);
            program.Channel?.AppendLine(Localizer.Format("msg.mobile.running", "Building and deploying {0} ({1}) to {2}…", project.Name, framework, device?.Label ?? MobileTargets.Title(platform)));
            if (program.Channel is { } channel)
            {
                Workbench.Output.Show(channel);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.build.couldNotStart", "Could not start {0}: {1}", project.Name, ex.Message));
        }
    }

    /// <summary>
    /// Debugs on the device (M20b, ADR 0065): the run with the platform's debugger properties builds, deploys and starts
    /// the app, and the Mono debugger adapter (<c>codalus-debughost --mono-dap</c>) connects to it, or waits for it to
    /// connect, through a free local port. A run that fails before the app is up cancels the session.
    /// </summary>
    private async Task DebugOnDeviceAsync(ProjectInfo project, string framework, MobileDevice? device)
    {
        int port;
        using (var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }

        var platform = MobileTargets.PlatformOf(framework);
        if (MobileTargets.RunsOnCoreClr(project, framework))
        {
            await DebugCoreClrOnDeviceAsync(project, framework, device).ConfigureAwait(true);
            return;
        }

        if (Mobile.DebugPlan(EnvironmentCheck.FindDotnet() ?? "dotnet", project, framework, device, Solutions.Solution?.Active?.Configuration, port, Debugger.JustMyCode) is not { } plan)
        {
            Workbench.Notifications.Info(Localizer.Get("msg.mobile.noDeviceDebugging", "Debugging on an iPhone or iPad isn't available yet: debug in a simulator, or run on the device without debugging (Ctrl+F5)."));
            return;
        }

        if (Codalus.Workbench.Debugging.DebugCompanion.Resolve() is not { } host)
        {
            Workbench.Notifications.Error(Localizer.Get("msg.mobile.noAdapter", "The Mono debugger adapter (codalus-debughost) wasn't found next to Codalus IDE."));
            return;
        }

        var adapter = new Codalus.Debugging.DebugAdapterStartInfo(host.FileName, [.. host.Arguments, "--mono-dap"], host.WorkingDirectory);
        var name = $"{project.Name} ({(device?.Name ?? MobileTargets.Title(platform))})";
        RunningProgram program;
        try
        {
            program = Runs.StartPlan(project.Path, project.Name, plan.Run);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.build.couldNotStart", "Could not start {0}: {1}", project.Name, ex.Message));
            return;
        }

        program.Channel?.AppendLine(Localizer.Format("msg.mobile.debugging", "Building and deploying {0} ({1}) to {2} for debugging…", project.Name, framework, device?.Label ?? MobileTargets.Title(platform)));
        if (program.Channel is { } channel)
        {
            Workbench.Output.Show(channel);
        }

        using var failed = new CancellationTokenSource();
        _ = program.Exited.ContinueWith(t => { if (t.Result != 0) { failed.Cancel(); } }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        ClearDebugLogForNewSession();
        DebugLog.AppendLine($"> {name}: the Mono debugger, port {port.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        try
        {
            await Debugger.LaunchAdapterAsync(name, adapter, System.Text.Json.JsonSerializer.SerializeToElement(plan.Adapter, Codalus.Debugging.DapJsonContext.Default.MonoDebugArguments), failed.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (failed.IsCancellationRequested)
        {
            Workbench.Notifications.Warning(Localizer.Format("msg.mobile.runFailed", "{0} didn't start, so debugging didn't either. Its Run output has the details.", project.Name));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or Codalus.Debugging.DapException or Codalus.Debugging.DapConnectionLostException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.mobile.couldNotDebug", "Could not debug {0}: {1}", project.Name, ex.Message));
            program.Stop();
        }
    }

    /// <summary>
    /// Debugs an app that runs on CoreCLR (.NET 11's default on mobile, ADR 0114), which the Mono debugger can't: in an
    /// iOS simulator the app is a process of this Mac, started by the run with the debugger's startup hook, and the
    /// Codalus debugger attaches to it once the hook reports. On Android the debugger runs on the device (ADR 0115,
    /// <see cref="DebugCoreClrOnAndroidAsync"/>). Elsewhere (an iPhone, Mac Catalyst) it says at once that it can't
    /// yet, instead of waiting for a Mono runtime that never connects.
    /// </summary>
    private async Task DebugCoreClrOnDeviceAsync(ProjectInfo project, string framework, MobileDevice? device)
    {
        var platform = MobileTargets.PlatformOf(framework);
        if (platform == MobilePlatform.Android && device is not null && MobileTargets.DebugsCoreClr(framework, device))
        {
            await DebugCoreClrOnAndroidAsync(project, framework, device).ConfigureAwait(true);
            return;
        }

        if (!MobileTargets.DebugsCoreClr(framework, device))
        {
            Workbench.Notifications.Info(platform == MobilePlatform.iOS
                ? Localizer.Get("msg.mobile.noDeviceDebugging", "Debugging on an iPhone or iPad isn't available yet: debug in a simulator, or run on the device without debugging (Ctrl+F5).")
                : Localizer.Format("msg.mobile.noCoreClrDebugging", "{0} runs on CoreCLR (.NET 11's default), which can't be debugged on {1} yet. Set <UseMonoRuntime>true</UseMonoRuntime> in the project to debug it on Mono, or run it without debugging (Ctrl+F5).", project.Name, MobileTargets.Title(platform)));
            return;
        }

        if (Debugger.StartupHook is not { } hook || !File.Exists(hook))
        {
            Workbench.Notifications.Error(Localizer.Get("msg.mobile.noHook", "The debugger's startup hook (Codalus.Debugging.StartupHook.dll) wasn't found next to Codalus IDE."));
            return;
        }

        (LaunchPlan Run, string WaitFolder, string Folder) plan;
        RunningProgram program;
        try
        {
            plan = Mobile.CoreClrDebugPlan(EnvironmentCheck.FindDotnet() ?? "dotnet", project, framework, device, Solutions.Solution?.Active?.Configuration, hook)!.Value;
            program = Runs.StartPlan(project.Path, project.Name, plan.Run);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.build.couldNotStart", "Could not start {0}: {1}", project.Name, ex.Message));
            return;
        }

        var name = $"{project.Name} ({(device?.Name ?? MobileTargets.Title(platform))})";
        program.Channel?.AppendLine(Localizer.Format("msg.mobile.debugging", "Building and deploying {0} ({1}) to {2} for debugging…", project.Name, framework, device?.Label ?? MobileTargets.Title(platform)));
        if (program.Channel is { } channel)
        {
            Workbench.Output.Show(channel);
        }

        // A run that ends before the app reported (a build error, a failed install) ends the wait: with an error, or
        // without one when the app never got to start.
        using var failed = new CancellationTokenSource();
        var reported = false;
        _ = program.Exited.ContinueWith(_ => { if (!Volatile.Read(ref reported)) { failed.Cancel(); } }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        DebugLog.Clear();
        DebugLog.AppendLine($"> {name}: the Codalus debugger");
        try
        {
            await Debugger.LaunchWaitingAsync(name, plan.WaitFolder, failed.Token).ConfigureAwait(true);
            Volatile.Write(ref reported, true);
        }
        catch (OperationCanceledException) when (failed.IsCancellationRequested)
        {
            Workbench.Notifications.Warning(Localizer.Format("msg.mobile.runFailed", "{0} didn't start, so debugging didn't either. Its Run output has the details.", project.Name));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or Codalus.Debugging.DapException or Codalus.Debugging.DapConnectionLostException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.mobile.couldNotDebug", "Could not debug {0}: {1}", project.Name, ex.Message));
            program.Stop();
        }
        finally
        {
            // The app has the hook loaded by now, or never will: the copy and the wait folder go when the run ends.
            var folder = plan.Folder;
            _ = program.Exited.ContinueWith(_ => RemoveDebugFolder(folder), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// <summary>How long F5 waits for an Android app to report: a build and an install.</summary>
    private static readonly TimeSpan s_androidReportWait = TimeSpan.FromMinutes(15);

    /// <summary>How long an Android app may run without reporting before F5 says it won't (a Release build).</summary>
    private static readonly TimeSpan s_androidUnheldWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Debugs a CoreCLR app on a running Android device or emulator (ADR 0115). The run (<c>dotnet run</c>) builds,
    /// installs and starts the app with the debugger's startup hook, which holds it in a folder of its own storage.
    /// Once it has reported there the debugger is copied into the app (Android empties that folder at each install),
    /// started as the app's user through adb, and attaches; the hook goes on once the breakpoints are set.
    /// </summary>
    private async Task DebugCoreClrOnAndroidAsync(ProjectInfo project, string framework, MobileDevice device)
    {
        if (Debugger.StartupHook is not { } hook || !File.Exists(hook))
        {
            Workbench.Notifications.Error(Localizer.Get("msg.mobile.noHook", "The debugger's startup hook (Codalus.Debugging.StartupHook.dll) wasn't found next to Codalus IDE."));
            return;
        }

        if (Mobile.Android().Adb is not { } adbPath || Mobile.Adb((program, arguments, limit, ct) => EnvironmentCheck.RunProcessAsync(program, arguments, null, limit, ct)) is not { } adb)
        {
            Workbench.Notifications.Error(Localizer.Get("msg.mobile.noAdb", "adb wasn't found in the Android SDK, so the app can't be debugged."));
            return;
        }

        var android = new AndroidDebugging(adb, Paths.Cache);
        var bundlePlatform = await android.PlatformAsync(device.Id, CancellationToken.None).ConfigureAwait(true);
        if (bundlePlatform is null)
        {
            Workbench.Notifications.Info(Localizer.Format("msg.mobile.androidAbi", "The Codalus debugger runs on 64-bit Android (arm64 and x86_64), and {0} isn't: run the app without debugging (Ctrl+F5), or set <UseMonoRuntime>true</UseMonoRuntime> to debug it on Mono.", device.Name));
            return;
        }

        if (Codalus.Workbench.Debugging.Remote.RemoteDebuggers.CodalusBundle(bundlePlatform) is not { } bundle)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.mobile.noAndroidDebugger", "The Codalus debugger for {0} isn't installed with Codalus IDE (in a source checkout: eng/get-codalus-debugger.sh {0}).", bundlePlatform));
            return;
        }

        string? package = null;
        try
        {
            var properties = await ProjectManagement.GetPropertiesAsync(project.Path, ["ApplicationId"], Solutions.Solution?.Active?.Configuration, CancellationToken.None).ConfigureAwait(true);
            package = properties.Properties.FirstOrDefault(p => p.Name == "ApplicationId")?.Value is { Length: > 0 } id ? id.Trim() : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Codalus.Rpc.JsonRpcRemoteException or Codalus.Rpc.JsonRpcConnectionLostException)
        {
        }

        if (package is null)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.mobile.noPackage", "{0} has no ApplicationId, the app's package name, which debugging on Android needs.", project.Name));
            return;
        }

        var wait = AndroidDebugging.WaitFolder(package, Guid.NewGuid().ToString("N")[..12]);
        RunningProgram program;
        try
        {
            var plan = Mobile.AndroidCoreClrDebugPlan(EnvironmentCheck.FindDotnet() ?? "dotnet", project, framework, device, Solutions.Solution?.Active?.Configuration, hook, wait);
            program = Runs.StartPlan(project.Path, project.Name, plan);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.build.couldNotStart", "Could not start {0}: {1}", project.Name, ex.Message));
            return;
        }

        var name = $"{project.Name} ({device.Name})";
        program.Channel?.AppendLine(Localizer.Format("msg.mobile.debugging", "Building and deploying {0} ({1}) to {2} for debugging…", project.Name, framework, device.Label));
        if (program.Channel is { } channel)
        {
            Workbench.Output.Show(channel);
        }

        // The app reports once it is installed and started; a run that ends first (a build error) ends the wait.
        using var failed = new CancellationTokenSource();
        var reported = false;
        _ = program.Exited.ContinueWith(_ => { if (!Volatile.Read(ref reported)) { failed.Cancel(); } }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        try
        {
            if (await WaitForAndroidAppAsync(android, device.Id, package, wait, failed.Token).ConfigureAwait(true) is not { } reason)
            {
                await android.EnsureDebuggerAsync(device.Id, package, bundle, () => program.Channel?.AppendLine(Localizer.Get("msg.mobile.copyingDebugger", "Copying the Codalus debugger to the device (once per version of Codalus IDE)…")), failed.Token).ConfigureAwait(true);
                ClearDebugLogForNewSession();
                DebugLog.AppendLine($"> {name}: the Codalus debugger on the device");
                await Debugger.LaunchWaitingAsync(name, wait, AndroidDebugging.Adapter(adbPath, device.Id, package, bundle.Id, log: Environment.GetEnvironmentVariable(Codalus.Debugging.CoreClrAdapter.LogVariable) is { Length: > 0 }), failed.Token).ConfigureAwait(true);
                Volatile.Write(ref reported, true);
                return;
            }

            Workbench.Notifications.Warning(reason);
        }
        catch (OperationCanceledException) when (failed.IsCancellationRequested)
        {
            Workbench.Notifications.Warning(Localizer.Format("msg.mobile.runFailed", "{0} didn't start, so debugging didn't either. Its Run output has the details.", project.Name));
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidDataException or Codalus.Debugging.DapException or Codalus.Debugging.DapConnectionLostException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.mobile.couldNotDebug", "Could not debug {0}: {1}", project.Name, ex.Message));
        }

        // Not debugged: the app would wait for the debugger.
        program.Stop();
        _ = android.RemoveWaitFolderAsync(device.Id, package, wait, CancellationToken.None);
    }

    /// <summary>
    /// Waits for the Android app to report in <paramref name="wait"/>: null once it has, else why it won't (it runs
    /// without having waited: a Release build, or one whose startup hooks are off; or the wait is over).
    /// </summary>
    private static async Task<string?> WaitForAndroidAppAsync(AndroidDebugging android, string serial, string package, string wait, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan? running = null;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(true);
            if (await android.ReportedProcessAsync(serial, package, wait, cancellationToken).ConfigureAwait(true) is not null)
            {
                return null;
            }

            if (await android.RunningProcessAsync(serial, package, cancellationToken).ConfigureAwait(true) is null)
            {
                running = null;
            }
            else if (running is null)
            {
                running = started.Elapsed;
            }
            else if (started.Elapsed - running > s_androidUnheldWait)
            {
                return Localizer.Format("msg.mobile.androidUnheld", "{0} started without waiting for the debugger: a Release build, or one with startup hooks turned off, isn't debugged. Debug a Debug build.", package);
            }

            if (started.Elapsed > s_androidReportWait)
            {
                return Localizer.Format("msg.mobile.androidNoReport", "{0} didn't start in {1} minutes, so debugging didn't either.", package, s_androidReportWait.TotalMinutes.ToString("0", System.Globalization.CultureInfo.CurrentCulture));
            }
        }
    }

    private static void RemoveDebugFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ------------------------------------------------------------------ installing

    /// <summary>Tests: whether the user can write the .NET root, in place of trying (<see cref="WorkloadCommand"/>).</summary>
    internal Func<string, bool> CanWriteDotnet { get; set; } = Elevation.CanWrite;

    /// <summary>
    /// A <c>dotnet workload</c> command line for the terminal, under <c>sudo</c> when the root of the <c>dotnet</c> it
    /// runs can't be written by the user (<see cref="Elevation.WorkloadCommand"/>): the line, and that root when sudo is
    /// in it.
    /// </summary>
    private (string Command, string? SudoRoot) WorkloadCommand(params string[] arguments)
    {
        var dotnet = EnvironmentCheck.FindDotnet();
        var root = dotnet is null ? null : Path.GetDirectoryName(EnvironmentCheck.RealPath(dotnet));
        var sudo = Workbench.Platform != Codalus.Platform.PlatformKind.Windows && root is not null && !CanWriteDotnet(root);
        return (Elevation.WorkloadCommand(Workbench.Platform, dotnet, !sudo, arguments), sudo ? root : null);
    }

    /// <summary>Offers <c>dotnet workload restore</c> for a project whose workloads aren't installed.</summary>
    private async Task OfferWorkloadsAsync(ProjectInfo project)
    {
        var workloads = MobileTargets.MissingWorkloads(project, [], Workbench.Platform);
        var install = Localizer.Get("msg.mobile.installWorkloads", "Install Workloads");
        var message = Localizer.Format("msg.mobile.workloadsMissing", "{0} needs .NET workloads that aren't installed ({1}).", project.Name, workloads.Count > 0 ? string.Join(", ", workloads) : string.Join(", ", project.MissingWorkloadPacks));
        if (await Workbench.Notifications.Warning(message, install).Result.ConfigureAwait(true) == install)
        {
            await InstallWorkloadsAsync(project).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// <c>dotnet workload restore</c> for the project, in the terminal, after the user agrees to the exact command. In
    /// a .NET root only an administrator can change it runs under <c>sudo</c>, which asks in the terminal
    /// (<see cref="WorkloadCommand"/>). It runs in the project's folder: the SDK its <c>global.json</c> picks is the one
    /// whose workloads it needs.
    /// </summary>
    public async Task InstallWorkloadsAsync(ProjectInfo project)
    {
        var (command, sudoRoot) = WorkloadCommand("workload", "restore", project.Path);
        var elevation = sudoRoot is null ? string.Empty
            : Localizer.Format("msg.mobile.androidSudo", "\n\n{0} can only be changed by an administrator: it runs with sudo in the terminal.", sudoRoot);
        if (!await Dialogs.ConfirmAsync(
                Localizer.Get("msg.mobile.installWorkloadsTitle", "Install .NET Workloads"),
                Localizer.Format("msg.mobile.installWorkloadsMessage", "This installs the workloads {0} needs (they're large, and take a while).\n\nThis runs in the terminal:\n{1}", project.Name, command) + elevation,
                Localizer.Get("msg.setup.runInTerminal", "Run in Terminal")).ConfigureAwait(true))
        {
            return;
        }

        SetupLog.AppendLine($"[{DateTime.Now:T}] {command}");
        RunInstallInTerminal(new InstallPlan("workloads", InstallKind.Terminal, Localizer.Get("msg.mobile.workloadsTerminal", "Install .NET workloads"), command), Path.GetDirectoryName(project.Path));
        Workbench.Notifications.Info(Localizer.Get("msg.mobile.workloadsStarted", "The workloads install in the terminal. When it's done, reload the project (or reopen the solution)."));
    }

    /// <summary>
    /// Installs the Android SDK (and a JDK when none is found) through the Android workload's own target, after the
    /// user agrees: installing accepts the Android SDK's licenses, which the dialog says and links.
    /// </summary>
    public async Task InstallAndroidDependenciesAsync()
    {
        var project = MobileProjects().FirstOrDefault(p => MobileTargets.FrameworkFor(p, MobilePlatform.Android) is not null && p.MissingWorkloadPacks.Length == 0);
        if (project is null)
        {
            Workbench.Notifications.Info(Localizer.Get("msg.mobile.needsAndroidProject", "Installing the Android SDK goes through the Android workload: open a project with an Android target framework, with its workloads installed."));
            return;
        }

        var locations = Mobile.Android();
        var sdk = locations.Sdk ?? Mobile.Toolchain.DefaultSdkDirectory;

        // An SDK only an administrator can change (the classic IDE's, under Program Files) is changed elevated (ADR 0104),
        // and then the restore's files go to a temporary folder rather than the project's obj, where they'd be theirs.
        var writable = CanWriteSdk(sdk);
        var intermediate = writable ? null : Path.Combine(Path.GetTempPath(), "codalus-android-dependencies", Guid.NewGuid().ToString("N"));
        var command = Mobile.InstallAndroidDependenciesCommand(EnvironmentCheck.FindDotnet() ?? "dotnet", project, intermediate);
        var elevation = writable ? string.Empty
            : Workbench.Platform == Codalus.Platform.PlatformKind.Windows ? Localizer.Format("msg.mobile.androidElevated", "\n\n{0} can only be changed by an administrator: Windows will ask for permission.", sdk)
            : Localizer.Format("msg.mobile.androidSudo", "\n\n{0} can only be changed by an administrator: it runs with sudo in the terminal.", sdk);
        if (!await Dialogs.ConfirmAsync(
                Localizer.Get("msg.mobile.installAndroidTitle", "Install the Android SDK"),
                Localizer.Format(
                    "msg.mobile.installAndroidMessage",
                    "This installs the Android SDK into {0}{1} through the Android workload. Installing accepts the Android SDK's licenses (https://developer.android.com/studio/terms).\n\nThis runs:\ndotnet {2}",
                    sdk,
                    locations.Jdk is null ? Localizer.Format("msg.mobile.andJdk", ", and a JDK into {0},", Mobile.Toolchain.DefaultJdkDirectory) : string.Empty,
                    string.Join(' ', command.Arguments)) + elevation,
                Localizer.Get("msg.setup.installNow", "Install")).ConfigureAwait(true))
        {
            return;
        }

        int? code;
        try
        {
            code = await RunSdkChangeAsync(command, sdk, Localizer.Get("msg.mobile.androidSetupChannel", "Android SDK Setup")).ConfigureAwait(true);
        }
        finally
        {
            if (intermediate is not null)
            {
                try
                {
                    Directory.Delete(intermediate, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        if (code is null)
        {
            return;
        }

        if (code == 0)
        {
            await Workbench.Settings.SetAsync(MobileService.SdkSetting, sdk, Codalus.Workbench.Settings.SettingScope.User, CancellationToken.None).ConfigureAwait(true);
            if (locations.Jdk is null)
            {
                await Workbench.Settings.SetAsync(MobileService.JdkSetting, Mobile.Toolchain.DefaultJdkDirectory, Codalus.Workbench.Settings.SettingScope.User, CancellationToken.None).ConfigureAwait(true);
            }

            Workbench.Notifications.Info(Localizer.Get("msg.mobile.androidInstalled", "The Android SDK is installed."));
            await RefreshDevicesAsync().ConfigureAwait(true);
        }
        else
        {
            Workbench.Notifications.Error(Localizer.Format("msg.mobile.androidFailed", "Installing the Android SDK failed (exit code {0}). Its output has the details.", code));
        }
    }
}
