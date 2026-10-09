using System.Text.Json.Serialization;

namespace Daedalus.Shell.Docking;

/// <summary>
/// The persisted dock layout (<c>layout.json</c> in the state directory). Tool windows are stored by id and
/// resolved through the tool-window registry on restore, so windows of uninstalled plugins simply drop out.
/// </summary>
public sealed record LayoutSnapshot(int Version, LayoutNode Root)
{
    public const int CurrentVersion = 1;

    public IReadOnlyList<string>? LeftPinned { get; init; }

    public IReadOnlyList<string>? RightPinned { get; init; }

    public IReadOnlyList<string>? TopPinned { get; init; }

    public IReadOnlyList<string>? BottomPinned { get; init; }

    public WindowPlacement? Window { get; init; }
}

/// <summary>A node of the layout tree. <see cref="Kind"/> is <c>proportional</c>, <c>splitter</c>, <c>tools</c> or <c>documents</c>.</summary>
public sealed record LayoutNode(string Kind)
{
    public string? Id { get; init; }

    /// <summary><c>horizontal</c> or <c>vertical</c> (proportional docks).</summary>
    public string? Orientation { get; init; }

    /// <summary><c>left</c>, <c>right</c>, <c>top</c> or <c>bottom</c> (tool docks).</summary>
    public string? Alignment { get; init; }

    /// <summary>Share of the parent; null means automatic.</summary>
    public double? Proportion { get; init; }

    public string? Active { get; init; }

    public IReadOnlyList<string>? Tools { get; init; }

    public IReadOnlyList<LayoutNode>? Children { get; init; }
}

public sealed record WindowPlacement(double Width, double Height, int? X, int? Y, bool Maximized);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(LayoutSnapshot))]
[JsonSerializable(typeof(ShellState))]
[JsonSerializable(typeof(EditorSession))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ShellJsonContext : JsonSerializerContext;

/// <summary>The documents open in a solution, reopened with it (<c>sessions/&lt;hash&gt;.json</c> in the state folder).</summary>
public sealed record EditorSession(IReadOnlyList<SessionDocument> Documents, string? Active)
{
    /// <summary>The startup project the user chose (null: the default, the first executable project).</summary>
    public string? StartupProject { get; init; }

    /// <summary>The launch profile chosen per project path.</summary>
    public Dictionary<string, string>? LaunchProfiles { get; init; }
}

/// <summary>An open file and its caret (zero-based line and character).</summary>
public sealed record SessionDocument(string Path, int Line, int Character);

/// <summary>Small bits of per-user state that survive restarts (<c>state.json</c>).</summary>
public sealed record ShellState
{
    public IReadOnlyList<string>? RecentCommands { get; init; }
}
