using DaedalusStudio.Diagnostics.Host;
using DaedalusStudio.Rpc;

// Usage: daedalus-diaghost. The diagnostics host (M18, ADR 0044): EventPipe sessions on .NET processes and trace
// processing, over JSON-RPC on stdio (docs/diagnostics-protocol.md). TraceEvent's console output goes to stderr.
var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
Console.SetOut(Console.Error);

await using var connection = new JsonRpcConnection(stdin, stdout);
await using var server = new DiagnosticsServer(connection);
server.Register();
connection.Start();
await Task.WhenAny(connection.Completion, server.ExitRequested).ConfigureAwait(false);
return 0;
