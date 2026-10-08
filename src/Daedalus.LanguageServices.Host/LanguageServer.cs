using System.Diagnostics;
using Daedalus.LanguageServices.Protocol;
using StreamJsonRpc;

namespace Daedalus.LanguageServices.Host;

/// <summary>JSON-RPC target. Methods are thin adapters over <see cref="WorkspaceManager"/>.</summary>
public sealed class LanguageServer : IAsyncDisposable
{
    private readonly WorkspaceManager _workspace = new();
    private readonly Action _exit;

    public LanguageServer(Action exit) => _exit = exit;

    [JsonRpcMethod(Methods.Initialize, UseSingleObjectParameterDeserialization = true)]
    public InitializeResult Initialize(InitializeParams request)
    {
        _ = request;
        var version = typeof(LanguageServer).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        return new InitializeResult("daedalus-lshost", version, new SemanticTokensLegend(TokenTypes.All, TokenModifiers.All));
    }

    [JsonRpcMethod(Methods.OpenSolution, UseSingleObjectParameterDeserialization = true)]
    public Task<OpenSolutionResult> OpenSolutionAsync(OpenSolutionParams request, CancellationToken cancellationToken)
        => _workspace.OpenSolutionAsync(request.Path, cancellationToken);

    [JsonRpcMethod(Methods.DidOpen, UseSingleObjectParameterDeserialization = true)]
    public void DidOpen(DidOpenParams request) => _workspace.OpenDocument(request.Path, request.Text, request.Version);

    // Applied synchronously so that edits are processed strictly in arrival order.
    [JsonRpcMethod(Methods.DidChange, UseSingleObjectParameterDeserialization = true)]
    public void DidChange(DidChangeParams request) => _workspace.ChangeDocument(request.Path, request.Version, request.Changes);

    [JsonRpcMethod(Methods.DidClose, UseSingleObjectParameterDeserialization = true)]
    public void DidClose(DidCloseParams request) => _workspace.CloseDocument(request.Path);

    [JsonRpcMethod(Methods.SemanticTokensFull, UseSingleObjectParameterDeserialization = true)]
    public Task<SemanticTokens> SemanticTokensFullAsync(SemanticTokensParams request, CancellationToken cancellationToken)
        => _workspace.GetSemanticTokensAsync(request.Path, lineRange: null, cancellationToken);

    [JsonRpcMethod(Methods.SemanticTokensRange, UseSingleObjectParameterDeserialization = true)]
    public Task<SemanticTokens> SemanticTokensRangeAsync(SemanticTokensRangeParams request, CancellationToken cancellationToken)
        => _workspace.GetSemanticTokensAsync(request.Path, (request.StartLine, request.EndLine), cancellationToken);

    [JsonRpcMethod(Methods.Shutdown)]
    public void Shutdown() => Trace.TraceInformation("shutdown requested");

    [JsonRpcMethod(Methods.Exit)]
    public void Exit() => _exit();

    public ValueTask DisposeAsync()
    {
        _workspace.Dispose();
        return ValueTask.CompletedTask;
    }
}
