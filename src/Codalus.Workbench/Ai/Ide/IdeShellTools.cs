namespace Codalus.Workbench.Ai.Ide;

/// <summary>M60's tools for agents: The IDE itself: its commands (under each command's own enablement, never the ones agents don't get), its settings, a picture of a window, and the integrated terminal.</summary>
public static class IdeShellTools
{
    /// <summary>The group's tools, in the order agents see them.</summary>
    public static IReadOnlyList<IdeToolDefinition> Tools { get; } = [];
}
