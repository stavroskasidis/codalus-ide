using System.Diagnostics;
using Codalus.Platform;
using Codalus.Rpc;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Mobile;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Setup;

namespace Codalus.Shell;

/// <summary>
/// Environment setup (M37): what's installed of the tools Codalus IDE uses, and installing what's missing with the
/// user's consent. When the IDE starts, a missing .NET SDK or git is offered; when a solution opens, a <c>global.json</c>
/// SDK that isn't installed is. Help → Check Environment lists everything with a fix. Every install shows its exact
/// command first; what needs a password runs in the integrated terminal, where the installer asks itself.
/// </summary>
public sealed partial class ShellHost
{
    private EnvironmentCheck? _environmentCheck;

    public EnvironmentCheck EnvironmentCheck => _environmentCheck ??= new EnvironmentCheck(Workbench.Platform, Environment.GetEnvironmentVariable)
    {
        // What the hosts next to the shell ask for (M49): the IDE's own runtime requirement.
        HostFrameworks = IdeRuntime.Requirements(AppContext.BaseDirectory, Environment.GetEnvironmentVariable),
    };

    /// <summary>Tests: an environment check on a described machine.</summary>
    internal void UseEnvironmentCheck(EnvironmentCheck check) => _environmentCheck = check;

    private readonly Lock _offerGate = new();

    /// <summary>The runtime notification while it shows (M49): reports meanwhile don't add another.</summary>
    private Workbench.Notifications.Notification? _runtimeNotification;

    /// <summary>The missing-SDK notification while it shows: its install brings the runtime, so it stands for both.</summary>
    private Workbench.Notifications.Notification? _sdkNotification;

    /// <summary>
    /// Whether a notification shows: unanswered, and still a toast (or never one: the Notifications window was open). One
    /// hidden with Escape or pushed out by newer toasts sits in the center unseen, so a new report shows it again.
    /// </summary>
    private bool Showing([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] Workbench.Notifications.Notification? notification) =>
        notification is { Result.IsCompleted: false } && !Notifications.HasLeftToasts(notification);

    /// <summary>A solution that couldn't open because the build host's runtime is missing: opened again after the install.</summary>
    private string? _solutionWaitingForRuntime;

    /// <summary>Tests: the solution that opens again once the runtime is installed.</summary>
    internal string? SolutionWaitingForRuntime => _solutionWaitingForRuntime;

    /// <summary>The last check's results.</summary>
    public IReadOnlyList<ToolStatus> Tools { get; private set; } = [];

    public event EventHandler? ToolsChanged;

    private Workbench.Output.OutputChannel SetupLog => Workbench.Output.GetOrCreate("codalus.setup", Localizer.Get("msg.setup.channel", "Environment Setup"));

    /// <summary>
    /// A .NET SDK that isn't on this process's <c>PATH</c> (one in the user's <c>~/.dotnet</c> from dotnet-install or
    /// M37's own installs, or <c>/usr/local/share/dotnet</c> for an app started from the macOS Finder, whose PATH is the
    /// system's minimal one) is found where the installers put it: the IDE's processes, the build host's MSBuildLocator
    /// first, get it as <c>DOTNET_ROOT</c> and on <c>PATH</c>.
    /// </summary>
    /// <remarks>
    /// M49: <see cref="EnvironmentCheck.ChooseDotnetRoot"/> decides. The app hosts (which don't look on the PATH) are
    /// pointed at the chosen root only when that helps them find their runtime, and through the architecture's own
    /// <c>DOTNET_ROOT_ARM64</c> too when that one is set elsewhere. Run again after an install of .NET.
    /// </remarks>
    private void UseUserDotnet()
    {
        if (EnvironmentCheck.ChooseDotnetRoot() is not { } choice)
        {
            Workbench.Log.AppendLine("[setup] No .NET SDK found (DOTNET_ROOT, PATH, or where the installers put it); Help → Check Environment offers to install one.");
            return;
        }

        ApplyDotnetRoot(choice);
        if (EnvironmentCheck.HostRuntimeStatus() is { NeedsAttention: true, Detail: { } missing })
        {
            Workbench.Log.AppendLine($"[setup] {missing}");
        }
    }

    private void AddDotnetToEnvironment(string folder) =>
        ApplyDotnetRoot(new DotnetRootChoice(folder, AddToPath: true, EnvironmentCheck.PointHostsAt(folder), $"Using the .NET SDK in {folder} (DOTNET_ROOT)."));

