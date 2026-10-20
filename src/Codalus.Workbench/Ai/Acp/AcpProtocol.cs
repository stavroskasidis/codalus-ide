using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codalus.Workbench.Ai.Acp;

// The Agent Client Protocol's messages that Codalus IDE uses as a client (M48, ADR 0079), hand-written from the official
// JSON schema: protocol version 1, schema/v1/schema.json of agent-client-protocol 1.12.0 (2026-10-16). Only the fields
// the IDE reads or writes are here; unknown fields are ignored, and serialization is source-generated (Native AOT).
// Outgoing messages are records; incoming ones are classes with optional members, so a field an agent leaves out reads
// as its default rather than failing.

/// <summary>The protocol version and schema these types follow.</summary>
public static class AcpVersion
{
    /// <summary>The ACP major version Codalus speaks (<c>initialize</c>'s <c>protocolVersion</c>).</summary>
    public const int Protocol = 1;

    /// <summary>The schema release the types were written from.</summary>
    public const string Schema = "1.12.0";
}

/// <summary>An empty object (<c>{}</c>): a capability with no options, or a result with no fields.</summary>
public sealed class AcpEmpty
{
    public static AcpEmpty Instance { get; } = new();
}

// ------------------------------------------------------------------ initialization

public sealed record AcpImplementation(string Name, string? Title, string Version);

public sealed record AcpFileSystemCapabilities(bool ReadTextFile, bool WriteTextFile);

public sealed record AcpAuthCapabilities(bool Terminal);

/// <summary>The session updates the client can show beyond the baseline: compaction and notices.</summary>
public sealed record AcpClientSessionCapabilities(AcpEmpty? Compaction, AcpEmpty? Notices);

/// <summary>What Codalus offers the agent. Elicitation is left out (null), so the agent doesn't ask for forms.</summary>
public sealed record AcpClientCapabilities(AcpFileSystemCapabilities Fs, bool Terminal, AcpClientSessionCapabilities? Session, AcpAuthCapabilities Auth);

public sealed record AcpInitializeRequest(int ProtocolVersion, AcpClientCapabilities ClientCapabilities, AcpImplementation ClientInfo);

public sealed class AcpPromptCapabilities
{
    public bool Image { get; init; }

    public bool Audio { get; init; }

    public bool EmbeddedContext { get; init; }
}

public sealed class AcpMcpCapabilities
{
    public bool Http { get; init; }

    public bool Sse { get; init; }
}

public sealed class AcpAgentCapabilities
{
    public bool LoadSession { get; init; }

    public AcpPromptCapabilities? PromptCapabilities { get; init; }

    public AcpMcpCapabilities? McpCapabilities { get; init; }
}

/// <summary>A way to sign in: <c>agent</c> (the default; the agent runs it on <c>authenticate</c>) or <c>terminal</c>.</summary>
public sealed class AcpAuthMethod
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    public string? Description { get; init; }

    public string? Type { get; init; }

    public IReadOnlyList<string>? Args { get; init; }

    public bool IsTerminal => Type == "terminal";
}

public sealed class AcpInitializeResponse
{
    public int ProtocolVersion { get; init; }

    public AcpAgentCapabilities? AgentCapabilities { get; init; }

    public IReadOnlyList<AcpAuthMethod>? AuthMethods { get; init; }

    public AcpImplementation? AgentInfo { get; init; }
}

public sealed record AcpAuthenticateRequest(string MethodId);

// ------------------------------------------------------------------ sessions

public sealed record AcpEnvVariable(string Name, string Value);

/// <summary>An MCP server the agent starts over stdio (the stdio variant of <c>McpServer</c> has no <c>type</c>).</summary>
public sealed record AcpMcpServerStdio(string Name, string Command, IReadOnlyList<string> Args, IReadOnlyList<AcpEnvVariable> Env);

public sealed record AcpNewSessionRequest(string Cwd, IReadOnlyList<AcpMcpServerStdio> McpServers);

public sealed class AcpSessionMode
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    public string? Description { get; init; }
}

public sealed class AcpSessionModeState
{
    public string CurrentModeId { get; init; } = "";

    public IReadOnlyList<AcpSessionMode>? AvailableModes { get; init; }
}

public sealed class AcpNewSessionResponse
{
    public string SessionId { get; init; } = "";

    public AcpSessionModeState? Modes { get; init; }
}

public sealed record AcpSetModeRequest(string SessionId, string ModeId);

// ------------------------------------------------------------------ prompt turns

/// <summary>A content block (<c>text</c>, <c>image</c>, <c>audio</c>, <c>resource_link</c>, <c>resource</c>), flattened.</summary>
public sealed class AcpContentBlock
{
    public string Type { get; init; } = "text";

