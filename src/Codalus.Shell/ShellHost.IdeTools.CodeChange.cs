using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Codalus.LanguageServices.Client;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai.Ide;
using Codalus.Workbench.Ai.Mcp;

namespace Codalus.Shell;

/// <summary>
/// M60's tools for agents: the shell's side of <see cref="IdeCodeChangeTools"/>. The language service's Rename, code
/// actions and formatting, and Find and Replace in Files' engine, applied through <see cref="ApplyWorkspaceEditAsync"/>
/// as the IDE's own commands are, without their preview (D52: the tools are actions, approved by
/// <c>ai.agent.ideActions</c>): an open file changes in its editor as one undo step and stays unsaved, a closed file is
/// written. An edit that would touch a file outside the workspace or excluded from AI is refused whole.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>Closed files the tools wrote, which the language service may not have read again yet.</summary>
    private readonly HashSet<string> _agentWrites = new(Codalus.Workbench.Projects.SolutionService.PathComparer);

    private async Task<IdeToolResult> CodeChangeToolAsync(string tool, JsonElement args, string? root, CancellationToken cancellationToken)
    {
        await SyncAgentWritesAsync(cancellationToken).ConfigureAwait(true);
        var result = tool switch
        {
            "rename_symbol" => await RenameSymbolToolAsync(args, root, cancellationToken).ConfigureAwait(true),
            "get_code_actions" => await CodeActionsToolAsync(args, root, cancellationToken).ConfigureAwait(true),
            "apply_code_action" => await ApplyCodeActionToolAsync(args, root, cancellationToken).ConfigureAwait(true),
            "format_document" => await FormatDocumentToolAsync(args, root, cancellationToken).ConfigureAwait(true),
            "replace_in_files" => await ReplaceInFilesToolAsync(args, root, cancellationToken).ConfigureAwait(true),
            _ => throw new McpException($"There is no tool '{tool}'."),
        };
        return result with { Text = result.Text.ReplaceLineEndings("\n") };
    }

    // ------------------------------------------------------------------ rename

    private async Task<IdeToolResult> RenameSymbolToolAsync(JsonElement args, string? root, CancellationToken cancellationToken)
    {
        var newName = Str(args, "newName") is { Length: > 0 } given ? given.Trim() : throw new IdeToolException("A newName is needed.");
        var (client, path, position) = await ResolvePositionAsync(args, root, cancellationToken).ConfigureAwait(true);
        var uri = DocumentUris.FromPath(path);
        string? oldName = null;
        if (client.Capabilities?.PrepareRenameProvider != false)
        {
            if (await client.PrepareRenameAsync(uri, position, cancellationToken).ConfigureAwait(true) is not { } prepared)
            {
                return IdeToolResult.Error($"That can't be renamed (at {Relative(path, root)}:{(position.Line + 1).ToString(CultureInfo.InvariantCulture)}). Give the symbol's declaration or a use of it.");
            }

            oldName = prepared.Placeholder;
        }

        if (oldName is not { Length: > 0 } && await new LineCache(this).LineAsync(path, position.Line, cancellationToken).ConfigureAwait(true) is { } line)
        {
            // Without a placeholder (the C# host gives the range): the identifier at the position, as Rename's dialog shows.
            oldName = Regex.Match(line[Math.Min(position.Character, line.Length)..], @"^@?\w+").Value;
        }

        if (Languages.LanguageIdOf(path) == CSharpLanguageId && !IsIdentifier(newName))
        {
            return IdeToolResult.Error($"'{newName}' isn't a valid C# identifier.");
        }

        if (oldName == newName)
        {
            return IdeToolResult.Error($"The symbol is already named '{newName}'.");
        }

        var edit = await client.RenameAsync(uri, position, newName, cancellationToken).ConfigureAwait(true);
        if (edit is null || edit.DocumentChanges.Length == 0 && edit.ResourceOperations.Length == 0)
        {
            return IdeToolResult.Error("Nothing to rename there.");
        }

        return await ApplyAgentEditAsync(edit, oldName is { Length: > 0 } ? $"Renamed '{oldName}' to '{newName}'." : $"Renamed to '{newName}'.", root, cancellationToken).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ code actions

    /// <summary>The code actions at the place an agent gave, numbered from 1 in the lightbulb's order (fixes first).</summary>
    private async Task<(LanguageHostClient Client, string Path, List<CodeAction> Actions)> AgentCodeActionsAsync(JsonElement args, string? root, CancellationToken cancellationToken)
    {
        var path = RequiredPath(args, "path", root);
        await EnsureReadableAsync(path, root, cancellationToken).ConfigureAwait(true);
        var line = Int(args, "line") ?? throw new IdeToolException("A line is needed.");
        var lines = new LineCache(this);
        var text = await lines.LineAsync(path, line - 1, cancellationToken).ConfigureAwait(true)
            ?? throw new IdeToolException(string.Create(CultureInfo.InvariantCulture, $"{Relative(path, root)} has no line {line}."));
        LanguageServices.Protocol.Range range;
        if (Int(args, "endLine") is { } endLine && endLine > line)
        {
            // A selection of whole lines: from the first one's code to the end of the last.
            var last = await lines.LineAsync(path, endLine - 1, cancellationToken).ConfigureAwait(true)
                ?? throw new IdeToolException(string.Create(CultureInfo.InvariantCulture, $"{Relative(path, root)} has no line {endLine}."));
            range = new(new Position(line - 1, text.Length - text.TrimStart().Length), new Position(endLine - 1, last.Length));
        }
        else
        {
            // A caret: on the identifier, or at the line's code (the language service offers the fixes for its whole line).
            var character = text.Length - text.TrimStart().Length;
            if (Str(args, "name") is { Length: > 0 } name)
            {
                var simple = name[(name.LastIndexOf('.') + 1)..];
                var match = Regex.Match(text, @"(?<![\w@])@?" + Regex.Escape(simple) + @"(?!\w)");
                character = match.Success ? match.Index : text.IndexOf(simple, StringComparison.Ordinal);
                if (character < 0)
                {
                    throw new IdeToolException($"'{simple}' isn't on line {line.ToString(CultureInfo.InvariantCulture)} of {Relative(path, root)}: {text.Trim()}");
                }
            }

            range = new(new Position(line - 1, character), new Position(line - 1, character));
        }

        var client = await LanguageClientAsync(path).ConfigureAwait(true);
        var diagnostics = Languages.Diagnostics.For(path).Where(d => d.Range.Start.Line <= range.End.Line && d.Range.End.Line >= range.Start.Line).ToArray();
        var actions = await client.CodeActionsAsync(DocumentUris.FromPath(path), range, diagnostics, cancellationToken).ConfigureAwait(true);
        return (client, path, [.. actions.OrderBy(a => a.Kind == CodeActionKind.QuickFix ? 0 : 1).ThenBy(a => a.IsPreferred == true ? 0 : 1)]);
    }

    private async Task<IdeToolResult> CodeActionsToolAsync(JsonElement args, string? root, CancellationToken cancellationToken)
    {
        var (_, path, actions) = await AgentCodeActionsAsync(args, root, cancellationToken).ConfigureAwait(true);
        if (actions.Count == 0)
        {
            return new("No fixes or refactorings here. A fix needs its diagnostic on the line (get_diagnostics); a refactoring, the place it applies to (name, or a range with endLine).");
        }

        var sb = new StringBuilder().Append("Code actions at ").Append(Relative(path, root)).Append(':').Append(Int(args, "line")?.ToString(CultureInfo.InvariantCulture)).AppendLine(":");
        for (var i = 0; i < actions.Count; i++)
        {
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(". [").Append(actions[i].Kind ?? "refactor").Append("] ").Append(actions[i].Title);
            sb.AppendLine(actions[i].IsPreferred == true ? " (preferred)" : "");
        }

        return new(sb.Append("Apply one with apply_code_action at the same place, by its title or number.").ToString());
    }

    private async Task<IdeToolResult> ApplyCodeActionToolAsync(JsonElement args, string? root, CancellationToken cancellationToken)
    {
        var title = Str(args, "title");
        var index = Int(args, "index");
        if (title is not { Length: > 0 } && index is null)
        {
            throw new IdeToolException("A title (or an index) from get_code_actions is needed.");
        }

        var (client, _, actions) = await AgentCodeActionsAsync(args, root, cancellationToken).ConfigureAwait(true);
        CodeAction? picked = null;
        if (title is { Length: > 0 })
        {
            picked = actions.FirstOrDefault(a => a.Title == title) ?? actions.FirstOrDefault(a => string.Equals(a.Title, title, StringComparison.OrdinalIgnoreCase));
            if (picked is null)
            {
                var partial = actions.Where(a => a.Title.Contains(title, StringComparison.OrdinalIgnoreCase)).ToList();
                if (partial.Count > 1)
                {
                    return IdeToolResult.Error($"'{title}' matches {partial.Count.ToString(CultureInfo.InvariantCulture)} actions: {string.Join("; ", partial.Select(a => a.Title))}. Give the whole title or the number.");
                }

                picked = partial.FirstOrDefault();
            }
        }
        else if (index is >= 1 && index <= actions.Count)
        {
            picked = actions[index.Value - 1];
        }

        if (picked is null)
        {
            return IdeToolResult.Error(actions.Count == 0
                ? "No code actions here now (the code may have changed): call get_code_actions again."
                : "No such action here. The actions now: " + string.Join("; ", actions.Select((a, i) => (i + 1).ToString(CultureInfo.InvariantCulture) + ". " + a.Title)));
        }

        // A refactoring that would ask first (Generate constructor…, Extract interface…) takes the dialog's defaults.
        if (await client.CodeActionOptionsAsync(picked, cancellationToken).ConfigureAwait(true) is { } options)
        {
            picked = new CodeActionChoices(options.NameLabel is null ? null : options.Name, [.. options.Items.Where(i => i.Checked).Select(i => i.Id)]).AttachTo(picked);
        }

        var resolved = picked.Edit is { } ready && (ready.DocumentChanges.Length > 0 || ready.ResourceOperations.Length > 0)
            ? picked
            : await client.ResolveCodeActionAsync(picked, cancellationToken).ConfigureAwait(true);
        if (resolved.Edit is not { } edit || edit.DocumentChanges.Length == 0 && edit.ResourceOperations.Length == 0)
        {
            return IdeToolResult.Error($"'{picked.Title}' made no changes (it may need an editor feature Codalus doesn't have).");
        }

        return await ApplyAgentEditAsync(edit, $"Applied '{picked.Title}'.", root, cancellationToken).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ formatting

    private async Task<IdeToolResult> FormatDocumentToolAsync(JsonElement args, string? root, CancellationToken cancellationToken)
    {
        var path = RequiredPath(args, "path", root);
        await EnsureReadableAsync(path, root, cancellationToken).ConfigureAwait(true);
        if (!_models.ContainsKey(path) && !File.Exists(path))
        {
            return IdeToolResult.Error($"{Relative(path, root)} doesn't exist.");
        }

        var client = await LanguageClientAsync(path).ConfigureAwait(true);
        LanguageServices.Protocol.Range? range = null;
        if (Int(args, "startLine") is { } start)
        {
            var end = Math.Max(start, Int(args, "endLine") ?? start);
            var last = await new LineCache(this).LineAsync(path, end - 1, cancellationToken).ConfigureAwait(true)
                ?? throw new IdeToolException(string.Create(CultureInfo.InvariantCulture, $"{Relative(path, root)} has no line {end}."));
            range = new(new Position(Math.Max(0, start - 1), 0), new Position(end - 1, last.Length));
        }

        var editor = Editors.FirstOrDefault(e => string.Equals(e.Model.Path, path, StringComparison.Ordinal))?.Controller?.Editor.Options;
        var options = new FormattingOptions(editor?.IndentationSize ?? 4, editor?.ConvertTabsToSpaces ?? true);
        var version = _models.TryGetValue(path, out var model) ? model.Version : (int?)null;
        var edits = await client.FormatAsync(DocumentUris.FromPath(path), range, options, cancellationToken).ConfigureAwait(true);
        if (edits.Length == 0)
        {
            return new($"{Relative(path, root)} is formatted already: nothing changed.");
        }

        var edit = new WorkspaceEdit([new TextDocumentEdit(new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(path), version), edits)]);
        return await ApplyAgentEditAsync(edit, range is null ? "Formatted the document." : "Formatted the lines.", root, cancellationToken).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ replace in files

    private async Task<IdeToolResult> ReplaceInFilesToolAsync(JsonElement args, string? root, CancellationToken cancellationToken)
    {
        if (root is null)
        {
            throw new IdeToolException("No solution or folder is open.");
        }

        var find = Str(args, "find") is { Length: > 0 } query ? query : throw new IdeToolException("The text to find is needed.");
        var replace = Str(args, "replace") ?? throw new IdeToolException("The replacement (replace) is needed; an empty string removes the matches.");
        var types = Str(args, "glob") is { Length: > 0 } glob ? glob + ";" + FindInFilesOptions.DefaultFileTypes : FindInFilesOptions.DefaultFileTypes;
        var options = new FindInFilesOptions(find, Bool(args, "matchCase") ?? false, Bool(args, "wholeWord") ?? false, Bool(args, "regex") ?? false, FindScopes.EntireSolution, types);
        Regex regex;
        try
        {
            regex = options.CreateRegex();
        }
        catch (ArgumentException ex)
        {
            throw new IdeToolException("The regular expression is invalid: " + ex.Message);
        }

        var (files, openTexts) = await FilesToSearchAsync(options, cancellationToken).ConfigureAwait(true);
        var candidates = new List<string>();
        var excluded = 0;
        foreach (var file in files)
        {
            if (!IsInWorkspace(file, root) || _models.TryGetValue(file, out var open) && open.IsReadOnly)
            {
                continue;
            }

            if (await IsExcludedAsync(file, root, cancellationToken).ConfigureAwait(true))
            {
                excluded++;
                continue;
            }

            candidates.Add(file);
        }

        ReplacementPlan plan;
        try
        {
            plan = await PlanReplacementsAsync(candidates, openTexts, regex, options, replace, cancellationToken).ConfigureAwait(true);
        }
        catch (RegexMatchTimeoutException)
        {
            return IdeToolResult.Error("The regular expression took too long on a line; make it simpler.");
        }

        var skipped = excluded == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $" ({excluded} file(s) excluded from AI weren't searched.)");
        if (plan.Changes.Count == 0)
        {
            return new(string.Create(CultureInfo.InvariantCulture, $"No matches in {candidates.Count} file(s): nothing changed.") + skipped);
        }

        var result = await ApplyAgentEditAsync(new WorkspaceEdit([.. plan.Changes]), null, root, cancellationToken, plan.PerFile).ConfigureAwait(true);
        if (!result.IsError)
        {
            FindResults.ShowReplaced(options, replace, plan.Rows, plan.Occurrences, plan.Changes.Count);
        }

        return result with
        {
            Text = result.IsError ? result.Text : string.Create(CultureInfo.InvariantCulture, $"Replaced {plan.Occurrences} match(es) in {plan.Changes.Count} file(s).") + skipped + "\n" + result.Text,
        };
    }

    // ------------------------------------------------------------------ applying an agent's edit

    /// <summary>
    /// Makes the language service read again the closed files the tools wrote. Its watcher reads a changed file once it
    /// has been quiet for 200 ms, and a tool called sooner would get an answer, and an edit, made from the old text (the
    /// second of two quick formats of a closed file undid the first's work): a <c>didOpen</c> and <c>didClose</c> make it
    /// read the file from disk, before the requests that follow on the connection.
    /// </summary>
    private async Task SyncAgentWritesAsync(CancellationToken cancellationToken)
    {
        if (_agentWrites.Count == 0)
        {
            return;
        }

        List<string> written = [.. _agentWrites];
        _agentWrites.Clear();
        foreach (var path in written)
        {
            if (_models.ContainsKey(path) || !File.Exists(path) || Languages.LanguageIdOf(path) is not { } language
                || await Languages.ClientAsync(language).ConfigureAwait(true) is not { } client)
            {
                continue; // open (the editor's text is the language service's), gone, or no language service
            }

            try
            {
                var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(true);
                var uri = DocumentUris.FromPath(path);
                await client.DidOpenAsync(uri, language, () => (text, 1), cancellationToken).ConfigureAwait(true);
                await client.DidCloseAsync(uri, cancellationToken).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Its watcher reads it.
            }
        }
    }

    /// <summary>
    /// Applies a workspace edit an agent's tool made, after checking that every file it touches (renamed ones too) is in
    /// the workspace and not excluded from AI; refused whole otherwise. The result names each changed file with its
    /// number of edits (or of <paramref name="counts"/>: replacements), and whether it changed in its editor or on disk.
    /// </summary>
    private async Task<IdeToolResult> ApplyAgentEditAsync(WorkspaceEdit edit, string? done, string? root, CancellationToken cancellationToken, IReadOnlyDictionary<string, int>? counts = null)
    {
        var touched = edit.DocumentChanges.Select(c => c.TextDocument.Uri)
            .Concat(edit.ResourceOperations.Select(o => o.Uri))
            .Concat(edit.ResourceOperations.Select(o => o.NewUri).OfType<string>())
            .Distinct(StringComparer.Ordinal)
            .Select(uri => DocumentUris.ToPath(uri) ?? uri)
            .ToList();
        var outside = touched.Where(p => !IsInWorkspace(p, root)).ToList();
        if (outside.Count > 0)
        {
            return IdeToolResult.Error("Refused: the change would also edit files outside the workspace: " + string.Join(", ", outside) + ". Nothing was changed.");
        }

        var excluded = new List<string>();
        foreach (var path in touched)
        {
            if (await IsExcludedAsync(path, root, cancellationToken).ConfigureAwait(true))
            {
                excluded.Add(Relative(path, root));
            }
        }

        if (excluded.Count > 0)
        {
            return IdeToolResult.Error("Refused: the change would edit files excluded from AI: " + string.Join(", ", excluded) + ". Nothing was changed.");
        }

        var readOnly = touched.Where(p => _models.TryGetValue(p, out var m) && m.IsReadOnly).Select(p => Relative(p, root)).ToList();
        if (readOnly.Count > 0)
        {
            return IdeToolResult.Error("Refused: the change would edit read-only documents: " + string.Join(", ", readOnly) + ". Nothing was changed.");
        }

        // What each file is before the edit: open in an editor (it changes there, unsaved) or not (written).
        var files = new List<(string Path, int Edits, bool Open)>();
        foreach (var group in edit.DocumentChanges.GroupBy(c => c.TextDocument.Uri, StringComparer.Ordinal))
        {
            if (DocumentUris.ToPath(group.Key) is { } path)
            {
                files.Add((path, counts?.GetValueOrDefault(path) ?? group.Sum(c => c.Edits.Length), _models.ContainsKey(path)));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        foreach (var path in touched.Where(p => !_models.ContainsKey(p)))
        {
            _agentWrites.Add(path);
        }

        if (!await ApplyWorkspaceEditAsync(edit, quiet: true).ConfigureAwait(true))
        {
            return IdeToolResult.Error("The change couldn't be applied in full: a file changed meanwhile, or couldn't be written or renamed (see the IDE's notifications). Read the files again before retrying.");
        }

        var sb = new StringBuilder();
        if (done is not null)
        {
            sb.AppendLine(done);
        }

        sb.Append("Changed ").Append(files.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(files.Count == 1 ? " file:" : " files:");
        foreach (var (path, edits, open) in files.OrderBy(f => Relative(f.Path, root), StringComparer.Ordinal))
        {
            sb.Append("- ").Append(Relative(path, root)).Append(": ").Append(edits.ToString(CultureInfo.InvariantCulture))
                .Append(counts is null ? (edits == 1 ? " edit" : " edits") : (edits == 1 ? " replacement" : " replacements"))
                .AppendLine(open ? " (in its open editor, unsaved; one undo step)" : " (written to disk)");
        }

        foreach (var operation in edit.ResourceOperations)
        {
            if (DocumentUris.ToPath(operation.Uri) is { } from)
            {
                sb.Append("- ").Append(Relative(from, root)).AppendLine(operation.Kind == ResourceOperationKinds.Rename && operation.NewUri is { } to && DocumentUris.ToPath(to) is { } target
                    ? " renamed to " + Relative(target, root)
                    : " deleted");
            }
        }

        return new(sb.ToString().TrimEnd());
    }
}
