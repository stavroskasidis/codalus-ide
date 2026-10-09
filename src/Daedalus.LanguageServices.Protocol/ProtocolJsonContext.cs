using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daedalus.LanguageServices.Protocol;

/// <summary>Source-generated serialization metadata so the protocol works under Native AOT and trimming.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(DidOpenTextDocumentParams))]
[JsonSerializable(typeof(DidChangeTextDocumentParams))]
[JsonSerializable(typeof(DidCloseTextDocumentParams))]
[JsonSerializable(typeof(DidSaveTextDocumentParams))]
[JsonSerializable(typeof(PublishDiagnosticsParams))]
[JsonSerializable(typeof(CompletionParams))]
[JsonSerializable(typeof(CompletionList))]
[JsonSerializable(typeof(CompletionItem))]
[JsonSerializable(typeof(TextDocumentPositionParams))]
[JsonSerializable(typeof(Hover))]
[JsonSerializable(typeof(SignatureHelpParams))]
[JsonSerializable(typeof(SignatureHelp))]
[JsonSerializable(typeof(Location[]))]
[JsonSerializable(typeof(DocumentHighlight[]))]
[JsonSerializable(typeof(FoldingRangeParams))]
[JsonSerializable(typeof(FoldingRange[]))]
[JsonSerializable(typeof(SemanticTokensParams))]
[JsonSerializable(typeof(SemanticTokensRangeParams))]
[JsonSerializable(typeof(SemanticTokens))]
[JsonSerializable(typeof(LogMessageParams))]
[JsonSerializable(typeof(DidChangeConfigurationParams))]
[JsonSerializable(typeof(ProjectsUpdateParams))]
[JsonSerializable(typeof(ProjectsRemoveParams))]
[JsonSerializable(typeof(DocumentOptionsParams))]
[JsonSerializable(typeof(DocumentOptions))]
[JsonSerializable(typeof(SourceGeneratedTextParams))]
[JsonSerializable(typeof(SourceGeneratedText))]
[JsonSerializable(typeof(WorkspaceStatusParams))]
[JsonSerializable(typeof(EmptyObject))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class ProtocolJsonContext : JsonSerializerContext;

/// <summary><c>{}</c> (params of <c>initialized</c>, <c>shutdown</c>, <c>exit</c>, and empty results).</summary>
public sealed record EmptyObject;
