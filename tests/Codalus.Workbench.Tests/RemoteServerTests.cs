using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Codalus.Platform.Pty;
using Codalus.Remote.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Debugging.Remote;
using Codalus.Workbench.FileSystem;
using Codalus.Workbench.Remote;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The remote development server (M21, ADR 0050 and 0051) through local stand-ins only: the server started here as a
/// process (every OS), a WSL distribution (Windows, <c>CODALUS_TEST_WSL</c>, default <c>Ubuntu</c>, with .NET), and a
/// fake <c>ssh</c> that runs the command here (Linux and macOS). Never another machine.
/// </summary>
public sealed class RemoteServerTests
{
    private static string RepoRoot => typeof(RemoteServerTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "CodalusRepoRoot").Value!;

    private static string Configuration => typeof(RemoteServerTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "CodalusConfiguration").Value!;

    /// <summary>The framework-dependent server build (the stand-ins run it with dotnet).</summary>
    internal static string ServerDll => Path.Combine(RepoRoot, "src", "Codalus.Remote.Server", "bin", Configuration, Codalus.Tests.RepoBuild.TargetFramework, "codalus-server.dll");

    internal static string ServerVersion => FileVersionInfo.GetVersionInfo(ServerDll).ProductVersion!.Split('+')[0];

    private static CancellationToken Timeout(int seconds = 60) =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token).Token;

    private static Task<RemoteConnection> ConnectLocalAsync(TimeSpan? latency = null) =>
        RemoteConnection.StartAsync(new RemoteTransport("dotnet", [ServerDll, "--stdio"], "local server"), ServerVersion, Timeout(), latency);

    private static string TempFolder()
    {
        var folder = Path.Combine(Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-remote-" + Guid.NewGuid().ToString("N")[..8]);
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
        var output = new MemoryStream(Encoding.ASCII.GetBytes("Welcome to the machine!\nLast login: today\n\nCODALUS-SERVER 1.2.3 1\n\u0001\u0002frames"));
        var frames = await RemoteConnection.SkipToFramesAsync(output, Timeout());
        var rest = new MemoryStream();
        await frames.CopyToAsync(rest, Timeout());
        Assert.Equal("\u0001\u0002frames", Encoding.ASCII.GetString(rest.ToArray()));

        await Assert.ThrowsAsync<IOException>(() => RemoteConnection.SkipToFramesAsync(new MemoryStream("sh: codalus-server: not found\n"u8.ToArray()), Timeout()));
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
        await Assert.ThrowsAsync<JsonRpcRemoteException>(() => connection.SpawnAsync(new SpawnParams(0, "/no/such/program-codalus", []), Timeout()));
    }

    [Fact]
    public void Plugin_folders_map_to_their_copies_in_the_bundle()
    {
        // ADR 0057: a workspace plugin's folder on this machine is the bundle's plugins/<id> on the server.
        var root = Path.Combine(Path.GetTempPath(), "bundle-paths");
        IReadOnlyList<BundlePart> parts = [new("pluginhost", Path.Combine(root, "pluginhost")), new("plugins/codalus.dotnettest", Path.Combine(root, "plugins", "DotNetTest"))];
        Assert.Equal("/home/me/.codalus-server/1.0/plugins/codalus.dotnettest", RemoteSession.BundlePath("/home/me/.codalus-server/1.0", parts, Path.Combine(root, "plugins", "DotNetTest")));
        Assert.Equal("/s/pluginhost/codalus-pluginhost.dll", RemoteSession.BundlePath("/s", parts, Path.Combine(root, "pluginhost", "codalus-pluginhost.dll")));
        Assert.Null(RemoteSession.BundlePath("/s", parts, Path.Combine(root, "plugins", "Other")));
        Assert.Null(RemoteSession.BundlePath("/s", parts, Path.Combine(root, "plugins", "DotNetTest2")));
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
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        connection.Log += (_, line) => log.Enqueue(line);
        connection.Reconnected += (_, down) => reconnected.TrySetResult(down);
        connection.Disconnected += (_, _) => disconnected = true;

        // The network goes: the process on the server doesn't notice, what's written meanwhile arrives afterwards.
        connection.DropLink();
        await process.Input.WriteAsync("during\n"u8.ToArray(), Timeout());
        var down = await reconnected.Task.WaitAsync(Timeout());
        Assert.True(down < TimeSpan.FromSeconds(5), $"Reconnecting took {down} (a loaded Windows PC, the 1.0 validation: 8.3 s); the connection's log:\n{string.Join("\n", log)}");
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
        using var terminal = RemotePseudoTerminal.Start(connection, RemotePathMap.Identity, new PtyStartInfo(program, arguments, Path.GetTempPath(), new Dictionary<string, string> { ["CODALUS_REMOTE_TEST"] = "1" }, 80, 24), loginShell: false);
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
        var running = new TaskCompletionSource();
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var host = new SupervisedProcess("echo host", launch, new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50), ShutdownRequest = null, ExitNotification = null });
        host.Log += (_, line) =>
        {
            events.Enqueue("log: " + line);
            if (line == "echo: started")
            {
                running.TrySetResult();
            }
        };
        host.Crashed += (_, e) =>
        {
            events.Enqueue($"crashed: exit code {e.ExitCode}, will restart {e.WillRestart}, during startup {e.DuringStartup}: {e.Error?.Message}");
            if (!e.WillRestart)
            {
                restarted.TrySetException(new InvalidOperationException("The supervisor gave up instead of restarting the host."));
            }
        };
        host.Restarted += (_, _) =>
        {
            events.Enqueue("restarted");
            restarted.TrySetResult();
        };
        string State() =>
            $"connected {connection.IsConnected}, host running {host.IsRunning}, gave up {host.GaveUp}, last failure '{host.LastFailure?.Message}'; events: {string.Join(" | ", events)}";
        await host.StartAsync(Timeout());
        var first = host.ProcessId;
        Assert.NotNull(first);

        // Once the host runs and has written to standard error, as a real host logs while it starts. The test killed it
        // as soon as the server said it started, so it was restarted only if the kill came before that line: on a
        // loaded Mac it came after, and the kill counted as a failed start, which is never restarted (develop's 0.4.0
        // run). A process killed from outside is restarted, whatever it printed.
        await running.Task.WaitAsync(Timeout()).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        Assert.True(running.Task.IsCompleted, $"The echo host didn't say it started: {State()}");

        // The host "crashes" (its process is killed on the server): the supervisor starts it again there.
        Process.GetProcessById(first!.Value).Kill();
        await ((Task)restarted.Task).WaitAsync(Timeout()).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        Assert.True(restarted.Task.IsCompletedSuccessfully, $"The killed host wasn't started again: {State()}");
        Assert.NotEqual(first, host.ProcessId);
    }

    // ------------------------------------------------------------------ paths

    [Fact]
    public void Paths_and_uris_are_rewritten_in_json_both_ways()
    {
        var root = OperatingSystem.IsWindows() ? @"\\wsl.localhost\Ubuntu" : "/codalus-remote/ssh-pi";
        var map = RemotePathMap.Under(root);
        var local = OperatingSystem.IsWindows() ? @"\\wsl.localhost\Ubuntu\home\pi\App\Program.cs" : "/codalus-remote/ssh-pi/home/pi/App/Program.cs";
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

        // A document's text (a string longer than any path) passes as it is, escapes and all, in an array too.
        var text = "/home/pi/App/Program.cs \"quoted\" \\ é\n" + new string('x', RemotePathMap.RawStringLength);
        var large = "{\"params\":{\"items\":[" + System.Text.Json.JsonSerializer.Serialize(text) + "," + System.Text.Json.JsonSerializer.Serialize(local) + "]}}";
        using var parsed = System.Text.Json.JsonDocument.Parse(map.MessageToRemote(Encoding.UTF8.GetBytes(large)));
        var items = parsed.RootElement.GetProperty("params").GetProperty("items");
        Assert.Equal(text, items[0].GetString());
        Assert.Equal("/home/pi/App/Program.cs", items[1].GetString());
    }

    /// <summary>
    /// A remote window's host connection rewrites the paths of every message it writes (<see cref="MappedJsonStream"/>):
    /// with the writer loop (M57) that happens there, so the didOpen of a large file doesn't hold its caller (the UI
    /// thread) for the encoding, the rewrite or a link that's slow to take it.
    /// </summary>
    [Fact]
    public async Task A_large_did_open_through_the_path_rewrite_does_not_hold_its_caller()
    {
        var root = OperatingSystem.IsWindows() ? @"\\wsl.localhost\Ubuntu" : "/codalus-remote/ssh-pi";
        var local = OperatingSystem.IsWindows() ? @"\\wsl.localhost\Ubuntu\home\pi\App\Big.xml" : "/codalus-remote/ssh-pi/home/pi/App/Big.xml";
        var toRemote = new System.IO.Pipelines.Pipe();
        var fromRemote = new System.IO.Pipelines.Pipe();
        using var link = new ManualResetEventSlim();
        var mapped = new MappedJsonStream(new BlockingWriteStream(toRemote.Writer.AsStream(), link), RemotePathMap.Under(root));
        await using var connection = new JsonRpcConnection(fromRemote.Reader.AsStream(), mapped);
        connection.Start();

        var text = string.Concat(Enumerable.Repeat("<item path=\"" + local + "\">&lt;v&gt;</item>\n", 60_000)); // ~4 MB
        var uri = new Uri(local).AbsoluteUri;
        var queueing = Task.Factory.StartNew(() => connection.NotifyAsync(
            LanguageServices.Protocol.Methods.DidOpen,
            new LanguageServices.Protocol.DidOpenTextDocumentParams(new LanguageServices.Protocol.TextDocumentItem(uri, "xml", 1, text)),
            LanguageServices.Protocol.ProtocolJsonContext.Default.DidOpenTextDocumentParams,
            CancellationToken.None), TestContext.Current.CancellationToken, TaskCreationOptions.None, TaskScheduler.Default);

        // The call returns while the link takes nothing (it blocked on its first byte before: encoded, rewritten, written).
        var sent = await queueing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(sent.IsCompleted);

        link.Set();
        var reader = new FramedMessageReader(toRemote.Reader.AsStream());
        var message = await reader.ReadMessageAsync(Timeout());
        await sent.WaitAsync(Timeout());
        using var document = System.Text.Json.JsonDocument.Parse(message);
        var item = document.RootElement.GetProperty("params").GetProperty("textDocument");
        Assert.Equal("file:///home/pi/App/Big.xml", item.GetProperty("uri").GetString());
        Assert.Equal(text, item.GetProperty("text").GetString()); // the text's own paths stay
    }

    /// <summary>A stream whose writes block their caller's thread until <paramref name="link"/> is set: a link that takes nothing.</summary>
    private sealed class BlockingWriteStream(Stream inner, ManualResetEventSlim link) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            link.Wait(TimeSpan.FromSeconds(30), cancellationToken);
            return inner.WriteAsync(buffer, cancellationToken);
        }
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

        // Measured in alternating rounds (no latency, 50 ms, 150 ms, nine times), as the shell's completion budget is, and
        // each compared by its fastest sample, not its median. Load only adds time, and adds more to a longer request: a
        // 150 ms request overlaps more of a burst than a 1 ms one, so the medians went over the budget during whole-suite
        // runs on the Mac (docs/ci-flakes.md) and, under load that comes and goes on Windows, in 15 of 20 runs measured
        // one after the other and 15 of 20 in alternating rounds; compared by the fastest, 0 of 20. Every sample pays the
        // round trips the protocol makes, so one more shows in the fastest too. Each comes with its samples.
        int[] rtts = [0, 50, 150];
        var times = rtts.ToDictionary(rtt => rtt, _ => new List<double>());
        for (var round = 0; round < 9; round++)
        {
            foreach (var rtt in rtts)
            {
                connection.SimulatedLatency = TimeSpan.FromMilliseconds(rtt);
                var watch = Stopwatch.StartNew();
                await fs.StatAsync(folder, Timeout());
                times[rtt].Add(watch.Elapsed.TotalMilliseconds);
            }
        }

        double Fastest(int rtt) => times[rtt].Min();
        string Samples(int rtt) => string.Join(", ", times[rtt].Select(t => t.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)));
        var local = Fastest(0);
        var report = new StringBuilder(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"stat round trip: {local:F1} ms with no latency [{Samples(0)}]"));
        foreach (var rtt in rtts.Skip(1))
        {
            report.Append(System.Globalization.CultureInfo.InvariantCulture, $"; {Fastest(rtt):F1} ms at {rtt} ms [{Samples(rtt)}]");
        }

        if (Environment.GetEnvironmentVariable("CI") != "true")
        {
            foreach (var rtt in rtts.Skip(1))
            {
                Assert.True(Fastest(rtt) <= local + rtt + 20, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Fastest(rtt):F1} ms at {rtt} ms RTT is over the budget of {local + rtt + 20:F1} ms ({report})."));
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
            // With the tests' .NET there: the framework-dependent server of a Debug build runs on it.
            return Codalus.Tests.WslStandIn.Distribution is { } distribution ? new WslTarget(distribution) : null;
        }

        var script = Path.Combine(folder, "fake-ssh");
        Codalus.Tests.StandInScript.Write(script, """
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
            Assert.Skip("No stand-in target here: WSL with .NET on Windows (CODALUS_TEST_WSL), the fake ssh on Linux and macOS.");
            return;
        }

        Environment.SetEnvironmentVariable(RemoteServerBundle.ServerVariable, ServerDll);
        var version = "test-" + Guid.NewGuid().ToString("N")[..8];
        // A home of its own on the target: never the real ~/.codalus-server (WSL's on Windows, this machine's elsewhere).
        var serverRoot = "/tmp/codalus-server-tests-" + Guid.NewGuid().ToString("N")[..8];
        var bundle = new RemoteServerBundle(Path.Combine(folder, "cache"), version, [], RemoteServerBundle.LocateServer, serverRoot + "/.codalus-server");
        InstalledServer? installed = null;
        try
        {
            installed = await bundle.EnsureAsync(target, null, Timeout(300));
            Assert.True(installed.Copied);
            Assert.EndsWith("/.codalus-server/" + version, installed.Folder, StringComparison.Ordinal);
            Assert.False(installed.SelfContained);
            Assert.NotNull(installed.Platform);
            // The fake ssh runs here: inside WSL (Linux validation from Windows) that is a WSL distribution too.
            Assert.Equal(target is WslTarget || Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") is { Length: > 0 }, installed.WslDistribution is not null);

            // Again: the checksum there matches, nothing is copied.
            Assert.False((await bundle.EnsureAsync(target, null, Timeout(300))).Copied);

            // It runs there (the framework-dependent build, on the target's dotnet).
            Assert.Equal(ServerRuntime(), installed.Runtime); // the start script looks for a dotnet that has it
            var (program, arguments) = target.Shell(RemoteServerBundle.StartScript(installed.Folder, null, installed.Runtime));
            await using var connection = await RemoteConnection.StartAsync(new RemoteTransport(program, arguments, "stand-in"), ServerVersion, Timeout(120));
            Assert.Equal(target is WslTarget || OperatingSystem.IsLinux() ? "linux" : "macos", connection.Server.Os); // the fake ssh runs it here
            // The server names its folder canonically: macOS's /tmp is a link to /private/tmp.
            static string Canonical(string path) => OperatingSystem.IsMacOS() && path.StartsWith("/tmp/", StringComparison.Ordinal) ? "/private" + path : path;
            Assert.Equal(Canonical(installed.Folder + "/server/"), Canonical(connection.Server.ServerFolder.Replace('\\', '/')));

            // Resumable there (M21b): the daemon outlives the transport, which is started again.
            var (relayProgram, relayArguments) = target.Shell(RemoteServerBundle.StartScript(installed.Folder, ["--relay", Guid.NewGuid().ToString("N"), "--grace", "60"], installed.Runtime));
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
    /// .NET folder on the machine that runs the containers, to mount into one. Null, with what's missing, when there is
    /// none. On Windows that .NET is the WSL distribution's, not the one running the tests: it must have the runtime the
    /// server was built for (a distribution with only .NET 11's release candidates can't run a server built for 11.0.0).
    /// </summary>
    private static ((IReadOnlyList<string> Tool, string DotnetRoot)? StandIn, string Missing) ContainerStandIn()
    {
        const string Wanted = "Podman with docker.io/library/ubuntu:24.04 (here on Linux, in WSL on Windows)";
        const string Image = "docker.io/library/ubuntu:24.04";
        IReadOnlyList<string> tool;
        IReadOnlyList<string> shell;
        if (OperatingSystem.IsWindows())
        {
            var distribution = Environment.GetEnvironmentVariable("CODALUS_TEST_WSL") is { Length: > 0 } name ? name : "Ubuntu";
            if (!RemoteTargets.WslDistributionsAsync(CancellationToken.None).GetAwaiter().GetResult().Contains(distribution, StringComparer.OrdinalIgnoreCase))
            {
                return (null, $"{Wanted}: no WSL distribution {distribution}");
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
            return (null, $"{Wanted}: not on this OS");
        }

        try
        {
            if (Run(tool, ["image", "exists", Image]).Code != 0)
            {
                return (null, $"{Wanted}: no podman, or no {Image} image");
            }

            var root = Run(shell, ["if [ -x \"$HOME/.dotnet/dotnet\" ]; then echo \"$HOME/.dotnet\"; else dirname \"$(readlink -f \"$(command -v dotnet)\")\"; fi"]).Output.Trim();
            if (!root.StartsWith('/'))
            {
                return (null, $"{Wanted}: no dotnet on its PATH");
            }

            var needed = ServerRuntime();
            var runtimes = Run(shell, [$"ls '{root}/shared/Microsoft.NETCore.App'"]).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return runtimes.Any(r => RunsOn(needed, r))
                ? ((tool, root), string.Empty)
                : (null, $"{Wanted}: its .NET ({root}) has no Microsoft.NETCore.App {needed} or a later patch to run the server with, only {string.Join(", ", runtimes)}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (null, $"{Wanted}: {ex.Message}");
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

    /// <summary>The Microsoft.NETCore.App version the server's runtimeconfig.json asks for.</summary>
    private static string ServerRuntime()
    {
        using var config = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(ServerDll, ".runtimeconfig.json")));
        var options = config.RootElement.GetProperty("runtimeOptions");
        var frameworks = options.TryGetProperty("frameworks", out var list) ? list.EnumerateArray().ToList() : [options.GetProperty("framework")];
        return frameworks.Single(f => f.GetProperty("name").GetString() == "Microsoft.NETCore.App").GetProperty("version").GetString()!;
    }

    /// <summary>
    /// Whether the host runs an app that asks for <paramref name="needed"/> on <paramref name="installed"/> (the default
    /// roll-forward: the same major and minor, a patch as late or later; a prerelease only for a prerelease).
    /// </summary>
    internal static bool RunsOn(string needed, string installed)
    {
        static (Version Core, bool Prerelease) Parse(string version) =>
            (Version.Parse(version.Split('-')[0]), version.Contains('-', StringComparison.Ordinal));

        var (want, wantPre) = Parse(needed);
        var (have, havePre) = Parse(installed);
        if (have.Major != want.Major || have.Minor != want.Minor || (havePre && !wantPre))
        {
            return false;
        }

        return have != want ? have > want : !havePre || string.CompareOrdinal(installed, needed) >= 0;
    }

    [Fact]
    public void The_container_stand_in_needs_a_runtime_that_runs_the_server()
    {
        // The Windows validation found WSL's .NET with only 11.0.0-rc.* runtimes, which a server built for 11.0.0 refuses.
        Assert.False(RunsOn("11.0.0", "11.0.0-rc.3.26522.148"));
        Assert.False(RunsOn("11.0.0", "10.0.12"));
        Assert.False(RunsOn("11.0.2", "11.0.1"));
        Assert.True(RunsOn("11.0.0", "11.0.0"));
        Assert.True(RunsOn("11.0.0", "11.0.3"));
        Assert.True(RunsOn("11.0.0-rc.2.26504.105", "11.0.0-rc.3.26522.148"));
        Assert.True(RunsOn("11.0.0-rc.2.26504.105", "11.0.0"));
    }

    [Fact]
    public async Task A_running_container_is_a_remote_window_target_the_server_is_copied_into_and_runs_in()
    {
        var (standIn, missing) = ContainerStandIn();
        if (standIn is not { } containers)
        {
            Assert.Skip("No container stand-in: " + missing + ".");
            return;
        }

        var folder = TempFolder();
        var name = "codalus-remote-test-" + Guid.NewGuid().ToString("N")[..8];
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
            var options = new RemoteSessionOptions(spec, ServerVersion, Path.Combine(folder, "cache")) { ServerHome = "/tmp/.codalus-server" };
            await using var session = await RemoteSession.ConnectAsync(options, "/tmp", null, Timeout(300));
            Assert.Equal("linux", session.Connection.Server.Os);
            Assert.StartsWith("/tmp/.codalus-server/", session.Connection.Server.ServerFolder.Replace('\\', '/'), StringComparison.Ordinal);
            Assert.False(session.Map.IsIdentity); // the container's paths are under a local root of their own
        }
        finally
        {
            Environment.SetEnvironmentVariable(RemoteServerBundle.ServerVariable, null);
            var remove = new ProcessStartInfo(tool[0], [.. tool.Skip(1), "rm", "-f", "-v", name]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using (var process = Process.Start(remove)!)
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }

            Directory.Delete(folder, recursive: true);
        }
    }

    // ------------------------------------------------------------------ the .NET the server runs on

    /// <summary>The .NET root these tests run on (<c>&lt;root&gt;/shared/Microsoft.NETCore.App/&lt;version&gt;/</c>): it runs the server.</summary>
    private static string RunningRoot() =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", ".."));

    private static string DotnetExe => OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

    /// <summary>
    /// A stand-in .NET root with only an older runtime (Homebrew's .NET 10 on the maintainer's Mac): its <c>dotnet</c>
    /// lists that runtime and fails as the .NET host does for anything else. A placeholder on Windows, never started.
    /// </summary>
    private static string OldDotnet(string folder)
    {
        var root = Path.Combine(folder, "old-dotnet");
        var runtime = Path.Combine(root, "shared", "Microsoft.NETCore.App", "10.0.8");
        Directory.CreateDirectory(runtime);
        File.WriteAllText(Path.Combine(runtime, "Microsoft.NETCore.App.deps.json"), "{}");
        Directory.CreateDirectory(Path.Combine(root, "host", "fxr"));
        var dotnet = Path.Combine(root, DotnetExe);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(dotnet, "never started");
        }
        else
        {
            Codalus.Tests.StandInScript.Write(dotnet, $$"""
                #!/bin/sh
                if [ "$1" = --list-runtimes ]; then echo "Microsoft.NETCore.App 10.0.8 [{{root}}/shared/Microsoft.NETCore.App]"; exit 0; fi
                echo "You must install or update .NET to run this application. (the stand-in dotnet, only 10.0.8)" >&2
                exit 150
                """.Replace("\r\n", "\n", StringComparison.Ordinal));
        }

        return root;
    }

    [Fact]
    public async Task The_local_server_starts_on_a_dotnet_with_its_runtime_not_the_first_one_on_the_path()
    {
        // The macOS smoke (develop 607dc3a2): Homebrew's dotnet with only .NET 10 first on the PATH, ~/.dotnet with
        // .NET 11. The build host (an app host, DOTNET_ROOT) started on ~/.dotnet; the local server, dotnet
        // codalus-server.dll, on Homebrew's: "You must install or update .NET… Framework 'Microsoft.NETCore.App' 11.0.0".
        var folder = TempFolder();
        try
        {
            var real = RunningRoot();
            var old = OldDotnet(folder);
            var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();
            DotnetRootResolver Machine(string dotnetRoot, string path) => new()
            {
                // The machine's own DOTNET_ROOT_<ARCH>, CODALUS_SKIP_RUNTIME_CHECK and roll-forward settings don't count.
                ProcessEnvironment = n => n switch { "PATH" => path, "DOTNET_ROOT" => dotnetRoot, _ => null },
                WindowsRegisteredLocation = _ => null,
            };
            var options = new RemoteSessionOptions(RemoteTargetSpec.Parse("local")!, ServerVersion, Path.Combine(folder, "cache"))
            {
                LocalServer = ServerDll,
                MapLocalPaths = false,
                // The hosts' root (DOTNET_ROOT, as the shell sets it) has the runtime; the stand-in is first on the PATH.
                DotnetRoots = Machine(real, old + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")),
            };
            await using (var session = await RemoteSession.ConnectAsync(options, null, null, Timeout(120)))
            {
                Assert.Equal(ServerVersion, session.Connection.Server.ServerVersion);
                // The server names the dotnet it runs on: the hosts' root's, which its own hosts then run on too.
                Assert.Equal(Platform.RealPath.Resolve(Path.Combine(real, DotnetExe)), Platform.RealPath.Resolve(session.Connection.Server.Dotnet!));
            }

            // No DOTNET_ROOT that has it: the first dotnet on the PATH that does, behind the stand-in.
            var described = Machine(Path.Combine(folder, "no-such-root"), old + Path.PathSeparator + real);
            await using (var session = await RemoteSession.ConnectAsync(options with { DotnetRoots = described }, null, null, Timeout(120)))
            {
                Assert.Equal(Platform.RealPath.Resolve(Path.Combine(real, DotnetExe)), Platform.RealPath.Resolve(session.Connection.Server.Dotnet!));
            }

            // Nowhere: it isn't started, and the error says which runtime it needs and where it looked.
            var error = await Assert.ThrowsAsync<HostRuntimeMissingException>(() => RemoteSession.ConnectAsync(options with { DotnetRoots = Machine(old, old) }, null, null, Timeout(120)));
            Assert.Contains($"codalus-server needs the .NET runtime Microsoft.NETCore.App {ServerRuntime()} (rollForward Major), which isn't installed in {old} (found: 10.0.8).", error.Message, StringComparison.Ordinal);
            Assert.IsAssignableFrom<JsonRpcConnectionLostException>(error); // what ConnectRemoteAsync reports (the runtime's notification)
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task The_start_script_runs_the_server_on_a_dotnet_with_its_runtime_not_the_first_one_on_the_path()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake ssh runs the start script here on Linux and macOS; WSL's PATH isn't the test's to describe.");
            return;
        }

        // A Mac over SSH gets the framework-dependent server (no osx-* bundle is shipped): the same Homebrew-first PATH.
        var folder = TempFolder();
        try
        {
            var target = StandIn(folder)!;
            var installed = Path.Combine(folder, "installed");
            Directory.CreateDirectory(installed);
            Directory.CreateSymbolicLink(Path.Combine(installed, "server"), Path.GetDirectoryName(ServerDll)!);
            var old = OldDotnet(folder);
            var real = RunningRoot();
            RemoteTransport Transport(string? runtime) => target.Shell(RemoteServerBundle.StartScript(installed, null, runtime)) is var (program, arguments)
                ? new RemoteTransport(program, arguments, "stand-in") { Environment = new Dictionary<string, string> { ["PATH"] = old + ":" + real + ":/usr/bin:/bin", ["DOTNET_ROOT"] = string.Empty } }
                : throw new InvalidOperationException();

            await using (var connection = await RemoteConnection.StartAsync(Transport(ServerRuntime()), ServerVersion, Timeout(120)))
            {
                Assert.Equal(Platform.RealPath.Resolve(Path.Combine(real, DotnetExe)), Platform.RealPath.Resolve(connection.Server.Dotnet!));
            }

            // Without the runtime to look for, as before: the first dotnet on the PATH, which can't run it.
            var before = await Assert.ThrowsAsync<InvalidOperationException>(() => RemoteConnection.StartAsync(Transport(null), ServerVersion, Timeout(120)));
            Assert.Contains("You must install or update .NET", before.Message, StringComparison.Ordinal);

            // None has it: a line says what it needs and where it looked, then the PATH's first dotnet says why it can't.
            var none = await Assert.ThrowsAsync<InvalidOperationException>(() => RemoteConnection.StartAsync(Transport("99.0.0"), ServerVersion, Timeout(120)));
            Assert.Contains("codalus-server needs the .NET runtime Microsoft.NETCore.App 99.0.0 or later, which no dotnet has on PATH (" + old + ":" + real, none.Message, StringComparison.Ordinal);
            Assert.Contains("You must install or update .NET", none.Message, StringComparison.Ordinal);
        }
        finally
        {
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
            Assert.Skip("No stand-in target here: WSL with .NET on Windows (CODALUS_TEST_WSL), the fake ssh on Linux and macOS.");
            return;
        }

        var root = "/tmp/codalus-server-tests-" + Guid.NewGuid().ToString("N")[..8];
        var home = root + "/.codalus-server/test-damaged"; // never the real home
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
