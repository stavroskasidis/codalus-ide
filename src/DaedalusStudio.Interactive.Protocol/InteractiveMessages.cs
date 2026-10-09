using System.Text.Json;
using System.Text.Json.Serialization;
using DaedalusStudio.LanguageServices.Protocol;

namespace DaedalusStudio.Interactive.Protocol;

/// <summary>
/// The C# Interactive host protocol (ADR 0015, docs/interactive-protocol.md): JSON-RPC over stdio between the shell and
/// <c>daedalus-replhost</c>. The host keeps one script state; positions are UTF-16 offsets into the submitted text.
/// </summary>
public static class InteractiveMethods
{
    /// <summary>Starts a new script state (references, usings, <c>#load</c> files). Also the first request after a (re)start.</summary>
    public const string Reset = "repl/reset";

    /// <summary>Runs one submission on the current state.</summary>
    public const string Execute = "repl/execute";

    /// <summary>Host → shell notification: text a submission wrote to <c>Console.Out</c> or <c>Console.Error</c>.</summary>
    public const string Output = "repl/output";

    /// <summary>Whether the text is a complete submission (Enter runs it) or needs more lines.</summary>
    public const string IsComplete = "repl/isComplete";

    public const string Completion = "repl/completion";

    public const string SignatureHelp = "repl/signatureHelp";

    /// <summary>Semantic tokens of a pending submission, LSP-encoded with the legend of <see cref="TokenTypes"/>.</summary>
    public const string Tokens = "repl/tokens";

    public const string Shutdown = "shutdown";

    public const string Exit = "exit";
}

/// <summary>
/// <see cref="References"/> are assembly paths (or <c>nuget:Id, Version</c>); <see cref="Usings"/> are namespaces;
/// <see cref="LoadFiles"/> are <c>.csx</c> files run first, as <c>#load</c> would.
/// </summary>
public sealed record ResetParams(string WorkingDirectory, string[] References, string[] Usings, string[] LoadFiles);

/// <summary><see cref="Errors"/> lists references and load files that failed; the state is usable regardless.</summary>
public sealed record ResetResult(string Runtime, string[] Errors);

public sealed record ExecuteParams(int Submission, string Code);

/// <summary>
/// The outcome of a submission. <see cref="Value"/> is the formatted value of a trailing expression (null when the
/// submission has none); <see cref="Error"/> is the formatted exception when it threw; compile errors only fill
/// <see cref="Diagnostics"/>.
/// </summary>
public sealed record ExecuteResult(bool Success, string? Value, string? Error, ReplDiagnostic[] Diagnostics, double ElapsedMilliseconds);

/// <summary>A compiler diagnostic; <see cref="Line"/> and <see cref="Character"/> are zero-based, in the submission.</summary>
public sealed record ReplDiagnostic(string Id, string Severity, string Message, int Line, int Character);

public sealed record OutputParams(int Submission, string Text, bool IsError);

public sealed record CodeParams(string Code);

public sealed record IsCompleteResult(bool Complete);

public sealed record CodePositionParams(string Code, int Position);

/// <summary>Items replace the text from <see cref="Start"/> to the requested position.</summary>
public sealed record ReplCompletionResult(int Start, CompletionItem[] Items);

public sealed record TokensResult(int[] Data);

/// <summary>Source-generated serialization metadata (Native AOT in the shell).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ResetParams))]
[JsonSerializable(typeof(ResetResult))]
[JsonSerializable(typeof(ExecuteParams))]
[JsonSerializable(typeof(ExecuteResult))]
[JsonSerializable(typeof(OutputParams))]
[JsonSerializable(typeof(CodeParams))]
[JsonSerializable(typeof(IsCompleteResult))]
[JsonSerializable(typeof(CodePositionParams))]
[JsonSerializable(typeof(ReplCompletionResult))]
[JsonSerializable(typeof(SignatureHelp))]
[JsonSerializable(typeof(TokensResult))]
[JsonSerializable(typeof(EmptyObject))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class InteractiveJsonContext : JsonSerializerContext;
