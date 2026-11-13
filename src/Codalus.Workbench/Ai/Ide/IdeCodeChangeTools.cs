namespace Codalus.Workbench.Ai.Ide;

/// <summary>
/// M60's tools for agents: Code changes: rename, code actions (fixes and refactorings), formatting and replace in files.
/// Every change is an action (<c>ai.agent.ideActions</c>, D52) applied through the IDE's own edit path: a file open in an
/// editor changes in its document as one undo step (left unsaved), a closed file is written; a change that would touch a
/// file excluded from AI is refused. Listing the code actions only reads.
/// </summary>
public static class IdeCodeChangeTools
{
    private const string Place =
        """
        "path":{"type":"string","description":"A file, relative to the workspace root."},
        "line":{"type":"integer","description":"A 1-based line of that file."},
        "endLine":{"type":"integer","description":"The last line of a range of lines (a selection), for refactorings such as Extract Method."},
        "name":{"type":"string","description":"An identifier on the line: the place is there, as a caret on it. Without it, the whole line."}
        """;

    /// <summary>The group's tools, in the order agents see them.</summary>
    public static IReadOnlyList<IdeToolDefinition> Tools { get; } =
    [
        IdeTools.Action("rename_symbol", "Rename a symbol everywhere it is used, with the C# language service's Rename (the compiler's references, not text matches: overloads, overrides, partial declarations and a file named after the type follow). Files open in Codalus IDE change in the editor as one undo step (left unsaved); closed files are written. Returns each changed file with its number of edits.", "{" + IdeTools.Position + ""","newName":{"type":"string","description":"The new name."}}""", "newName"),
        IdeTools.Read("get_code_actions", "The fixes and refactorings the language service offers at a place (the lightbulb, Ctrl+.): numbered, with their kind (quickfix, refactor…) and title. A fix needs its diagnostic on the line (get_diagnostics). Apply one with apply_code_action at the same place.", "{" + Place + "}", "path", "line"),
        IdeTools.Action("apply_code_action", "Apply one of the fixes or refactorings get_code_actions lists at the same place, by its title (or its number), as the lightbulb does; a refactoring that would ask (Generate constructor…) takes its defaults. Returns each changed file with its number of edits.", "{" + Place + ""","title":{"type":"string","description":"The action's title as get_code_actions gave it (or a unique part of it)."},"index":{"type":"integer","description":"Its number in get_code_actions' list, instead of the title."}}""", "path", "line"),
        IdeTools.Action("format_document", "Format a file, or a range of its lines, with the language service's formatter and the project's .editorconfig (Format Document, Format Selection).", """{"path":{"type":"string","description":"A file, relative to the workspace root."},"startLine":{"type":"integer","description":"The first 1-based line to format; the whole file without it."},"endLine":{"type":"integer","description":"The last line to format (default: startLine)."}}""", "path"),
        IdeTools.Action("replace_in_files", "Replace text in the solution's files with Find and Replace in Files' engine (open files as edited, bin, obj and dot folders and files excluded from AI left out). Returns the number of replacements in each changed file; the changed lines show in the IDE's Find Results.", """{"find":{"type":"string","description":"The text, or a .NET regular expression when regex is true."},"replace":{"type":"string","description":"What each match becomes; with regex, $1 and ${name} are its groups."},"regex":{"type":"boolean"},"matchCase":{"type":"boolean"},"wholeWord":{"type":"boolean"},"glob":{"type":"string","description":"Only files whose name matches, e.g. *.cs (several separated by ;)."}}""", "find", "replace"),
    ];
}
