using Codalus.Debugging.Companion;
using Codalus.Rpc;

namespace Codalus.Workbench.Debugging;

/// <summary>
/// The debug companion host (<c>codalus-debughost</c>, M14b, ADR 0032): the debuggee's debugger attributes, dumps and
/// Create Dump. It starts the first time it is needed and is supervised like the other hosts.
/// </summary>
public sealed class DebugCompanion(Func<ProcessLaunchInfo?> launch) : IAsyncDisposable
{
    public const string EnvironmentVariable = "CODALUS_DEBUGHOST";

    private static readonly SupervisionPolicy s_policy = new() { ShutdownRequest = null, MaxRestarts = 5 };
    private readonly SemaphoreSlim _start = new(1, 1);
    private SupervisedProcess? _process;

    /// <summary>The host's stderr and supervision messages.</summary>
    public event EventHandler<string>? Log;

    /// <summary>Resolution order: <c>CODALUS_DEBUGHOST</c> (an apphost or a .dll), then <c>debughost/</c> next to the shell.</summary>
    public static ProcessLaunchInfo? Resolve()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv)
        {
            candidates.Add(fromEnv);
        }

        var exe = OperatingSystem.IsWindows() ? "codalus-debughost.exe" : "codalus-debughost";
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "debughost", exe));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "debughost", "codalus-debughost.dll"));
        return candidates.FirstOrDefault(File.Exists) is { } path ? ProcessLaunchInfo.FromPath(path) : null;
    }

    public async Task<TypeDisplayResult> TypeDisplayAsync(string typeName, IReadOnlyList<string> modules, CancellationToken cancellationToken) =>
        await InvokeAsync(CompanionMethods.TypeDisplay, new TypeDisplayParams(typeName, modules), Json.TypeDisplayParams, Json.TypeDisplayResult, cancellationToken).ConfigureAwait(false);

    public async Task<DumpSummary> OpenDumpAsync(string path, CancellationToken cancellationToken) =>
        await InvokeAsync(CompanionMethods.OpenDump, new OpenDumpParams(path), Json.OpenDumpParams, Json.DumpSummary, cancellationToken).ConfigureAwait(false);

    public async Task<DumpHeapResult> DumpHeapAsync(string id, int top, CancellationToken cancellationToken) =>
        await InvokeAsync(CompanionMethods.DumpHeap, new DumpHeapParams(id, top), Json.DumpHeapParams, Json.DumpHeapResult, cancellationToken).ConfigureAwait(false);

    public async Task<ulong[]> DumpInstancesAsync(string id, ulong methodTable, int max, CancellationToken cancellationToken) =>
        await InvokeAsync(CompanionMethods.DumpInstances, new DumpInstancesParams(id, methodTable, max), Json.DumpInstancesParams, Json.UInt64Array, cancellationToken).ConfigureAwait(false);

    public async Task<DumpObject> DumpObjectAsync(string id, ulong address, CancellationToken cancellationToken) =>
        await InvokeAsync(CompanionMethods.DumpObject, new DumpObjectParams(id, address), Json.DumpObjectParams, Json.DumpObject, cancellationToken).ConfigureAwait(false);

    public async Task CloseDumpAsync(string id, CancellationToken cancellationToken) =>
        await InvokeAsync(CompanionMethods.CloseDump, new CloseDumpParams(id), Json.CloseDumpParams, Json.Boolean, cancellationToken).ConfigureAwait(false);

    public async Task<CreateDumpResult> CreateDumpAsync(int processId, string path, string kind, CancellationToken cancellationToken) =>
        await InvokeAsync(CompanionMethods.CreateDump, new CreateDumpParams(processId, path, kind), Json.CreateDumpParams, Json.CreateDumpResult, cancellationToken).ConfigureAwait(false);

    private static CompanionJsonContext Json => CompanionJsonContext.Default;

    private async Task<TResult> InvokeAsync<TParams, TResult>(string method, TParams parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TParams> paramsInfo, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken)
    {
        var connection = await ConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.InvokeAsync(method, parameters, paramsInfo, resultInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonRpcConnection> ConnectionAsync(CancellationToken cancellationToken)
    {
        await _start.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is null)
            {
                var info = launch() ?? throw new InvalidOperationException("The debug companion host (codalus-debughost) was not found.");
                _process = new SupervisedProcess("debug companion host", info, s_policy);
                _process.Log += (_, line) => Log?.Invoke(this, line);
            }

            if (!_process.IsRunning)
            {
                await _process.StartAsync(cancellationToken).ConfigureAwait(false);
            }

            return _process.Connection;
        }
        finally
        {
            _start.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not null)
        {
            await _process.DisposeAsync().ConfigureAwait(false);
        }

        _start.Dispose();
    }
}
