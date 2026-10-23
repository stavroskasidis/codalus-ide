using Codalus.Rpc;

namespace Codalus.Workbench.Ai.Acp;

/// <summary>
/// The client side of the connection test (ADR 0083): the agent runs in an empty folder with no workspace, so every file,
/// terminal and permission request it sends is refused, and each is a line of the AI Output channel (the method only).
/// Its updates are ignored: no prompt is ever sent.
/// </summary>
public sealed class RefusingClientHandler(Action<string> log) : IAcpClientHandler
{
    private int _refused;

    /// <summary>How many requests were refused (tests).</summary>
    public int Refused => Volatile.Read(ref _refused);

    private JsonRpcHandlerException Refuse(string method)
    {
        Interlocked.Increment(ref _refused);
        log(method + " → refused (connection test)");
        return new JsonRpcHandlerException(JsonRpcConnection.InvalidParams, "Codalus IDE is only testing the connection, so it refuses what the agent asks for.");
    }

    public Task OnUpdateAsync(AcpSessionNotification notification) => Task.CompletedTask;

    public Task<AcpRequestPermissionResponse> RequestPermissionAsync(AcpRequestPermissionRequest request, CancellationToken cancellationToken) =>
        Task.FromException<AcpRequestPermissionResponse>(Refuse(AcpMethods.SessionRequestPermission));

    public Task<AcpReadTextFileResponse> ReadTextFileAsync(AcpReadTextFileRequest request, CancellationToken cancellationToken) =>
        Task.FromException<AcpReadTextFileResponse>(Refuse(AcpMethods.ReadTextFile));

    public Task WriteTextFileAsync(AcpWriteTextFileRequest request, CancellationToken cancellationToken) =>
        Task.FromException(Refuse(AcpMethods.WriteTextFile));

    public Task<AcpCreateTerminalResponse> CreateTerminalAsync(AcpCreateTerminalRequest request, CancellationToken cancellationToken) =>
        Task.FromException<AcpCreateTerminalResponse>(Refuse(AcpMethods.TerminalCreate));

    public Task<AcpTerminalOutputResponse> TerminalOutputAsync(AcpTerminalRequest request, CancellationToken cancellationToken) =>
        Task.FromException<AcpTerminalOutputResponse>(Refuse(AcpMethods.TerminalOutput));

    public Task<AcpTerminalExitStatus> WaitForTerminalExitAsync(AcpTerminalRequest request, CancellationToken cancellationToken) =>
        Task.FromException<AcpTerminalExitStatus>(Refuse(AcpMethods.TerminalWaitForExit));

    public Task KillTerminalAsync(AcpTerminalRequest request, CancellationToken cancellationToken) =>
        Task.FromException(Refuse(AcpMethods.TerminalKill));

    public Task ReleaseTerminalAsync(AcpTerminalRequest request, CancellationToken cancellationToken) =>
        Task.FromException(Refuse(AcpMethods.TerminalRelease));
}
