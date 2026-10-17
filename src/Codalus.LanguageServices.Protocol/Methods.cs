namespace Codalus.LanguageServices.Protocol;

/// <summary>
/// JSON-RPC method names: LSP 3.17 where it has a message, and <c>codalus/</c> extensions (docs/protocol.md) where it
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

    // Codalus extensions
    public const string ProjectsUpdate = "codalus/projects/update";
    public const string ProjectsRemove = "codalus/projects/remove";
    public const string DocumentOptions = "codalus/documentOptions";
    public const string SourceGeneratedText = "codalus/sourceGeneratedText";
    public const string GeneratedDocuments = "codalus/generatedDocuments";
    public const string Browse = "codalus/browse";
    public const string BrowseDescribe = "codalus/browse/describe";
    public const string MetadataText = "codalus/metadataText";
    public const string WorkspaceStatus = "codalus/workspaceStatus";
    public const string TestCandidates = "codalus/testCandidates";
    public const string TypeOutline = "codalus/typeOutline";
    public const string ReferenceItems = "codalus/references";
    public const string GoToBase = "codalus/base";
    public const string InheritanceMargin = "codalus/inheritanceMargin";
    public const string SymbolInfo = "codalus/symbolInfo";
    public const string DocCommentTemplate = "codalus/docCommentTemplate";
    public const string CodeCleanup = "codalus/codeCleanup";
    public const string PasteImports = "codalus/pasteImports";
    public const string CodeActionOptions = "codalus/codeActionOptions";
    public const string SyntaxTree = "codalus/syntaxTree";
    public const string CodeMetrics = "codalus/codeMetrics";

    /// <summary>ASP.NET Core endpoints in the solution: minimal API routes and controller actions (M15a).</summary>
    public const string AspNetEndpoints = "codalus/aspnet/endpoints";

    /// <summary>EF Core DbContext types in the solution, with their provider and connection name (M27a).</summary>
    public const string EfContexts = "codalus/ef/contexts";

    /// <summary>Client → SQL server notification: a SQL document's dialect and schema (M27b).</summary>
    public const string SqlContext = "codalus/sql/context";

    /// <summary>An Avalonia XAML file's logical tree, for the previewer's selection (M19a, ADR 0047).</summary>
    public const string XamlLogicalTree = "codalus/xaml/logicalTree";

    /// <summary>A form's or user control's designer model, read from InitializeComponent (M41a, ADR 0073).</summary>
    public const string WinFormsRead = "codalus/winforms/read";

    /// <summary>The Windows Forms designer's changes as minimal text edits of the designer file (M41a, ADR 0073).</summary>
    public const string WinFormsEdit = "codalus/winforms/edit";

    /// <summary>A project's own components and controls, for the Toolbox (M41a, ADR 0073).</summary>
    public const string WinFormsToolbox = "codalus/winforms/toolbox";
}
