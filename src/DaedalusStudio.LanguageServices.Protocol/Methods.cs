namespace DaedalusStudio.LanguageServices.Protocol;

/// <summary>
/// JSON-RPC method names: LSP 3.17 where it has a message, and <c>daedalus/</c> extensions (docs/protocol.md) where it
/// doesn't.
/// </summary>
public static class Methods
{
    public const string Initialize = "initialize";
    public const string Initialized = "initialized";
    public const string Shutdown = "shutdown";
    public const string Exit = "exit";

    public const string DidOpen = "textDocument/didOpen";
    public const string DidChange = "textDocument/didChange";
    public const string DidClose = "textDocument/didClose";
    public const string DidSave = "textDocument/didSave";
    public const string DidChangeConfiguration = "workspace/didChangeConfiguration";

    public const string Completion = "textDocument/completion";
    public const string CompletionResolve = "completionItem/resolve";
    public const string Hover = "textDocument/hover";
    public const string SignatureHelp = "textDocument/signatureHelp";
    public const string Definition = "textDocument/definition";
    public const string Implementation = "textDocument/implementation";
    public const string DocumentHighlight = "textDocument/documentHighlight";
    public const string FoldingRange = "textDocument/foldingRange";
    public const string SemanticTokensFull = "textDocument/semanticTokens/full";
    public const string SemanticTokensRange = "textDocument/semanticTokens/range";
    public const string References = "textDocument/references";
    public const string PrepareRename = "textDocument/prepareRename";
    public const string Rename = "textDocument/rename";
    public const string CodeAction = "textDocument/codeAction";
    public const string CodeActionResolve = "codeAction/resolve";
    public const string Formatting = "textDocument/formatting";
    public const string RangeFormatting = "textDocument/rangeFormatting";
    public const string OnTypeFormatting = "textDocument/onTypeFormatting";
    public const string WorkspaceSymbol = "workspace/symbol";

    // server → client
    public const string PublishDiagnostics = "textDocument/publishDiagnostics";
    public const string LogMessage = "window/logMessage";

    // Daedalus extensions
    public const string ProjectsUpdate = "daedalus/projects/update";
    public const string ProjectsRemove = "daedalus/projects/remove";
    public const string DocumentOptions = "daedalus/documentOptions";
    public const string SourceGeneratedText = "daedalus/sourceGeneratedText";
    public const string MetadataText = "daedalus/metadataText";
    public const string WorkspaceStatus = "daedalus/workspaceStatus";
    public const string TestCandidates = "daedalus/testCandidates";
    public const string TypeOutline = "daedalus/typeOutline";
}