    private void ApplyDotnetRoot(DotnetRootChoice choice)
    {
        foreach (var (name, value) in choice.Variables)
        {
            if (name != "DOTNET_ROOT")
            {
                Workbench.Log.AppendLine($"[setup] {name} was {Environment.GetEnvironmentVariable(name)}: it's {value} now, or the helper processes would still look there first.");
            }

            Environment.SetEnvironmentVariable(name, value);
        }

        if (choice.AddToPath)
        {
            Environment.SetEnvironmentVariable("PATH", choice.Root + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        }

        if (choice.Log is { } line)
        {
            Workbench.Log.AppendLine($"[setup] {line}");
        }
    }

    /// <summary>Whether <see cref="Tools"/> has every tool, not only the required ones startup checks.</summary>
    public bool ToolsComplete { get; private set; }

    /// <summary>Checks the environment; <paramref name="offer"/> offers to install what the IDE needs and is missing.</summary>
    /// <param name="requiredOnly">The required tools alone (startup: what it offers to install).</param>
    public async Task<IReadOnlyList<ToolStatus>> CheckEnvironmentAsync(bool offer, bool requiredOnly = false)
    {
        try
        {
            Tools = requiredOnly
                ? await EnvironmentCheck.CheckRequiredAsync(Solutions.Solution?.Directory, CancellationToken.None).ConfigureAwait(true)
                : [.. await EnvironmentCheck.CheckAsync(Solutions.Solution?.Directory, CancellationToken.None).ConfigureAwait(true), .. await MobileToolsAsync().ConfigureAwait(true), .. await WindowsAppToolsAsync().ConfigureAwait(true), .. NetFrameworkTools()];
            ToolsComplete = !requiredOnly;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Workbench.Log.AppendLine($"[setup] The environment check failed: {ex.Message}");
            return Tools;
        }

        ToolsChanged?.Invoke(this, EventArgs.Empty);
        foreach (var tool in Tools)
        {
            Workbench.Log.AppendLine($"[setup] {tool.Title}: {tool.State}{(tool.Version is { } v ? " " + v : string.Empty)}{(tool.Path is { } p ? " (" + p + ")" : string.Empty)}");
        }

        if (offer)
        {
            // Without any SDK, installing one brings the runtime too: the runtime isn't offered on its own then.
            var noSdk = Tools.Any(t => t is { Id: "dotnet", State: ToolState.Missing });
            foreach (var tool in Tools.Where(t => t is { Required: true, NeedsAttention: true } && !(noSdk && t.Id == "dotnet-runtime")))
            {
                _ = OfferInstallAsync(tool);
            }
        }

        return Tools;
    }

    private async Task OfferInstallAsync(ToolStatus tool)
    {
        if (tool.Id == "dotnet-runtime")
        {
            OfferHostRuntime(tool);
            return;
        }

        if (tool is { Id: "dotnet", State: ToolState.Missing })
        {
            OfferSdk(tool);
            return;
        }

        var message = tool switch
        {
            { Id: "dotnet", State: ToolState.Mismatch } => Localizer.Format("msg.setup.sdkMismatch", "{0} Codalus IDE uses another SDK for now.", tool.Detail),
            { Id: "dotnet" } => Localizer.Get("msg.setup.sdkMissing", "The .NET SDK isn't installed. Codalus IDE needs it to open, build and debug .NET projects."),
            { Id: "git" } => Localizer.Get("msg.setup.gitMissing", "Git isn't installed. Source control and cloning need it."),
            _ => Localizer.Format("msg.setup.toolMissing", "{0} isn't installed.", tool.Title),
        };
        var install = Localizer.Get("msg.setup.install", "Install…");
        var details = Localizer.Get("msg.setup.details", "Check Environment");
        var choice = await Workbench.Notifications.Warning(message, install, details).Result.ConfigureAwait(true);
        if (choice == install)
        {
            await InstallToolAsync(tool).ConfigureAwait(true);
        }
        else if (choice == details)
        {
            OpenCheckEnvironment();
        }
    }

    /// <summary>
    /// The one notification about the IDE's own .NET runtime (M49), however many hosts and plugins couldn't start: what's
    /// needed and what's there, Install .NET (M37's flow: dotnet-install into <c>~/.dotnet</c>, or winget), then the hosts
    /// start again. While it (or the missing-SDK one, whose install brings the runtime) shows, another report adds
    /// nothing; once it's answered, closed or hidden (Escape), a new failure (a solution opened later) shows it again. Any thread.
    /// </summary>
    /// <returns>Whether a notification about it is showing now.</returns>
    private bool OfferHostRuntime(ToolStatus tool)
    {
        var message = Localizer.Format("msg.setup.runtimeMissing", "Codalus IDE needs the .NET {0} runtime, which isn't installed (found: {1}). Without it, plugins, builds, language services and debugging can't start.", tool.WantedVersion, tool.Found ?? "none");
        var install = Localizer.Format("msg.setup.installRuntime", "Install .NET {0}", tool.WantedChannel);
        var details = Localizer.Get("msg.setup.details", "Check Environment");
        Workbench.Notifications.Notification notification;
        Workbench.Notifications.Notification? hidden;
        lock (_offerGate)
        {
            if (Showing(_runtimeNotification) || Showing(_sdkNotification))
            {
                return true;
            }

            hidden = _runtimeNotification is { Result.IsCompleted: false } unseen ? unseen : null; // shown again, not twice
            notification = _runtimeNotification = Workbench.Notifications.Error(message, install, details);
        }

        if (hidden is not null)
        {
            Workbench.Notifications.Dismiss(hidden);
        }

        Workbench.Log.AppendLine($"[setup] {tool.Detail}");
        _ = notification.Result.ContinueWith(t => Infrastructure.Ui.Post(() => OnInstallChoice(t.Result, tool, install, details)), TaskScheduler.Default);
        return true;
    }

    /// <summary>The missing SDK's notification, once while it shows; it replaces a runtime one (its install brings the runtime).</summary>
    private bool OfferSdk(ToolStatus tool)
    {
        var message = Localizer.Get("msg.setup.sdkMissing", "The .NET SDK isn't installed. Codalus IDE needs it to open, build and debug .NET projects.");
        var install = Localizer.Get("msg.setup.install", "Install…");
        var details = Localizer.Get("msg.setup.details", "Check Environment");
        Workbench.Notifications.Notification notification;
        Workbench.Notifications.Notification? replaced;
        Workbench.Notifications.Notification? hidden;
        lock (_offerGate)
        {
            if (Showing(_sdkNotification))
            {
                return true;
            }

            hidden = _sdkNotification is { Result.IsCompleted: false } unseen ? unseen : null;
            replaced = _runtimeNotification is { Result.IsCompleted: false } runtime ? runtime : null;
            notification = _sdkNotification = Workbench.Notifications.Warning(message, install, details);
        }

        foreach (var stale in new[] { hidden, replaced })
        {
            if (stale is not null)
            {
                Workbench.Notifications.Dismiss(stale);
            }
        }

        _ = notification.Result.ContinueWith(t => Infrastructure.Ui.Post(() => OnInstallChoice(t.Result, tool, install, details)), TaskScheduler.Default);
        return true;
    }

    private void OnInstallChoice(string? choice, ToolStatus tool, string install, string details)
    {
        if (choice == install)
        {
            _ = InstallToolAsync(tool);
        }
        else if (choice == details)
        {
            OpenCheckEnvironment();
        }
    }

    /// <summary>
    /// A host of the IDE wasn't started because its runtime is missing (M49): the runtime notification, once while it
    /// shows; with no SDK at all, the SDK's (its install brings the runtime). A program that isn't one of the IDE's hosts
    /// (a designer on the app's runtime) isn't the IDE's runtime: its caller reports it. Any thread.
    /// </summary>
    /// <returns>Whether a notification about it is showing, so the caller needn't show the failure itself.</returns>
    public bool ReportHostRuntimeMissing(HostRuntimeMissingException error)
    {
        Workbench.Log.AppendLine($"[setup] {error.Message}");
        if (!IsIdeHost(error.Result) || error.Result.Missing.FirstOrDefault() is not { } missing)
        {
            return false;
        }

        if (EnvironmentCheck.FindSdkRoot() is null)
        {
            return OfferSdk(Tools.FirstOrDefault(t => t is { Id: "dotnet", State: ToolState.Missing })
                ?? new ToolStatus("dotnet", ".NET SDK", true, ToolState.Missing, "Opening, building, running and debugging .NET projects; Codalus IDE's own helper processes"));
        }

        var status = EnvironmentCheck.HostRuntimeStatus();
        if (!status.NeedsAttention)
        {
            // The check's root and the host's differ (DOTNET_ROOT changed since): the host's own result.
            status = status with
            {
                State = ToolState.Missing,
                WantedVersion = missing.Reference.Version.Text,
                WantedChannel = IdeRuntime.Channel(missing.Reference),
                Found = missing.Installed.Count == 0 ? "none" : $"{FrameworkResolver.Describe(missing.Installed)} in {error.Result.DotnetRoot}",
                Detail = error.Message,
            };
        }

        return OfferHostRuntime(status);
    }

    /// <summary>One of the IDE's own hosts (<see cref="IdeRuntime.HostFolders"/>): by its name, wherever it runs from (a development build's or the tests' hosts too), or next to the shell.</summary>
    private static bool IsIdeHost(HostRuntimeCheckResult result) =>
        IdeRuntime.HostFolders.Any(folder => string.Equals(Path.GetFileNameWithoutExtension(result.Program), "codalus-" + folder, StringComparison.OrdinalIgnoreCase))
        || IsUnder(result.RuntimeConfigPath, AppContext.BaseDirectory) && !Path.GetFileNameWithoutExtension(result.Program).Contains("designer", StringComparison.OrdinalIgnoreCase) && !Path.GetFileNameWithoutExtension(result.Program).Contains("previewer", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string folder) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Starts the hosts again after the runtime they need was installed (M49): the plugins that failed with their host,
    /// the language services that couldn't start, and the solution that couldn't open. They start with the IDE's
    /// environment as it is now (the new <c>DOTNET_ROOT</c>).
    /// </summary>
    public async Task RestartHostsAsync()
    {
        Workbench.Log.AppendLine("[setup] Starting the helper processes again.");
        lock (_offerGate)
        {
            if (_runtimeNotification is { Result.IsCompleted: false } stale)
            {
                Workbench.Notifications.Dismiss(stale); // the runtime is there now
            }
        }

        await Plugins.RetryFailedHostsAsync(CancellationToken.None).ConfigureAwait(true);
        await Languages.RetryFailedAsync().ConfigureAwait(true);
        if (Interlocked.Exchange(ref _solutionWaitingForRuntime, null) is { } solution && !Solutions.IsOpen)
        {
            await OpenSolutionAsync(solution).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// What the solution's mobile projects need (M20a): their workloads, and the Android SDK and a JDK when one targets
    /// Android. Nothing when the solution has no mobile project.
    /// </summary>
    private async Task<IReadOnlyList<ToolStatus>> MobileToolsAsync()
    {
        var projects = MobileProjects();
        if (projects.Count == 0)
        {
            return [];
        }

        var rows = new List<ToolStatus>();
        var dotnet = EnvironmentCheck.FindDotnet() ?? "dotnet";
        var installed = await Mobile.InstalledWorkloadsAsync(dotnet, Solutions.Solution?.Directory, CancellationToken.None).ConfigureAwait(true) ?? [];
        var needed = projects.SelectMany(p => p.TargetFrameworks.Select(MobileTargets.PlatformOf)
                .Where(f => MobileTargets.RunsOn(f, Workbench.Platform))
                .Select(f => MobileTargets.Workload(f, p.UsesMaui)))
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal);
        foreach (var workload in needed)
        {
            var found = installed.Contains(workload, StringComparer.OrdinalIgnoreCase) || (workload.StartsWith("maui-", StringComparison.Ordinal) && installed.Contains("maui", StringComparer.OrdinalIgnoreCase));
            rows.Add(new ToolStatus(WorkloadToolPrefix + workload, Localizer.Format("msg.setup.workload", ".NET workload {0}", workload), false, found ? ToolState.Found : ToolState.Missing,
                Localizer.Get("msg.setup.workloadNeededFor", "Building the solution's .NET MAUI and mobile projects")));
        }

        if (projects.Any(p => MobileTargets.FrameworkFor(p, MobilePlatform.Android) is not null))
        {
            rows.AddRange(Mobile.Toolchain.Status(Mobile.Android()));
        }

        return rows;
    }

    private const string WorkloadToolPrefix = "workload:";

    /// <summary>The install plan for a tool on this machine, or null.</summary>
    public InstallPlan? PlanFor(ToolStatus tool) => tool.Id is "android-sdk" or "jdk"
        ? MobileProjects().Any(p => MobileTargets.FrameworkFor(p, MobilePlatform.Android) is not null)
            ? new InstallPlan(tool.Id, InstallKind.Run, Localizer.Get("msg.setup.installAndroid", "Install the Android SDK and a JDK through the Android workload"), "dotnet build -t:InstallAndroidDependencies")
            : null
        : tool.Id.StartsWith(WorkloadToolPrefix, StringComparison.Ordinal)
        ? new InstallPlan(tool.Id, InstallKind.Terminal, Localizer.Format("msg.setup.installWorkload", "Install the .NET workload {0}", tool.Id[WorkloadToolPrefix.Length..]), "dotnet workload install " + tool.Id[WorkloadToolPrefix.Length..])
        : WindowsApps.PlanFor(tool, hasWinget: Workbench.Platform == PlatformKind.Windows && EnvironmentCheck.FindOnPath("winget") is not null) is { } windowsApp
        ? windowsApp // Developer Mode's Settings page, the Windows App SDK runtime (M44)
        : NetFrameworkProjects.PlanFor(tool) is { } netFramework
        ? netFramework // .NET Framework's download page (M43a)
        : InstallPlans.For(
        tool,
        Workbench.Platform,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        hasWinget: Workbench.Platform == PlatformKind.Windows && EnvironmentCheck.FindOnPath("winget") is not null,
        linuxDistribution: Workbench.Platform == PlatformKind.Linux ? InstallPlans.LinuxDistribution() : null,
        emulatedX64: EnvironmentCheck.EmulatedX64);

    /// <summary>
    /// Installs a tool after the user agrees to the exact command (<paramref name="confirm"/>: tests answer for the user),
    /// then checks again.
    /// </summary>
    /// <param name="then">After a successful install, in place of the usual follow-up (M49: an update checks again).</param>
    public async Task InstallToolAsync(ToolStatus tool, Func<InstallPlan, Task<bool>>? confirm = null, ProcessRunner? run = null, Func<Task>? then = null)
    {
        if (tool.Id is "android-sdk" or "jdk")
        {
            // Through the Android workload's own target, which needs a project (M20a).
            await InstallAndroidDependenciesAsync().ConfigureAwait(true);
            await CheckEnvironmentAsync(offer: false).ConfigureAwait(true);
            return;
        }

        if (PlanFor(tool) is not { } plan)
        {
            Workbench.Notifications.Info(Localizer.Format("msg.setup.noPlan", "There's no way to install {0} from here on this system.", tool.Title));
            return;
        }

        var agreed = confirm is not null
            ? await confirm(plan).ConfigureAwait(true)
            : await Dialogs.ConfirmAsync(
                Localizer.Format("msg.setup.installTitle", "Install {0}", tool.Title),
                Localizer.Format("msg.setup.installMessage", "{0}.\n\nThis runs:\n{1}", plan.Summary, plan.Command),
                plan.Kind switch
                {
                    InstallKind.OpenPage => Localizer.Get("msg.setup.openPage", "Open Page"),
                    InstallKind.Terminal => Localizer.Get("msg.setup.runInTerminal", "Run in Terminal"),
                    _ => Localizer.Get("msg.setup.installNow", "Install"),
                }).ConfigureAwait(true);
        if (!agreed)
        {
            return;
        }

        SetupLog.AppendLine($"[{DateTime.Now:T}] {plan.Summary}: {plan.Command}");
        switch (plan.Kind)
        {
            case InstallKind.OpenPage:
                if (plan.Command.StartsWith("ms-settings:", StringComparison.Ordinal))
                {
                    OpenWindowsSettings(plan.Command["ms-settings:".Length..]); // Developer Mode (M44)
                }
                else
                {
                    OpenExternalUrl(new Uri(plan.Command));
                }

                return;
            case InstallKind.Terminal:
                RunInstallInTerminal(plan);
                Workbench.Notifications.Info(Localizer.Get("msg.setup.terminalStarted", "The install runs in the terminal. When it's done, check the environment again (Help → Check Environment)."));
                return;
        }

        StatusBar.ShowMessage(Localizer.Format("msg.setup.installing", "Installing {0}…", tool.Title), TimeSpan.FromSeconds(10));
        Workbench.Output.Show(SetupLog);
        var (code, output) = await (run ?? StreamInstallAsync)(plan.Program!, plan.Arguments ?? [], null, CancellationToken.None).ConfigureAwait(true);
        if (run is not null)
        {
            SetupLog.Append(output);
        }

        if (code != 0)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.setup.installFailed", "Installing {0} failed (exit code {1}). The Environment Setup output has the details.", tool.Title, code));
            return;
        }

        if (plan.AddsToPath is { } folder)
        {
            AddDotnetToEnvironment(folder);
        }

        if (tool.Id is "dotnet" or "dotnet-runtime")
        {
            // M49: the root may have changed (winget installs into Program Files, not where DOTNET_ROOT points; an
            // architecture's DOTNET_ROOT_ARM64 wins over DOTNET_ROOT): choose again, so the hosts follow the new .NET.
            UseUserDotnet();
        }

        var after = await CheckEnvironmentAsync(offer: false).ConfigureAwait(true);
        var ownRuntime = after.FirstOrDefault(t => t.Id == "dotnet-runtime");
        var hostsStartedAgain = false;
        if (tool.Id is "dotnet" or "dotnet-runtime" && ownRuntime is { NeedsAttention: false })
        {
            // The IDE's own hosts that failed for it start again here and now (M49), whichever notification the install
            // came from (the runtime's, an update's, the SDK's); a restart isn't needed for them.
            await RestartHostsAsync().ConfigureAwait(true);
            hostsStartedAgain = true;
        }

        if (then is not null)
        {
            await then().ConfigureAwait(true);
        }
        else if (tool.Id == "dotnet-runtime" && hostsStartedAgain)
        {
            Workbench.Notifications.Info(Localizer.Format("msg.setup.runtimeInstalled", "The .NET {0} runtime is installed, and Codalus IDE's helper processes started again.", ownRuntime!.Version));
        }
        else if (after.FirstOrDefault(t => t.Id == tool.Id) is { NeedsAttention: false } installed)
        {
            // Offered, not awaited: the install is done whether or not the user restarts now.
            var restart = Localizer.Get("msg.setup.restart", "Restart Now");
            var offer = Workbench.Notifications.Info(Localizer.Format("msg.setup.installed", "{0} {1} is installed. Restart Codalus IDE so that all of it uses it.", installed.Title, installed.Version), restart);
            _ = offer.Result.ContinueWith(t => { if (t.Result == restart) { Infrastructure.Ui.Post(Restart); } }, TaskScheduler.Default);
        }
        else
        {
            Workbench.Notifications.Warning(Localizer.Format("msg.setup.stillMissing", "The install finished, but {0} still isn't found. The Environment Setup output has the details.", tool.Title));
        }
    }

    private void RunInstallInTerminal(InstallPlan plan)
    {
        var (program, arguments) = Workbench.Platform == PlatformKind.Windows
            ? ("cmd.exe", (IReadOnlyList<string>)["/c", plan.Command + " & echo. & pause"])
            : ("bash", ["-c", plan.Command + "; echo; read -r -p 'Done. Press Enter to close. '"]);
        var session = Terminals.Run(plan.Summary, program, arguments, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), null);
        Layout.ShowToolWindow(TerminalViewModel.ToolWindowId);
        TerminalWindow.Show(session);
    }

