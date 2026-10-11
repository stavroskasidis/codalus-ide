using DaedalusStudio.Data.Host;
using DaedalusStudio.Data.Host.Sql;
using DaedalusStudio.Rpc;

// Usage: daedalus-datahost [--lsp]. Without --lsp: the data protocol (docs/data-protocol.md, ADR 0037). With --lsp: the
// SQL language server (ADR 0038). Both speak JSON-RPC on stdio; providers' console output goes to stderr.
var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
Console.SetOut(Console.Error);

// Managed networking for SQL Server everywhere: Microsoft's native SNI isn't shipped (its licence isn't OSI-approved).
AppContext.SetSwitch("Switch.Microsoft.Data.SqlClient.UseManagedNetworkingOnWindows", true);

await using var connection = new JsonRpcConnection(stdin, stdout);
if (args.Contains("--lsp"))
{
    var language = new SqlLanguageServer(connection);
    language.Register();
    connection.Start();
    await Task.WhenAny(connection.Completion, language.ExitRequested).ConfigureAwait(false);
    return 0;
}

await using var server = new DataServer(connection);
server.Register();
connection.Start();
await Task.WhenAny(connection.Completion, server.ExitRequested).ConfigureAwait(false);
return 0;
