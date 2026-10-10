using System.Text.Json;

namespace DaedalusStudio.LanguageServices.Protocol;

// The subset of LSP 3.17 that Daedalus uses (https://microsoft.github.io/language-server-protocol/). Shapes and names
// follow the specification; properties a server may send as one of several types are narrowed to the form our
// servers send (documented on the property). Enumerations are integers on the wire, so they are int constants here.

public sealed record Position(int Line, int Character);

public sealed record Range(Position Start, Position End);

public sealed record Location(string Uri, Range Range);

public sealed record TextEdit(Range Range, string NewText);

public sealed record TextDocumentIdentifier(string Uri);

public sealed record VersionedTextDocumentIdentifier(string Uri, int Version);

public sealed record TextDocumentItem(string Uri, string LanguageId, int Version, string Text);

public sealed record TextDocumentPositionParams(TextDocumentIdentifier TextDocument, Position Position);

/// <summary><see cref="Kind"/> is <c>plaintext</c> or <c>markdown</c>.</summary>
public sealed record MarkupContent(string Kind, string Value)
{
    public static MarkupContent Markdown(string value) => new("markdown", value);
}

// ------------------------------------------------------------------ lifecycle

public sealed record ClientInfo(string Name, string? Version);

public sealed record InitializeParams(int ProcessId, ClientInfo ClientInfo, string? RootUri, JsonElement? Capabilities, JsonElement? InitializationOptions);

public sealed record ServerInfo(string Name, string? Version);

public sealed record InitializeResult(ServerCapabilities Capabilities, ServerInfo ServerInfo);

public static class TextDocumentSyncKind
{
    public const int None = 0;
    public const int Full = 1;
    public const int Incremental = 2;
}

public sealed record ServerCapabilities
{
    public int TextDocumentSync { get; init; } = TextDocumentSyncKind.Incremental;

    public CompletionOptions? CompletionProvider { get; init; }

    public bool HoverProvider { get; init; }

    public SignatureHelpOptions? SignatureHelpProvider { get; init; }

    public bool DefinitionProvider { get; init; }

    public bool ImplementationProvider { get; init; }

    public bool DocumentHighlightProvider { get; init; }

    public bool FoldingRangeProvider { get; init; }

    public SemanticTokensOptions? SemanticTokensProvider { get; init; }

    public bool ReferencesProvider { get; init; }

    /// <summary>True here means rename with <c>prepareRename</c> (LSP's <c>{ prepareProvider: true }</c>) unless <see cref="PrepareRenameProvider"/> is false.</summary>
    public bool RenameProvider { get; init; }

    /// <summary>Set by the client for servers that state it: false means rename without <c>prepareRename</c>.</summary>
    public bool? PrepareRenameProvider { get; init; }

    public bool CodeActionProvider { get; init; }

    public bool DocumentFormattingProvider { get; init; }

    public bool DocumentRangeFormattingProvider { get; init; }

    public DocumentOnTypeFormattingOptions? DocumentOnTypeFormattingProvider { get; init; }

    public bool WorkspaceSymbolProvider { get; init; }

    public bool DocumentSymbolProvider { get; init; }

    public bool CallHierarchyProvider { get; init; }

    public bool TypeHierarchyProvider { get; init; }

    public bool InlayHintProvider { get; init; }

    public CodeLensOptions? CodeLensProvider { get; init; }

    public bool SelectionRangeProvider { get; init; }
}

public sealed record CodeLensOptions(bool ResolveProvider);

public sealed record CompletionOptions(string[] TriggerCharacters, string[]? AllCommitCharacters, bool ResolveProvider);

public sealed record SignatureHelpOptions(string[] TriggerCharacters, string[] RetriggerCharacters);

public sealed record SemanticTokensOptions(SemanticTokensLegend Legend, bool Range, bool Full);

public sealed record SemanticTokensLegend(string[] TokenTypes, string[] TokenModifiers);

// ------------------------------------------------------------------ document sync

public sealed record DidOpenTextDocumentParams(TextDocumentItem TextDocument);

/// <summary>An incremental change (<see cref="Range"/> set) or the whole text (<see cref="Range"/> null).</summary>
public sealed record TextDocumentContentChangeEvent(Range? Range, string Text);

/// <summary>Changes apply in order, each to the result of the previous one.</summary>
public sealed record DidChangeTextDocumentParams(VersionedTextDocumentIdentifier TextDocument, TextDocumentContentChangeEvent[] ContentChanges);

