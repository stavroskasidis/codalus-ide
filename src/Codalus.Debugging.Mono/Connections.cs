using System.Net;
using System.Net.Sockets;
using Mono.Debugger.Soft;
using Mono.Debugging.Client;
using Mono.Debugging.Soft;

namespace Codalus.Debugging.Mono;

/// <summary>
/// Connects to a runtime whose debugger agent listens (ADR 0065): an Android app started with <c>debug.mono.connect</c>,
/// reached through <c>adb forward</c>. The agent there waits for the client to say <c>start debugger: sdb</c> (a length
/// byte, then ASCII) before the soft debugger's handshake, as .NET Meteor (MIT) found. Retries while the app starts.
/// </summary>
internal sealed class ListeningAgentArgs : SoftDebuggerStartArgs, ISoftDebuggerConnectionProvider, IDisposable
{
    private readonly IPEndPoint _endPoint;
    private readonly string _appName;
    private readonly bool _greet;
    private TcpClient? _client;
    private volatile bool _connected;

    public ListeningAgentArgs(IPEndPoint endPoint, string appName, bool greet, TimeSpan wait)
    {
        _endPoint = endPoint;
        _appName = appName;
        _greet = greet;
        TimeBetweenConnectionAttempts = 500;
        MaxConnectionAttempts = Math.Max(1, (int)(wait.TotalMilliseconds / TimeBetweenConnectionAttempts));
    }

    public override ISoftDebuggerConnectionProvider ConnectionProvider => this;

    public IAsyncResult BeginConnect(DebuggerStartInfo dsi, AsyncCallback callback)
    {
        _client?.Dispose();
        _client = new TcpClient();
        return _client.BeginConnect(_endPoint.Address, _endPoint.Port, callback, null);
    }

    public void EndConnect(IAsyncResult result, out VirtualMachine vm, out string appName)
    {
        var client = _client ?? throw new InvalidOperationException("Not connecting.");
        client.EndConnect(result);
        var stream = client.GetStream();
        if (_greet)
        {
            const string command = "start debugger: sdb";
            var bytes = new byte[command.Length + 1];
            bytes[0] = (byte)command.Length;
            System.Text.Encoding.ASCII.GetBytes(command, 0, command.Length, bytes, 1);
            stream.Write(bytes, 0, bytes.Length);
        }

        var connection = new StreamConnection(client, stream);
        vm = VirtualMachineManager.Connect(connection, null, null);
        connection.StartKeepAlive(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        _connected = true;
        appName = _appName;
    }

    /// <summary>
    /// Cancels an attempt that hasn't connected. Mono.Debugging also calls this once the session has started, to end
    /// the launch: the connection is the session's then, and stays open.
    /// </summary>
    public void CancelConnect(IAsyncResult result)
    {
        if (!_connected)
        {
            _client?.Close();
        }
    }

    public bool ShouldRetryConnection(Exception ex) => true;

    public void Dispose() => _client?.Dispose();

    /// <summary>
    /// The soft debugger's transport over a TCP stream. The agent in a .NET for Android app drops a connection that sends
    /// it nothing for about eleven seconds (found on an Android 15 emulator, in both of the runtime's debugging modes; the
    /// agent's own keepalive events don't count), so a version request (answered even while the app runs) goes out every
    /// couple of seconds, always with the same id (int.MaxValue): its reply is kept in one slot and never read.
    /// </summary>
    private sealed class StreamConnection(TcpClient client, NetworkStream stream) : Connection
    {
        private static readonly byte[] s_versionRequest = [0, 0, 0, 11, 0x7f, 0xff, 0xff, 0xff, 0, 1, 1]; // length, id, flags, VM, VERSION
        private readonly Lock _send = new();
        private Timer? _keepAlive;

        public void StartKeepAlive(TimeSpan delay, TimeSpan interval) =>
            _keepAlive ??= new Timer(_ =>
            {
                try
                {
                    lock (_send)
                    {
                        stream.Write(s_versionRequest);
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    _keepAlive?.Dispose();
                }
            }, null, delay, interval);

        /// <summary>With <c>CODALUS_MONODBG_LOGFILE</c>, what happens to the connection goes to that log.</summary>
        private static void Trace(string text)
        {
            if (Environment.GetEnvironmentVariable("CODALUS_MONODBG_LOGFILE") is { Length: > 0 })
            {
                Console.Out.WriteLine($"{DateTime.Now:HH:mm:ss.fff} connection: {text}");
            }
        }

        protected override int TransportSend(byte[] buf, int buf_offset, int len)
        {
            lock (_send)
            {
                stream.Write(buf, buf_offset, len);
            }

            return len;
        }

        protected override int TransportReceive(byte[] buf, int buf_offset, int len)
        {
            var read = stream.Read(buf, buf_offset, len);
            if (read == 0)
            {
                Trace("end of stream");
            }

            return read;
        }

        protected override void TransportSetTimeouts(int send_timeout, int receive_timeout)
        {
            client.SendTimeout = send_timeout;
            client.ReceiveTimeout = receive_timeout;
        }

        protected override void TransportClose()
        {
            _keepAlive?.Dispose();
            Trace("closed");
            stream.Close();
            client.Close();
        }

        protected override void TransportShutdown()
        {
            Trace("shut down");
            client.Dispose();
        }
    }
}
