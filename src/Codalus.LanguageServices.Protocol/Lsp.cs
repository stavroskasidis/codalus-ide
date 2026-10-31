using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codalus.LanguageServices.Protocol;

// The subset of LSP 3.17 that Codalus uses (https://microsoft.github.io/language-server-protocol/). Shapes and names
// follow the specification; properties a server may send as one of several types are narrowed to the form our
// servers send (documented on the property). Enumerations are integers on the wire, so they are int constants here.

public sealed record Position(int Line, int Character);

public sealed record Range(Position Start, Position End);

public sealed record Location(string Uri, Range Range);

public sealed record TextEdit(Range Range, string NewText);

public sealed record TextDocumentIdentifier(string Uri);

public sealed record VersionedTextDocumentIdentifier(string Uri, int Version);

/// <summary><see cref="Text"/> is the whole document, written in segments (<see cref="DocumentTextConverter"/>).</summary>
public sealed record TextDocumentItem(string Uri, string LanguageId, int Version, [property: JsonConverter(typeof(DocumentTextConverter))] string Text);

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

    /// <summary>The server answers <c>textDocument/diagnostic</c> (pull diagnostics, LSP 3.17) instead of publishing them.</summary>
    public bool DiagnosticProvider { get; init; }
}

public sealed record CodeLensOptions(bool ResolveProvider);

public sealed record CompletionOptions(string[] TriggerCharacters, string[]? AllCommitCharacters, bool ResolveProvider);

public sealed record SignatureHelpOptions(string[] TriggerCharacters, string[] RetriggerCharacters);

public sealed record SemanticTokensOptions(SemanticTokensLegend Legend, bool Range, bool Full);

public sealed record SemanticTokensLegend(string[] TokenTypes, string[] TokenModifiers);

// ------------------------------------------------------------------ document sync

public sealed record DidOpenTextDocumentParams(TextDocumentItem TextDocument);

/// <summary>An incremental change (<see cref="Range"/> set) or the whole text (<see cref="Range"/> null).</summary>
public sealed record TextDocumentContentChangeEvent(Range? Range, [property: JsonConverter(typeof(DocumentTextConverter))] string Text);

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

    /// <summary>
    /// Extension (<c>codalusGlyph</c>): the symbol's kind, finer than <see cref="Kind"/> (an extension method, a local or
    /// a parameter, a delegate), as one of <see cref="SymbolGlyphs"/>. Sent in the list.
    /// </summary>
    [JsonPropertyName("codalusGlyph")]
    public string? Glyph { get; init; }

    /// <summary>
    /// Extension (<c>codalusDescription</c>): the item's description as classified text (the signature with its overload
    /// count, then the documentation). Filled by <c>completionItem/resolve</c>, beside the Markdown <see cref="Documentation"/>.
    /// </summary>
    [JsonPropertyName("codalusDescription")]
    public SymbolDescription? Description { get; init; }

    /// <summary>
    /// Extension (<c>codalusMatchesTargetType</c>): the item's type converts to the type expected where it goes (an
    /// argument, an assignment, a return value). Sent in the list; the list preselects such an item and filters by it.
    /// </summary>
    [JsonPropertyName("codalusMatchesTargetType")]
    public bool? MatchesTargetType { get; init; }

    /// <summary>
    /// Extension (<c>codalusSoftSelection</c>): while nothing is typed, this preselected item is selected softly (a
    /// commit character doesn't commit it). Sent in the list.
    /// </summary>
    [JsonPropertyName("codalusSoftSelection")]
    public bool? SoftSelection { get; init; }

    /// <summary>
    /// Extension (<c>codalusEnterKeyRule</c>): whether Enter, after committing the item, also starts a new line:
    /// <c>always</c>, or <c>afterFullyTypedWord</c> (when the whole name was typed); absent for never. Sent in the list.
    /// </summary>
    [JsonPropertyName("codalusEnterKeyRule")]
    public string? EnterKeyRule { get; init; }

    /// <summary>
    /// Extension (<c>codalusCommitCharacter</c>): on a <c>completionItem/resolve</c> request, the character typed to
    /// commit the item (the server may change the edit for it: <c>List</c> rather than <c>List&lt;&gt;</c> before <c>&lt;</c>).
    /// The editor no longer sends it: committing doesn't wait for a resolve (M56). TODO(issue): remove it, with
    /// <c>codalusIncludesCommitCharacter</c> and the C# host's path for it.
    /// </summary>
    [JsonPropertyName("codalusCommitCharacter")]
    public string? CommitCharacter { get; init; }

    /// <summary>
    /// Extension (<c>codalusIncludesCommitCharacter</c>): in a resolved item, the edit already inserts the commit
    /// character, so the editor doesn't type it again.
    /// </summary>
    [JsonPropertyName("codalusIncludesCommitCharacter")]
    public bool? IncludesCommitCharacter { get; init; }
}

