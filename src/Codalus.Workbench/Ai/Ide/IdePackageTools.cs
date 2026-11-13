namespace Codalus.Workbench.Ai.Ide;

/// <summary>M60's tools for agents: EF Core migrations (through the user's dotnet-ef) and NuGet packages (through the package manager's one path, with its feeds and credentials).</summary>
public static class IdePackageTools
{
    /// <summary>The group's tools, in the order agents see them.</summary>
    public static IReadOnlyList<IdeToolDefinition> Tools { get; } = [];
}
