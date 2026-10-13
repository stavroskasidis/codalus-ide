using System.Diagnostics;
using System.Globalization;
using DaedalusStudio.Debugging;
using DaedalusStudio.LanguageServices.Client;
using DaedalusStudio.Platform;
using DaedalusStudio.Rpc;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Debugging;
using DaedalusStudio.Workbench.Debugging.Remote;
using DaedalusStudio.Workbench.FileSystem;
using DaedalusStudio.Workbench.Languages;
using DaedalusStudio.Workbench.Notifications;
using DaedalusStudio.Workbench.Output;
using DaedalusStudio.Workbench.Projects;
using DaedalusStudio.Workbench.Remote;
using DaedalusStudio.Workbench.StatusBar;
using DaedalusStudio.Workbench.Terminal;

namespace DaedalusStudio.Shell;

/// <summary>
/// Remote development (M21, ADR 0050): a remote window — this window connected to <c>daedalus-server</c> on an SSH
/// machine or a WSL distribution — opens a folder or solution there. The build and language hosts, terminals and the
/// debugger run on the server; the editor keeps its buffers here and reads and writes files through it. The status
/// bar shows the connection and its round trip; the Ports window the forwarded ports.
/// </summary>
public sealed partial class ShellHost
{
    public const string RemoteStatusItem = "daedalus.remote";
    public const string AutoForwardPortsSetting = "remote.autoForwardPorts";

    /// <summary>Simulated round trip for budgets and tests (milliseconds), applied to a remote window's connection.</summary>
    public const string RemoteLatencyVariable = "DAEDALUS_REMOTE_LATENCY_MS";

    /// <summary>The terminal profile of a remote window: the user's login shell on the remote machine.</summary>
    public static readonly TerminalProfile RemoteShellProfile = new("remote", "Remote shell", "$remote-login-shell", [], "remote");

    private IReadOnlyList<TerminalProfile>? _localProfiles;

    /// <summary>The session of a remote window, or null for a local one.</summary>
    public RemoteSession? Remote { get; private set; }

    public RemotePortsViewModel PortsWindow { get; private set; } = null!;

    /// <summary>Opens a URL of a forwarded port (tests replace it).</summary>
    public Action<Uri> RemoteBrowser { get; set; } = ShellIntegration.OpenUrl;

    /// <summary>Starts another <c>daedalus</c> for a remote window (tests replace it): its arguments.</summary>
    public Action<IReadOnlyList<string>> StartWindow { get; set; } = arguments =>
    {
        if (Environment.ProcessPath is { } self)
        {
            var start = new ProcessStartInfo(self) { UseShellExecute = false };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            Process.Start(start)?.Dispose();
        }
    };

    private OutputChannel RemoteLog => Workbench.Output.GetOrCreate("daedalus.remote", "Remote");

    private void InitializeRemote() => PortsWindow = new RemotePortsViewModel(this);

    /// <summary>Whether <paramref name="path"/> is a remote window's file (served by its server).</summary>
    public bool IsRemotePath(string path) => Remote is not null && FileSystems.IsRemote(path);

    /// <summary>A path for messages: a remote file's path on its machine, with the machine.</summary>
    public string RemoteDisplay(string path) =>
        Remote is { } remote && remote.Map.Covers(path) ? $"{remote.Map.ToRemote(path)} ({remote.Display})" : path;

    // ------------------------------------------------------------------ connecting

    /// <summary>The hosts a remote window copies to the server: the build, C# and markup hosts this shell runs.</summary>
    internal static IReadOnlyList<BundlePart> RemoteHostParts()
    {
        var parts = new List<BundlePart>();
        void Add(string name, string? hostPath)
        {
            if (hostPath is not null && Path.GetDirectoryName(Path.GetFullPath(hostPath)) is { } folder && Directory.Exists(folder))
            {
                parts.Add(new BundlePart(name, folder));
            }
        }

        static string? PathOf(ProcessLaunchInfo? launch) =>
            launch is null ? null : launch.FileName == "dotnet" && launch.Arguments.Count > 0 ? launch.Arguments[0] : launch.FileName;

        Add("buildhost", BuildHostLocator.Resolve());
        Add("lshost", PathOf(HostLaunchInfo.Resolve()));
        Add("markuphost", PathOf(MarkupHostLaunch.Resolve()));
        return parts;
    }

