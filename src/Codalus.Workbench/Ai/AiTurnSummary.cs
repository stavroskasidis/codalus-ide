using System.Globalization;
using Codalus.Workbench.Localization;

namespace Codalus.Workbench.Ai;

/// <summary>A file a turn changed (ADR 0092): before its first change and after its last.</summary>
/// <param name="Path">The file, relative to the workspace (as the steps name it).</param>
/// <param name="OldText">Its text before the turn; null when the turn created it.</param>
/// <param name="NewText">Its text after the turn.</param>
/// <param name="Reviewed">Every change went through the preview; otherwise the agent wrote some of it by itself.</param>
public sealed record AiChangedFile(string Path, string? OldText, string NewText, bool Reviewed);

/// <summary>
/// AI Chat's compact view of a turn (ADR 0092): its steps in one line ("Read 3 files · searched twice · edited
/// Program.cs · ran 2 commands"), and the files it changed, each before and after.
/// </summary>
public static class AiTurnSummary
{
    /// <summary>What a step did, by its ACP kind or the built-in tool's name.</summary>
    public static string KindOf(AiAgentStep step) => step.Id is not null
        ? step.Kind is null or "other" or "switch_mode" ? "other" : step.Kind
        : ToolKind(step.Tool);

    private static string ToolKind(string tool) => tool switch
    {
        "read_file" or "list_files" or "get_solution" or "get_open_files" or "get_diagnostics" or "get_build_output" or "get_document_outline"
            or "get_symbol_info" or "go_to_definition" or "get_test_results" or "get_run_output" => "read",
        "search_text" or "search_code" or "find_symbols" or "find_references" or "find_implementations" or "call_hierarchy" or "type_hierarchy" => "search",
        "edit_file" or "create_file" => "edit",
        "run_command" or "build" or "run_tests" or "run_project" or "stop_run" => "execute",
        _ when tool.StartsWith("debug_", StringComparison.Ordinal) => "debug",
        _ when tool.StartsWith("browser_", StringComparison.Ordinal) => "browser",
        _ => "other",
    };

    /// <summary>The steps in one line, most frequent kinds first; empty without steps.</summary>
    public static string Describe(IReadOnlyList<AiAgentStep> steps)
    {
        if (steps.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var group in steps.GroupBy(KindOf).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            var count = group.Count();
            parts.Add(group.Key switch
            {
                "read" => count == 1 ? Localizer.Get("msg.ai.summary.readOne", "read 1 file") : Localizer.Format("msg.ai.summary.read", "read {0} files", count),
                "search" => count == 1 ? Localizer.Get("msg.ai.summary.searchedOnce", "searched once") : Localizer.Format("msg.ai.summary.searched", "searched {0} times", count),
                "edit" => Edited(group.ToList()),
                "delete" => count == 1 ? Localizer.Get("msg.ai.summary.deletedOne", "deleted 1 file") : Localizer.Format("msg.ai.summary.deleted", "deleted {0} files", count),
                "move" => count == 1 ? Localizer.Get("msg.ai.summary.movedOne", "moved 1 file") : Localizer.Format("msg.ai.summary.moved", "moved {0} files", count),
                "execute" => count == 1 ? Localizer.Get("msg.ai.summary.ranOne", "ran 1 command") : Localizer.Format("msg.ai.summary.ran", "ran {0} commands", count),
                "think" => Localizer.Get("msg.ai.summary.thought", "thought"),
                "fetch" => count == 1 ? Localizer.Get("msg.ai.summary.fetchedOne", "fetched 1 page") : Localizer.Format("msg.ai.summary.fetched", "fetched {0} pages", count),
                "debug" => count == 1 ? Localizer.Get("msg.ai.summary.debuggedOnce", "used the debugger once") : Localizer.Format("msg.ai.summary.debugged", "used the debugger {0} times", count),
                "browser" => count == 1 ? Localizer.Get("msg.ai.summary.browserOnce", "used the browser once") : Localizer.Format("msg.ai.summary.browser", "used the browser {0} times", count),
                _ => count == 1 ? Localizer.Get("msg.ai.summary.otherOne", "1 other step") : Localizer.Format("msg.ai.summary.other", "{0} other steps", count),
            });
        }

        var failed = steps.Count(s => s.IsError || s.Status == "failed");
        if (failed > 0)
        {
            parts.Add(failed == 1 ? Localizer.Get("msg.ai.summary.failedOne", "1 failed") : Localizer.Format("msg.ai.summary.failed", "{0} failed", failed));
        }

        var line = string.Join(" · ", parts);
        return line.Length == 0 ? line : char.ToUpper(line[0], CultureInfo.CurrentCulture) + line[1..];
    }

    private static string Edited(List<AiAgentStep> steps)
    {
        var files = steps.SelectMany(s => s.Diffs.Select(d => d.Path).Concat(s.Locations.Select(l => l.Path)))
            .Concat(steps.Where(s => s.Id is null && s.Target.Length > 0).Select(s => s.Target))
            .Select(System.IO.Path.GetFileName)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return files.Count switch
        {
            0 => steps.Count == 1 ? Localizer.Get("msg.ai.summary.editedOnce", "edited once") : Localizer.Format("msg.ai.summary.editedSteps", "edited {0} times", steps.Count),
            1 => Localizer.Format("msg.ai.summary.editedFile", "edited {0}", files[0]!),
            _ => Localizer.Format("msg.ai.summary.editedFiles", "edited {0} files", files.Count),
        };
    }

    /// <summary>The step that runs now (the last one not finished), for the folded line while the turn streams.</summary>
    public static AiAgentStep? Current(IReadOnlyList<AiAgentStep> steps) =>
        steps.LastOrDefault(s => s.Status is "pending" or "in_progress" || (s.Id is null && s.Outcome is null));

    /// <summary>The files the turn changed, in the order it first changed them: before the first change, after the last.</summary>
    public static IReadOnlyList<AiChangedFile> ChangedFiles(IReadOnlyList<AiAgentStep> steps)
    {
        var files = new List<AiChangedFile>();
        foreach (var diff in steps.Where(s => !s.IsError && s.Status is null or "completed").SelectMany(s => s.Diffs))
        {
            var at = files.FindIndex(f => f.Path == diff.Path);
            if (at < 0)
            {
                files.Add(new AiChangedFile(diff.Path, diff.OldText, diff.NewText, diff.Reviewed));
            }
            else
            {
                files[at] = files[at] with { NewText = diff.NewText, Reviewed = files[at].Reviewed && diff.Reviewed };
            }
        }

        return [.. files.Where(f => f.OldText != f.NewText)];
    }
}