public sealed record DidCloseTextDocumentParams(TextDocumentIdentifier TextDocument);

public sealed record DidSaveTextDocumentParams(TextDocumentIdentifier TextDocument);

// ------------------------------------------------------------------ diagnostics

public static class DiagnosticSeverity
{
    public const int Error = 1;
    public const int Warning = 2;
    public const int Information = 3;
    public const int Hint = 4;
}

public static class DiagnosticTag
{
    public const int Unnecessary = 1;
    public const int Deprecated = 2;
}

public sealed record CodeDescription(string Href);

/// <summary><see cref="Code"/> is always a string here (LSP also allows an integer).</summary>
public sealed record Diagnostic(Range Range, int Severity, string? Code, string? Source, string Message)
{
    public CodeDescription? CodeDescription { get; init; }

    public int[]? Tags { get; init; }
}

public sealed record PublishDiagnosticsParams(string Uri, int? Version, Diagnostic[] Diagnostics);

// ------------------------------------------------------------------ completion

public static class CompletionTriggerKind
{
    public const int Invoked = 1;
    public const int TriggerCharacter = 2;
    public const int TriggerForIncompleteCompletions = 3;
}

public static class CompletionItemKind
{
    public const int Text = 1;
    public const int Method = 2;
    public const int Function = 3;
    public const int Constructor = 4;
    public const int Field = 5;
    public const int Variable = 6;
    public const int Class = 7;
    public const int Interface = 8;
    public const int Module = 9;
    public const int Property = 10;
    public const int Unit = 11;
    public const int Value = 12;
    public const int Enum = 13;
    public const int Keyword = 14;
    public const int Snippet = 15;
    public const int Color = 16;
    public const int File = 17;
    public const int Reference = 18;
    public const int Folder = 19;
    public const int EnumMember = 20;
    public const int Constant = 21;
    public const int Struct = 22;
    public const int Event = 23;
    public const int Operator = 24;
    public const int TypeParameter = 25;
}

public sealed record CompletionContext(int TriggerKind, string? TriggerCharacter);

public sealed record CompletionParams(TextDocumentIdentifier TextDocument, Position Position, CompletionContext? Context);

/// <summary>
/// A completion item. Our servers leave the edit to <c>completionItem/resolve</c> (which fills <see cref="TextEdit"/>
/// and <see cref="AdditionalTextEdits"/>, e.g. a <c>using</c>), and the list's <see cref="CompletionItemDefaults.EditRange"/>
/// is the span being completed.
/// </summary>
public sealed record CompletionItem(string Label)
{
    public int? Kind { get; init; }

    public string? Detail { get; init; }

    /// <summary>Always <see cref="MarkupContent"/> here (LSP also allows a string).</summary>
    public MarkupContent? Documentation { get; init; }

    public string? SortText { get; init; }

    public string? FilterText { get; init; }

    public string? InsertText { get; init; }

    /// <summary><see cref="LanguageServices.Protocol.InsertTextFormat.Snippet"/>: the text has tab stops (M12b).</summary>
    public int? InsertTextFormat { get; init; }

    public TextEdit? TextEdit { get; init; }

    public TextEdit[]? AdditionalTextEdits { get; init; }

    public string[]? CommitCharacters { get; init; }

    public bool? Preselect { get; init; }

    public JsonElement? Data { get; init; }
}

public static class InsertTextFormat
{
    public const int PlainText = 1;
    public const int Snippet = 2;
}

public sealed record CompletionItemDefaults(Range? EditRange);

public sealed record CompletionList(bool IsIncomplete, CompletionItem[] Items)
{
    public CompletionItemDefaults? ItemDefaults { get; init; }
}

// ------------------------------------------------------------------ hover, signature help

public sealed record Hover(MarkupContent Contents, Range? Range);

public sealed record SignatureHelpContext(int TriggerKind, string? TriggerCharacter, bool IsRetrigger);

public sealed record SignatureHelpParams(TextDocumentIdentifier TextDocument, Position Position, SignatureHelpContext? Context);

/// <summary><see cref="Label"/> is a string here: a substring of the signature's label (LSP also allows offsets).</summary>
public sealed record ParameterInformation(string Label, MarkupContent? Documentation);

