using System.Security.Cryptography;
using System.Text;
using Codalus.Diagnostics.Protocol;
using Codalus.Rpc;
using Microsoft.Diagnostics.NETCore.Client;

namespace Codalus.Diagnostics.Host.Sessions;

/// <summary>
/// What a session runs on: a process on this machine by its id, or (M63b, ADR 0102) a runtime by the diagnostic port's
/// socket it connects to (<see cref="Port"/>: a compose service in a container, started with
/// <c>DOTNET_DiagnosticPorts=&lt;socket&gt;,connect,nosuspend</c> on a folder mounted from here).
/// </summary>
internal readonly record struct DiagnosticsTarget(int ProcessId, string? Port)
{
    public bool IsPort => Port is not null;

    /// <summary>How messages name it: "Process 123", or the socket's name (the service's).</summary>
    public string Display => Port is { } port ? $"The runtime at {Path.GetFileName(port)}" : $"Process {ProcessId}";

    public static DiagnosticsTarget Of(int processId, string? port) => new(processId, string.IsNullOrEmpty(port) ? null : port);
}

/// <summary>
/// The host's listeners on diagnostic ports' sockets (M63b, ADR 0102). A runtime started with
/// <c>DOTNET_DiagnosticPorts=&lt;path&gt;,connect,nosuspend</c> connects to whatever listens at the path, again and again
/// (every half second at most) while nothing does, without waiting for it. The socket is in a folder of this machine
/// mounted into the service's container, so the host listens there (<see cref="DiagnosticsClientConnector.FromDiagnosticPort"/>
/// in listen mode: the reverse of connecting to a process's own socket by its id) and the runtime in the container
/// connects to it through the mount.
/// <para>
/// Why this way round, rather than the runtime listening (its default socket under a mounted <c>TMPDIR</c>, or a
/// <c>listen</c> port): the runtime makes its listening socket readable and writable by its own user only (0600), and a
/// service's user is the image's (<c>$APP_UID</c>, 1654, in .NET 8 and later), so the IDE's user couldn't connect to it.
/// The host's own socket is opened to every user here (0666) instead, inside a folder only the IDE writes, and a
/// runtime that connects only advertises itself.
/// </para>
/// One listener serves every session on a port at once (counters, a trace and a snapshot share it: each command takes
/// the runtime's next connection), held while a session uses it, and closed with the last one, so the next start
/// accepts the runtime that runs then (a restarted service is a new runtime).
/// </summary>
internal static class PortConnections
{
    /// <summary>How long a start waits for the runtime to connect: it retries every half second, so this is for one that isn't there.</summary>
    private static readonly TimeSpan s_acceptTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A socket's path is at most 108 bytes with its NUL on Linux (104 on macOS): a longer one is reached through a link (<see cref="BindPath"/>).</summary>
    internal const int MaxSocketPath = 100;

    private static readonly Lock s_lock = new();
    private static readonly Dictionary<string, Connection> s_connections = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Task> s_closing = new(StringComparer.Ordinal);

    internal sealed class Connection(string port)
    {
        public string Port { get; } = port;

        public CancellationTokenSource Cancel { get; } = new();

        public Task<DiagnosticsClientConnector> Accepting { get; set; } = null!;

        public int Leases { get; set; }
    }

    /// <summary>A session's hold on a port's listener; disposing it (once) lets the listener close with the last one.</summary>
    internal sealed class Lease : IAsyncDisposable
    {
        private readonly Connection _connection;
        private int _released;

        internal Lease(Connection connection, DiagnosticsClient client)
        {
            _connection = connection;
            Client = client;
        }

