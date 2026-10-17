using Codalus.Debugging.Companion;
using Codalus.Debugging.Host;
using Codalus.Rpc;

// Usage: codalus-debughost. The debug companion (M14b, ADR 0032): JSON-RPC on stdio, the requests in CompanionMethods.
// codalus-debughost --mono-dap: the Mono debugger adapter (M20b, ADR 0065), DAP on stdio.
// codalus-debughost --activate-app <AUMID>: starts a registered packaged Windows app, printing its process id (M44, ADR 0074).
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