public sealed record SignatureInformation(string Label, MarkupContent? Documentation, ParameterInformation[] Parameters)
{
    public int? ActiveParameter { get; init; }
}

public sealed record SignatureHelp(SignatureInformation[] Signatures, int ActiveSignature, int ActiveParameter);

// ------------------------------------------------------------------ highlights, folding, semantic tokens

public static class DocumentHighlightKind
{
    public const int Text = 1;
    public const int Read = 2;
    public const int Write = 3;
}

public sealed record DocumentHighlight(Range Range, int Kind);

public sealed record FoldingRangeParams(TextDocumentIdentifier TextDocument);

/// <summary><see cref="Kind"/> is <c>comment</c>, <c>imports</c>, <c>region</c> or null.</summary>
public sealed record FoldingRange(int StartLine, int EndLine)
{
    public int? StartCharacter { get; init; }

    public int? EndCharacter { get; init; }

    public string? Kind { get; init; }

    public string? CollapsedText { get; init; }
}

public sealed record SemanticTokensParams(TextDocumentIdentifier TextDocument);

public sealed record SemanticTokensRangeParams(TextDocumentIdentifier TextDocument, Range Range);

/// <summary>Five integers per token: deltaLine, deltaStartChar, length, tokenType, tokenModifiers.</summary>
public sealed record SemanticTokens(int[] Data)
{
    public string? ResultId { get; init; }
}

// ------------------------------------------------------------------ configuration

/// <summary>Daedalus servers read <c>{ "analysisScope": "openDocuments" | "solution" }</c> (also accepted as initialization options).</summary>
public sealed record DidChangeConfigurationParams(JsonElement? Settings);

// ------------------------------------------------------------------ window

public static class MessageType
{
    public const int Error = 1;
    public const int Warning = 2;
    public const int Info = 3;
    public const int Log = 4;
}

public sealed record LogMessageParams(int Type, string Message);

// ------------------------------------------------------------------ references, rename

public sealed record ReferenceContext(bool IncludeDeclaration);

public sealed record ReferenceParams(TextDocumentIdentifier TextDocument, Position Position, ReferenceContext Context);

/// <summary>The range of the symbol to rename and its current name; null (JSON) when renaming isn't possible there.</summary>
public sealed record PrepareRenameResult(Range Range, string Placeholder);

public sealed record RenameParams(TextDocumentIdentifier TextDocument, Position Position, string NewName);

public sealed record OptionalVersionedTextDocumentIdentifier(string Uri, int? Version);

/// <summary>Edits of one document. The version is null for files that aren't open.</summary>
public sealed record TextDocumentEdit(OptionalVersionedTextDocumentIdentifier TextDocument, TextEdit[] Edits);

/// <summary>Only <see cref="DocumentChanges"/> (text edits per document) is used here.</summary>
public sealed record WorkspaceEdit(TextDocumentEdit[] DocumentChanges);

// ------------------------------------------------------------------ code actions

public static class CodeActionKind
{
    public const string QuickFix = "quickfix";
    public const string Refactor = "refactor";
}

public sealed record CodeActionContext(Diagnostic[] Diagnostics, string[]? Only);

public sealed record CodeActionParams(TextDocumentIdentifier TextDocument, Range Range, CodeActionContext Context);

/// <summary>Listed without <see cref="Edit"/>; <c>codeAction/resolve</c> computes it.</summary>
public sealed record CodeAction(string Title)
{
    public string? Kind { get; init; }

    public bool? IsPreferred { get; init; }

    public WorkspaceEdit? Edit { get; init; }

    public JsonElement? Data { get; init; }
}

// ------------------------------------------------------------------ formatting

public sealed record FormattingOptions(int TabSize, bool InsertSpaces);

public sealed record DocumentFormattingParams(TextDocumentIdentifier TextDocument, FormattingOptions Options);

public sealed record DocumentRangeFormattingParams(TextDocumentIdentifier TextDocument, Range Range, FormattingOptions Options);

public sealed record DocumentOnTypeFormattingParams(TextDocumentIdentifier TextDocument, Position Position, string Ch, FormattingOptions Options);

public sealed record DocumentOnTypeFormattingOptions(string FirstTriggerCharacter, string[]? MoreTriggerCharacter);

// ------------------------------------------------------------------ workspace symbols

