using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using DaedalusStudio.Platform.Pty;
using DaedalusStudio.Remote.Protocol;
using DaedalusStudio.Rpc;
using DaedalusStudio.Workbench.Debugging.Remote;
using DaedalusStudio.Workbench.FileSystem;
using DaedalusStudio.Workbench.Remote;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>
/// The remote development server (M21, ADR 0050 and 0051) through local stand-ins only: the server started here as a
/// process (every OS), a WSL distribution (Windows, <c>DAEDALUS_TEST_WSL</c>, default <c>Ubuntu</c>, with .NET), and a
/// fake <c>ssh</c> that runs the command here (Linux and macOS). Never another machine.
/// </summary>
public sealed class RemoteServerTests
{
    private static string RepoRoot => typeof(RemoteServerTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "DaedalusRepoRoot").Value!;

    private static string Configuration => typeof(RemoteServerTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "DaedalusConfiguration").Value!;

    /// <summary>The framework-dependent server build (the stand-ins run it with dotnet).</summary>
    internal static string ServerDll => Path.Combine(RepoRoot, "src", "DaedalusStudio.Remote.Server", "bin", Configuration, "net10.0", "daedalus-server.dll");

    internal static string ServerVersion => FileVersionInfo.GetVersionInfo(ServerDll).ProductVersion!.Split('+')[0];

