using DaedalusStudio.LanguageServices.Host;
using DaedalusStudio.Rpc;

// Usage: daedalus-lshost. Speaks LSP (plus daedalus/ extensions) on stdio; see docs/protocol.md.
var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();

// stdout carries the protocol; anything else (analyzers, generators) writing to Console goes to stderr.
Console.SetOut(Console.Error);

await using var connection = new JsonRpcConnection(stdin, stdout);
await using var server = new LanguageServer(connection);
server.Register();
connection.Start();
await Task.WhenAny(connection.Completion, server.ExitRequested).ConfigureAwait(false);
return 0;
