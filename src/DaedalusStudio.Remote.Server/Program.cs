using System.Text;
using DaedalusStudio.Remote.Protocol;
using DaedalusStudio.Remote.Server;
using DaedalusStudio.Rpc;
using DaedalusStudio.Rpc.Multiplexing;

// Usage: daedalus-server [--stdio]. The remote development server (M21, ADR 0049, docs/remote-protocol.md): it prints
// its preamble line, then speaks multiplexed frames (ADR 0050) on stdin and stdout until stdin ends. It never listens
// on a network port. `daedalus-server --echo` copies stdin to stdout and exits with 3 (a test program for spawning).
if (args is ["--echo", ..])
{
    Console.Error.WriteLine("echo: started");
    await Console.OpenStandardInput().CopyToAsync(Console.OpenStandardOutput()).ConfigureAwait(false);
    Console.Error.WriteLine("echo: done");
    return 3;
}

if (args is ["--version", ..])
{
    Console.WriteLine(RemoteServer.Version);
    return 0;
}

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
Console.SetOut(Console.Error);
var preamble = Encoding.ASCII.GetBytes($"\n{RemoteMethods.Preamble} {RemoteServer.Version} {RemoteMethods.ProtocolVersion}\n");
await stdout.WriteAsync(preamble).ConfigureAwait(false);
await stdout.FlushAsync().ConfigureAwait(false);

await using var multiplexer = new ChannelMultiplexer(stdin, stdout);
var control = multiplexer.Start();
await using var connection = new JsonRpcConnection(control.Stream, control.Stream);
await using var server = new RemoteServer(multiplexer, connection);
server.Register();
connection.Start();
await Task.WhenAny(connection.Completion, multiplexer.Completion, server.ExitRequested).ConfigureAwait(false);
return 0;
