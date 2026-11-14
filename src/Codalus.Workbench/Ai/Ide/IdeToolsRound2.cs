namespace Codalus.Workbench.Ai.Ide;

/// <summary>
/// The IDE's tools for agents, round 2 (M60, ADR 0099), by group: each group's definitions live in a class of its own,
/// and the shell serves each group in a file of its own (<c>ShellHost.IdeTools.&lt;Group&gt;.cs</c>). Every tool passes
/// the same dispatcher as round 1's: paths confined to the workspace, excluded files left out, answers redacted and cut
/// to size, a line in the AI Output channel per call, and actions approved as <c>ai.agent.ideActions</c> says.
/// </summary>
public static class IdeToolsRound2
{
    /// <summary>Every round-2 tool, in the order agents see them.</summary>
    public static IReadOnlyList<IdeToolDefinition> All { get; } =
    [
        .. IdeCodeChangeTools.Tools,
        .. IdeRuntimeTools.Tools,
        .. IdeServiceTools.Tools,
        .. IdePackageTools.Tools,
        .. IdeGitTools.Tools,
        .. IdeShellTools.Tools,
        .. IdeDesktopAppTools.Tools,
    ];
}
