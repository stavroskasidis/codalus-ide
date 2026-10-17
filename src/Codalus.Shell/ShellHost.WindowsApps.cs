using Codalus.Workbench.Debugging;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Build;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Setup;

namespace Codalus.Shell;

/// <summary>
/// Windows App SDK apps (M44, ADR 0074, D31): WinUI 3 projects and .NET MAUI's Windows target, on Windows. Unpackaged
/// apps run and debug as programs (netcoredbg; Hot Reload through <c>dotnet watch</c>). A packaged (MSIX) app is built,
/// its layout registered with <c>Add-AppxPackage -Register</c> (Developer Mode, which is checked and explained first),
/// started through Windows' activation manager (the debug companion's <c>--activate-app</c>), and followed, or
/// debugged by attaching netcoredbg to its process.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>Runs PowerShell and the debug companion for packaged apps (tests give stand-ins).</summary>
    internal ProcessRunner WindowsAppRunner { get; set; } = (program, arguments, directory, cancellationToken) =>
        EnvironmentCheck.RunProcessAsync(program, arguments, directory, TimeSpan.FromMinutes(3), cancellationToken);

    /// <summary>Whether Developer Mode is on (null: unknown); tests give a stand-in.</summary>
    internal Func<bool?> DeveloperModeProbe { get; set; } = ReadDeveloperMode;

    /// <summary>Opens a page of Windows' Settings (tests record it).</summary>
    internal Action<string> OpenWindowsSettings { get; set; } = Codalus.Platform.ShellIntegration.OpenWindowsSettings;

    private static bool? ReadDeveloperMode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(WindowsApps.DeveloperModeKey);
            return WindowsApps.DeveloperMode(key?.GetValue(WindowsApps.DeveloperModeValue));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a run of <paramref name="info"/> starts a package: a WinUI 3 project, or a MAUI project running its Windows
    /// target, whose <c>WindowsPackageType</c> is <c>MSIX</c>.
    /// </summary>
    internal bool RunsAsPackage(ProjectInfo info) =>
        WindowsApps.IsPackaged(info.WindowsPackageType)
        && (info.UsesWinUI || (info.UsesMaui && Codalus.Workbench.Mobile.MobileTargets.PlatformOf(MobileRunFramework(info)) == Codalus.Workbench.Mobile.MobilePlatform.Windows));

    /// <summary>
    /// Starts a packaged WinUI 3 app (its project's <c>WindowsPackageType</c> is <c>MSIX</c>) the way packaged apps start,
    /// and returns true; false for every other project, which takes the usual path (MAUI's Windows target comes through
    /// the device path). Only on Windows in a local window: elsewhere the Windows-only gate has spoken already.
    /// </summary>
    internal async Task<bool> TryStartPackagedAppAsync(string project, bool debug)
    {
        if (Remote is not null || !OperatingSystem.IsWindows() || !Solutions.Projects.TryGetValue(project, out var info)
            || !info.UsesWinUI || !RunsAsPackage(info))
        {
            return false;
        }

        await StartPackagedAppAsync(info, framework: null, debug).ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// MAUI's Windows target on "Windows Machine" (M44): a packaged app as packaged apps start; an unpackaged one built
    /// for <paramref name="framework"/> alone (the other frameworks need their own workloads), then run, debugged with
    /// netcoredbg, or run under <c>dotnet watch</c> with <paramref name="hotReload"/>.
    /// </summary>
    internal async Task StartWindowsTargetAsync(ProjectInfo project, string framework, bool debug, bool hotReload)
    {
        if (WindowsApps.IsPackaged(project.WindowsPackageType))
        {
            await StartPackagedAppAsync(project, framework, debug).ConfigureAwait(true);
            return;
        }

        if (!hotReload && !await BuildFrameworkAsync(project, framework).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            if (!debug)
            {
                var program = await Runs.StartAsync(project.Path, RunsInConsoleWindow, CancellationToken.None, hotReload, targetFramework: framework).ConfigureAwait(true);
                OnHotReloadStarted(program);
                if (program.Channel is { } channel)
                {
                    Workbench.Output.Show(channel);
                }

                return;
            }

            var (info, profiles, error) = await Runs.GetProfilesAsync(project.Path, CancellationToken.None, framework).ConfigureAwait(true);
            var plan = await Runs.PlanAsync(info, Runs.ResolveProfile(project.Path, profiles), CancellationToken.None).ConfigureAwait(true);
            DebugLog.Clear();
            if (error is not null)
            {
                DebugLog.AppendLine("Ignoring the launch settings: " + error);
            }

            DebugLog.AppendLine($"> {plan.FileName} {plan.Arguments}".TrimEnd());
            Debugger.JustMyCode = Workbench.Settings.GetBoolean(JustMyCodeSetting, true);
            Debugger.StepFiltering = Workbench.Settings.GetBoolean(StepFilteringSetting, true);
            Layout.ShowToolWindow(ViewModels.DebugConsoleViewModel.ToolWindowId, focus: false);
            await Debugger.LaunchAsync([new DebugLaunch($"{project.Name} ({framework})", plan) { Console = ConsoleFor(info) }], CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or Codalus.Rpc.JsonRpcRemoteException or Codalus.Rpc.JsonRpcConnectionLostException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.build.couldNotStart", "Could not start {0}: {1}", project.Name, ex.Message));
        }
    }

    /// <summary>Builds one framework of a project (the build settings' properties, and <c>TargetFramework</c>).</summary>
    private async Task<bool> BuildFrameworkAsync(ProjectInfo project, string framework)
    {
        if (Builds.IsBuilding)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.build.aBuildIsAlreadyRunning", "A build is already running."), TimeSpan.FromSeconds(4));
            return false;
        }

        if (Workbench.Settings.GetBoolean(SaveAllBeforeBuildSetting, true))
        {
            await SaveAllAsync().ConfigureAwait(true);
        }

        Workbench.Output.Show(Builds.Channel);
        var result = await Builds.BuildAsync(BuildTargets.Build, project.Path, new Dictionary<string, string> { ["TargetFramework"] = framework }, CancellationToken.None).ConfigureAwait(true);
        if (!result.Succeeded && result is { Canceled: false, Errors: > 0 })
        {
            StatusBar.ShowMessage(Localizer.Get("msg.build.theBuildFailedSoTheProgram", "The build failed, so the program was not started."), TimeSpan.FromSeconds(6));
        }

        return result.Succeeded;
    }

    /// <summary>
    /// A packaged app: Developer Mode first (explained, with the Settings page), then the build, the layout's
    /// registration (with the Windows App SDK runtime's framework packages it depends on), the activation, and either
    /// following the process or attaching the debugger to it.
    /// </summary>
    internal async Task StartPackagedAppAsync(ProjectInfo project, string? framework, bool debug)
    {
        if (DeveloperModeProbe() != true)
        {
            var open = Localizer.Get("msg.windowsApps.openSettings", "Open Settings");
            if (await Workbench.Notifications.Warning(
                    Localizer.Format("msg.windowsApps.developerMode", "{0} is a packaged (MSIX) app: running it from its build output means registering that layout with Windows, which needs Developer Mode. Turn it on in Settings → System → For developers, or set WindowsPackageType to None to run it unpackaged.", project.Name),
                    open).Result.ConfigureAwait(true) == open)
            {
                OpenWindowsSettings(WindowsApps.DeveloperSettingsPage);
            }

            return;
        }

        if (!(framework is null ? await BuildBeforeRunAsync(project.Path).ConfigureAwait(true) : await BuildFrameworkAsync(project, framework).ConfigureAwait(true)))
        {
            return;
        }

        var channel = Workbench.Output.GetOrCreate("codalus.run." + project.Name, "Run: " + project.Name);
        channel.Clear();
        Workbench.Output.Show(channel);
        try
        {
            var (info, profiles, _) = await Runs.GetProfilesAsync(project.Path, CancellationToken.None, framework).ConfigureAwait(true);
            if (WindowsApps.LayoutManifest(info, File.Exists) is not { } manifest)
            {
                Workbench.Notifications.Error(Localizer.Format("msg.windowsApps.noLayout", "The build of {0} didn't write an AppxManifest.xml next to its output, so there's no package layout to register.", project.Name));
                return;
            }

            if (WindowsApps.ReadManifest(await File.ReadAllTextAsync(manifest).ConfigureAwait(true)) is not { Aumid: { } aumid } identity)
            {
                Workbench.Notifications.Error(Localizer.Format("msg.windowsApps.badManifest", "{0} has no package identity or application to start.", manifest));
                return;
            }

            var dependencies = WindowsApps.FrameworkPackages(
                project,
                WindowsApps.PackagesRoot(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                WindowsApps.Architecture(identity),
                folder => Directory.Exists(folder) ? Directory.EnumerateFiles(folder) : []);
            var frameworks = await Task.Run(() => dependencies.Select(path => new FrameworkPackage(path, WindowsApps.ReadPackageIdentity(path))).ToList()).ConfigureAwait(true);
            var register = WindowsApps.RegisterArguments(manifest, frameworks);
            channel.AppendLine(Localizer.Format("msg.windowsApps.registering", "Registering {0} ({1}) from {2}…", identity.Name, identity.Version, Path.GetDirectoryName(manifest)));
            channel.AppendLine("> powershell " + register[^1]);
            var (code, output) = await WindowsAppRunner("powershell.exe", register, Path.GetDirectoryName(manifest), CancellationToken.None).ConfigureAwait(true);
            if (output.Trim().Length > 0)
            {
                channel.AppendLine(output.TrimEnd());
            }

            if (code != 0)
            {
                Workbench.Notifications.Error(Localizer.Format("msg.windowsApps.registerFailed", "Registering {0}'s package layout failed (exit code {1}). {2}", project.Name, code, WindowsApps.RegisterFailureHint(output) ?? Localizer.Get("msg.windowsApps.seeRun", "Its Run output has the details.")));
                return;
            }

            if (DebugCompanion.Resolve() is not { } companion)
            {
                Workbench.Notifications.Error(Localizer.Get("msg.windowsApps.noCompanion", "The debug companion (codalus-debughost), which starts packaged apps, wasn't found next to Codalus IDE."));
                return;
            }

            var profile = Runs.ResolveProfile(project.Path, profiles);
            channel.AppendLine(Localizer.Format("msg.windowsApps.activating", "Starting {0}…", aumid));
            var (activated, activation) = await WindowsAppRunner(companion.FileName, [.. companion.Arguments, .. WindowsApps.ActivateArguments(aumid, profile?.CommandLineArgs)], null, CancellationToken.None).ConfigureAwait(true);
            if (activated != 0 || WindowsApps.ParseProcessId(activation) is not { } processId)
            {
                channel.AppendLine(activation.TrimEnd());
                Workbench.Notifications.Error(Localizer.Format("msg.windowsApps.activateFailed", "Windows didn't start {0}: {1}", project.Name, activation.Trim()));
                return;
            }

            channel.AppendLine(Localizer.Format("msg.windowsApps.started", "{0} is running (process {1}).", project.Name, processId));
            if (debug)
            {
                DebugLog.Clear();
                DebugLog.AppendLine($"> {aumid}: attaching to process {processId.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                Debugger.JustMyCode = Workbench.Settings.GetBoolean(JustMyCodeSetting, true);
                Debugger.StepFiltering = Workbench.Settings.GetBoolean(StepFilteringSetting, true);
                Layout.ShowToolWindow(ViewModels.DebugConsoleViewModel.ToolWindowId, focus: false);
                await Debugger.AttachAsync(processId, CancellationToken.None, name: $"{project.Name} (package)").ConfigureAwait(true);
            }
            else
            {
                Runs.Track(project.Path, project.Name, processId);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or ArgumentException or Codalus.Rpc.JsonRpcRemoteException or Codalus.Rpc.JsonRpcConnectionLostException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.build.couldNotStart", "Could not start {0}: {1}", project.Name, ex.Message));
        }
    }

    /// <summary>Check Environment's rows for the solution's Windows App SDK apps (on Windows): Developer Mode and the runtime.</summary>
    private async Task<IReadOnlyList<ToolStatus>> WindowsAppToolsAsync()
    {
        var projects = Solutions.Projects.Values.Where(p => p.State == ProjectStates.Loaded && WindowsApps.IsWindowsAppSdk(p)).ToList();
        if (projects.Count == 0 || Workbench.Platform != Codalus.Platform.PlatformKind.Windows)
        {
            return [];
        }

        IReadOnlyList<WindowsAppRuntime>? runtimes = null;
        try
        {
            var (code, output) = await WindowsAppRunner("powershell.exe", WindowsApps.RuntimeListArguments(), null, CancellationToken.None).ConfigureAwait(true);
            runtimes = code == 0 ? WindowsApps.ParseRuntimes(output) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Workbench.Log.AppendLine($"[setup] Listing the Windows App SDK runtimes failed: {ex.Message}");
        }

        return WindowsApps.ToolRows(projects, DeveloperModeProbe(), runtimes);
    }
}
