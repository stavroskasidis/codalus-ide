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
    public const string DocumentSymbol = "textDocument/documentSymbol";
    public const string PrepareCallHierarchy = "textDocument/prepareCallHierarchy";
    public const string CallHierarchyIncomingCalls = "callHierarchy/incomingCalls";
    public const string CallHierarchyOutgoingCalls = "callHierarchy/outgoingCalls";
    public const string PrepareTypeHierarchy = "textDocument/prepareTypeHierarchy";
    public const string TypeHierarchySupertypes = "typeHierarchy/supertypes";
    public const string TypeHierarchySubtypes = "typeHierarchy/subtypes";
    public const string InlayHint = "textDocument/inlayHint";
    public const string CodeLens = "textDocument/codeLens";
    public const string CodeLensResolve = "codeLens/resolve";
    public const string SelectionRange = "textDocument/selectionRange";

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
    public const string ReferenceItems = "daedalus/references";
    public const string GoToBase = "daedalus/base";
    public const string InheritanceMargin = "daedalus/inheritanceMargin";
    public const string SymbolInfo = "daedalus/symbolInfo";
    public const string DocCommentTemplate = "daedalus/docCommentTemplate";
    public const string CodeCleanup = "daedalus/codeCleanup";
    public const string CodeActionOptions = "daedalus/codeActionOptions";
    public const string SyntaxTree = "daedalus/syntaxTree";
    public const string CodeMetrics = "daedalus/codeMetrics";

    /// <summary>ASP.NET Core endpoints in the solution: minimal API routes and controller actions (M15a).</summary>
    public const string AspNetEndpoints = "daedalus/aspnet/endpoints";

    /// <summary>EF Core DbContext types in the solution, with their provider and connection name (M27a).</summary>
    public const string EfContexts = "daedalus/ef/contexts";

    /// <summary>Client → SQL server notification: a SQL document's dialect and schema (M27b).</summary>
    public const string SqlContext = "daedalus/sql/context";

    /// <summary>An Avalonia XAML file's logical tree, for the previewer's selection (M19a, ADR 0047).</summary>
    public const string XamlLogicalTree = "daedalus/xaml/logicalTree";
}
