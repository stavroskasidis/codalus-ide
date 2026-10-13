using Codalus.Markup.Host;
using Codalus.Rpc;

// Usage: codalus-markuphost. Speaks LSP on stdio for JSON, XML and MSBuild, YAML and .editorconfig (ADR 0016).
var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
Console.SetOut(Console.Error);

await using var connection = new JsonRpcConnection(stdin, stdout);
var server = new MarkupServer(connection);
server.Register();
connection.Start();
await Task.WhenAny(connection.Completion, server.ExitRequested).ConfigureAwait(false);
return 0;
