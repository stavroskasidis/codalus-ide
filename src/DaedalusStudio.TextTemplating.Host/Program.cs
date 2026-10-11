using DaedalusStudio.Rpc;
using DaedalusStudio.TextTemplating.Host;
using Microsoft.Win32.SafeHandles;

// Usage: daedalus-t4host. Runs T4 text templates for the shell over stdio (ADR 0035). Template code may write to the
// console: stdout carries the protocol, so the console's writers go nowhere.
var stdin = OperatingSystem.IsWindows() ? Console.OpenStandardInput() : new FileStream(new SafeFileHandle(0, ownsHandle: false), FileAccess.Read, 1);
var stdout = OperatingSystem.IsWindows() ? Console.OpenStandardOutput() : new FileStream(new SafeFileHandle(1, ownsHandle: false), FileAccess.Write, 1);
Console.SetOut(TextWriter.Null);
Console.SetIn(TextReader.Null);

await using var connection = new JsonRpcConnection(stdin, stdout);
var server = new TextTemplatingServer(connection);
server.Register();
connection.Start();
await Task.WhenAny(connection.Completion, server.ExitRequested).ConfigureAwait(false);
return 0;