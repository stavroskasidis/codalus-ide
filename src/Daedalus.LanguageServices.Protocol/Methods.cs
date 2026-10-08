namespace Daedalus.LanguageServices.Protocol;

/// <summary>
/// JSON-RPC method names. Standard LSP names are reused where the semantics match;
/// IDE-specific extensions use the <c>daedalus/</c> prefix and are documented in docs/protocol.md.
/// </summary>
public static class Methods
{
    public const string Initialize = "initialize";
    public const string Shutdown = "shutdown";
    public const string Exit = "exit";

    public const string OpenSolution = "daedalus/openSolution";

    public const string DidOpen = "textDocument/didOpen";
    public const string DidChange = "textDocument/didChange";
    public const string DidClose = "textDocument/didClose";
    public const string SemanticTokensFull = "textDocument/semanticTokens/full";
    public const string SemanticTokensRange = "textDocument/semanticTokens/range";
}