    /// <summary>Runs an install, its output going to the Environment Setup channel as it comes.</summary>
    private async Task<(int, string)> StreamInstallAsync(string program, IReadOnlyList<string> arguments, string? directory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, e) => { if (e.Data is { } line) { SetupLog.AppendLine(line); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) { SetupLog.AppendLine(line); } };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(true);
            return (process.ExitCode, string.Empty);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetupLog.AppendLine(ex.Message);
            return (-1, ex.Message);
        }
    }

    /// <summary>When a solution opens: its <c>global.json</c> SDK, offered when it isn't installed.</summary>
    private async Task CheckSolutionSdkAsync(string solutionPath)
    {
        if (Path.GetDirectoryName(solutionPath) is not { } folder || EnvironmentCheck.GlobalJson(folder) is null)
        {
            return;
        }

        // The SDK alone (M51): the whole check ran git, Docker and Podman too at every open, seconds of CPU for nothing.
        if (await EnvironmentCheck.CheckDotnetAsync(folder, CancellationToken.None).ConfigureAwait(true) is { State: ToolState.Mismatch } sdk)
        {
            Tools = await EnvironmentCheck.CheckAsync(folder, CancellationToken.None).ConfigureAwait(true);
            ToolsComplete = false; // without the mobile, Windows app and .NET Framework rows
            ToolsChanged?.Invoke(this, EventArgs.Empty);
            await OfferInstallAsync(sdk).ConfigureAwait(true);
        }
    }

    public void OpenCheckEnvironment()
    {
        Layout.OpenDocument(Layout.FindDocument<EnvironmentDocument>(EnvironmentDocument.DocumentId) ?? new EnvironmentDocument(new EnvironmentViewModel(this)));
    }
}
