using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>Where a step of an agent's turn stands.</summary>
public enum AiStepStatus
{
    /// <summary>Asked for, not started yet.</summary>
    Pending,

    /// <summary>Running.</summary>
    Running,

    /// <summary>Done.</summary>
    Completed,

    /// <summary>It failed, or the user refused it.</summary>
    Failed,

    /// <summary>The turn was stopped before the step ended.</summary>
    Cancelled,
}

/// <summary>A file a step worked on, and the line when it names one (1-based).</summary>
public sealed record AiStepLocation(string Path, int? Line = null)
{
    /// <summary>The file's name and line, as a step shows it (the whole path is its tooltip).</summary>
    public string Label => System.IO.Path.GetFileName(Path.TrimEnd('/', '\\')) is { Length: > 0 } name
        ? Line is { } line ? $"{name}:{line}" : name
        : Path;
}

/// <summary>
/// A step of an agent's turn as AI Chat shows it (M34h, <c>Views/AiStepView</c>): its kind (an icon), a title, what it
/// worked on, its status, the files it touched and its details, which open under it. The built-in agent's tool calls
/// map onto it (<see cref="From(AiAgentStep, bool, Action{AiStepLocation}?)"/>), and an external agent's tool calls take
/// the same shape (<see cref="FromAcp(AiAgentStep, Action{AiStepLocation}?)"/>, M48; the kinds are the Agent Client
/// Protocol's: read, edit, delete, move, search, execute, think, fetch, other).
/// </summary>
public sealed class AiStepItem : ObservableObject
{
    private bool _isExpanded;

    public AiStepItem(string kind, string title, AiStepStatus status)
    {
        Kind = kind;
        Title = title;
        Status = status;
    }

    /// <summary>What kind of step it is: read, edit, delete, move, search, execute, think, fetch, build or other.</summary>
    public string Kind { get; }

    /// <summary>The tool (<c>read_file</c>), or a sentence saying what the step does.</summary>
    public string Title { get; }

    /// <summary>The title is a tool's name, drawn in the code font.</summary>
    public bool TitleIsCode { get; init; }

    public AiStepStatus Status { get; }

    /// <summary>What the step worked on when it isn't a file (a search pattern, a command line).</summary>
    public string? Target { get; init; }

    /// <summary>How it ended, in a few words (<c>54 lines</c>, <c>not allowed</c>).</summary>
    public string? Outcome { get; init; }

    /// <summary>The files it worked on: links that open them.</summary>
    public IReadOnlyList<AiStepLocation> Locations { get; init; } = [];

    /// <summary>More than fits on its line (output, a diff, a message), shown when the step is expanded.</summary>
    public string? Details { get; init; }

    /// <summary>Opens a location (the shell's editor); null leaves the locations as text.</summary>
    public Action<AiStepLocation>? Open { get; init; }

    /// <summary>Its details are showing.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    public bool HasTarget => !string.IsNullOrEmpty(Target);

    public bool HasOutcome => !string.IsNullOrEmpty(Outcome);

    public bool HasLocations => Locations.Count > 0;

    public bool HasDetails => !string.IsNullOrEmpty(Details);

    /// <summary>Its outcome after an arrow, as the step's line shows it.</summary>
    public string OutcomeText => "→ " + Outcome;

    /// <summary>The icon of its kind.</summary>
    public string KindIcon => Kind switch
    {
        "read" => "document-text",
        "edit" => "edit",
        "delete" => "trash",
        "move" => "arrow-right",
        "search" => "search",
        "execute" => "terminal",
        "think" => "lightbulb",
        "fetch" => "globe",
        "build" => "build",
        "mcp" => "extensions",
        _ => "tools",
    };

    /// <summary>The icon of its status: a shape for each, so color doesn't carry it alone.</summary>
    public string StatusIcon => Status switch
    {
        AiStepStatus.Completed => "check",
        AiStepStatus.Failed => "error",
        AiStepStatus.Cancelled => "stop",
        _ => "timer",
    };

    public string StatusText => Status switch
    {
        AiStepStatus.Pending => Localizer.Get("msg.ai.stepPending", "waiting"),
        AiStepStatus.Running => Localizer.Get("msg.ai.stepRunning", "running"),
        AiStepStatus.Completed => Localizer.Get("msg.ai.stepCompleted", "done"),
        AiStepStatus.Failed => Localizer.Get("msg.ai.stepFailed", "failed"),
        _ => Localizer.Get("msg.ai.stepCancelled", "stopped"),
    };

    public bool IsFailed => Status == AiStepStatus.Failed;