    public string? Text { get; init; }

    public string? Uri { get; init; }

    public string? Name { get; init; }

    public string? Title { get; init; }

    public string? MimeType { get; init; }

    public AcpEmbeddedResource? Resource { get; init; }

    public static AcpContentBlock FromText(string text) => new() { Type = "text", Text = text };
}

public sealed class AcpEmbeddedResource
{
    public string? Uri { get; init; }

    public string? Text { get; init; }

    public string? MimeType { get; init; }
}

public sealed record AcpPromptRequest(string SessionId, IReadOnlyList<AcpContentBlock> Prompt);

public sealed class AcpPromptResponse
{
    /// <summary><c>end_turn</c>, <c>max_tokens</c>, <c>max_turn_requests</c>, <c>refusal</c> or <c>cancelled</c>.</summary>
    public string StopReason { get; init; } = "end_turn";
}

public sealed record AcpCancelNotification(string SessionId);

/// <summary><c>session/update</c>: the update is read by its <c>sessionUpdate</c> discriminator.</summary>
public sealed class AcpSessionNotification
{
    public string SessionId { get; init; } = "";

    public JsonElement Update { get; init; }
}

/// <summary><c>user_message_chunk</c>, <c>agent_message_chunk</c>, <c>agent_thought_chunk</c>.</summary>
public sealed class AcpContentChunk
{
    public AcpContentBlock? Content { get; init; }
}

public sealed class AcpToolCallLocation
{
    public string Path { get; init; } = "";

    public int? Line { get; init; }
}

/// <summary>A tool call's content (<c>content</c>, <c>diff</c>, <c>terminal</c>), flattened.</summary>
public sealed class AcpToolCallContent
{
    public string Type { get; init; } = "content";

    public AcpContentBlock? Content { get; init; }

    public string? Path { get; init; }

    public string? OldText { get; init; }

    public string? NewText { get; init; }

    public string? TerminalId { get; init; }
}

/// <summary><c>tool_call</c> and <c>tool_call_update</c> (whose fields are all optional but the id), and a permission's tool call.</summary>
public sealed class AcpToolCall
{
    public string ToolCallId { get; init; } = "";

    public string? Title { get; init; }

    /// <summary><c>read</c>, <c>edit</c>, <c>delete</c>, <c>move</c>, <c>search</c>, <c>execute</c>, <c>think</c>, <c>fetch</c>, <c>switch_mode</c>, <c>other</c>.</summary>
    public string? Kind { get; init; }

    /// <summary><c>pending</c>, <c>in_progress</c>, <c>completed</c>, <c>failed</c>.</summary>
    public string? Status { get; init; }

    public IReadOnlyList<AcpToolCallContent>? Content { get; init; }

    public IReadOnlyList<AcpToolCallLocation>? Locations { get; init; }

    public JsonElement? RawInput { get; init; }
}

public sealed class AcpPlanEntry
{
    public string Content { get; init; } = "";

    public string? Priority { get; init; }

    /// <summary><c>pending</c>, <c>in_progress</c>, <c>completed</c>.</summary>
    public string? Status { get; init; }
}

public sealed class AcpPlan
{
    public IReadOnlyList<AcpPlanEntry>? Entries { get; init; }
}

public sealed class AcpCommandInput
{
    public string? Hint { get; init; }
}

public sealed class AcpAvailableCommand
{
    public string Name { get; init; } = "";

    public string? Description { get; init; }

    public AcpCommandInput? Input { get; init; }
}

public sealed class AcpAvailableCommandsUpdate
{
    public IReadOnlyList<AcpAvailableCommand>? AvailableCommands { get; init; }
}

public sealed class AcpCurrentModeUpdate
{
    public string CurrentModeId { get; init; } = "";
}

public sealed class AcpNotice
{
    public string? Severity { get; init; }

    public string? Title { get; init; }
}

public sealed class AcpCompactionUpdate
{
    public string CompactionId { get; init; } = "";

    public string? Status { get; init; }

    public string? Error { get; init; }
}

// ------------------------------------------------------------------ the client's methods

public sealed class AcpPermissionOption
{
    public string OptionId { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary><c>allow_once</c>, <c>allow_always</c>, <c>reject_once</c>, <c>reject_always</c>.</summary>
    public string Kind { get; init; } = "";
}

public sealed class AcpRequestPermissionRequest
{
    public string SessionId { get; init; } = "";

    public AcpToolCall? ToolCall { get; init; }

    public IReadOnlyList<AcpPermissionOption>? Options { get; init; }
}

/// <summary><c>{ "outcome": "cancelled" }</c> or <c>{ "outcome": "selected", "optionId": … }</c>.</summary>
public sealed record AcpPermissionOutcome(string Outcome, string? OptionId)
{
    public static AcpPermissionOutcome Cancelled { get; } = new("cancelled", null);

