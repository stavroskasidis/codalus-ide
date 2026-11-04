using System.Text.Json;
using Codalus.Workbench.Ai.Ide;

namespace Codalus.Shell;

/// <summary>Plugins' AI tools (ADR 0092): declared in a manifest's <c>contributes.aiTools</c>, served by the plugin's host.</summary>
public sealed partial class ShellHost
{
    private IReadOnlyList<IdeToolDefinition> PluginIdeTools() => [];

    private Task<IdeToolResult> CallPluginIdeToolAsync(IdeToolDefinition definition, JsonElement args, CancellationToken cancellationToken) =>
        Task.FromResult(IdeToolResult.Error($"There is no tool '{definition.Name}'."));
}
