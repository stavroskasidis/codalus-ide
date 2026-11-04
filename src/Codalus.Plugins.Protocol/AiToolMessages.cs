using System.Text.Json;

namespace Codalus.Plugins.Protocol;

// Tools for AI agents (docs/plugin-protocol.md#ai-tools, ADR 0092). Proposed.

/// <summary>Method names of the AI tools API (proposed).</summary>
public static class AiToolMethods
{
    // host → shell (notifications)
    public const string RegisterTool = "aiTools/registerTool";
    public const string UnregisterTool = "aiTools/unregisterTool";

    // shell → host (requests)
    public const string Invoke = "aiTools/invoke";
}

/// <summary>JSON-RPC error codes of the AI tool requests.</summary>
public static class AiToolErrorCodes
{
    /// <summary>No such tool is registered (its plugin isn't active).</summary>
    public const int NoTool = -33030;

    /// <summary>The tool failed (the message says why).</summary>
    public const int Failed = -33031;
}

/// <summary><c>aiTools/registerTool</c> and <c>unregisterTool</c>.</summary>
public sealed record AiToolParams(string PluginId, string Name);

/// <summary><c>aiTools/invoke</c>: the tool and its arguments (an object).</summary>
public sealed record AiToolInvokeParams(string Name, JsonElement Arguments);

/// <summary>What a tool answers: text for the model, and whether it failed.</summary>
public sealed record AiToolResultDto(string Text, bool IsError);