    private static CancellationToken Timeout(int seconds = 60) =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token).Token;

    private static Task<RemoteConnection> ConnectLocalAsync(TimeSpan? latency = null) =>
        RemoteConnection.StartAsync(new RemoteTransport("dotnet", [ServerDll, "--stdio"], "local server"), ServerVersion, Timeout(), latency);

    private static string TempFolder()
    {
        var folder = Path.Combine(Platform.RealPath.Resolve(Path.GetTempPath()), "daedalus-remote-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    // ------------------------------------------------------------------ the server

    [Fact]
    public async Task Hello_reports_the_server_its_dotnet_and_the_version_lock()
    {
        await using var connection = await ConnectLocalAsync();
        var hello = connection.Server;
        Assert.Equal(ServerVersion, hello.ServerVersion);
        Assert.Equal(RemoteMethods.ProtocolVersion, hello.ProtocolVersion);
        Assert.NotNull(hello.Dotnet);
        Assert.True(File.Exists(hello.Dotnet));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), hello.Home);

        // Another protocol is refused.
        var error = await Assert.ThrowsAsync<JsonRpcRemoteException>(() => connection.Control.InvokeAsync(
            RemoteMethods.Hello, new HelloParams("x", RemoteMethods.ProtocolVersion + 1), RemoteJsonContext.Default.HelloParams, RemoteJsonContext.Default.HelloResult, Timeout()));
        Assert.Contains("protocol", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_preamble_is_found_after_what_a_login_shell_printed()
    {
        var output = new MemoryStream(Encoding.ASCII.GetBytes("Welcome to the machine!\nLast login: today\n\nDAEDALUS-SERVER 1.2.3 1\n\u0001\u0002frames"));
        var frames = await RemoteConnection.SkipToFramesAsync(output, Timeout());
        var rest = new MemoryStream();
        await frames.CopyToAsync(rest, Timeout());
        Assert.Equal("\u0001\u0002frames", Encoding.ASCII.GetString(rest.ToArray()));

        await Assert.ThrowsAsync<IOException>(() => RemoteConnection.SkipToFramesAsync(new MemoryStream("sh: daedalus-server: not found\n"u8.ToArray()), Timeout()));
    }

    [Fact]
    public async Task A_spawned_process_has_its_stdio_on_a_channel_and_its_stderr_and_exit_as_notifications()
    {
        await using var connection = await ConnectLocalAsync();
        await using var process = await connection.SpawnAsync(new SpawnParams(0, ServerDll, ["--echo"]), Timeout());
        var errors = new List<string>();
        process.ErrorLine += line =>
        {
            lock (errors)
            {
                errors.Add(line);
            }
        };
        process.BeginErrorRead();
        Assert.True(process.Id > 0);

        await process.Input.WriteAsync("over a channel\n"u8.ToArray(), Timeout());
        var buffer = new byte[64];
        var read = 0;
        while (!Encoding.ASCII.GetString(buffer, 0, read).Contains('\n', StringComparison.Ordinal))
        {
            read += await process.Output.ReadAsync(buffer.AsMemory(read), Timeout());
        }

        Assert.Equal("over a channel\n", Encoding.ASCII.GetString(buffer, 0, read));
        await process.Channel.CloseOutputAsync(); // its stdin ends
        Assert.Equal(3, await process.Exited.WaitAsync(Timeout()));
        lock (errors)
        {
            Assert.Contains("echo: started", errors);
        }

        // A program that doesn't exist is an error, not a hang.
        await Assert.ThrowsAsync<JsonRpcRemoteException>(() => connection.SpawnAsync(new SpawnParams(0, "/no/such/program-daedalus", []), Timeout()));
    }

    // ------------------------------------------------------------------ reconnecting (M21b, ADR 0054)

    private static Task<RemoteConnection> ConnectResumableAsync() =>
        RemoteConnection.StartAsync(new RemoteTransport("dotnet", [ServerDll, "--relay", Guid.NewGuid().ToString("N"), "--grace", "120"], "local relay"), ServerVersion, Timeout());

    private static async Task<string> ReadLineAsync(Stream stream)
    {
        var buffer = new byte[256];
        var read = 0;
        while (!Encoding.ASCII.GetString(buffer, 0, read).Contains('\n', StringComparison.Ordinal))
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), Timeout());
            Assert.NotEqual(0, count);
            read += count;
        }

        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    [Fact]
    public async Task A_dropped_link_reconnects_and_the_server_and_its_processes_go_on()
    {
        await using var connection = await ConnectResumableAsync();
        Assert.True(connection.IsResumable);
        await using var process = await connection.SpawnAsync(new SpawnParams(0, ServerDll, ["--echo"]), Timeout());
        await process.Input.WriteAsync("before\n"u8.ToArray(), Timeout());
        Assert.Equal("before\n", await ReadLineAsync(process.Output));

        var reconnected = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = false;
        connection.Reconnected += (_, down) => reconnected.TrySetResult(down);
        connection.Disconnected += (_, _) => disconnected = true;

        // The network goes: the process on the server doesn't notice, what's written meanwhile arrives afterwards.
        connection.DropLink();
        await process.Input.WriteAsync("during\n"u8.ToArray(), Timeout());
        var down = await reconnected.Task.WaitAsync(Timeout());
        Assert.True(down < TimeSpan.FromSeconds(5), $"Reconnecting took {down}.");
        Assert.Equal("during\n", await ReadLineAsync(process.Output));
        _ = await connection.ListPortsAsync(Timeout());
        Assert.False(process.Exited.IsCompleted);
        Assert.Equal(1, connection.Reconnections);
        Assert.False(disconnected);

        // Again, while requests are waiting.
        var ping = connection.PingAsync(Timeout());
        connection.DropLink();
        await ping;
        await process.Input.WriteAsync("after\n"u8.ToArray(), Timeout());
        Assert.Equal("after\n", await ReadLineAsync(process.Output));
        await process.Channel.CloseOutputAsync();
        Assert.Equal(3, await process.Exited.WaitAsync(Timeout()));
    }

    [Fact]
    public async Task A_server_that_ended_while_the_link_was_down_ends_the_session()
    {
        await using var connection = await ConnectResumableAsync();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Disconnected += (_, _) => disconnected.TrySetResult();

        // The daemon goes (the machine restarted): the next relay starts another one, which isn't this session.
        using (var daemon = Process.GetProcessById(connection.Server.ProcessId))
        {
            daemon.Kill();
        }

        await disconnected.Task.WaitAsync(Timeout());
        Assert.False(connection.IsConnected);
    }

    [Fact]
    public async Task Files_are_read_written_listed_renamed_watched_and_deleted_through_the_server()
    {
        var folder = TempFolder();
        await using var connection = await ConnectLocalAsync();
        var fs = new RemoteFileSystem(connection, RemotePathMap.Identity);
        var file = Path.Combine(folder, "sub", "a.txt");

        await fs.WriteAllBytesAsync(file, "first"u8.ToArray(), Timeout());
        Assert.Equal("first", await File.ReadAllTextAsync(file, Timeout()));
        Assert.Equal("first"u8.ToArray(), await fs.ReadAllBytesAsync(file, Timeout()));
        var stat = await fs.StatAsync(file, Timeout());
        Assert.True(stat is { Exists: true, IsDirectory: false, Size: 5 });
        Assert.True((await fs.StatAsync(Path.Combine(folder, "sub"), Timeout())).IsDirectory);
        Assert.False((await fs.StatAsync(Path.Combine(folder, "nope"), Timeout())).Exists);
        await Assert.ThrowsAsync<FileNotFoundException>(() => fs.ReadAllBytesAsync(Path.Combine(folder, "nope.txt"), Timeout()));

        var changes = new List<FileChangeEvent>();
        var changed = new TaskCompletionSource();
        using (fs.Watch(Path.Combine(folder, "sub"), recursive: true, batch =>
        {
            lock (changes)
            {
                changes.AddRange(batch);
            }

            changed.TrySetResult();
        }))
        {
            // The watch is set up asynchronously (slowly under load, FSEvents on macOS): write until it sees one.
            var timeout = Timeout();
            for (var attempt = 0; !changed.Task.IsCompleted; attempt++)
            {
                await File.WriteAllTextAsync(Path.Combine(folder, "sub", "b.txt"), "outside " + attempt, timeout);
                await Task.WhenAny(changed.Task, Task.Delay(500, timeout));
            }
        }

        lock (changes)
        {
            Assert.Contains(changes, c => c.Path == Path.Combine(folder, "sub", "b.txt"));
        }

        var entries = await fs.ReadDirectoryAsync(Path.Combine(folder, "sub"), Timeout());
        Assert.Equal(["a.txt", "b.txt"], entries.Select(e => e.Name));
        await fs.RenameAsync(file, Path.Combine(folder, "sub", "c.txt"), overwrite: false, Timeout());
        Assert.True(File.Exists(Path.Combine(folder, "sub", "c.txt")));
        await fs.DeleteAsync(Path.Combine(folder, "sub"), recursive: true, Timeout());
        Assert.False(Directory.Exists(Path.Combine(folder, "sub")));
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public async Task A_terminal_runs_in_a_pseudo_terminal_on_the_server()
    {
        await using var connection = await ConnectLocalAsync();
        var (program, arguments) = OperatingSystem.IsWindows() ? ("cmd.exe", (string[])["/c", "echo remote-terminal-ok"]) : ("/bin/sh", ["-c", "echo remote-terminal-ok"]);
        using var terminal = RemotePseudoTerminal.Start(connection, RemotePathMap.Identity, new PtyStartInfo(program, arguments, Path.GetTempPath(), new Dictionary<string, string> { ["DAEDALUS_REMOTE_TEST"] = "1" }, 80, 24), loginShell: false);
        var output = new StringBuilder();
        var exited = new TaskCompletionSource<int>();
        terminal.Output += data =>
        {
            lock (output)
            {
                output.Append(Encoding.UTF8.GetString(data.Span));
            }
        };
        terminal.Exited += code => exited.TrySetResult(code);
        await terminal.Started.WaitAsync(Timeout());
        Assert.True(terminal.ProcessId > 0);
        Assert.Equal(0, await exited.Task.WaitAsync(Timeout()));
        lock (output)
        {
            Assert.Contains("remote-terminal-ok", output.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_port_that_starts_listening_is_detected_and_forwarded_through_a_tunnel()
    {
        await using var connection = await ConnectLocalAsync();
        await using var ports = new PortForwarder(connection);
        await ports.StartAsync(Timeout());
        var forwarded = new TaskCompletionSource<ForwardedPort>();
        ports.AutoForwarded += (_, port) => forwarded.TrySetResult(port);

        // A "web app" on the remote machine: an echo server that starts after the session did.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var remotePort = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(Timeout());
            var stream = client.GetStream();
            var buffer = new byte[64];
            var read = await stream.ReadAsync(buffer, Timeout());
            await stream.WriteAsync(Encoding.ASCII.GetBytes("echo:" + Encoding.ASCII.GetString(buffer, 0, read)), Timeout());
        }, TestContext.Current.CancellationToken);

        // Other programs (other tests) may start listening meanwhile: wait for this port's forward.
        await forwarded.Task.WaitAsync(Timeout(30));
        var deadline = Stopwatch.StartNew();
        while (!ports.Forwarded.Any(p => p.RemotePort == remotePort))
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(30), $"Port {remotePort} wasn't forwarded.");
            await Task.Delay(50, Timeout());
        }

        var port = ports.Forwarded.First(p => p.RemotePort == remotePort);
        Assert.Equal("auto", port.Origin);
        Assert.NotEqual(remotePort, port.LocalPort); // taken here by the "remote" listener itself: another one
        Assert.Contains(ports.Listening, p => p.Port == remotePort);

        using var local = new TcpClient();
        await local.ConnectAsync(IPAddress.Loopback, port.LocalPort, Timeout());
        var socket = local.GetStream();
        await socket.WriteAsync("hi"u8.ToArray(), Timeout());
        var answer = new byte[16];
        var count = await socket.ReadAsync(answer, Timeout());
        Assert.Equal("echo:hi", Encoding.ASCII.GetString(answer, 0, count));

        ports.Stop(remotePort);
        Assert.DoesNotContain(ports.Forwarded, p => p.RemotePort == remotePort);
    }

    [Fact]
    public async Task Hosts_start_through_the_launcher_and_are_supervised_like_local_ones()
    {
        await using var connection = await ConnectLocalAsync();
        var launch = ProcessLaunchInfo.FromPath(ServerDll, "--echo") with { Launcher = new RemoteProcessLauncher(connection, RemotePathMap.Identity, mapJson: false) };
        var restarted = new TaskCompletionSource();
        await using var host = new SupervisedProcess("echo host", launch, new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50), ShutdownRequest = null, ExitNotification = null });
        host.Restarted += (_, _) => restarted.TrySetResult();
        await host.StartAsync(Timeout());
        var first = host.ProcessId;
        Assert.NotNull(first);

        // The host "crashes" (its process is killed on the server): the supervisor starts it again there.
        Process.GetProcessById(first!.Value).Kill();
        await restarted.Task.WaitAsync(Timeout());
        Assert.NotEqual(first, host.ProcessId);
    }

    // ------------------------------------------------------------------ paths

    [Fact]
    public void Paths_and_uris_are_rewritten_in_json_both_ways()
    {
        var root = OperatingSystem.IsWindows() ? @"\\wsl.localhost\Ubuntu" : "/daedalus-remote/ssh-pi";
        var map = RemotePathMap.Under(root);
        var local = OperatingSystem.IsWindows() ? @"\\wsl.localhost\Ubuntu\home\pi\App\Program.cs" : "/daedalus-remote/ssh-pi/home/pi/App/Program.cs";
        Assert.Equal("/home/pi/App/Program.cs", map.ToRemote(local));
        Assert.Equal(local, map.ToLocal("/home/pi/App/Program.cs"));
        Assert.Equal("/", map.ToRemote(root));
        Assert.Equal(@"C:\elsewhere", map.ToRemote(@"C:\elsewhere"));

        var localUri = new Uri(local).AbsoluteUri;
        var message = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"textDocument/completion\",\"params\":{\"textDocument\":{\"uri\":\"" + localUri + "\"},\"path\":"
            + System.Text.Json.JsonSerializer.Serialize(local) + ",\"text\":\"// /home/pi stays\",\"position\":{\"line\":3,\"character\":7},\"flag\":true,\"none\":null}}";
        var toRemote = Encoding.UTF8.GetString(map.MessageToRemote(Encoding.UTF8.GetBytes(message)));
        Assert.Contains("\"uri\":\"file:///home/pi/App/Program.cs\"", toRemote, StringComparison.Ordinal);
        Assert.Contains("\"path\":\"/home/pi/App/Program.cs\"", toRemote, StringComparison.Ordinal);
        Assert.Contains("\"text\":\"// /home/pi stays\"", toRemote, StringComparison.Ordinal);
        Assert.Contains("\"line\":3", toRemote, StringComparison.Ordinal);
        Assert.Contains("\"flag\":true", toRemote, StringComparison.Ordinal);
        Assert.Contains("\"none\":null", toRemote, StringComparison.Ordinal);

        var toLocal = Encoding.UTF8.GetString(map.MessageToLocal(Encoding.UTF8.GetBytes(
            """{"result":[{"uri":"file:///home/pi/My%20App/a.cs","file":"/home/pi/App/b.cs","label":"/","detail":"a / b","url":"https://x/y","cmd":"/usr/bin/dotnet build"}]}""")));
        Assert.Contains(System.Text.Json.JsonSerializer.Serialize(new Uri(map.ToLocal("/home/pi/My App/a.cs")).AbsoluteUri), toLocal, StringComparison.Ordinal);
        Assert.Contains(System.Text.Json.JsonSerializer.Serialize(map.ToLocal("/home/pi/App/b.cs")), toLocal, StringComparison.Ordinal);
        Assert.Contains("\"label\":\"/\"", toLocal, StringComparison.Ordinal);
        Assert.Contains("\"detail\":\"a / b\"", toLocal, StringComparison.Ordinal);
        Assert.Contains("\"url\":\"https://x/y\"", toLocal, StringComparison.Ordinal);
        Assert.Contains("\"cmd\":\"/usr/bin/dotnet build\"", toLocal, StringComparison.Ordinal); // a command line, not a path

        Assert.Equal("file:///home/a%20b/%C3%BC(1).cs", RemotePathMap.RemoteUri("/home/a b/ü(1).cs"));
        Assert.True(RemotePathMap.Identity.IsIdentity);
    }

    [Fact]
    public void Targets_parse_from_the_command_line()
    {
        Assert.Equal(RemoteTargetSpec.Local, RemoteTargetSpec.Parse("local")!.Kind);
        var ssh = RemoteTargetSpec.Parse("ssh:pi@raspberry:2222")!;
        Assert.Equal("SSH: pi@raspberry:2222", ssh.Display);
        var target = Assert.IsType<SshTarget>(ssh.Target);
        Assert.Equal(["BatchMode=yes"], target.Options);
        Assert.Contains("BatchMode=yes", target.Shell("true").Arguments);
        Assert.Equal("ssh:pi@raspberry:2222", ssh.ToString());
        Assert.NotNull(RemoteTargetSpec.Parse("ssh://pi@raspberry"));
        Assert.Null(RemoteTargetSpec.Parse("ssh:"));
        Assert.Null(RemoteTargetSpec.Parse("ftp:x"));
        Assert.Equal(OperatingSystem.IsWindows(), RemoteTargetSpec.Parse("wsl:Ubuntu") is not null);
        Assert.Equal("linux-x64", RemoteServerBundle.Platform("Linux", "x86_64"));
        Assert.Equal("osx-arm64", RemoteServerBundle.Platform("Darwin", "arm64"));
        Assert.Null(RemoteServerBundle.Platform("FreeBSD", "amd64"));
    }

    // ------------------------------------------------------------------ budgets

    /// <summary>
    /// A request through the server costs one round trip more at a simulated 50 and 150 ms (the completion budget's
    /// "≤ local + RTT + 20 ms", here for the transport alone; the shell test measures completion). Asserted off CI.
    /// </summary>
    [Fact]
    public async Task A_request_costs_one_round_trip_more_at_simulated_latency()
    {
        var folder = TempFolder();
        await using var connection = await ConnectLocalAsync(TimeSpan.Zero);
        var fs = new RemoteFileSystem(connection, RemotePathMap.Identity);
        async Task<double> MedianAsync()
        {
            var times = new List<double>();
            for (var i = 0; i < 9; i++)
            {
                var watch = Stopwatch.StartNew();
                await fs.StatAsync(folder, Timeout());
                times.Add(watch.Elapsed.TotalMilliseconds);
            }

            return times.Order().ElementAt(times.Count / 2);
        }

        var local = await MedianAsync();
        var report = new StringBuilder(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"stat round trip: {local:F1} ms with no latency"));
        foreach (var rtt in (int[])[50, 150])
        {
            connection.SimulatedLatency = TimeSpan.FromMilliseconds(rtt);
            var remote = await MedianAsync();
            report.Append(System.Globalization.CultureInfo.InvariantCulture, $"; {remote:F1} ms at {rtt} ms");
            if (Environment.GetEnvironmentVariable("CI") != "true")
            {
                Assert.True(remote <= local + rtt + 20, $"{remote:F1} ms at {rtt} ms RTT is over the budget of {local + rtt + 20:F1} ms.");
            }
        }

        TestContext.Current.SendDiagnosticMessage(report.ToString());
        Directory.Delete(folder, recursive: true);
    }

    // ------------------------------------------------------------------ installing on a target (stand-ins)

    /// <summary>A target that stands in for another machine: WSL on Windows, a fake ssh running here on Linux and macOS.</summary>
    internal static RemoteTarget? StandIn(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            var distribution = Environment.GetEnvironmentVariable("DAEDALUS_TEST_WSL") is { Length: > 0 } name ? name : "Ubuntu";
            var listed = RemoteTargets.WslDistributionsAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (!listed.Contains(distribution, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            var dotnet = RemoteShell.RunAsync(new WslTarget(distribution), "PATH=\"$PATH:$HOME/.dotnet\" command -v dotnet", CancellationToken.None).GetAwaiter().GetResult();
            return dotnet.Succeeded && dotnet.Output.Trim().Length > 0 ? new WslTarget(distribution) : null;
        }

        var script = Path.Combine(folder, "fake-ssh");
        File.WriteAllText(script, """
            #!/bin/sh
            # A stand-in for ssh: skip the options and the destination, run the command here.
            while [ $# -gt 0 ]; do
              case "$1" in
                -T) shift ;;
                -o|-p|-i|-l) shift 2 ;;
                *) break ;;
              esac
            done
            shift
            exec env HOME="{HOME}" sh -c "$*"
            """.Replace("{HOME}", Path.Combine(folder, "home"), StringComparison.Ordinal).Replace("\r\n", "\n", StringComparison.Ordinal));
        Directory.CreateDirectory(Path.Combine(folder, "home")); // never the real home (found on the Mac)
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new SshTarget("stand-in", Program: script) { Options = ["BatchMode=yes"] };
    }

    [Fact]
    public async Task The_server_is_copied_checked_and_started_on_a_stand_in_target_once_per_version()
    {
        var folder = TempFolder();
        var target = StandIn(folder);
        if (target is null)
        {
            Directory.Delete(folder, recursive: true);
            Assert.Skip("No stand-in target here: WSL with .NET on Windows (DAEDALUS_TEST_WSL), the fake ssh on Linux and macOS.");
            return;
        }

        Environment.SetEnvironmentVariable(RemoteServerBundle.ServerVariable, ServerDll);
        var version = "test-" + Guid.NewGuid().ToString("N")[..8];
        // A home of its own on the target: never the real ~/.daedalus-server (WSL's on Windows, this machine's elsewhere).
        var serverRoot = "/tmp/daedalus-server-tests-" + Guid.NewGuid().ToString("N")[..8];
        var bundle = new RemoteServerBundle(Path.Combine(folder, "cache"), version, [], RemoteServerBundle.LocateServer, serverRoot + "/.daedalus-server");
        InstalledServer? installed = null;
        try
        {
            installed = await bundle.EnsureAsync(target, null, Timeout(300));
            Assert.True(installed.Copied);
            Assert.EndsWith("/.daedalus-server/" + version, installed.Folder, StringComparison.Ordinal);
            Assert.False(installed.SelfContained);
            Assert.NotNull(installed.Platform);
            // The fake ssh runs here: inside WSL (Linux validation from Windows) that is a WSL distribution too.
            Assert.Equal(target is WslTarget || Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") is { Length: > 0 }, installed.WslDistribution is not null);

            // Again: the checksum there matches, nothing is copied.
            Assert.False((await bundle.EnsureAsync(target, null, Timeout(300))).Copied);

            // It runs there (the framework-dependent build, on the target's dotnet).
            var (program, arguments) = target.Shell(RemoteServerBundle.StartScript(installed.Folder));
            await using var connection = await RemoteConnection.StartAsync(new RemoteTransport(program, arguments, "stand-in"), ServerVersion, Timeout(120));
            Assert.Equal(target is WslTarget || OperatingSystem.IsLinux() ? "linux" : "macos", connection.Server.Os); // the fake ssh runs it here
            // The server names its folder canonically: macOS's /tmp is a link to /private/tmp.
            static string Canonical(string path) => OperatingSystem.IsMacOS() && path.StartsWith("/tmp/", StringComparison.Ordinal) ? "/private" + path : path;
            Assert.Equal(Canonical(installed.Folder + "/server/"), Canonical(connection.Server.ServerFolder.Replace('\\', '/')));

            // Resumable there (M21b): the daemon outlives the transport, which is started again.
            var (relayProgram, relayArguments) = target.Shell(RemoteServerBundle.StartScript(installed.Folder, ["--relay", Guid.NewGuid().ToString("N"), "--grace", "60"]));
            await using var resumable = await RemoteConnection.StartAsync(new RemoteTransport(relayProgram, relayArguments, "stand-in"), ServerVersion, Timeout(120));
            Assert.True(resumable.IsResumable);
            var daemon = resumable.Server.ProcessId;
            var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            resumable.Reconnected += (_, _) => reconnected.TrySetResult();
            resumable.DropLink();
            await reconnected.Task.WaitAsync(Timeout(60));
            await resumable.PingAsync(Timeout());
            var hello = await resumable.Control.InvokeAsync(RemoteMethods.Hello, new HelloParams(ServerVersion, RemoteMethods.ProtocolVersion), RemoteJsonContext.Default.HelloParams, RemoteJsonContext.Default.HelloResult, Timeout());
            Assert.Equal(daemon, hello.ProcessId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RemoteServerBundle.ServerVariable, null);
            await RemoteShell.RunAsync(target, $"rm -rf \"{serverRoot}\"", CancellationToken.None);

            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Podman with the local Ubuntu image (never pulled): here on Linux, in the WSL distribution on Windows; and the
    /// .NET folder on the machine that runs the containers, to mount into one. Null when there is none.
    /// </summary>
    private static (IReadOnlyList<string> Tool, string DotnetRoot)? ContainerStandIn()
    {
        const string Image = "docker.io/library/ubuntu:24.04";
        IReadOnlyList<string> tool;
        IReadOnlyList<string> shell;
        if (OperatingSystem.IsWindows())
        {
            var distribution = Environment.GetEnvironmentVariable("DAEDALUS_TEST_WSL") is { Length: > 0 } name ? name : "Ubuntu";
            if (!RemoteTargets.WslDistributionsAsync(CancellationToken.None).GetAwaiter().GetResult().Contains(distribution, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            tool = ["wsl.exe", "-d", distribution, "--exec", "podman"];
            shell = ["wsl.exe", "-d", distribution, "--exec", "sh", "-lc"];
        }
        else if (OperatingSystem.IsLinux())
        {
            tool = ["podman"];
            shell = ["sh", "-lc"];
        }
        else
        {
            return null;
        }

        try
        {
            if (Run(tool, ["image", "exists", Image]).Code != 0)
            {
                return null;
            }

            var root = Run(shell, ["if [ -x \"$HOME/.dotnet/dotnet\" ]; then echo \"$HOME/.dotnet\"; else dirname \"$(readlink -f \"$(command -v dotnet)\")\"; fi"]).Output.Trim();
            return root.StartsWith('/') ? (tool, root) : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }

        static (int Code, string Output) Run(IReadOnlyList<string> command, IReadOnlyList<string> arguments)
        {
            var start = new ProcessStartInfo(command[0], [.. command.Skip(1), .. arguments]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(60_000);
            return (process.ExitCode, output);
        }
    }

    [Fact]
    public async Task A_running_container_is_a_remote_window_target_the_server_is_copied_into_and_runs_in()
    {
        if (ContainerStandIn() is not { } containers)
        {
            Assert.Skip("No container stand-in: Podman with docker.io/library/ubuntu:24.04 (here on Linux, in WSL on Windows).");
            return;
        }

        var folder = TempFolder();
        var name = "daedalus-remote-test-" + Guid.NewGuid().ToString("N")[..8];
        var tool = containers.Tool;

        // A bare Ubuntu with the .NET of the machine that runs it, read-only, and no ICU: the server and hosts run on it.
        var run = new ProcessStartInfo(tool[0], [.. tool.Skip(1), "run", "-d", "--rm", "--name", name,
            "-v", containers.DotnetRoot + ":/opt/dotnet:ro",
            "-e", "PATH=/opt/dotnet:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
            "-e", "DOTNET_ROOT=/opt/dotnet", "-e", "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1",
            "docker.io/library/ubuntu:24.04", "sleep", "900"])
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using (var process = Process.Start(run)!)
        {
            var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, "podman run: " + error);
        }

        Environment.SetEnvironmentVariable(RemoteServerBundle.ServerVariable, ServerDll);
        try
        {
            var spec = RemoteTargetSpec.Parse("container:" + name, containerTool: tool)!;
            Assert.Equal(("container", "Container: " + name), (spec.Kind, spec.Display));
            var options = new RemoteSessionOptions(spec, ServerVersion, Path.Combine(folder, "cache")) { ServerHome = "/tmp/.daedalus-server" };
            await using var session = await RemoteSession.ConnectAsync(options, "/tmp", null, Timeout(300));
            Assert.Equal("linux", session.Connection.Server.Os);
            Assert.StartsWith("/tmp/.daedalus-server/", session.Connection.Server.ServerFolder.Replace('\\', '/'), StringComparison.Ordinal);
            Assert.False(session.Map.IsIdentity); // the container's paths are under a local root of their own
        }
        finally
        {
            Environment.SetEnvironmentVariable(RemoteServerBundle.ServerVariable, null);
            var remove = new ProcessStartInfo(tool[0], [.. tool.Skip(1), "rm", "-f", name]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using (var process = Process.Start(remove)!)
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }

            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_damaged_bundle_is_refused_by_its_checksum()
    {
        var folder = TempFolder();
        var target = StandIn(folder);
        if (target is null)
        {
            Directory.Delete(folder, recursive: true);
            Assert.Skip("No stand-in target here: WSL with .NET on Windows (DAEDALUS_TEST_WSL), the fake ssh on Linux and macOS.");
            return;
        }

        var root = "/tmp/daedalus-server-tests-" + Guid.NewGuid().ToString("N")[..8];
        var home = root + "/.daedalus-server/test-damaged"; // never the real home
        try
        {
            using var damaged = new MemoryStream("not the archive"u8.ToArray());
            var result = await RemoteShell.RunAsync(target, RemoteServerBundle.InstallScript(home, new string('0', 64)), Timeout(), damaged);
            Assert.Contains("checksum ", result.Output, StringComparison.Ordinal);
            var left = await RemoteShell.RunAsync(target, $"ls -d \"{home}\"* 2>/dev/null | wc -l", Timeout());
            Assert.Equal("0", left.Output.Trim());
        }
        finally
        {
            await RemoteShell.RunAsync(target, $"rm -rf \"{root}\"", CancellationToken.None);
            Directory.Delete(folder, recursive: true);
        }
    }
}
