using System.Text.Json;
using Codalus.Workbench.Ai.Ide;

namespace Codalus.Shell;

/// <summary>
/// The IDE's tools for agents, round 2 (M60, ADR 0099): each group served by a file of its own
/// (<c>ShellHost.IdeTools.&lt;Group&gt;.cs</c>), behind round 1's dispatcher and its checks.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>A round-2 tool's answer, or null when <paramref name="tool"/> isn't one.</summary>
    private async Task<IdeToolResult?> Round2IdeToolAsync(string tool, JsonElement args, string? root, CancellationToken cancellationToken)
    {
        static bool In(IReadOnlyList<IdeToolDefinition> group, string name) => group.Any(t => t.Name == name);

        if (In(IdeCodeChangeTools.Tools, tool))
        {
            return await CodeChangeToolAsync(tool, args, root, cancellationToken).ConfigureAwait(true);
        }

        if (In(IdeRuntimeTools.Tools, tool))
        {
            return await RuntimeToolAsync(tool, args, root, cancellationToken).ConfigureAwait(true);
        }

        if (In(IdeServiceTools.Tools, tool))
        {
            return await ServiceToolAsync(tool, args, root, cancellationToken).ConfigureAwait(true);
        }

        if (In(IdePackageTools.Tools, tool))
        {
            return await PackageToolAsync(tool, args, root, cancellationToken).ConfigureAwait(true);
        }

        if (In(IdeGitTools.Tools, tool))
        {
            return await GitToolAsync(tool, args, root, cancellationToken).ConfigureAwait(true);
        }

        if (In(IdeShellTools.Tools, tool))
        {
            return await ShellToolAsync(tool, args, root, cancellationToken).ConfigureAwait(true);
        }

        if (In(IdeDesktopAppTools.Tools, tool))
        {
            return await DesktopAppToolAsync(tool, args, cancellationToken).ConfigureAwait(true);
        }

        return null;
    }
}
