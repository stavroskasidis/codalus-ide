// The runtime's diagnostic port (dotnet/diagnostics, documentation/design-docs/ipc-protocol.md): a Unix domain socket
// $TMPDIR/dotnet-diagnostic-<pid>-<key>-socket off Windows, the named pipe \\.\pipe\dotnet-diagnostic-<pid> on Windows.
using System.IO.Pipes;
using System.Net.Sockets;

static class DiagnosticIpc
{
    /// <summary>The port's path (socket) or pipe name, once the runtime has opened it.</summary>
    public static string? Port(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            // Listed, not opened: opening a pipe to look for it would take one of its instances.
            var name = $"dotnet-diagnostic-{pid}";
            return Directory.EnumerateFiles(@"\\.\pipe\").Any(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase)) ? name : null;
        }
        return Directory.GetFiles(Path.GetTempPath(), $"dotnet-diagnostic-{pid}-*-socket").FirstOrDefault();
    }

    public static async Task<string?> WaitForPort(int pid, int tries = 300)
    {
        for (var n = 0; n < tries && Port(pid) is null; n++)
        {
            await Task.Delay(10);
        }
        return Port(pid);
    }

    /// <summary>Process/ResumeRuntime: lets a runtime held by DOTNET_DefaultDiagnosticPortSuspend start.</summary>
    public static string ResumeRuntime(int pid)
    {
        var port = Port(pid) ?? throw new InvalidOperationException($"process {pid} never opened its diagnostic port (did its runtime start?)");
        using var stream = Connect(port);
        var header = new byte[20];
        "DOTNET_IPC_V1\0"u8.CopyTo(header);
        BitConverter.TryWriteBytes(header.AsSpan(14), (ushort)20);
        header[16] = 0x04; // Process
        header[17] = 0x01; // ResumeRuntime
        stream.Write(header);
        var response = new byte[24];
        var read = stream.ReadAtLeast(response, 18, throwOnEndOfStream: false);
        return read >= 18 && response[16] == 0xFF && response[17] == 0x00 ? "OK" : $"unexpected response ({read} bytes, {response[16]:x2} {response[17]:x2})";
    }

    private static Stream Connect(string port)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", port, PipeDirection.InOut);
            pipe.Connect(5000);
            return pipe;
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(port));
        return new NetworkStream(socket, ownsSocket: true);
    }
}
