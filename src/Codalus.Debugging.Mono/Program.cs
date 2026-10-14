using Codalus.Debugging.Mono;

// codalus-monodbg (M20b, ADR 0065): a Debug Adapter Protocol server on stdio for apps on the Mono runtime.
// stdout carries only the protocol: the soft debugger's libraries write their errors with Console.WriteLine, which
// goes to stderr (the shell logs it).
var protocol = Console.OpenStandardOutput();
if (Environment.GetEnvironmentVariable("CODALUS_MONODBG_LOGFILE") is { Length: > 0 } logFile)
{
    var log = new StreamWriter(logFile, append: true) { AutoFlush = true };
    Console.SetOut(log);
    Console.SetError(log);
}
else
{
    Console.SetOut(Console.Error);
}

var server = new DapServer(Console.OpenStandardInput(), protocol);
using var adapter = new MonoAdapter(server);
await server.RunAsync(adapter.HandleAsync, adapter.AfterResponse, CancellationToken.None).ConfigureAwait(false);
return 0;
