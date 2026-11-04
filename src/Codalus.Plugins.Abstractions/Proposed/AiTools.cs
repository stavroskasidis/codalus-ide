using System.Text.Json;

namespace Codalus.Plugins.Proposed;

/// <summary>
/// Tools for AI agents (proposed, ADR 0092): a plugin gives AI Chat's agents (the built-in one and external agents) and
/// agents that reach the IDE through <c>codalus mcp</c> a tool of its own, beside the IDE's. A tool is declared in the
/// manifest (<c>contributes.aiTools</c>: its name, description and input schema), so agents see it before the plugin runs,
/// and registered when its plugin activates (<c>onAiTool:&lt;name&gt;</c>, implicit), at its first call. The user allows
/// every call first, and what the tool returns is redacted before an agent sees it.
/// </summary>
public interface IAiTools
{
    /// <summary>Registers the tool whose <see cref="AiTool.Name"/> the manifest declares. Dispose to unregister.</summary>
    IDisposable RegisterTool(AiTool tool);
}

/// <summary>
/// A tool for AI agents. Calls come on any thread and are cancelled when the agent's turn is stopped. The arguments
/// follow the manifest's input schema, but come from a model: check them.
/// </summary>
public abstract class AiTool
{
    /// <summary>Creates the tool the manifest's <c>aiTools[].name</c> declares.</summary>
    protected AiTool(string name) => Name = name;

    /// <summary>The name in the manifest, e.g. <c>lint_sql</c>.</summary>
    public string Name { get; }

    /// <summary>Runs the tool. Throw <see cref="AiToolException"/> for a failure the agent should read.</summary>
    public abstract Task<AiToolResult> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>What a tool answers: text for the model, and whether it failed.</summary>
/// <param name="Text">The answer; the IDE redacts secrets in it.</param>
/// <param name="IsError">Whether the call failed (the text says why).</param>
public sealed record AiToolResult(string Text, bool IsError = false);

/// <summary>A tool's failure, which the agent reads as the call's error.</summary>
public sealed class AiToolException : Exception
{
    /// <summary>A failure without a message.</summary>
    public AiToolException()
    {
    }

    /// <summary>A failure the agent reads as <paramref name="message"/>.</summary>
    public AiToolException(string message)
        : base(message)
    {
    }

    /// <summary>A failure the agent reads as <paramref name="message"/>, caused by <paramref name="innerException"/>.</summary>
    public AiToolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
