using Codalus.Rpc;

namespace Codalus.Workbench.Ai.Acp;

/// <summary>
/// A client side that refuses everything: the connection test's (ADR 0083: the agent runs in an empty folder with no
/// workspace, and no prompt is ever sent), and a session the window doesn't know (ADR 0097). Every file, terminal and
/// permission request is refused, and each is a line of the AI Output channel (the method only); updates are ignored.
/// </summary>
public sealed class RefusingClientHandler(Action<string> log, string why = "connection test", string message = "Codalus IDE is only testing the connection, so it refuses what the agent asks for.") : IAcpClientHandler
{
    private int _refused;

    /// <summary>How many requests were refused (tests).</summary>
    public int Refused => Volatile.Read(ref _refused);

    private JsonRpcHandlerException Refuse(string method)
    {
        Interlocked.Increment(ref _refused);
        log($"{method} → refused ({why})");
        return new JsonRpcHandlerException(JsonRpcConnection.InvalidParams, message);
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
