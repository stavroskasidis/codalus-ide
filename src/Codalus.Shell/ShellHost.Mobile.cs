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
        service.Changed += (_, _) => Ui.Post(() =>
        {
            if (ReferenceEquals(Mobile, service))
            {
                DevicesWindow.Refresh();
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
    public string? MobileRunFramework(ProjectInfo project) => MobileTargets.RunFramework(project, Solutions.ActiveFramework(project.Path), Workbench.Platform);

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

        Workbench.Context.Set("mobileProjects", MobileProjects().Count > 0);
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
        var platforms = project.TargetFrameworks.Select(MobileTargets.PlatformOf).Where(p => MobileTargets.RunsOn(p, Workbench.Platform)).Distinct().ToList();
        var current = MobileTargets.PlatformOf(MobileRunFramework(project));
        var items = new List<QuickPickItem>();
        foreach (var device in Mobile.Devices.Where(d => platforms.Contains(d.Platform)).OrderBy(d => d.Platform != current).ThenBy(d => d.Platform).ThenByDescending(d => d.IsRunning))
        {
            var chosen = Mobile.DeviceFor(device.Platform)?.Id == device.Id && device.Platform == current;
            items.Add(new QuickPickItem(device.Label) { Tag = device, Icon = "phone", Detail = device.Problem ?? (chosen ? Localizer.Get("msg.mobile.current", "current") : null) });
        }

        items.Add(new QuickPickItem(Localizer.Get("msg.mobile.openDevices", "Open the Devices window")) { Tag = "devices" });
        if (await Palette.ShowAsync(items, Localizer.Format("msg.mobile.pickDevice", "Run {0} on", project.Name)).ConfigureAwait(true) is not { } picked)
        {
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

    public void ShowDevices()
    {
        DevicesWindow.Refresh();
        Layout.ShowToolWindow(DevicesViewModel.ToolWindowId, focus: true);
        _ = RefreshDevicesAsync();
    }

    /// <summary>Starts an emulator or boots a simulator.</summary>
    public async Task<MobileDevice?> StartDeviceAsync(MobileDevice device)
    {
        try
        {
            if (device.Kind == MobileDeviceKind.Simulator)
            {
                await Mobile.BootSimulatorAsync(device, CancellationToken.None).ConfigureAwait(true);
                return Mobile.Devices.FirstOrDefault(d => d.Id == device.Id) ?? device;
            }

            StatusBar.ShowMessage(Localizer.Format("msg.mobile.starting", "Starting {0}…", device.Name), TimeSpan.FromSeconds(10));
            var started = await Mobile.StartEmulatorAsync(device, CancellationToken.None).ConfigureAwait(true);
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
    /// SDK builds what's needed, so there's no separate build first.
    /// </summary>
    public async Task StartOnDeviceAsync(string projectPath, bool debug)
    {
        if (!Solutions.Projects.TryGetValue(projectPath, out var project))
        {
            return;
        }

        if (MobileRunFramework(project) is not { } framework)
        {
            Workbench.Notifications.Warning(Localizer.Format("msg.mobile.noFramework", "{0} has no target framework this machine runs: Android runs everywhere, iOS and Mac Catalyst need a Mac.", project.Name));
            return;
        }

        if (project.MissingWorkloadPacks.Length > 0)
        {
            await OfferWorkloadsAsync(project).ConfigureAwait(true);
            return;
        }

        var platform = MobileTargets.PlatformOf(framework);
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
                ? Localizer.Get("msg.mobile.noDevices", "There's no Android device or emulator. Connect a phone with USB debugging on, or create an emulator with the Android SDK's Device Manager.")
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
        DebugLog.Clear();
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

    // ------------------------------------------------------------------ installing

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
    /// <c>dotnet workload restore</c> for the project, in the terminal (it may need an administrator, and the terminal
    /// is where <c>sudo</c> asks), after the user agrees to the exact command.
    /// </summary>
    public async Task InstallWorkloadsAsync(ProjectInfo project)
    {
        var command = $"dotnet workload restore \"{project.Path}\"";
        if (!await Dialogs.ConfirmAsync(
                Localizer.Get("msg.mobile.installWorkloadsTitle", "Install .NET Workloads"),
                Localizer.Format("msg.mobile.installWorkloadsMessage", "This installs the workloads {0} needs (they're large, and take a while).\n\nThis runs in the terminal:\n{1}", project.Name, command),
                Localizer.Get("msg.setup.runInTerminal", "Run in Terminal")).ConfigureAwait(true))
        {
            return;
        }

        SetupLog.AppendLine($"[{DateTime.Now:T}] {command}");
        RunInstallInTerminal(new InstallPlan("workloads", InstallKind.Terminal, Localizer.Get("msg.mobile.workloadsTerminal", "Install .NET workloads"), command));
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

        var plan = Mobile.InstallAndroidDependenciesPlan(EnvironmentCheck.FindDotnet() ?? "dotnet", project);
        var locations = Mobile.Android();
        if (!await Dialogs.ConfirmAsync(
                Localizer.Get("msg.mobile.installAndroidTitle", "Install the Android SDK"),
                Localizer.Format(
                    "msg.mobile.installAndroidMessage",
                    "This installs the Android SDK into {0}{1} through the Android workload. Installing accepts the Android SDK's licenses (https://developer.android.com/studio/terms).\n\nThis runs:\ndotnet {2}",
                    locations.Sdk ?? Mobile.Toolchain.DefaultSdkDirectory,
                    locations.Jdk is null ? Localizer.Format("msg.mobile.andJdk", ", and a JDK into {0},", Mobile.Toolchain.DefaultJdkDirectory) : string.Empty,
                    string.Join(' ', plan.ArgumentList!)),
                Localizer.Get("msg.setup.installNow", "Install")).ConfigureAwait(true))
        {
            return;
        }

        var program = Runs.StartPlan(project.Path + "#android-dependencies", Localizer.Get("msg.mobile.androidSetupChannel", "Android SDK Setup"), plan);
        if (program.Channel is { } channel)
        {
            Workbench.Output.Show(channel);
        }

        var code = await program.Exited.ConfigureAwait(true);
        if (code == 0)
        {
            await Workbench.Settings.SetAsync(MobileService.SdkSetting, locations.Sdk ?? Mobile.Toolchain.DefaultSdkDirectory, Codalus.Workbench.Settings.SettingScope.User, CancellationToken.None).ConfigureAwait(true);
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