    private static TimeSpan? SimulatedLatency() =>
        int.TryParse(Environment.GetEnvironmentVariable(RemoteLatencyVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var ms) && ms > 0
            ? TimeSpan.FromMilliseconds(ms)
            : null;

    /// <summary>
    /// Makes this window a remote window: connects to <paramref name="spec"/> (installing or updating the server there),
    /// moves the hosts, terminals and file system to it, and opens <paramref name="remotePath"/> (a folder, solution,
    /// project or file there) when given. False when it couldn't connect (it said why).
    /// </summary>
    public async Task<bool> ConnectRemoteAsync(RemoteTargetSpec spec, string? remotePath, RemoteSessionOptions? options = null)
    {
        if (Remote is not null)
        {
            Workbench.Notifications.Warning($"This window is already connected to {Remote.Display}. Open Remote opens another window.");
            return false;
        }

        options ??= new RemoteSessionOptions(spec, ShellInfo.Version, Paths.Cache) { Hosts = RemoteHostParts(), Latency = SimulatedLatency() };
        Workbench.StatusBar.Set(new StatusBarItem(RemoteStatusItem, $"$(sync) {spec.Display}…", StatusBarAlignment.Left, 100, ContributionSource.Shell.Id) { Tooltip = "Connecting…" });
        var progress = new Progress<string>(line =>
        {
            RemoteLog.AppendLine(line);
            StatusBar.ShowMessage(line);
        });
        RemoteSession session;
        try
        {
            session = await Task.Run(() => RemoteSession.ConnectAsync(options, remotePath, progress, CancellationToken.None)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or System.ComponentModel.Win32Exception or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException or UnauthorizedAccessException)
        {
            RemoteLog.AppendLine($"[error] {spec.Display}: {ex}");
            StatusBar.ClearMessage();
            Workbench.StatusBar.Remove(RemoteStatusItem);
            Workbench.Notifications.Error($"Couldn't connect to {spec.Display}: {ex.Message}");
            return false;
        }

        StatusBar.ClearMessage();
        Remote = session;
        Solutions.Placement = session.Place;
        Languages.Placement = session.Place;
        _localProfiles ??= Terminals.Profiles;
        Terminals.Profiles = [RemoteShellProfile];
        Terminals.PtyFactory = info => session.StartTerminal(info, loginShell: info.FileName == RemoteShellProfile.Executable);
        session.Connection.Log += (_, line) => RemoteLog.AppendLine(line);
        session.Disconnected += (_, error) => Ui.Post(() => OnRemoteDisconnected(session, error));
        session.LatencyChanged += (_, _) => Ui.Post(UpdateRemoteStatus);
        session.Ports.AutoForward = Workbench.Settings.GetBoolean(AutoForwardPortsSetting, true);
        session.Ports.Changed += (_, _) => Ui.Post(PortsWindow.Refresh);
        session.Ports.AutoForwarded += (_, port) => Ui.Post(() => OnPortAutoForwarded(port));
        Workbench.Context.Set("remoteConnected", true);
        UpdateRemoteStatus();
        PortsWindow.Refresh();
        var server = session.Connection.Server;
        RemoteLog.AppendLine($"Connected to {session.Display}: daedalus-server {server.ServerVersion} on {server.Os} {server.Architecture} as {server.User}; .NET {server.DotnetVersion ?? "not installed"}{(server.Dotnet is { } d ? " (" + d + ")" : string.Empty)}.");
        if (server.Dotnet is null)
        {
            Workbench.Notifications.Warning($".NET isn't installed on {session.Display}: projects won't load or build there. Files and the terminal work.");
        }

        if (remotePath is not null)
        {
            await OpenRemoteAsync(session.Map.ToLocal(remotePath)).ConfigureAwait(true);
        }

        return true;
    }

    /// <summary>
    /// Opens a remote path (in this machine's form): a folder's solution (<c>.slnx</c>, then <c>.sln</c>, then a single
    /// project) or the folder itself, a solution or project, or a file in the editor.
    /// </summary>
    public async Task OpenRemoteAsync(string path)
    {
        if (Remote is not { } remote)
        {
            return;
        }

        FileEntryInfo stat;
        try
        {
            stat = await remote.FileSystem.StatAsync(path, CancellationToken.None).ConfigureAwait(true);
        }
        catch (IOException ex)
        {
            Workbench.Notifications.Error($"Couldn't open {RemoteDisplay(path)}: {ex.Message}");
            return;
        }

        if (!stat.Exists)
        {
            Workbench.Notifications.Error($"'{RemoteDisplay(path)}' doesn't exist.");
            return;
        }

        remote.MountWorkspace(stat.IsDirectory ? path : Path.GetDirectoryName(path)!);
        if (stat.IsDirectory)
        {
            var entries = await remote.FileSystem.ReadDirectoryAsync(path, CancellationToken.None).ConfigureAwait(true);
            foreach (var pattern in s_openPatterns)
            {
                var extension = pattern[1..];
                var matches = entries.Where(e => !e.IsDirectory && e.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 1 || (matches.Count > 1 && extension is ".slnx" or ".sln"))
                {
                    await OpenSolutionAsync(Path.Combine(path, matches.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).First().Name)).ConfigureAwait(true);
                    return;
                }
            }

            await OpenFolderWorkspaceAsync(path).ConfigureAwait(true);
            return;
        }

        if (s_openPatterns.Any(p => path.EndsWith(p[1..], StringComparison.OrdinalIgnoreCase)))
        {
            await OpenSolutionAsync(path).ConfigureAwait(true);
            return;
        }

        await OpenFileAsync(path).ConfigureAwait(true);
    }

    private void OnRemoteWorkspaceOpened(string path)
    {
        RemoteLog.AppendLine($"Opened {RemoteDisplay(path)}.");
        UpdateRemoteStatus();
    }

    private void UpdateRemoteStatus()
    {
        if (Remote is not { } remote)
        {
            Workbench.StatusBar.Remove(RemoteStatusItem);
            return;
        }

        var connected = remote.Connection.IsConnected;
        var latency = remote.Latency is { } rtt ? " · " + Math.Max(1, (int)Math.Round(rtt.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture) + " ms" : string.Empty;
        Workbench.StatusBar.Set(new StatusBarItem(RemoteStatusItem, connected ? $"$(link) {remote.Display}{latency}" : $"$(warning) {remote.Display}: disconnected", StatusBarAlignment.Left, 100, ContributionSource.Shell.Id)
        {
            Tooltip = connected ? $"Remote window on {remote.Display} (daedalus-server {remote.Connection.Server.ServerVersion}); round trip{latency}. Click for remote actions." : $"The connection to {remote.Display} was lost.",
            Command = "remote.menu",
            AccessibleName = connected ? $"Connected to {remote.Display}" : $"Disconnected from {remote.Display}",
        });
    }

    private void OnRemoteDisconnected(RemoteSession session, Exception? error)
    {
        if (!ReferenceEquals(Remote, session))
        {
            return;
        }

        RemoteLog.AppendLine($"[error] The connection to {session.Display} ended{(error is null ? "." : ": " + error.Message)}");
        UpdateRemoteStatus();
        Workbench.Notifications.Error($"The connection to {session.Display} was lost. Unsaved changes are kept in the editor; reconnect by opening the remote window again.");
    }

    private void OnPortAutoForwarded(ForwardedPort port)
    {
        RemoteLog.AppendLine($"Port {port.RemotePort.ToString(CultureInfo.InvariantCulture)} on {Remote?.Display} is forwarded to {port.LocalAddress}.");
        _ = OfferBrowserAsync(port);
    }

    private async Task OfferBrowserAsync(ForwardedPort port)
    {
        var choice = await Workbench.Notifications.Info($"Port {port.RemotePort.ToString(CultureInfo.InvariantCulture)}{(port.ProcessName is { } name ? " (" + name + ")" : string.Empty)} on {Remote?.Display} is forwarded to {port.LocalAddress}.", "Open in Browser", "Show Ports").Result.ConfigureAwait(true);
        if (choice == "Open in Browser")
        {
            RemoteBrowser(new Uri("http://" + port.LocalAddress + "/"));
        }
        else if (choice == "Show Ports")
        {
            ShowPorts();
        }
    }

    /// <summary>Closes the connection: the solution closes, the hosts stop, and the window is local again.</summary>
    public async Task DisconnectRemoteAsync()
    {
        if (Remote is not { } remote)
        {
            return;
        }

        if (Solutions.IsOpen)
        {
            await CloseSolutionAsync().ConfigureAwait(true);
        }

        foreach (var terminal in Terminals.Sessions)
        {
            Terminals.Close(terminal);
        }

        Remote = null;
        Solutions.Placement = null;
        Languages.Placement = null;
        Terminals.PtyFactory = null;
        Terminals.Profiles = _localProfiles ?? Terminals.Profiles;
        Workbench.Context.Set("remoteConnected", false);
        await remote.DisposeAsync().ConfigureAwait(true);
        UpdateRemoteStatus();
        PortsWindow.Refresh();
        RemoteLog.AppendLine($"Disconnected from {remote.Display}.");
    }

    // ------------------------------------------------------------------ commands

    /// <summary>
    /// Open Remote: picks SSH or a WSL distribution and the remote folder, then opens a remote window on it (another
    /// window; this one when it has nothing open). <paramref name="argument"/> may be a target (<c>ssh:host</c>,
    /// <c>wsl:Ubuntu</c>).
    /// </summary>
    public async Task OpenRemoteWindowAsync(string? argument = null)
    {
        var spec = RemoteTargetSpec.Parse(argument);
        if (spec is null)
        {
            List<QuickPickItem> items = [new("SSH…") { Tag = "ssh", Detail = "A Linux or macOS machine, through your ssh: its config, keys and agent" }];
            if (OperatingSystem.IsWindows())
            {
                foreach (var distribution in await RemoteTargets.WslDistributionsAsync(CancellationToken.None).ConfigureAwait(true))
                {
                    items.Add(new QuickPickItem("WSL: " + distribution) { Tag = "wsl:" + distribution, Detail = "A distribution of the Windows Subsystem for Linux" });
                }
            }

            if (await Palette.ShowAsync(items, "Open a remote window on…").ConfigureAwait(true) is not { Tag: string picked })
            {
                return;
            }

            if (picked == "ssh")
            {
                var host = await Dialogs.InputAsync("Open Remote over SSH", "The host, as for ssh: user@host, user@host:port, or a Host of ~/.ssh/config. Keys and agents only; a password prompt can't appear.", string.Empty,
                    text => RemoteTargetSpec.Parse("ssh:" + text) is null ? "That isn't a host ssh takes." : null).ConfigureAwait(true);
                if (host is null)
                {
                    return;
                }

                picked = "ssh:" + host;
            }

            spec = RemoteTargetSpec.Parse(picked);
            if (spec is null)
            {
                return;
            }
        }

        var path = await Dialogs.InputAsync("Open Remote", $"The folder, solution or project to open on {spec.Display} (an absolute path there; ~ is the home folder).", "~",
            text => text.Trim().Length > 0 && (text.Trim().StartsWith('/') || text.Trim().StartsWith('~')) ? null : "An absolute path, or one in ~.").ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        path = path.Trim();
        if (Remote is null && !Solutions.IsOpen && !Editors.Any())
        {
            if (await ConnectRemoteAsync(spec, path.StartsWith('~') ? null : path).ConfigureAwait(true) && path.StartsWith('~') && Remote is { } remote)
            {
                await OpenRemoteAsync(remote.Map.ToLocal(remote.Connection.Server.Home.TrimEnd('/') + path[1..])).ConfigureAwait(true);
            }

            return;
        }

        List<string> arguments = ["--remote", spec.ToString(), path];
        if (Options.UserDataDir is { } userData)
        {
            arguments.AddRange(["--user-data-dir", userData]);
        }

        StartWindow(arguments);
        StatusBar.ShowMessage($"Opening a remote window on {spec.Display}…", TimeSpan.FromSeconds(5));
    }

    /// <summary>The remote actions (the status bar item's menu).</summary>
    public async Task ShowRemoteMenuAsync()
    {
        List<QuickPickItem> items = [new("Open Remote Window…") { Tag = "remote.open" }];
        if (Remote is { } remote)
        {
            items.Insert(0, new QuickPickItem("Show Forwarded Ports") { Tag = "remote.ports", Detail = $"{remote.Ports.Forwarded.Count.ToString(CultureInfo.InvariantCulture)} forwarded" });
            items.Insert(1, new QuickPickItem("Forward a Port…") { Tag = "remote.forwardPort" });
            items.Add(new QuickPickItem("Show the Remote Log") { Tag = "remote.showLog" });
            items.Add(new QuickPickItem("Close Remote Connection") { Tag = "remote.close", Detail = remote.Display });
        }

        if (await Palette.ShowAsync(items, Remote?.Display ?? "Remote").ConfigureAwait(true) is { Tag: string command })
        {
            await Workbench.Commands.ExecuteAsync(command, [], CancellationToken.None).ConfigureAwait(true);
        }
    }

    public void ShowPorts()
    {
        PortsWindow.Refresh();
        Layout.ShowToolWindow(RemotePortsViewModel.ToolWindowId, focus: true);
    }

    public void ShowRemoteLog() => Workbench.Output.Show(RemoteLog);

    /// <summary>Forwards a port of the remote machine (asks for it when <paramref name="argument"/> is null).</summary>
    public async Task ForwardPortAsync(string? argument = null)
    {
        if (Remote is not { } remote)
        {
            Workbench.Notifications.Info("Ports are forwarded in a remote window.");
            return;
        }

        argument ??= await Dialogs.InputAsync("Forward a Port", $"The port on {remote.Display} to reach from this machine.", string.Empty,
            text => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536 ? null : "A port number, 1 to 65535.").ConfigureAwait(true);
        if (argument is null || !int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            return;
        }

        try
        {
            var forwarded = remote.Ports.Forward(port);
            StatusBar.ShowMessage($"Port {port.ToString(CultureInfo.InvariantCulture)} on {remote.Display} is forwarded to {forwarded.LocalAddress}.", TimeSpan.FromSeconds(5));
            PortsWindow.Refresh();
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            Workbench.Notifications.Error($"Couldn't forward port {port.ToString(CultureInfo.InvariantCulture)}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ run and debug on the remote machine

    /// <summary>
    /// Run (in a terminal there) or debug (netcoredbg there, through the server) a project of a remote window: its
    /// launch profile's arguments, folder and variables, in the remote machine's terms. A web app's URL opens once its
    /// port is forwarded.
    /// </summary>
    private async Task StartRemoteAsync(RemoteSession remote, string project, Projects.Protocol.RunInfo info, LaunchProfile? profile, bool debug)
    {
        var plan = Runs.Adjust(LaunchPlanner.Plan(info, profile));
        if (remote.Dotnet is { } dotnet && !string.Equals(Path.GetFileNameWithoutExtension(plan.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // An apphost finds its runtime through DOTNET_ROOT when .NET isn't in a default place (~/.dotnet).
            plan = plan with { Environment = new Dictionary<string, string>(plan.Environment, StringComparer.Ordinal) { ["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet)! } };
        }

        var name = Path.GetFileNameWithoutExtension(project);
        if (!debug)
        {
            var terminal = Terminals.Run(name + " (" + remote.Display + ")", plan.FileName, plan.SplitArguments(), plan.WorkingDirectory, plan.Environment);
            Layout.ShowToolWindow(TerminalViewModel.ToolWindowId);
            TerminalWindow.Show(terminal);
        }
        else
        {
            var adapter = await RemoteDebuggerAsync(remote).ConfigureAwait(true);
            if (adapter is null)
            {
                return;
            }

            DebugLog.AppendLine($"> {plan.FileName} {plan.Arguments}   (on {remote.Display})".TrimEnd());
            var arguments = new LaunchArguments(plan.FileName, [.. plan.SplitArguments()], plan.WorkingDirectory, new Dictionary<string, string>(plan.Environment));
            var start = new DebugAdapterStartInfo(adapter, ["--interpreter=vscode"], null, null, remote.Display) { Launcher = remote.DebugLauncher };
            Debugger.JustMyCode = Workbench.Settings.GetBoolean(JustMyCodeSetting, true);
            Debugger.StepFiltering = Workbench.Settings.GetBoolean(StepFilteringSetting, true);
            Layout.ShowToolWindow(DebugConsoleViewModel.ToolWindowId, focus: false);
            await Debugger.LaunchAsync([new DebugLaunch(name, plan) { Remote = new RemoteLaunch(start, arguments, new SourceMap(), remote.Display) }], CancellationToken.None).ConfigureAwait(true);
        }

        if (plan.BrowserUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback)
        {
            _ = BrowseWhenForwardedAsync(remote, uri);
        }
    }

    /// <summary>netcoredbg on the remote machine: copied there on first use; this machine's for the local stand-in.</summary>
    private async Task<string?> RemoteDebuggerAsync(RemoteSession remote)
    {
        if (remote.Spec.Target is { } target)
        {
            return await EnsureRemoteDebuggerAsync(target).ConfigureAwait(true);
        }

        if (DebugAdapterLocator.Resolve() is { } local)
        {
            return local;
        }

        Workbench.Notifications.Error("The debugger (netcoredbg) was not found.");
        return null;
    }

    /// <summary>Opens a web app's URL once its port is forwarded (on the local port it got), for up to a minute.</summary>
    private async Task BrowseWhenForwardedAsync(RemoteSession remote, Uri url)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < TimeSpan.FromMinutes(1) && ReferenceEquals(Remote, remote))
        {
            if (remote.Ports.Forwarded.FirstOrDefault(p => p.RemotePort == url.Port) is { } port)
            {
                RemoteBrowser(new UriBuilder(url) { Host = "localhost", Port = port.LocalPort }.Uri);
                return;
            }

            await Task.Delay(500).ConfigureAwait(true);
        }
    }
}
