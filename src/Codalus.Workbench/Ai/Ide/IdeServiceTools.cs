namespace Codalus.Workbench.Ai.Ide;

/// <summary>M60's tools for agents: Compose services and Aspire resources: listed with their state, started, stopped, restarted or debugged one at a time, and their logs.</summary>
public static class IdeServiceTools
{
    /// <summary>The group's tools, in the order agents see them.</summary>
    public static IReadOnlyList<IdeToolDefinition> Tools { get; } = [];
}