/// <summary>Values of <see cref="CompletionItem.EnterKeyRule"/>.</summary>
public static class EnterKeyRules
{
    public const string Always = "always";
    public const string AfterFullyTypedWord = "afterFullyTypedWord";
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

/// <summary>
/// <see cref="Description"/> is an extension (<c>codalusDescription</c>): the same Quick Info as classified text, which
/// clients that know it draw in the editor's colors; <see cref="Contents"/> stays Markdown for every other client.
/// </summary>
public sealed record Hover(MarkupContent Contents, Range? Range)
{
    [JsonPropertyName("codalusDescription")]
    public SymbolDescription? Description { get; init; }
}

/// <summary>
/// Extension: a run of classified text. <see cref="Kind"/> is a semantic token type of the legend (<see cref="TokenTypes"/>:
/// <c>keyword</c>, <c>class</c>, <c>method</c>, <c>parameter</c>, <c>punctuation</c>…), <c>text</c> for plain text, or
/// <c>lineBreak</c> between lines. In a signature, <see cref="Parameter"/> is the index of the parameter the run belongs to.
/// </summary>
public sealed record ClassifiedRun(string Text, string Kind)
{
    public const string PlainText = "text";

    public const string LineBreak = "lineBreak";

    public int? Parameter { get; init; }
}

/// <summary>
/// Extension: one part of a symbol's documentation. <see cref="Kind"/> is one of <see cref="DescriptionSectionKinds"/>;
/// <see cref="Text"/> is its body without a heading (clients write "Returns:" and the others themselves, once).
/// </summary>
public sealed record DescriptionSection(string Kind, ClassifiedRun[] Text);

/// <summary>Extension: the kinds of <see cref="DescriptionSection"/>, in the order Quick Info shows them.</summary>
public static class DescriptionSectionKinds
{
    public const string Summary = "summary";
    public const string TypeParameters = "typeParameters";
    public const string AnonymousTypes = "anonymousTypes";
    public const string Usage = "usage";
    public const string Returns = "returns";
    public const string Value = "value";
    public const string Exceptions = "exceptions";
    public const string Remarks = "remarks";
    public const string Captures = "captures";
    public const string Nullability = "nullability";
    public const string Other = "other";
}

/// <summary>
/// Extension: a symbol's description as Quick Info and the completion list's tooltip show it. <see cref="Signature"/> is
/// the classified declaration, with generic type arguments as the site uses them; <see cref="Overloads"/> is how many
/// other overloads there are (the signature doesn't say it); <see cref="Glyph"/> is one of <see cref="SymbolGlyphs"/>.
/// </summary>
public sealed record SymbolDescription(ClassifiedRun[] Signature, DescriptionSection[] Sections)
{
    public string? Glyph { get; init; }

    public int Overloads { get; init; }
}

/// <summary>Extension: the symbol kinds the C# service names, for icons and the completion list's filters.</summary>
public static class SymbolGlyphs
{
    public const string Namespace = "namespace";
    public const string Class = "class";
    public const string Struct = "struct";
    public const string Interface = "interface";
    public const string Enum = "enum";
    public const string EnumMember = "enumMember";
    public const string Delegate = "delegate";
    public const string Module = "module";
    public const string TypeParameter = "typeParameter";
    public const string Method = "method";
    public const string ExtensionMethod = "extensionMethod";
    public const string Property = "property";
    public const string Field = "field";
    public const string Event = "event";
    public const string Constant = "constant";
    public const string Local = "local";
    public const string Parameter = "parameter";
    public const string Keyword = "keyword";
    public const string Snippet = "snippet";
    public const string Operator = "operator";
    public const string Label = "label";
    public const string Text = "text";
}

public sealed record SignatureHelpContext(int TriggerKind, string? TriggerCharacter, bool IsRetrigger);

public sealed record SignatureHelpParams(TextDocumentIdentifier TextDocument, Position Position, SignatureHelpContext? Context);

/// <summary><see cref="Label"/> is a string here: a substring of the signature's label (LSP also allows offsets).</summary>
public sealed record ParameterInformation(string Label, MarkupContent? Documentation);

public sealed record SignatureInformation(string Label, MarkupContent? Documentation, ParameterInformation[] Parameters)
{
    public int? ActiveParameter { get; init; }

    /// <summary>
    /// Extension (<c>codalusParts</c>): <see cref="Label"/> as classified runs, each parameter's runs marked with its
    /// index, with generic type arguments substituted as the call site uses them. Clients without it use the label.
    /// </summary>
    [JsonPropertyName("codalusParts")]
    public ClassifiedRun[]? Parts { get; init; }
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

/// <summary>
/// Codalus servers read <c>{ "analysisScope": "openDocuments" | "currentProject" | "solution", "analysisProject": "&lt;project file&gt;" }</c>
/// (also accepted as initialization options).
/// </summary>
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
public sealed record WorkspaceEdit(TextDocumentEdit[] DocumentChanges)
{
    /// <summary>
    /// Files renamed or deleted (LSP's <c>RenameFile</c> and <c>DeleteFile</c> in <c>documentChanges</c>), applied after
    /// the text edits, which are in the old files' positions: Roslyn's Rename File to Match Type, a removed document.
    /// </summary>
    public ResourceOperation[] ResourceOperations { get; init; } = [];
}

/// <summary>A file renamed (<see cref="NewUri"/>) or deleted, in a <see cref="WorkspaceEdit"/>.</summary>
public sealed record ResourceOperation(string Kind, string Uri, string? NewUri = null);

public static class ResourceOperationKinds
{
    public const string Rename = "rename";
    public const string Delete = "delete";
}

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

    /// <summary>A command the server runs for the action (<c>workspace/executeCommand</c>), after its edit if it has one.</summary>
    public Command? Command { get; init; }

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

