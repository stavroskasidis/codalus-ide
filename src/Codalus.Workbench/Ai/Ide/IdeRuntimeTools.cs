namespace Codalus.Workbench.Ai.Ide;

/// <summary>M60's tools for agents: Hot Reload (Apply Code Changes) and profiling of the program being run or debugged.</summary>
public static class IdeRuntimeTools
{
    /// <summary>The group's tools, in the order agents see them.</summary>
    public static IReadOnlyList<IdeToolDefinition> Tools { get; } = [];
}
