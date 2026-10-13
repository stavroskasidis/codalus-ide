using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Codalus.Remote.Protocol;
using Codalus.Rpc;
using Codalus.Rpc.Multiplexing;

namespace Codalus.Remote.Server;

/// <summary>
/// A server that outlives its link (M21b, ADR 0054). The client starts <c>codalus-server --relay &lt;session&gt;</c>
/// through ssh (or wsl.exe, docker exec); the relay connects to the session's daemon over a Unix socket in a private
/// folder, starting it when there's none, prints the preamble with the daemon's instance, and copies bytes both ways.
/// The daemon (<c>--daemon &lt;session&gt;</c>) runs the multiplexer on a <see cref="ResumableStream"/>: when the link
/// drops (the network, a sleeping laptop), the processes, terminals and channels go on, and the client's next relay
/// resumes where the bytes stopped. A daemon with no link for its grace period ends, and everything with it.
/// </summary>
public static class ServerDaemon
{
    /// <summary>How long a daemon waits for a client to come back by default.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(15);

    /// <summary>The private folder of the sockets: <c>$XDG_RUNTIME_DIR/codalus</c>, else <c>codalus-&lt;user&gt;</c> in the temporary folder.</summary>
    public static string SocketFolder()
    {
        // Socket paths are limited to about 104 bytes: macOS's $TMPDIR (/var/folders/…) is too long, /tmp isn't.
        var root = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime && Directory.Exists(runtime)
            ? Path.Combine(runtime, "codalus")
            : Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/tmp", "codalus-" + Environment.UserName);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(root);
            return root;
        }

        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if ((File.GetUnixFileMode(root) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute | UnixFileMode.GroupExecute)) != 0)
        {
            throw new IOException($"{root} is open to other users; remove it.");
        }

        return root;
    }

    public static string SocketPath(string session) => Path.Combine(SocketFolder(), session + ".sock");

    /// <summary>Session ids are what clients make up: letters, digits and dashes, so that they name files safely.</summary>
    public static bool IsValidSession(string session) =>
        session.Length is > 0 and <= 64 && session.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    // ------------------------------------------------------------------ relay

    /// <summary>The relay: connects to the daemon (started if needed), prints the preamble, and copies until either side ends.</summary>
    public static async Task<int> RelayAsync(string session, TimeSpan grace)
    {
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);
        Socket socket;
        try
        {
            socket = await ConnectAsync(session, grace).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or InvalidOperationException)
        {
            Console.Error.WriteLine($"codalus-server: no daemon for session {session}: {ex.Message}");
            return 2;
        }

        using var _ = socket;
        await using var link = new NetworkStream(socket, ownsSocket: false);

        // The daemon's first line is its instance: a new daemon is a new session, not the one the client resumes.
        var instance = await ReadLineAsync(link).ConfigureAwait(false);
        var preamble = Encoding.ASCII.GetBytes($"\n{RemoteMethods.Preamble} {RemoteServer.Version} {RemoteMethods.ProtocolVersion} resumable {instance}\n");
        await stdout.WriteAsync(preamble).ConfigureAwait(false);
        await stdout.FlushAsync().ConfigureAwait(false);

        var up = CopyAsync(stdin, link);
        var down = CopyAsync(link, stdout);
        await Task.WhenAny(up, down).ConfigureAwait(false);
        socket.Shutdown(SocketShutdown.Both);
        return 0;
    }

    private static async Task<Socket> ConnectAsync(string session, TimeSpan grace)
    {
        var path = SocketPath(session);
        var started = false;
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path)).ConfigureAwait(false);
                return socket;
            }
            catch (SocketException) when (deadline.Elapsed < TimeSpan.FromSeconds(30))
            {
                socket.Dispose();
            }

            if (!started)
            {
                StartDaemon(session, grace);
                started = true;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    /// <summary>Starts the daemon detached: its stdio isn't the relay's (which ends with ssh), its log is a file.</summary>
    private static void StartDaemon(string session, TimeSpan grace)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("The server's own path is unknown.");
        var start = new ProcessStartInfo(self)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "codalus-server.dll"));
        }

        start.ArgumentList.Add("--daemon");
        start.ArgumentList.Add(session);
        start.ArgumentList.Add("--grace");
        start.ArgumentList.Add(((int)grace.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The daemon didn't start.");
        process.StandardInput.Close();
        process.StandardOutput.Close();
        process.StandardError.Close();
    }

    private static async Task<string> ReadLineAsync(Stream stream)
    {
        var line = new StringBuilder();
        var one = new byte[1];
        while (await stream.ReadAsync(one).ConfigureAwait(false) == 1 && one[0] != (byte)'\n')
        {
            line.Append((char)one[0]);
            if (line.Length > 256)
            {
                throw new InvalidDataException("The daemon's first line is too long.");
            }
        }

        return line.ToString();
    }

    private static async Task CopyAsync(Stream from, Stream to)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                await to.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
        }
    }

    // ------------------------------------------------------------------ daemon

    /// <summary>The daemon: one session's server on a resumable stream, links accepted on its socket.</summary>
    public static async Task<int> DaemonAsync(string session, TimeSpan grace)
    {
        var path = SocketPath(session);
        var log = new StreamWriter(Path.Combine(SocketFolder(), session + ".log"), append: true) { AutoFlush = true };
        Console.SetOut(log);
        Console.SetError(log);

        // A hang-up of the ssh session that started the relay isn't the daemon's end.
        using var hangup = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => context.Cancel = true);

        if (File.Exists(path))
        {
            if (await IsAliveAsync(path).ConfigureAwait(false))
            {
                Console.Error.WriteLine($"daemon {session}: already running");
                return 0;
            }

            // A daemon that crashed left its socket behind.
            TryDelete(path);
        }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine($"daemon {session}: can't listen on {path} ({ex.Message})");
            return 0;
        }

        listener.Listen(4);
        var instance = Guid.NewGuid().ToString("N");
        Console.Error.WriteLine($"daemon {session}: instance {instance}, pid {Environment.ProcessId}, grace {grace}");

        using var resumable = new ResumableStream();
        await using var multiplexer = new ChannelMultiplexer(resumable, resumable);
        var control = multiplexer.Start();
        await using var connection = new JsonRpcConnection(control.Stream, control.Stream);
        await using var server = new RemoteServer(multiplexer, connection);
        server.Register();
        connection.Start();

        using var lifetime = new CancellationTokenSource();
        var graceTimer = new Timer(_ =>
        {
            if (!resumable.IsLinked)
            {
                Console.Error.WriteLine($"daemon {session}: no client for {grace}, ending");
                resumable.Close();
            }
        });
        resumable.LinkLost += (_, error) =>
        {
            Console.Error.WriteLine($"daemon {session}: link lost{(error is null ? string.Empty : ": " + error.Message)}");
            graceTimer.Change(grace, Timeout.InfiniteTimeSpan);
        };
        graceTimer.Change(TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);

        var accepting = AcceptAsync(listener, resumable, instance, session, lifetime.Token);
        await Task.WhenAny(connection.Completion, multiplexer.Completion, server.ExitRequested).ConfigureAwait(false);
        await lifetime.CancelAsync().ConfigureAwait(false);
        await graceTimer.DisposeAsync().ConfigureAwait(false);
        listener.Close();
        TryDelete(path);
        await accepting.ConfigureAwait(false);
        Console.Error.WriteLine($"daemon {session}: ended");
        resumable.Close();
        return 0;
    }

    private static async Task AcceptAsync(Socket listener, ResumableStream resumable, string instance, string session, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }

            var stream = new NetworkStream(client, ownsSocket: true);
            try
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(instance + "\n"), cancellationToken).ConfigureAwait(false);
                using var hello = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                hello.CancelAfter(TimeSpan.FromSeconds(30));
                await resumable.AttachAsync(stream, stream, hello.Token).ConfigureAwait(false);
                Console.Error.WriteLine($"daemon {session}: linked");
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
            {
                // A client that can't resume (it lost more than was kept) gets nothing; it opens a new session.
                Console.Error.WriteLine($"daemon {session}: a link failed: {ex.Message}");
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<bool> IsAliveAsync(string path)
    {
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await probe.ConnectAsync(new UnixDomainSocketEndPoint(path)).ConfigureAwait(false);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
