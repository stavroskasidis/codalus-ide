using System.Text.Json;
using Codalus.Workbench.Ai.Ide;

namespace Codalus.Shell;

/// <summary>
/// Plugins' tools for AI agents (proposed, ADR 0092): declared in a manifest's <c>contributes.aiTools</c>, listed beside
/// the IDE's tools, and served by the plugin's host after the user allows each call.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>The plugins' AI tools.</summary>
    public PluginAiTools PluginAiTools { get; private set; } = null!;

    private IReadOnlyList<IdeToolDefinition> PluginIdeTools() => PluginAiTools?.Definitions ?? [];

    private Task<IdeToolResult> CallPluginIdeToolAsync(IdeToolDefinition definition, JsonElement args, CancellationToken cancellationToken) =>
        PluginAiTools.CallAsync(definition.Name, args, cancellationToken);
}