public static class SymbolKind
{
    public const int File = 1;
    public const int Module = 2;
    public const int Namespace = 3;
    public const int Package = 4;
    public const int Class = 5;
    public const int Method = 6;
    public const int Property = 7;
    public const int Field = 8;
    public const int Constructor = 9;
    public const int Enum = 10;
    public const int Interface = 11;
    public const int Function = 12;
    public const int Variable = 13;
    public const int Constant = 14;
    public const int String = 15;
    public const int Number = 16;
    public const int Boolean = 17;
    public const int Array = 18;
    public const int Object = 19;
    public const int Key = 20;
    public const int Null = 21;
    public const int EnumMember = 22;
    public const int Struct = 23;
    public const int Event = 24;
    public const int Operator = 25;
    public const int TypeParameter = 26;
}

public sealed record WorkspaceSymbolParams(string Query);

public sealed record SymbolInformation(string Name, int Kind, Location Location)
{
    public string? ContainerName { get; init; }
}

// ------------------------------------------------------------------ document symbols (M12)

public sealed record DocumentSymbolParams(TextDocumentIdentifier TextDocument);

/// <summary>
/// A symbol in a document, with its children (members of a type, types of a namespace). Always the hierarchical form
/// here: flat <c>SymbolInformation[]</c> results from other servers are nested by range (<c>LspNormalizer</c>).
/// </summary>
public sealed record DocumentSymbol(string Name, int Kind, Range Range, Range SelectionRange)
{
    public string? Detail { get; init; }

    public DocumentSymbol[]? Children { get; init; }
}

// ------------------------------------------------------------------ call and type hierarchy (M12)

/// <summary><see cref="Data"/> lets the server find the symbol again; clients send items back unchanged.</summary>
public sealed record CallHierarchyItem(string Name, int Kind, string Uri, Range Range, Range SelectionRange)
{
    public string? Detail { get; init; }

    public JsonElement? Data { get; init; }
}

public sealed record CallHierarchyCallsParams(CallHierarchyItem Item);

/// <summary><see cref="FromRanges"/> are the calls, in <see cref="From"/>'s document.</summary>
public sealed record CallHierarchyIncomingCall(CallHierarchyItem From, Range[] FromRanges);

/// <summary><see cref="FromRanges"/> are the calls, in the caller's document.</summary>
public sealed record CallHierarchyOutgoingCall(CallHierarchyItem To, Range[] FromRanges);

public sealed record TypeHierarchyItem(string Name, int Kind, string Uri, Range Range, Range SelectionRange)
{
    public string? Detail { get; init; }

    public JsonElement? Data { get; init; }
}

public sealed record TypeHierarchyParams(TypeHierarchyItem Item);

// ------------------------------------------------------------------ inlay hints, CodeLens (M12)

public static class InlayHintKind
{
    public const int Type = 1;
    public const int Parameter = 2;
}

public sealed record InlayHintParams(TextDocumentIdentifier TextDocument, Range Range);

/// <summary><see cref="Label"/> is a string here (LSP also allows label parts; they are joined).</summary>
public sealed record InlayHint(Position Position, string Label)
{
    public int? Kind { get; init; }

    public bool? PaddingLeft { get; init; }

    public bool? PaddingRight { get; init; }

    /// <summary>Always <see cref="MarkupContent"/> here (LSP also allows a string).</summary>
    public MarkupContent? Tooltip { get; init; }
}

/// <summary>LSP's <c>Command</c>: <see cref="CommandId"/> is its <c>command</c> property.</summary>
public sealed record Command(string Title, [property: System.Text.Json.Serialization.JsonPropertyName("command")] string CommandId)
{
    public JsonElement[]? Arguments { get; init; }
}

public sealed record CodeLensParams(TextDocumentIdentifier TextDocument);

/// <summary>Listed without <see cref="Command"/>; <c>codeLens/resolve</c> fills it (only for lenses in view).</summary>
public sealed record CodeLens(Range Range)
{
    public Command? Command { get; init; }

    public JsonElement? Data { get; init; }
}

// ------------------------------------------------------------------ selection ranges (M12b)

public sealed record SelectionRangeParams(TextDocumentIdentifier TextDocument, Position[] Positions);

/// <summary>A range and the range containing it (Expand Selection goes outwards through <see cref="Parent"/>).</summary>
public sealed record SelectionRange(Range Range)
{
    public SelectionRange? Parent { get; init; }
}

