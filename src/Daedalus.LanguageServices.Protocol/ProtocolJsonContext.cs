using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daedalus.LanguageServices.Protocol;

/// <summary>Source-generated serialization metadata so the protocol works under Native AOT and trimming.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(OpenSolutionParams))]
[JsonSerializable(typeof(OpenSolutionResult))]
[JsonSerializable(typeof(DidOpenParams))]
[JsonSerializable(typeof(DidChangeParams))]
[JsonSerializable(typeof(DidCloseParams))]
[JsonSerializable(typeof(SemanticTokensParams))]
[JsonSerializable(typeof(SemanticTokensRangeParams))]
[JsonSerializable(typeof(SemanticTokens))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class ProtocolJsonContext : JsonSerializerContext;
