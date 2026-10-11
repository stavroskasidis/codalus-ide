using System.Globalization;
using DaedalusStudio.Debugging;
using DaedalusStudio.Platform;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Rpc;
using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Debugging;
using DaedalusStudio.Workbench.Debugging.Remote;
using DaedalusStudio.Workbench.Terminal;

namespace DaedalusStudio.Shell;

/// <summary>
/// Debugger sessions (M14c, ADR 0039): multiple startup projects in one session, programs in the integrated terminal,
/// attaching over SSH, in containers and in WSL, the WSL launch profile, and Just-In-Time debugging through crash dumps.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>Where a debugged console app's console is: <c>internalConsole</c> (the Debug Console) or <c>integratedTerminal</c>.</summary>
    public const string DebugConsoleSetting = "debug.console";

    private CrashDumpWatcher? _crashWatcher;
    private (RemoteTarget Target, string Name)? _lastRemote;
    private string _lastSshDestination = string.Empty;
    private readonly Queue<string> _pendingBrowserUrls = new();

    /// <summary>Starts debugged programs in the integrated terminal.</summary>
    public TerminalLauncher DebugTerminal { get; private set; } = null!;

    /// <summary>netcoredbg on remote targets: found, or copied there with consent.</summary>
    public RemoteDebuggers RemoteDebuggers { get; private set; } = null!;

    /// <summary>Just-In-Time debugging: crash dumps of the user's .NET programs, offered for opening (tests replace the user environment).</summary>
    public JitDebugging Jit { get; internal set; } = null!;

    private void InitializeDebugSessions()
    {
        DebugTerminal = new TerminalLauncher(Terminals, session => Ui.Post(() =>
        {
            Layout.ShowToolWindow(TerminalViewModel.ToolWindowId, focus: false);
            TerminalWindow.Show(session);
        }));
        Debugger.Terminal = DebugTerminal;
        Debugger.StartupHook = HeldLaunch.ResolveHook();
        var cache = Environment.GetEnvironmentVariable(NodeCacheVariable) is { Length: > 0 } shared ? shared : Paths.Cache;
        RemoteDebuggers = new RemoteDebuggers(cache);
        Jit = new JitDebugging(JitDebugging.ForThisSystem(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), Path.Combine(Paths.Logs, "crash-dumps"));
        _ = Task.Run(() =>
        {
            try
            {
                if (Jit.IsRegistered)
                {
                    Ui.Post(StartCrashWatcher);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Workbench.Log.AppendLine("[jit] " + ex.Message);
            }
        });
    }

    /// <summary>The console of a debugged program: the terminal for console apps when <c>debug.console</c> says so.</summary>
    private DebugConsoleKind ConsoleFor(RunInfo info) =>
        Workbench.Settings.GetString(DebugConsoleSetting, "internalConsole") == "integratedTerminal" && string.Equals(info.OutputType, "Exe", StringComparison.OrdinalIgnoreCase)
            ? DebugConsoleKind.Terminal
            : DebugConsoleKind.Internal;

    // ------------------------------------------------------------------ multiple startup projects

    /// <summary>
    /// Start (F5) or Start Without Debugging (Ctrl+F5) with multiple startup projects: the solution builds once, then
    /// the projects start in order; those debugged share one session.
    /// </summary>
    public async Task StartStartupProjectsAsync(bool debug)
    {
        var targets = Runs.StartupTargets;
        if (targets.Count == 0)
        {
            Workbench.Notifications.Warning("No startup project is set to start. Choose them in Configure Startup Projects.");
            return;
        }

        if (!await BuildAsync(BuildTargets.Build, null).ConfigureAwait(true))
        {
            if (Builds.LastBuild is { Result: { Canceled: false, Errors: > 0 } })
            {
                StatusBar.ShowMessage("The build failed, so the startup projects did not start.", TimeSpan.FromSeconds(6));
            }

            return;
        }

        var launches = new List<DebugLaunch>();
        DebugLog.Clear();
        _pendingBrowserUrls.Clear();
        foreach (var target in targets)
        {
            var name = Path.GetFileNameWithoutExtension(target.Project);
            try
            {
                var (containerInfo, containerProfiles, _) = await Runs.GetProfilesAsync(target.Project, CancellationToken.None).ConfigureAwait(true);
                if (Runs.ResolveProfile(target.Project, containerProfiles) is { } containerProfile && IsContainerProfile(containerProfile))
                {
                    // A container runs on its own (ADR 0042): debugged, it is a session of its own after this one.
                    await StartInContainerAsync(target.Project, containerInfo, containerProfile, debug: false).ConfigureAwait(true);
                    continue;
                }

                if (debug && target.Action == StartupAction.Start)
                {
                    var (info, profiles, error) = await Runs.GetProfilesAsync(target.Project, CancellationToken.None).ConfigureAwait(true);
                    if (error is not null)
                    {
                        DebugLog.AppendLine($"[{name}] Ignoring the launch settings: {error}");
                    }

                    var plan = await Runs.PlanAsync(info, Runs.ResolveProfile(target.Project, profiles), CancellationToken.None).ConfigureAwait(true);
                    var launch = plan.Wsl is not null
                        ? await WslLaunchAsync(name, plan).ConfigureAwait(true)
                        : new DebugLaunch(name, plan) { Console = ConsoleFor(info) };
                    if (launch is null)
                    {
                        continue;
                    }

                    DebugLog.AppendLine($"[{name}] > {plan.FileName} {plan.Arguments}".TrimEnd());
                    if (plan.BrowserUrl is { } url)
                    {
                        _pendingBrowserUrls.Enqueue(url);
                    }

                    launches.Add(launch);
                }
                else
                {
                    var program = await Runs.StartAsync(target.Project, Workbench.Settings.GetBoolean(ExternalConsoleSetting, false), CancellationToken.None).ConfigureAwait(true);
                    if (program.Channel is { } channel && launches.Count == 0 && !debug)
                    {
                        Workbench.Output.Show(channel);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or JsonRpcRemoteException or JsonRpcConnectionLostException or IOException)
            {
                Workbench.Notifications.Error($"Could not start {name}: {ex.Message}");
            }
        }

        if (launches.Count == 0)
        {
            return;
        }

        Debugger.JustMyCode = Workbench.Settings.GetBoolean(JustMyCodeSetting, true);
        Debugger.StepFiltering = Workbench.Settings.GetBoolean(StepFilteringSetting, true);
        Layout.ShowToolWindow(DebugConsoleViewModel.ToolWindowId, focus: false);
        try
        {
            await Debugger.LaunchAsync(launches, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            Workbench.Notifications.Error($"Could not start debugging {launches[0].Name}: {ex.Message}");
        }
    }

    /// <summary>Configure Startup Projects: single or multiple startup projects, with each one's action and launch profile.</summary>
    public StartupProjectsViewModel? OpenStartupProjects()
    {
        if (!RequireTrustedSolution("Startup projects"))
        {
            return null;
        }

        return OpenManagement(
            "startupProjects",
            () => new StartupProjectsViewModel(Runs, Solutions, () => Ui.Post(SaveEditorSession)),
            _ => "Startup Projects");
    }

    // ------------------------------------------------------------------ WSL launch profile

    /// <summary>
    /// The debug launch of a <c>WSL2</c> profile's plan: netcoredbg in the distribution (copied there on first use),
    /// launching the distribution's <c>dotnet</c> on the build output through <c>/mnt</c>. Null when the user declined
    /// or something is missing (it was reported).
    /// </summary>
    private async Task<DebugLaunch?> WslLaunchAsync(string name, LaunchPlan plan)
    {
        var wsl = plan.Wsl!;
        var target = new WslTarget(wsl.Distribution);
        var debugger = await EnsureRemoteDebuggerAsync(target).ConfigureAwait(true);
        if (debugger is null)
        {
            return null;
        }

        var dotnet = (await RemoteShell.RunAsync(target, "exec sh -lc 'command -v dotnet'", CancellationToken.None).ConfigureAwait(true)).Output.Trim();
        if (!dotnet.StartsWith('/'))
        {
            Workbench.Notifications.Error($".NET isn't installed in {target.Display}: 'dotnet' isn't on its PATH. Install the runtime there to run {name} in WSL.");
            return null;
        }

        var arguments = new LaunchArguments(dotnet, [.. wsl.Arguments], wsl.WorkingDirectory, new Dictionary<string, string>(plan.Environment));
        var map = new SourceMap(SourceFileMap(), SolutionFiles());
        return new DebugLaunch(name, plan) { Remote = new RemoteLaunch(RemoteShell.Adapter(target, debugger), arguments, map, target.Display) };
    }

    // ------------------------------------------------------------------ remote attach

    /// <summary>
    /// Attach to a Process on a remote target: over SSH, in a Docker or Podman container, or in a WSL distribution
    /// (Windows). <paramref name="argument"/> may name the kind (<c>ssh</c>, <c>container</c>, <c>wsl</c>).
    /// </summary>
    public async Task AttachToRemoteProcessAsync(string? argument = null)
    {
        if (Debugger.IsActive)
        {
            StatusBar.ShowMessage("A debug session is already running.", TimeSpan.FromSeconds(4));
            return;
        }

        var kind = argument;
        if (kind is null)
        {
            List<QuickPickItem> kinds =
            [
                new("SSH…") { Tag = "ssh", Detail = "A Linux or macOS machine, through your ssh and its keys" },
                new("Docker or Podman container…") { Tag = "container", Detail = "A running container, through " + string.Join(' ', RemoteTargets.ContainerTool(Workbench.Settings.GetString(RemoteTargets.ContainerToolSetting))) + " exec" },
            ];
            if (OperatingSystem.IsWindows())
            {
                kinds.Add(new QuickPickItem("WSL distribution…") { Tag = "wsl", Detail = "A Linux distribution of the Windows Subsystem for Linux" });
            }

            if (await Palette.ShowAsync(kinds, "Attach to a process on…").ConfigureAwait(true) is not { Tag: string picked })
            {
                return;
            }

            kind = picked;
        }

        RemoteTarget? target;
        try
        {
            target = kind switch
            {
                "ssh" => await PickSshTargetAsync().ConfigureAwait(true),
                "container" => await PickContainerAsync().ConfigureAwait(true),
                "wsl" => await PickWslAsync().ConfigureAwait(true),
                _ => null,
            };
        }
        catch (InvalidOperationException ex)
        {
            Workbench.Notifications.Error(ex.Message);
            return;
        }

        if (target is not null)
        {
            await AttachToRemoteAsync(target, processName: null).ConfigureAwait(true);
        }
    }

    private async Task<RemoteTarget?> PickSshTargetAsync()
    {
        var destination = await Dialogs.InputAsync(
            "Attach over SSH",
            "The machine: user@host, with :port if it isn't 22. Your ssh, its keys and ~/.ssh/config are used; netcoredbg is copied there on first use.",
            _lastSshDestination,
            text => SshTarget.Parse(text) is null ? "Enter user@host or user@host:port." : null).ConfigureAwait(true);
        if (destination is null || SshTarget.Parse(destination) is not { } target)
        {
            return null;
        }

        _lastSshDestination = destination.Trim();
        return target;
    }

    private async Task<RemoteTarget?> PickContainerAsync()
    {
        var tool = RemoteTargets.ContainerTool(Workbench.Settings.GetString(RemoteTargets.ContainerToolSetting));
        var containers = await RemoteTargets.ContainersAsync(tool, CancellationToken.None).ConfigureAwait(true);
        if (containers.Count == 0)
        {
            StatusBar.ShowMessage($"No containers are running ({string.Join(' ', tool)} ps).", TimeSpan.FromSeconds(5));
            return null;
        }

        var items = containers.Select(c => new QuickPickItem(c.Name) { Tag = c.Name, Detail = $"{c.Image} · {c.Id}" }).ToList();
        return await Palette.ShowAsync(items, "Attach to a process in a container").ConfigureAwait(true) is { Tag: string container }
            ? new ContainerTarget(container, tool)
            : null;
    }

    private async Task<RemoteTarget?> PickWslAsync()
    {
        var distributions = await RemoteTargets.WslDistributionsAsync(CancellationToken.None).ConfigureAwait(true);
        if (distributions.Count == 0)
        {
            StatusBar.ShowMessage("No WSL distributions are installed.", TimeSpan.FromSeconds(5));
            return null;
        }

        var items = distributions.Select((d, i) => new QuickPickItem(d) { Tag = d, Detail = i == 0 ? "default" : null }).ToList();
        return await Palette.ShowAsync(items, "Attach to a process in a WSL distribution").ConfigureAwait(true) is { Tag: string distribution }
            ? new WslTarget(distribution)
            : null;
    }

    /// <summary>Lists <paramref name="target"/>'s processes (.NET first), and attaches to the one picked (or named <paramref name="processName"/>).</summary>
    public async Task<bool> AttachToRemoteAsync(RemoteTarget target, string? processName, int? processId = null)
    {
        UseAskpassForSsh();
        IReadOnlyList<RemoteProcess> processes;
        try
        {
            StatusBar.ShowMessage($"Listing the processes of {target.Display}…", TimeSpan.FromSeconds(10));
            processes = await RemoteShell.ProcessesAsync(target, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Workbench.Notifications.Error(ex.Message);
            return false;
        }

        var process = processId is { } id ? processes.FirstOrDefault(p => p.Id == id)
            : processName is not null ? processes.FirstOrDefault(p => p.Name == processName && p.IsDotnet)
            : null;
        if (process is null)
        {
            var items = processes.Select(p => new QuickPickItem($"{p.Name} ({p.Id.ToString(CultureInfo.InvariantCulture)})")
            {
                Tag = p,
                Detail = (p.IsDotnet ? ".NET · " : string.Empty) + p.CommandLine,
            }).ToList();
            if (await Palette.ShowAsync(items, $"Attach to a process on {target.Display} (.NET processes are listed first)").ConfigureAwait(true) is not { Tag: RemoteProcess picked })
            {
                return false;
            }

            process = picked;
        }

        var debugger = await EnsureRemoteDebuggerAsync(target).ConfigureAwait(true);
        if (debugger is null)
        {
            return false;
        }

        try
        {
            DebugLog.Clear();
            DebugLog.AppendLine($"Attaching to {process.Name} ({process.Id.ToString(CultureInfo.InvariantCulture)}) on {target.Display} through {debugger}…");
            Layout.ShowToolWindow(DebugConsoleViewModel.ToolWindowId, focus: false);
            Debugger.JustMyCode = Workbench.Settings.GetBoolean(JustMyCodeSetting, true);
            Debugger.StepFiltering = Workbench.Settings.GetBoolean(StepFilteringSetting, true);
            var map = new SourceMap(SourceFileMap(), SolutionFiles());
            await Debugger.AttachAsync(process.Id, CancellationToken.None, new RemoteAttach(RemoteShell.Adapter(target, debugger), map, target.Display), process.Name).ConfigureAwait(true);
            _lastRemote = (target, process.Name);
            _lastAttached = null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            Workbench.Notifications.Error($"Could not attach to {process.Name} on {target.Display}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Reattach (Shift+Alt+P) after a remote attach: the same target and process name.</summary>
    private async Task<bool> ReattachRemoteAsync()
    {
        if (_lastRemote is not { } last)
        {
            return false;
        }

        await AttachToRemoteAsync(last.Target, last.Name).ConfigureAwait(true);
        return true;
    }

    /// <summary>netcoredbg's path on <paramref name="target"/>, copied there after the user agrees; null otherwise (reported).</summary>
    private async Task<string?> EnsureRemoteDebuggerAsync(RemoteTarget target)
    {
        UseAskpassForSsh();
        try
        {
            return await RemoteDebuggers.EnsureAsync(
                target,
                question => Dialogs.ConfirmAsync("Copy the Debugger", question, "Copy"),
                new Progress<string>(line => DebugLog.AppendLine(line)),
                CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            Workbench.Notifications.Error($"The debugger couldn't be set up on {target.Display}: {ex.Message}");
            return null;
        }
    }

    /// <summary>SSH passwords and passphrases go through the shell's askpass (ADR 0031), never a console.</summary>
    private void UseAskpassForSsh()
    {
        if (Askpass is { } askpass)
        {
            foreach (var (name, value) in askpass.Environment)
            {
                RemoteShell.Environment[name] = value;
            }
        }
    }

    /// <summary>The <c>debug.sourceFileMap</c> setting: <c>/remote/folder=C:\local\folder</c> entries.</summary>
    private IEnumerable<KeyValuePair<string, string>> SourceFileMap() =>
        Workbench.Settings.GetStrings(RemoteTargets.SourceFileMapSetting)
            .Select(e => e.Split('=', 2))
            .Where(p => p.Length == 2 && p[0].Trim().Length > 0 && p[1].Trim().Length > 0)
            .Select(p => new KeyValuePair<string, string>(p[0].Trim(), p[1].Trim()));

    /// <summary>The solution's source files, for inferring source maps.</summary>
    private List<string> SolutionFiles() =>
        [.. Solutions.Projects.Values.SelectMany(p => p.Items).Where(i => i.Kind is "Compile" or "Content" or "None").Select(i => i.Path).Distinct(SolutionPaths.Comparer)];

    // ------------------------------------------------------------------ Just-In-Time debugging

    /// <summary>
    /// Debug → Just-In-Time Debugging: turns crash dumps of the user's .NET programs on or off, after saying exactly what
    /// changes on this OS (off by default; ADR 0039).
    /// </summary>
    public async Task ConfigureJitDebuggingAsync(string? argument = null)
    {
        bool registered;
        try
        {
            registered = await Task.Run(() => Jit.IsRegistered).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Workbench.Notifications.Error("Just-In-Time debugging's state couldn't be read: " + ex.Message);
            return;
        }

        var turnOn = argument switch { "on" => true, "off" => false, _ => (bool?)null };
        if (turnOn is null)
        {
            var message = registered
                ? $"On: .NET programs you start write a dump to {Jit.DumpFolder} when they crash, and Daedalus Studio offers to open it. Turning it off removes the variables from {Jit.Environment.Description}."
                : $"When a .NET program you start crashes, it writes a full dump to {Jit.DumpFolder}, and Daedalus Studio offers to open it in the dump view. "
                  + $"This sets DOTNET_DbgEnableMiniDump, DOTNET_DbgMiniDumpType and DOTNET_DbgMiniDumpName in {Jit.Environment.Description}, for programs started from now on; no administrator rights are needed. Dumps can be large."
                  + (OperatingSystem.IsWindows() ? " Attaching to a crashing program live, as a registered system debugger, would need administrator rights and isn't offered." : string.Empty);
            var answer = await Dialogs.ShowAsync(new DialogRequest("Just-In-Time Debugging", message, [registered ? "Turn Off" : "Turn On", "Cancel"])).ConfigureAwait(true);
            if (answer.Button is not ("Turn On" or "Turn Off"))
            {
                return;
            }

            turnOn = answer.Button == "Turn On";
        }

        try
        {
            await Task.Run(() =>
            {
                if (turnOn == true)
                {
                    Jit.Register();
                }
                else
                {
                    Jit.Unregister();
                }
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or PlatformNotSupportedException)
        {
            Workbench.Notifications.Error("Just-In-Time debugging couldn't be changed: " + ex.Message);
            return;
        }

        if (turnOn == true)
        {
            StartCrashWatcher();
            StatusBar.ShowMessage("Just-In-Time debugging is on for programs started from now on.", TimeSpan.FromSeconds(6));
        }
        else
        {
            _crashWatcher?.Dispose();
            _crashWatcher = null;
            StatusBar.ShowMessage("Just-In-Time debugging is off.", TimeSpan.FromSeconds(6));
        }
    }

    private void StartCrashWatcher()
    {
        if (_crashWatcher is not null)
        {
            return;
        }

        try
        {
            _crashWatcher = new CrashDumpWatcher(Jit.DumpFolder);
            _crashWatcher.Crashed += (_, dump) => Ui.Post(() => _ = OfferCrashDumpAsync(dump));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Workbench.Log.AppendLine("[jit] The crash-dump folder can't be watched: " + ex.Message);
        }
    }

    private async Task OfferCrashDumpAsync(CrashDump dump)
    {
        var who = dump.ProcessId is { } pid ? $"{dump.Program} (process {pid.ToString(CultureInfo.InvariantCulture)})" : dump.Program;
        var choice = await Workbench.Notifications.Warning($"{who} crashed and wrote a dump.", "Open Dump", "Show in Folder").Result.ConfigureAwait(true);
        if (choice == "Open Dump")
        {
            await OpenDumpAsync(dump.Path).ConfigureAwait(true);
        }
        else if (choice == "Show in Folder")
        {
            ShellIntegration.RevealInFileManager(dump.Path);
        }
    }
}
