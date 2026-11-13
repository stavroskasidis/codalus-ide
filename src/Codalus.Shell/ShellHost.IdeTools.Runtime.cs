using System.Text.Json;
using Codalus.Workbench.Ai.Ide;
using Codalus.Workbench.Ai.Mcp;

namespace Codalus.Shell;

/// <summary>M60's tools for agents: the shell's side of <see cref="IdeRuntimeTools"/>.</summary>
public sealed partial class ShellHost
{
#pragma warning disable CA1822, IDE0060 // filled in by the group's tools
    private Task<IdeToolResult> RuntimeToolAsync(string tool, JsonElement args, string? root, CancellationToken cancellationToken) =>
        throw new McpException($"There is no tool '{tool}'.");
#pragma warning restore CA1822, IDE0060
}
