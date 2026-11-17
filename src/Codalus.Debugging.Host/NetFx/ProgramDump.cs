using System.Buffers.Binary;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;

namespace Codalus.Debugging.Host.NetFx;

/// <summary>
/// A dump of the program when a func-eval times out, written into the adapter's log folder (<c>CODALUS_CLR_DEBUGGER_LOG</c>)
/// only. A timed-out evaluation's thread list has the managed frames and a native frame's module and offset; the eval
/// thread waited in native code each time on Windows and Linux CI (docs/ci-flakes.md: runs 41449676058, 43587457886,
/// 43731649744), and only a dump has the native stacks and the memory that tell what it waited on.
/// <para>
/// The program's runtime writes it, asked over its diagnostic port (the IPC protocol of dotnet-dump, command set Dump,
/// GenerateCoreDump): it starts its own <c>createdump</c>, which can read its parent where a debugger's child may not
/// (Linux's ptrace scope, macOS' task port). Its diagnostic server is a native thread, so it answers while the debugger
/// holds the program's managed threads.
/// </para>
/// </summary>
internal static class ProgramDump
{
    /// <summary>How long the dump may take: the program stays stopped meanwhile, and the request waits.</summary>
    internal static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private const byte DumpCommandSet = 0x01;
    private const byte GenerateCoreDump = 0x01;
    private const uint NormalDump = 1; // the threads' stacks and the memory they point at, without the whole heap
    private const int HeaderSize = 20;

    /// <summary>
    /// Asks process <paramref name="processId"/>'s runtime for a dump into <paramref name="folder"/>, and says how that
    /// went for the timeout's diagnostic: the dump's path, or why there is none.
    /// </summary>
    public static string Write(int processId, string folder)
    {
        var path = Path.Combine(Path.GetFullPath(folder), string.Create(CultureInfo.InvariantCulture, $"func-eval-{processId}-{DateTime.Now:HHmmss}.dmp"));
        try
        {
            using var cancel = new CancellationTokenSource(Limit);
            using var port = Connect(processId, cancel.Token);
            if (port is null)
            {
                return " No dump: the program has no diagnostic port (DOTNET_EnableDiagnostics=0, or a runtime before .NET 5).";
            }

            port.Write(Request(path));
            var header = Read(port, HeaderSize, cancel.Token);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));
            var payload = Read(port, Math.Max(0, size - HeaderSize), cancel.Token);
            var result = payload.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(payload) : -1;
            return header[17] == 0x00 && result == 0 && File.Exists(path)
                ? $" A dump of the program: {path} ({(new FileInfo(path).Length / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MB)."
                : $" No dump: the runtime answered 0x{result.ToString("X8", CultureInfo.InvariantCulture)}{(header[17] == 0x00 ? string.Empty : " (an error)")}.";
        }
        catch (OperationCanceledException)
        {
            return $" No dump: the runtime didn't write it in {Limit.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s.";
        }
        catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            return " No dump: " + ex.Message;
        }
    }

    /// <summary>The GenerateCoreDump request: the header (magic, size, command), then the dump's path, its type and no diagnostics.</summary>
    internal static byte[] Request(string path)
    {
        var name = Encoding.Unicode.GetBytes(path + '\0');
        var payload = new byte[4 + name.Length + 4 + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)(path.Length + 1));
        name.CopyTo(payload, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4 + name.Length), NormalDump);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8 + name.Length), 0);
        var message = new byte[HeaderSize + payload.Length];
        Encoding.ASCII.GetBytes("DOTNET_IPC_V1\0").CopyTo(message, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(14), (ushort)message.Length);
        message[16] = DumpCommandSet;
        message[17] = GenerateCoreDump;
        payload.CopyTo(message, HeaderSize);
        return message;
    }

    /// <summary>The program's diagnostic port: a named pipe on Windows, elsewhere the socket its runtime made in the temporary folder.</summary>
    private static Stream? Connect(int processId, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", "dotnet-diagnostic-" + processId.ToString(CultureInfo.InvariantCulture), PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(TimeSpan.FromSeconds(5));
                return pipe;
            }
            catch (TimeoutException)
            {
                pipe.Dispose();
                return null;
            }
        }

        var socketPath = Directory.EnumerateFiles(Path.GetTempPath(), $"dotnet-diagnostic-{processId.ToString(CultureInfo.InvariantCulture)}-*-socket")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (socketPath is null)
        {
            return null;
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).AsTask().GetAwaiter().GetResult();
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static byte[] Read(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        stream.ReadExactlyAsync(buffer, cancellationToken).AsTask().GetAwaiter().GetResult();
        return buffer;
    }
}
