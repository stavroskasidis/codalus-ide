namespace Codalus.Workbench.Ai.Ide;

/// <summary>M60's tools for agents: Git: status, diffs, history and blame, staging, commits, stashes, branches and conflicts. Never a push or a forced operation.</summary>
public static class IdeGitTools
{
    /// <summary>The group's tools, in the order agents see them.</summary>
    public static IReadOnlyList<IdeToolDefinition> Tools { get; } = [];
}