    /// <summary>The step as a sentence, for screen readers.</summary>
    public string AccessibleName => string.Join(", ", new[]
    {
        string.Join(' ', new[] { Title, Target }.Concat(Locations.Select(l => l.Label)).Where(s => !string.IsNullOrEmpty(s))),
        StatusText,
        Outcome,
    }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>The tooltip of its details' toggle.</summary>
    public string DetailsToggleName => Localizer.Format("msg.ai.stepDetails", "Details of {0}", Title);

    /// <summary>Opens <paramref name="location"/>.</summary>
    public void OpenLocation(AiStepLocation location) => Open?.Invoke(location);

    /// <summary>Whether <paramref name="other"/> shows the same (so a re-rendered turn keeps the item and its expansion).</summary>
    public bool SameAs(AiStepItem other) =>
        Kind == other.Kind && Title == other.Title && Status == other.Status && Target == other.Target && Outcome == other.Outcome
        && Details == other.Details && Locations.SequenceEqual(other.Locations);

    /// <summary>
    /// A tool call of the built-in agent (M28c) as a step: its tool as the title, a file it read or changed as a
    /// location (only when the call succeeded), a pattern or command as its target. A step without an outcome is
    /// running while the turn is, and was stopped once the turn ended.
    /// </summary>
    public static AiStepItem From(AiAgentStep step, bool turnRunning, Action<AiStepLocation>? open = null)
    {
        var status = step.Outcome is null
            ? turnRunning ? AiStepStatus.Running : AiStepStatus.Cancelled
            : step.IsError ? AiStepStatus.Failed : AiStepStatus.Completed;
        var kind = KindOf(step.Tool);
        // A file it worked on is a link; a refused or failed call's path stays text (it may be outside the workspace).
        var onFile = step.Target.Length > 0 && step.Tool is "read_file" or "edit_file" or "create_file" && !step.Target.EndsWith('…') && !step.IsError;

        // A reading tool's outcome names what it read (the file, the pattern, the build): the line says that already.
        var outcome = step.Outcome is { } said && (said.Trim('\'', '"') == step.Target || said == step.Tool) ? null : step.Outcome;
        return new AiStepItem(kind, step.Tool, status)
        {
            TitleIsCode = true,
            Target = onFile || step.Target.Length == 0 ? null : step.Target,
            Locations = onFile ? [new AiStepLocation(step.Target)] : [],
            Outcome = outcome,
            Open = open,
        };
    }

    /// <summary>
    /// An external agent's tool call (M48, the Agent Client Protocol's <c>tool_call</c>): its kind, title and status as the
    /// agent reports them, the files it names as locations, and its details: the changes it reports (whether they went
    /// through the preview or the agent made them itself, read-only here) and its output. A command's output shows open
    /// while it runs.
    /// </summary>
    public static AiStepItem FromAcp(AiAgentStep step, Action<AiStepLocation>? open = null)
    {
        var status = step.Status switch
        {
            "completed" => AiStepStatus.Completed,
            "failed" => AiStepStatus.Failed,
            "cancelled" => AiStepStatus.Cancelled,
            "in_progress" => AiStepStatus.Running,
            _ => AiStepStatus.Pending,
        };
        var details = new List<string>();
        foreach (var diff in step.Diffs)
        {
            var title = diff.Reviewed
                ? Localizer.Format("msg.acp.diffReviewed", "Changes in {0} (reviewed in the preview)", diff.Path)
                : Localizer.Format("msg.acp.diffByAgent", "Changes in {0} (made by the agent itself, read-only)", diff.Path);
            details.Add(title + "\n" + string.Join('\n', AiDiffText.Lines(diff.OldText, diff.NewText).Select(l => l.Kind + " " + l.Text)));
        }

        if (step.Output is { Length: > 0 } output)
        {
            details.Add(Localizer.Get("msg.acp.output", "Output") + "\n" + output);
        }

        var running = step.TerminalId is not null && status is AiStepStatus.Running or AiStepStatus.Pending && step.Output is { Length: > 0 };
        return new AiStepItem(step.Kind ?? "other", step.Target, status)
        {
            Locations = [.. step.Locations.Select(l => new AiStepLocation(l.Path, l.Line))],
            Details = details.Count == 0 ? null : string.Join("\n\n", details),
            Open = open,
            IsExpanded = running,
        };
    }

    /// <summary>The kind of a built-in tool; an MCP server's tool (<c>server__tool</c>) is <c>mcp</c>.</summary>
    public static string KindOf(string tool) => tool switch
    {
        "read_file" or "list_files" or "get_diagnostics" or "get_build_output" => "read",
        "search_text" => "search",
        "edit_file" or "create_file" => "edit",
        "run_command" => "execute",
        "build" => "build",
        _ when tool.Contains("__", StringComparison.Ordinal) => "mcp",
        _ => "other",
    };
}
