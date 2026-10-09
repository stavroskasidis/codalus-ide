using DaedalusStudio.Plugins.Host;
using DaedalusStudio.Rpc;

// stdout carries the protocol; anything a plugin writes to Console goes to stderr (the shell logs it).
var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
Console.SetOut(Console.Error);

await using var connection = new JsonRpcConnection(stdin, stdout);
await using var server = new PluginHostServer(connection);
server.RegisterHandlers();
connection.Start();
await Task.WhenAny(connection.Completion, server.ExitRequested).ConfigureAwait(false);
return 0;
