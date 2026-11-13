namespace Codalus.Workbench.Ai.Ide;

/// <summary>M60's tools for agents: Code changes: rename, code actions (fixes and refactorings), formatting and replace in files. Each change is one undo step.</summary>
public static class IdeCodeChangeTools
{
    /// <summary>The group's tools, in the order agents see them.</summary>
    public static IReadOnlyList<IdeToolDefinition> Tools { get; } = [];
}
