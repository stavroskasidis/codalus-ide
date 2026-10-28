using Codalus.Debugging.Companion;
using Codalus.Debugging.Host;
using Codalus.Rpc;

// Usage: codalus-debughost. The debug companion (M14b, ADR 0032): JSON-RPC on stdio, the requests in CompanionMethods.
// codalus-debughost --mono-dap: the Mono debugger adapter (M20b, ADR 0065), DAP on stdio.
// codalus-debughost --netfx-dap: the desktop-CLR (.NET Framework) debugger adapter (M43b, ADR 0076), DAP on stdio; Windows only.
// codalus-debughost --clr-dap: CoreCLR's debugger adapter (M54, ADR 0086), DAP on stdio.
// codalus-debughost --launch <pipe> <program> [args]: starts a program for --clr-dap (ADR 0086); its standard handles are the program's.
// codalus-debughost --activate-app <AUMID>: starts a registered packaged Windows app, printing its process id (M44, ADR 0074).
if (args is [Codalus.Debugging.CoreClrAdapter.LaunchArgument, ..])
{
    return Codalus.Debugging.Host.CoreClr.Launcher.Run(args[1..]);
}

if (args.Contains("--activate-app"))
{
    return AppActivation.Run(args, Console.Out, Console.Error);
}

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
Console.SetOut(Console.Error);

if (args.Contains("--mono-dap"))
{
    return await Codalus.Debugging.Host.Mono.MonoAdapter.RunAsync(stdin, stdout, Console.Error).ConfigureAwait(false);
}

if (args.Contains(Codalus.Debugging.CoreClrAdapter.AdapterArgument))
{
    await using var trace = Codalus.Debugging.Host.CoreClr.TeeWriter.OpenLog();
    return await Codalus.Debugging.Host.NetFx.NetFxAdapter.RunAsync(stdin, stdout, Console.Error, new Codalus.Debugging.Host.CoreClr.DbgShimDebugger(), trace).ConfigureAwait(false);
}

if (args.Contains(Codalus.Debugging.DesktopClr.AdapterArgument))
{
    return await Codalus.Debugging.Host.NetFx.NetFxAdapter.RunAsync(stdin, stdout, Console.Error).ConfigureAwait(false);
}

using var attributes = new TypeAttributes();
using var dumps = new Dumps();
await using var connection = new JsonRpcConnection(stdin, stdout);
var json = CompanionJsonContext.Default;

// The work is synchronous (metadata reading, ClrMD): off the RPC loop, one request at a time per kind is fine.
connection.OnRequest(CompanionMethods.TypeDisplay, json.TypeDisplayParams, json.TypeDisplayResult,
    (p, _) => Task.Run(() => attributes.Find(p.TypeName, p.Modules)));
connection.OnRequest(CompanionMethods.OpenDump, json.OpenDumpParams, json.DumpSummary, (p, _) => Task.Run(() => dumps.Open(p.Path)));
connection.OnRequest(CompanionMethods.DumpHeap, json.DumpHeapParams, json.DumpHeapResult, (p, _) => Task.Run(() => dumps.Heap(p.Id, p.Top)));
connection.OnRequest(CompanionMethods.DumpInstances, json.DumpInstancesParams, json.UInt64Array, (p, _) => Task.Run(() => dumps.Instances(p.Id, p.MethodTable, p.Max)));
connection.OnRequest(CompanionMethods.DumpObject, json.DumpObjectParams, json.DumpObject, (p, _) => Task.Run(() => dumps.Object(p.Id, p.Address)));
connection.OnRequest(CompanionMethods.CloseDump, json.CloseDumpParams, json.Boolean, (p, _) => Task.FromResult(dumps.Close(p.Id)));
connection.OnRequest(CompanionMethods.CreateDump, json.CreateDumpParams, json.CreateDumpResult, (p, ct) => Dumps.CreateAsync(p.ProcessId, p.Path, p.Kind, Dumps.DumpTimeout, ct));

connection.Start();
await connection.Completion.ConfigureAwait(false);
return 0;