    public static AcpPermissionOutcome Selected(string optionId) => new("selected", optionId);
}

public sealed record AcpRequestPermissionResponse(AcpPermissionOutcome Outcome);

public sealed class AcpReadTextFileRequest
{
    public string SessionId { get; init; } = "";

    public string Path { get; init; } = "";

    /// <summary>The first line to read, 1-based.</summary>
    public int? Line { get; init; }

    public int? Limit { get; init; }
}

public sealed record AcpReadTextFileResponse(string Content);

public sealed class AcpWriteTextFileRequest
{
    public string SessionId { get; init; } = "";

    public string Path { get; init; } = "";

    public string Content { get; init; } = "";
}

public sealed class AcpCreateTerminalRequest
{
    public string SessionId { get; init; } = "";

    public string Command { get; init; } = "";

    public IReadOnlyList<string>? Args { get; init; }

    public IReadOnlyList<AcpEnvVariable>? Env { get; init; }

    public string? Cwd { get; init; }

    public long? OutputByteLimit { get; init; }
}

public sealed record AcpCreateTerminalResponse(string TerminalId);

/// <summary><c>terminal/output</c>, <c>wait_for_exit</c>, <c>kill</c> and <c>release</c>.</summary>
public sealed class AcpTerminalRequest
{
    public string SessionId { get; init; } = "";

    public string TerminalId { get; init; } = "";
}

public sealed record AcpTerminalExitStatus(int? ExitCode, string? Signal);

public sealed record AcpTerminalOutputResponse(string Output, bool Truncated, AcpTerminalExitStatus? ExitStatus);

/// <summary>The protocol's method names (schema/v1/meta.json).</summary>
public static class AcpMethods
{
    public const string Initialize = "initialize";
    public const string Authenticate = "authenticate";
    public const string SessionNew = "session/new";
    public const string SessionPrompt = "session/prompt";
    public const string SessionCancel = "session/cancel";
    public const string SessionSetMode = "session/set_mode";
    public const string SessionUpdate = "session/update";
    public const string SessionRequestPermission = "session/request_permission";
    public const string ReadTextFile = "fs/read_text_file";
    public const string WriteTextFile = "fs/write_text_file";
    public const string TerminalCreate = "terminal/create";
    public const string TerminalOutput = "terminal/output";
    public const string TerminalWaitForExit = "terminal/wait_for_exit";
    public const string TerminalKill = "terminal/kill";
    public const string TerminalRelease = "terminal/release";

    /// <summary>The error code of <c>auth_required</c>.</summary>
    public const int AuthRequired = -32000;

    /// <summary>The error code of a resource (a file) that doesn't exist.</summary>
    public const int ResourceNotFound = -32002;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AcpInitializeRequest))]
[JsonSerializable(typeof(AcpInitializeResponse))]
[JsonSerializable(typeof(AcpAuthenticateRequest))]
[JsonSerializable(typeof(AcpNewSessionRequest))]
[JsonSerializable(typeof(AcpNewSessionResponse))]
[JsonSerializable(typeof(AcpSetModeRequest))]
[JsonSerializable(typeof(AcpPromptRequest))]
[JsonSerializable(typeof(AcpPromptResponse))]
[JsonSerializable(typeof(AcpCancelNotification))]
[JsonSerializable(typeof(AcpSessionNotification))]
[JsonSerializable(typeof(AcpContentChunk))]
[JsonSerializable(typeof(AcpToolCall))]
[JsonSerializable(typeof(AcpPlan))]
[JsonSerializable(typeof(AcpAvailableCommandsUpdate))]
[JsonSerializable(typeof(AcpCurrentModeUpdate))]
[JsonSerializable(typeof(AcpNotice))]
[JsonSerializable(typeof(AcpCompactionUpdate))]
[JsonSerializable(typeof(AcpRequestPermissionRequest))]
[JsonSerializable(typeof(AcpRequestPermissionResponse))]
[JsonSerializable(typeof(AcpReadTextFileRequest))]
[JsonSerializable(typeof(AcpReadTextFileResponse))]
[JsonSerializable(typeof(AcpWriteTextFileRequest))]
[JsonSerializable(typeof(AcpCreateTerminalRequest))]
[JsonSerializable(typeof(AcpCreateTerminalResponse))]
[JsonSerializable(typeof(AcpTerminalRequest))]
[JsonSerializable(typeof(AcpTerminalOutputResponse))]
[JsonSerializable(typeof(AcpTerminalExitStatus))]
[JsonSerializable(typeof(AcpEmpty))]
public sealed partial class AcpJsonContext : JsonSerializerContext;
