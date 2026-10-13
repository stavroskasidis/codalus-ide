using System.Globalization;
using Codalus.Platform.Pty;
using Codalus.Remote.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Debugging.Remote;
using Codalus.Workbench.FileSystem;

namespace Codalus.Workbench.Remote;

/// <summary>
/// Where a remote window works (M21): <c>ssh:user@host:port</c> (or <c>ssh://…</c>), <c>wsl:Ubuntu</c> (Windows),
/// <c>container:name</c> (a running container, through <c>docker exec</c> or Podman's; M21b), or <c>local</c> — a server
/// on this machine, the stand-in that tests and the smoke test use.
/// </summary>
public sealed record RemoteTargetSpec(string Kind, string Name, RemoteTarget? Target)
{
    public const string Ssh = "ssh";
    public const string Wsl = "wsl";
    public const string Local = "local";
    public const string Container = "container";

    /// <summary>For the status bar and titles: <c>SSH: pi@host</c>, <c>WSL: Ubuntu</c>, <c>Local server</c>.</summary>
    public string Display => Kind switch
    {
        Ssh => "SSH: " + Name,
        Wsl => "WSL: " + (Name.Length == 0 ? "default" : Name),
        Container => "Container: " + Name,
        _ => "Local server",
    };

    /// <summary>A short id for the local root of its paths (<see cref="RemotePathMap"/>).</summary>
    public string Id => new([.. (Kind + "-" + Name).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' ? c : '_')]);

    public override string ToString() => Kind == Local ? Local : Kind + ":" + Name;

    /// <summary>
    /// Reads <c>ssh:host</c>, <c>ssh://user@host:port</c>, <c>wsl:Distribution</c>, <c>wsl</c>, <c>container:name</c>
    /// (run through <paramref name="containerTool"/>, the container CLI's command line) or <c>local</c>; null when it isn't one.
    /// </summary>
    public static RemoteTargetSpec? Parse(string? text, string sshProgram = "ssh", IReadOnlyList<string>? containerTool = null)
    {
        var value = text?.Trim() ?? string.Empty;
        if (value.StartsWith("container:", StringComparison.OrdinalIgnoreCase))
        {
            var name = value["container:".Length..].Trim();
            return name.Length == 0 || name.Any(char.IsWhiteSpace) || name.StartsWith('-')
                ? null
                : new RemoteTargetSpec(Container, name, new ContainerTarget(name, containerTool is { Count: > 0 } ? containerTool : ["docker"]));
        }

        if (value.Equals(Local, StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteTargetSpec(Local, string.Empty, null);
        }

        if (value.Equals(Wsl, StringComparison.OrdinalIgnoreCase) || value.StartsWith("wsl:", StringComparison.OrdinalIgnoreCase))
        {
            var distribution = value.Length > 4 ? value[4..].Trim() : string.Empty;
            return OperatingSystem.IsWindows() ? new RemoteTargetSpec(Wsl, distribution, new WslTarget(distribution)) : null;
        }

        if (value.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("ssh:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = value.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ? value : value[4..];
            return SshTarget.Parse(rest, sshProgram) is { } ssh
                ? new RemoteTargetSpec(Ssh, (ssh.User is null ? ssh.Host : ssh.User + "@" + ssh.Host) + (ssh.Port is { } port ? ":" + port.ToString(CultureInfo.InvariantCulture) : string.Empty), ssh with { Options = ["BatchMode=yes"] })
                : null;
        }

        return null;
    }
}

/// <summary>What a remote session needs from the shell: its version, cache folder, and the hosts it carries over.</summary>
public sealed record RemoteSessionOptions(RemoteTargetSpec Spec, string ClientVersion, string CacheFolder)
{
    /// <summary>Simulated network latency (budgets, tests; <c>CODALUS_REMOTE_LATENCY_MS</c>).</summary>
    public TimeSpan? Latency { get; init; }

    /// <summary>The local stand-in's server (a <c>.dll</c> or executable); by default the bundle's.</summary>
    public string? LocalServer { get; init; }

    /// <summary>Where the bundles go on the target (a shell expression; tests use a folder of their own).</summary>
    public string ServerHome { get; init; } = RemoteServerBundle.DefaultHome;

    /// <summary>The hosts to copy with the server (folder name and local folder).</summary>
    public IReadOnlyList<BundlePart> Hosts { get; init; } = [];

    /// <summary>
    /// Whether the server outlives a dropped link (M21b, ADR 0054: <c>--relay</c>, reconnected for
    /// <see cref="ReconnectFor"/>). Null: for remote targets, not the local stand-in.
    /// </summary>
    public bool? Resumable { get; init; }

    /// <summary>How long a dropped link is retried, and how long the server waits for it.</summary>
    public TimeSpan ReconnectFor { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Paths of a local stand-in go through a virtual root as on a real remote machine (Linux and macOS clients; tests).</summary>
    public bool MapLocalPaths { get; init; } = !OperatingSystem.IsWindows();
}

/// <summary>
/// A remote window's session (M21, ADR 0050): the connection to <c>codalus-server</c> on the target, the path map,
/// the mounted file system, where hosts run, terminals, and port forwarding. The shell keeps the UI and the editor
/// buffers; everything that reads the workspace or runs its code runs on the target.
/// </summary>
public sealed class RemoteSession : IAsyncDisposable
{
    /// <summary>The hosts that run on the server, by their folder next to the shell.</summary>
    public static readonly IReadOnlyList<string> RemoteHosts = ["buildhost", "lshost", "markuphost"];

    private readonly List<IDisposable> _mounts = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly InstalledServer? _installed;

    private RemoteSession(RemoteTargetSpec spec, RemoteConnection connection, RemotePathMap map, InstalledServer? installed, string? workspaceRoot)
    {
        Spec = spec;
        Connection = connection;
        Map = map;
        _installed = installed;
        FileSystem = new RemoteFileSystem(connection, map);
        if ((map.LocalRoot ?? workspaceRoot) is { } root)
        {
            _mounts.Add(FileSystems.Mount(root, FileSystem));
        }

        Launcher = new RemoteProcessLauncher(connection, map);
        DebugLauncher = new RemoteProcessLauncher(connection, map);
        Ports = new PortForwarder(connection);
        connection.Disconnected += (_, error) => Disconnected?.Invoke(this, error);
        _ = MeasureLatencyAsync();
    }

    public RemoteTargetSpec Spec { get; }

    public RemoteConnection Connection { get; }

    public RemotePathMap Map { get; }

    public RemoteFileSystem FileSystem { get; }

    /// <summary>Starts hosts on the server (their JSON's paths mapped).</summary>
    public IProcessLauncher Launcher { get; }

    /// <summary>Starts a debug adapter on the server (DAP's paths mapped too).</summary>
    public IProcessLauncher DebugLauncher { get; }

    public PortForwarder Ports { get; }

    public string Display => Spec.Display;

    /// <summary>The bundle's folder on the target (null for the local stand-in).</summary>
    public string? ServerFolder => _installed?.Folder;

    /// <summary>The transport's last measured round trip.</summary>
    public TimeSpan? Latency { get; private set; }

    public event EventHandler? LatencyChanged;

    public event EventHandler<Exception?>? Disconnected;

    /// <summary>
    /// Connects to <paramref name="options"/>' target: installs or updates the server bundle there, starts it through
    /// ssh or wsl.exe, says hello, mounts its file system and starts watching its ports.
    /// <paramref name="workspace"/> is the remote folder or file to open (a local stand-in's mount root).
    /// </summary>
    public static async Task<RemoteSession> ConnectAsync(RemoteSessionOptions options, string? workspace, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var spec = options.Spec;
        RemoteTransport transport;
        InstalledServer? installed = null;
        var resumable = options.Resumable ?? spec.Target is not null;
        string[] mode = resumable
            ? ["--relay", Guid.NewGuid().ToString("N"), "--grace", ((int)options.ReconnectFor.TotalSeconds).ToString(CultureInfo.InvariantCulture)]
            : ["--stdio"];
        if (spec.Target is { } target)
        {
            var bundle = new RemoteServerBundle(options.CacheFolder, options.ClientVersion, options.Hosts, RemoteServerBundle.LocateServer, options.ServerHome);
            progress?.Report($"Checking the Codalus server on {spec.Display}…");
            installed = await bundle.EnsureAsync(target, progress, cancellationToken).ConfigureAwait(false);
            var (program, arguments) = target.Shell(RemoteServerBundle.StartScript(installed.Folder, mode));
            transport = new RemoteTransport(program, arguments, spec.Display);
        }
        else
        {
            var server = options.LocalServer ?? LocalServerPath() ?? throw new InvalidOperationException("The remote server (codalus-server) isn't part of this installation.");
            var launch = ProcessLaunchInfo.FromPath(server, mode);
            transport = new RemoteTransport(launch.FileName, launch.Arguments, spec.Display);
        }

        progress?.Report($"Connecting to {spec.Display}…");
        var connection = await RemoteConnection.StartAsync(transport, options.ClientVersion, cancellationToken, options.Latency).ConfigureAwait(false);
        connection.ReconnectFor = options.ReconnectFor;
        try
        {
            if (connection.Server.ServerVersion != options.ClientVersion)
            {
                throw new InvalidOperationException($"The server on {spec.Display} is version {connection.Server.ServerVersion}, not {options.ClientVersion}.");
            }

            if (connection.Server.Os == "windows" && !OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException("Remote Windows machines are opened from Windows only for now.");
            }

            RemotePathMap map;
            if (spec.Target is null && (OperatingSystem.IsWindows() || !options.MapLocalPaths))
            {
                map = RemotePathMap.Identity;
            }
            else if (connection.Server.Os == "windows")
            {
                map = RemotePathMap.Identity;
            }
            else
            {
                map = RemotePathMap.Under(RemotePathMap.LocalRootFor(spec.Id, spec.Kind == RemoteTargetSpec.Wsl ? installed?.WslDistribution ?? spec.Name : null));
            }

            var root = map.IsIdentity ? workspace is null ? null : Directory.Exists(workspace) ? workspace : Path.GetDirectoryName(workspace) : null;
            var session = new RemoteSession(spec, connection, map, installed, root);
            await session.Ports.StartAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The server a local stand-in starts: <c>CODALUS_SERVER</c>, else <c>remote/</c> next to the shell.</summary>
    public static string? LocalServerPath()
    {
        if (Environment.GetEnvironmentVariable(RemoteServerBundle.ServerVariable) is { Length: > 0 } fromEnv)
        {
            return Directory.Exists(fromEnv) ? Path.Combine(fromEnv, "codalus-server.dll") : fromEnv;
        }

        var native = Path.Combine(AppContext.BaseDirectory, "remote", "server", OperatingSystem.IsWindows() ? "codalus-server.exe" : "codalus-server");
        var portable = Path.Combine(AppContext.BaseDirectory, "remote", "server", "codalus-server.dll");
        return File.Exists(portable) ? portable : File.Exists(native) ? native : null;
    }

    // ------------------------------------------------------------------ placement

    /// <summary>
    /// Where a host runs in this session: the build, C# and markup hosts on the server (from the bundle's folder there,
    /// or as they are for the local stand-in), with this session's launcher; null for a host that only runs locally.
    /// </summary>
    public ProcessLaunchInfo? Place(ProcessLaunchInfo local)
    {
        var path = local.FileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) && local.Arguments.Count > 0 ? local.Arguments[0] : local.FileName;
        var rest = local.FileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) && local.Arguments.Count > 0 ? local.Arguments.Skip(1).ToList() : [.. local.Arguments];
        var name = Path.GetFileNameWithoutExtension(path);
        var folder = name switch
        {
            "codalus-buildhost" => "buildhost",
            "codalus-lshost" => "lshost",
            "codalus-markuphost" => "markuphost",
            _ => null,
        };
        if (folder is null)
        {
            return null;
        }

        if (_installed is null)
        {
            return local with { Launcher = Launcher };
        }

        // Always the dll there: this machine's apphost is for this OS.
        return new ProcessLaunchInfo("dotnet", [$"{_installed.Folder}/{folder}/{name}.dll", .. rest.Select(Map.ToRemote)]) { Launcher = Launcher };
    }

    /// <summary>
    /// Serves a workspace folder from the server. With a path map every remote path is mounted already; a stand-in
    /// server on this machine (an identity map) serves the folders opened through it.
    /// </summary>
    public void MountWorkspace(string folder)
    {
        if (Map.IsIdentity && !FileSystems.IsRemote(folder))
        {
            lock (_mounts)
            {
                _mounts.Add(FileSystems.Mount(folder, FileSystem));
            }
        }
    }

    /// <summary>A terminal on the server; <paramref name="loginShell"/> starts the user's shell there.</summary>
    public PseudoTerminal StartTerminal(PtyStartInfo info, bool loginShell) => RemotePseudoTerminal.Start(Connection, Map, info, loginShell);

    /// <summary>The <c>dotnet</c> on the server, as a path of this machine's form (null when .NET isn't installed there).</summary>
    public string? Dotnet => Connection.Server.Dotnet is { } dotnet ? Map.ToLocal(dotnet) : null;

    /// <summary>The user's home folder on the server, as a path of this machine's form.</summary>
    public string Home => Map.ToLocal(Connection.Server.Home);

    private async Task MeasureLatencyAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested && Connection.IsConnected)
            {
                Latency = await Connection.PingAsync(_lifetime.Token).ConfigureAwait(false);
                LatencyChanged?.Invoke(this, EventArgs.Empty);
                await Task.Delay(TimeSpan.FromSeconds(5), _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await Ports.DisposeAsync().ConfigureAwait(false);
        lock (_mounts)
        {
            foreach (var mount in _mounts)
            {
                mount.Dispose();
            }

            _mounts.Clear();
        }

        await Connection.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