        public DiagnosticsClient Client { get; }

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _released, 1) == 0 ? new ValueTask(ReleaseAsync(_connection)) : ValueTask.CompletedTask;
    }

    /// <summary>
    /// The client of the runtime connected to <paramref name="port"/>: the listener there (started, and the runtime's
    /// connection waited for, if no session holds one) for as long as the lease is held. Fails with
    /// <see cref="DiagnosticsErrorCodes.NotAvailable"/>, saying why, when no runtime connects in time.
    /// </summary>
    public static async Task<Lease> AcquireAsync(string port, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new JsonRpcHandlerException(DiagnosticsErrorCodes.NotAvailable, "A diagnostic port's socket is a Linux and macOS target: on Windows a container's runtime can't reach the IDE's socket.");
        }

        Connection connection;
        lock (s_lock)
        {
            if (!s_connections.TryGetValue(port, out connection!))
            {
                connection = new Connection(port);
                var previous = s_closing.GetValueOrDefault(port) ?? Task.CompletedTask;
                connection.Accepting = AcceptAsync(port, previous, connection.Cancel.Token);
                s_connections[port] = connection;
            }

            connection.Leases++;
        }

        try
        {
            var connector = await connection.Accepting.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(connection, connector.Instance);
        }
        catch
        {
            await ReleaseAsync(connection).ConfigureAwait(false);
            throw;
        }
    }

    private static Task ReleaseAsync(Connection connection)
    {
        Task closing;
        lock (s_lock)
        {
            if (--connection.Leases > 0)
            {
                return Task.CompletedTask;
            }

            if (s_connections.TryGetValue(connection.Port, out var current) && current == connection)
            {
                s_connections.Remove(connection.Port);
            }

            // The next listener on the path waits for this one to close (closing deletes its socket file).
            closing = CloseAsync(connection);
            s_closing[connection.Port] = closing;
        }

        return closing;
    }

    private static async Task CloseAsync(Connection connection)
    {
        await connection.Cancel.CancelAsync().ConfigureAwait(false);
        try
        {
            var connector = await connection.Accepting.ConfigureAwait(false);
            await connector.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or JsonRpcHandlerException or IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // It never accepted a runtime: nothing to close.
        }
        finally
        {
            connection.Cancel.Dispose();
        }
    }

    private static async Task<DiagnosticsClientConnector> AcceptAsync(string port, Task previous, CancellationToken cancellationToken)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Closed either way.
        }

        var folder = Path.GetDirectoryName(port);
        if (folder is null || !Directory.Exists(folder))
        {
            throw new JsonRpcHandlerException(DiagnosticsErrorCodes.NotAvailable, $"{folder} doesn't exist: the folder the service's container mounts for its diagnostic port isn't there.");
        }

        var path = BindPath(port);
        DeleteStaleSocket(path);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(s_acceptTimeout);
        var accepting = DiagnosticsClientConnector.FromDiagnosticPort(path + ",listen", timeout.Token);
        var opened = OpenToContainersAsync(path, accepting);
        try
        {
            var connector = await accepting.ConfigureAwait(false);
            await opened.ConfigureAwait(false);
            if (connector.Instance is null)
            {
                // Cancelled, the library's way: a connector without a client (its listener closed).
                await connector.DisposeAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(timeout.Token);
            }

            return connector;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new JsonRpcHandlerException(
                DiagnosticsErrorCodes.NotAvailable,
                $"No .NET runtime connected to {port} within {s_acceptTimeout.TotalSeconds:0} s. The service must run in the IDE's fast mode (Compose Up or Debug Compose writes the DOTNET_DiagnosticPorts that names this socket), " +
                "and the engine must run its containers on this machine's Linux kernel: Unix sockets in a bind mount don't cross Docker Desktop's file sharing (macOS, Windows, and Docker Desktop on Linux).");
        }
    }

    /// <summary>
    /// The path the host binds for <paramref name="port"/>: the port itself, or, when it is too long for a socket
    /// (<see cref="MaxSocketPath"/>, as a solution's state folder deep in a tree is), the same name in a link to its
    /// folder in the temp folder (a socket is made, and connected to, through a link to its folder: the socket itself
    /// is in the folder the container mounts).
    /// </summary>
    internal static string BindPath(string port)
    {
        if (Encoding.UTF8.GetByteCount(port) <= MaxSocketPath)
        {
            return port;
        }

        var folder = Path.GetFullPath(Path.GetDirectoryName(port)!);
        var name = "codalus-diag-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(folder)))[..16];
        var link = Path.Combine(Path.GetTempPath(), name);
        if (!IsLinkTo(link, folder))
        {
            try
            {
                Directory.CreateSymbolicLink(link, folder);
            }
            catch (IOException)
            {
                // Something else has the name (another user's, or a stale link): a link of a name of its own.
                link = Path.Combine(Path.GetTempPath(), name + "-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateSymbolicLink(link, folder);
            }
        }

        var path = Path.Combine(link, Path.GetFileName(port));
        return Encoding.UTF8.GetByteCount(path) <= MaxSocketPath
            ? path
            : throw new JsonRpcHandlerException(DiagnosticsErrorCodes.NotAvailable, $"{port} is too long for a socket's path, and so is the temp folder's link to it ({path}).");
    }

    private static bool IsLinkTo(string link, string folder)
    {
        try
        {
            return new DirectoryInfo(link).LinkTarget is { } target && string.Equals(Path.TrimEndingDirectorySeparator(target), Path.TrimEndingDirectorySeparator(folder), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A socket file left by a listener that didn't close (the host killed): binding needs the name free.</summary>
    private static void DeleteStaleSocket(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Binding says what's wrong.
        }
    }

    /// <summary>
    /// The listener's socket is made with this user's umask; the service's user in its container (another uid, as
    /// <c>$APP_UID</c> is) needs to write to it to connect, so it is opened to every user once it exists. Until then
    /// the runtime's tries are refused, and it tries again.
    /// </summary>
    private static async Task OpenToContainersAsync(string path, Task accepting)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const UnixFileMode Everyone = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite;
        for (var attempt = 0; attempt < 200 && !accepting.IsCompleted; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.SetUnixFileMode(path, Everyone);
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }
    }
}
