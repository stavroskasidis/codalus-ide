using Codalus.Interactive.Host;
using Codalus.Rpc;
using Microsoft.Win32.SafeHandles;

// Usage: codalus-replhost. Speaks the C# Interactive protocol on stdio; see docs/interactive-protocol.md.
// On Unix the console streams lock Console.Out on every write, and Console.Out is the scripts' output writer, which
// waits for the protocol while holding that lock: use plain streams on the descriptors instead.
var stdin = OperatingSystem.IsWindows() ? Console.OpenStandardInput() : new FileStream(new SafeFileHandle(0, ownsHandle: false), FileAccess.Read, 1);
var stdout = OperatingSystem.IsWindows() ? Console.OpenStandardOutput() : new FileStream(new SafeFileHandle(1, ownsHandle: false), FileAccess.Write, 1);

await using var connection = new JsonRpcConnection(stdin, stdout);
await using var server = new ReplServer(connection);

// stdout carries the protocol: what scripts write to Console goes to the shell as repl/output, and they read no input.
Console.SetOut(server.Out);
Console.SetError(server.Error);
Console.SetIn(TextReader.Null);

server.Register();
connection.Start();
await Task.WhenAny(connection.Completion, server.ExitRequested).ConfigureAwait(false);
return 0;
