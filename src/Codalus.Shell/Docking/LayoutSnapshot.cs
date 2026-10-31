using System.Text.Json.Serialization;

namespace Codalus.Shell.Docking;

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
[JsonSerializable(typeof(Dictionary<string, LayoutSnapshot>))]
[JsonSerializable(typeof(ShellState))]
[JsonSerializable(typeof(EditorSession))]
[JsonSerializable(typeof(BreakpointExport))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
internal sealed partial class ShellJsonContext : JsonSerializerContext;

/// <summary>
/// What a solution reopens with: its documents, breakpoints, startup projects, configuration, Solution Explorer's
/// expansion… (<c>.codalus/state/&lt;solution file name&gt;/session.json</c> next to the solution, ADR 0070).
/// </summary>
public sealed record EditorSession(IReadOnlyList<SessionDocument> Documents, string? Active)
{
    /// <summary>The startup project the user chose (null: the default, the first executable project).</summary>
    public string? StartupProject { get; init; }

    /// <summary>The launch profile chosen per project path.</summary>
    public Dictionary<string, string>? LaunchProfiles { get; init; }

    /// <summary>The device each mobile platform's runs go to (M20a): platform → device id (an emulator as <c>avd:&lt;name&gt;</c>).</summary>
    public Dictionary<string, string>? MobileDevices { get; init; }

    public IReadOnlyList<SessionBreakpoint>? Breakpoints { get; init; }

    public IReadOnlyList<SessionFunctionBreakpoint>? FunctionBreakpoints { get; init; }

    /// <summary>Whether exception types that aren't listed break when thrown (ADR 0021).</summary>
    public bool? BreakOnOtherExceptions { get; init; }

    /// <summary>The ASP.NET Core environment chosen for runs (M15a); null: the launch profile's.</summary>
    public string? AspNetEnvironment { get; init; }

    /// <summary>Exception types whose setting differs from the defaults: type → break when thrown.</summary>
    public Dictionary<string, bool>? ExceptionTypes { get; init; }

    /// <summary>Module conditions of exception types (M14b): type → module names.</summary>
    public Dictionary<string, string>? ExceptionConditions { get; init; }

    /// <summary>Watch window expressions.</summary>
    public IReadOnlyList<string>? Watches { get; init; }

    /// <summary>Pinned data tips (M14b).</summary>
    public IReadOnlyList<SessionDataTip>? DataTips { get; init; }

    /// <summary>Whether several startup projects start together (M14c); null or false: the single startup project.</summary>
    public bool? MultipleStartupProjects { get; init; }

    /// <summary>The startup projects' actions, in start order (M14c).</summary>
    public IReadOnlyList<SessionStartupProject>? StartupProjects { get; init; }

    /// <summary>The active solution configuration (ADR 0070); reopened with it while the solution still has it.</summary>
    public string? Configuration { get; init; }

    /// <summary>The active solution platform, with <see cref="Configuration"/>.</summary>
    public string? Platform { get; init; }

    /// <summary>The target framework the editor uses per multi-targeted project (the navigation bar's project list).</summary>
    public Dictionary<string, string>? ActiveFrameworks { get; init; }

    /// <summary>Solution Explorer's expanded nodes, by node id (ADR 0070).</summary>
    public IReadOnlyList<string>? ExpandedNodes { get; init; }

    /// <summary>The solution and solution folders the user collapsed (they start expanded).</summary>
    public IReadOnlyList<string>? CollapsedNodes { get; init; }

    /// <summary>The Error List's filters.</summary>
    public SessionErrorList? ErrorList { get; init; }

    /// <summary>The Test Explorer's grouping (a <c>TestGrouping</c> name).</summary>
    public string? TestGrouping { get; init; }

    /// <summary>The solution's repositories the user added or left out, and the one Git Changes shows (ADR 0085).</summary>
    public SessionGitRepositories? GitRepositories { get; init; }
}

/// <summary>
/// A solution's repositories as the user changed them (ADR 0085): <see cref="Added"/> ones with no project in it,
/// <see cref="Removed"/> ones it found, and the one Git Changes shows (<see cref="Shown"/>; null: all of them).
/// </summary>
public sealed record SessionGitRepositories
{
    public IReadOnlyList<string>? Added { get; init; }

    public IReadOnlyList<string>? Removed { get; init; }

    public string? Shown { get; init; }
}

/// <summary>The Error List's filters, kept with the solution: what it shows and from which source (0: build and IntelliSense, 1: build, 2: IntelliSense).</summary>
public sealed record SessionErrorList(bool ShowErrors = true, bool ShowWarnings = true, bool ShowMessages = true, int Source = 0, bool CurrentDocumentOnly = false);

/// <summary>A project of multiple startup projects: <see cref="Action"/> is <c>Start</c> or <c>StartWithoutDebugging</c>.</summary>
public sealed record SessionStartupProject(string Path, string Action);

/// <summary>A data tip pinned to a line (1-based), kept with the solution.</summary>
public sealed record SessionDataTip(string Path, int Line, string Expression, string? Comment = null);

/// <summary>A breakpoint (1-based line), kept with the solution. <see cref="DependsOnPath"/> and <see cref="DependsOnLine"/> name the breakpoint it depends on.</summary>
public sealed record SessionBreakpoint(string Path, int Line, string? Condition = null, string? HitCondition = null, string? LogMessage = null, bool Enabled = true)
{
    public bool Temporary { get; init; }

    public string? DependsOnPath { get; init; }

    public int? DependsOnLine { get; init; }

    public string? Labels { get; init; }

    /// <summary>A thread or process filter (M14b), e.g. <c>ThreadName = "Worker"</c>.</summary>
    public string? Filter { get; init; }
}

/// <summary>A function breakpoint, kept with the solution.</summary>
public sealed record SessionFunctionBreakpoint(string Name, string? Condition = null, bool Enabled = true, string? Labels = null);

/// <summary>Breakpoints exported to a file (Breakpoints window → Export), in the session's format.</summary>
public sealed record BreakpointExport(IReadOnlyList<SessionBreakpoint> Breakpoints, IReadOnlyList<SessionFunctionBreakpoint>? FunctionBreakpoints);

/// <summary>An open file and its caret (zero-based line and character).</summary>
public sealed record SessionDocument(string Path, int Line, int Character)
{
    /// <summary>A pinned tab (M34d), pinned again when the session is restored.</summary>
    public bool? Pinned { get; init; }
}

/// <summary>Small bits of per-user state that survive restarts (<c>state.json</c>).</summary>
public sealed record ShellState
{
    public IReadOnlyList<string>? RecentCommands { get; init; }

    /// <summary>Code Search's window: its size, position, pin, preview and whether it is docked (M47).</summary>
    public ViewModels.CodeSearchWindowState? CodeSearch { get; init; }

    /// <summary>Whether Markdown editors' preview is collapsed, and its width.</summary>
    public Editor.MarkdownSplitState? MarkdownSplit { get; init; }
}
