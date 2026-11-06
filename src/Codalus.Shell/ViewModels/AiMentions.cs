using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>
/// AI Chat's @-mentions (ADR 0092): what goes with a question is only what it mentions. <c>@selection</c>, <c>@file</c>
/// (the active file), <c>@open</c> (the other open files), <c>@errors</c>, <c>@build</c> (the build output) and
/// <c>@solution</c> (its projects) are parts of the IDE's state; any other <c>@path</c> is a file of the workspace. An
/// agent with the IDE's tools reads the rest when it needs it.
/// </summary>
public static class AiMentions
{
    /// <summary>The IDE's parts, in the order the list offers them: the mention and what it sends.</summary>
    public static IReadOnlyList<(string Name, string Description)> Parts { get; } =
    [
        ("selection", Localizer.Get("msg.ai.mention.selection", "the editor's selection")),
        ("file", Localizer.Get("msg.ai.mention.file", "the active file")),
        ("open", Localizer.Get("msg.ai.mention.open", "the other open files")),
        ("errors", Localizer.Get("msg.ai.mention.errors", "the Error List")),
        ("build", Localizer.Get("msg.ai.mention.build", "the last build's output")),
        ("solution", Localizer.Get("msg.ai.mention.solution", "the solution's projects")),
    ];

    /// <summary>What <paramref name="question"/> mentions.</summary>
    public static AiContextChoice Parse(string question)
    {
        var words = question.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1 && w[0] == '@')
            .Select(w => w[1..].TrimEnd('.', ',', ';', ':', '?', '!', ')', '"', '\''))
            .Where(w => w.Length > 0)
            .ToList();
        bool Has(string name) => words.Contains(name, StringComparer.OrdinalIgnoreCase);
        var files = words.Where(w => !Parts.Any(p => p.Name.Equals(w, StringComparison.OrdinalIgnoreCase)) && (w.Contains('.', StringComparison.Ordinal) || w.Contains('/', StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new AiContextChoice(Has("selection"), Has("file"), Has("open"), Has("errors"), Has("build"), Has("solution")) { Files = files };
    }

    /// <summary>
    /// The suggestions for <c>@prefix</c>: the IDE's parts that start with it, then files whose name (or path) contains it,
    /// names that start with it first; at most 12.
    /// </summary>
    public static IEnumerable<AiCommandSuggestion> Suggest(string prefix, IReadOnlyList<string> files)
    {
        foreach (var (name, description) in Parts.Where(p => p.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            yield return new AiCommandSuggestion(name, "@" + name + " — " + description) { IsMention = true };
        }

        if (prefix.Length == 0)
        {
            yield break;
        }

        var matches = files
            .Select(f => (Path: f, Name: System.IO.Path.GetFileName(f)))
            .Where(f => f.Path.Contains(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? 0 : f.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(f => f.Path.Length)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Take(12 - Parts.Count(p => p.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
        foreach (var (path, _) in matches)
        {
            yield return new AiCommandSuggestion(path, "@" + path) { IsMention = true };
        }
    }
}
