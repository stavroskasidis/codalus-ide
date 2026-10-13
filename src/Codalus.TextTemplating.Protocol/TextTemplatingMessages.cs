using System.Text.Json.Serialization;

namespace Codalus.TextTemplating.Protocol;

/// <summary>Shell ↔ <c>codalus-t4host</c> (ADR 0035): JSON-RPC 2.0 with LSP framing over stdio.</summary>
public static class TextTemplatingMethods
{
    public const string ProtocolVersion = "0.1";

    public const string Initialize = "initialize";
    public const string Transform = "t4/transform";
    public const string Shutdown = "shutdown";
    public const string Exit = "exit";
}

public sealed record T4InitializeParams(int ProcessId, string ProtocolVersion);

public sealed record T4InitializeResult(string ServerName, string ServerVersion, string ProtocolVersion);

/// <summary>
/// Runs a template. <see cref="Content"/> is the template's text (the file on disk when null); <see cref="Namespace"/>
/// is offered to the template as <c>Host.ResolveParameterValue("", "", "namespace")</c> and the <c>namespace</c> parameter;
/// <see cref="IncludeFolders"/> and <see cref="ReferenceFolders"/> are searched for includes and assemblies after the
/// template's own folder.
/// </summary>
public sealed record T4TransformParams(string TemplatePath, string? Content, string? Namespace, string[]? IncludeFolders = null, string[]? ReferenceFolders = null, Dictionary<string, string>? Parameters = null);

/// <summary>The generated text and its extension (from the <c>output</c> directive, <c>.cs</c> by default), and the template's errors.</summary>
public sealed record T4TransformResult(string? Output, string Extension, T4Diagnostic[] Diagnostics);

/// <summary>An error or warning of a template: its file and 1-based line and column (0 when unknown).</summary>
public sealed record T4Diagnostic(bool IsWarning, string Message, string? File, int Line, int Column);

public sealed record T4Empty;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(T4InitializeParams))]
[JsonSerializable(typeof(T4InitializeResult))]
[JsonSerializable(typeof(T4TransformParams))]
[JsonSerializable(typeof(T4TransformResult))]
[JsonSerializable(typeof(T4Empty))]
public sealed partial class TextTemplatingJsonContext : JsonSerializerContext;
