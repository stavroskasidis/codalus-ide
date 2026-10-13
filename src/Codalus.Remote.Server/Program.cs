using System.Text;
using Codalus.Remote.Protocol;
using Codalus.Remote.Server;
using Codalus.Rpc;
using Codalus.Rpc.Multiplexing;

// Usage: codalus-server [--stdio | --relay <session>]. The remote development server (M21, ADR 0050, docs/remote-protocol.md): it prints
// its preamble line, then speaks multiplexed frames (ADR 0051) on stdin and stdout until stdin ends. It never listens
// on a network port. `codalus-server --echo` copies stdin to stdout and exits with 3 (a test program for spawning).
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

// `--relay <session> [--grace <seconds>]` and `--daemon <session> …`: a server that outlives its link (M21b, ADR 0054).
if (args is [("--relay" or "--daemon") and var mode, var session, ..])
{
    if (!ServerDaemon.IsValidSession(session))
    {
        Console.Error.WriteLine($"codalus-server: '{session}' isn't a session id.");
        return 2;
    }

    var grace = args is [_, _, "--grace", var seconds, ..] && int.TryParse(seconds, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
        ? TimeSpan.FromSeconds(value)
        : ServerDaemon.DefaultGrace;
    return mode == "--relay"
        ? await ServerDaemon.RelayAsync(session, grace).ConfigureAwait(false)
        : await ServerDaemon.DaemonAsync(session, grace).ConfigureAwait(false);
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
