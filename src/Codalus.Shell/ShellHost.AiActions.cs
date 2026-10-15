using System.Globalization;
using System.Text;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Git;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Testing;

namespace Codalus.Shell;

/// <summary>
/// AI actions on existing surfaces (M28b, ADR 0062), each run only when the user asks: a commit message in Git Changes,
/// a pull request's title and description, Ask AI in the exception helper, a failing test, Fix with AI for the
/// diagnostic at the caret, tests for the selection, and a CPU profile's summary. Answers that fill a box or change code
/// come back whole (code through the preview); questions open in AI Chat, where what was sent is shown.
/// </summary>
public sealed partial class ShellHost
{
    bool Editor.IEditorServices.CanAskAi => Ai.IsEnabled;

    void Editor.IEditorServices.AskAiAboutException(string description) => _ = ExplainExceptionWithAiAsync(description);

    /// <summary>Tells the user AI is off or has no provider; true when it can be used.</summary>
    private async Task<bool> EnsureAiAsync()
    {
        switch (Ai.State)
        {
            case AiState.Off:
                ShowAiChat();
                return false;
            case AiState.NoProvider:
                await ChooseAiProviderAsync().ConfigureAwait(true);
                return Ai.State == AiState.Ready;
            default:
                return true;
        }
    }

    private void ReportAiFailure(Exception ex) =>
        Workbench.Notifications.Warning(ex is AiUnavailableException { NeedsConfiguration: true }
            ? Localizer.Format("msg.ai.failedConfigure", "{0} Configure the provider in Tools → AI.", ex.Message)
            : ex.Message);

    // ------------------------------------------------------------------ Git

    /// <summary>Writes the commit message for the staged changes into Git Changes' box.</summary>
    public async Task GenerateCommitMessageAsync()
    {
        if (!await EnsureAiAsync().ConfigureAwait(true) || Git.Root is not { } root)
        {
            return;
        }

        try
        {
            var (_, diff, _) = await Git.RunAsync(root, ["diff", "--cached", "--no-ext-diff", "-M"], CancellationToken.None, readOnly: true).ConfigureAwait(true);
            if (diff.Trim().Length == 0)
            {
                ShowMessage(Localizer.Get("msg.ai.stageChangesFirst", "Stage the changes to commit first: the message describes the staged changes."));
                return;
            }

            var subjects = (await Git.LogAsync(new GitLogQuery(), 0, 10).ConfigureAwait(true)).Select(c => c.Subject).ToList();
            ShowMessage(Localizer.Get("msg.ai.writingCommitMessage", "Writing the commit message…"));
            GitChanges.Message = await AiActions.CommitMessageAsync(Ai, diff, root, subjects, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is AiUnavailableException or GitException)
        {
            ReportAiFailure(ex);
        }
    }

    /// <summary>Writes a pull request's title and description from the commits and diff between its branches.</summary>
    private async Task WritePullRequestWithAiAsync(CreatePullRequestViewModel form, string remote)
    {
        if (!await EnsureAiAsync().ConfigureAwait(true) || Git.Root is not { } root || form.Target is not { } target)
        {
            return;
        }

        try
        {
            var range = $"{remote}/{target}...{form.SourceBranch}";
            var commits = (await Git.LogAsync(new GitLogQuery { Revision = $"{remote}/{target}..{form.SourceBranch}" }, 0, 100).ConfigureAwait(true)).Select(c => c.Subject).ToList();
            var (_, diff, _) = await Git.RunAsync(root, ["diff", "--no-ext-diff", "-M", range], CancellationToken.None, readOnly: true).ConfigureAwait(true);
            ShowMessage(Localizer.Get("msg.ai.writingDescription", "Writing the title and description…"));
            var (title, description) = await AiActions.PullRequestAsync(Ai, commits, diff, root, CancellationToken.None).ConfigureAwait(true);
            if (title.Length > 0)
            {
                form.Title = title;
            }

            form.Description = description;
        }
        catch (Exception ex) when (ex is AiUnavailableException or GitException)
        {
            ReportAiFailure(ex);
        }
    }

    // ------------------------------------------------------------------ debugging and testing

    /// <summary>The exception helper's Ask AI: the exception with its stack, and the code around the current statement.</summary>
    public async Task ExplainExceptionWithAiAsync(string description)
    {
        if (!await EnsureAiAsync().ConfigureAwait(true))
        {
            return;
        }

        var items = new List<AiContextItem> { new(AiContextKind.Debugger, Localizer.Get("msg.ai.exception", "the exception"), description) };
        var top = Debugger.SelectedFrame ?? (Debugger.Frames.Count > 0 ? Debugger.Frames[0] : null);
        if (top is { Source.Path: { } path } frame && File.Exists(path))
        {
            items.Add(await LinesAroundAsync(path, frame.Line).ConfigureAwait(true));
        }

        if (Debugger.Frames.Count > 0)
        {
            var stack = string.Join('\n', Debugger.Frames.Take(30).Select(f => f.Source?.Path is { } p ? $"{f.Name} ({Path.GetFileName(p)}:{f.Line.ToString(CultureInfo.InvariantCulture)})" : f.Name));
            items.Add(new AiContextItem(AiContextKind.Debugger, Localizer.Get("msg.ai.callStack", "the call stack"), stack));
        }

        await AiChat.AskAsync(AiActions.ExplainExceptionQuestion, items).ConfigureAwait(true);
    }

    /// <summary>Asks AI Chat why the Test Explorer's selected test fails: its result, and its test file.</summary>
    public async Task ExplainTestWithAiAsync()
    {
        if (!await EnsureAiAsync().ConfigureAwait(true))
        {
            return;
        }

        if (TestExplorer.Selected?.Group.Node is not { } selected || Tests.Find(selected.Key) is not { IsLeaf: true, Result: { } result } node)
        {
            ShowMessage(Localizer.Get("msg.ai.selectATestWithAResult", "Select a test that has run in the Test Explorer first."));
            return;
        }

        var text = new StringBuilder().AppendLine(node.FullLabel).AppendLine(TestExplorerViewModel.OutcomeText(result.Outcome));
        foreach (var message in result.Messages)
        {
            text.AppendLine(message.Message);
            if (message.Expected is { } expected)
            {
                text.Append("Expected: ").AppendLine(expected);
            }

            if (message.Actual is { } actual)
            {
                text.Append("Actual: ").AppendLine(actual);
            }

            if (message.StackTrace is { } stack)
            {
                text.AppendLine(stack);
            }
        }

        if (result.Output.Length > 0)
        {
            text.AppendLine().AppendLine(result.Output.Length > 4000 ? result.Output[^4000..] : result.Output);
        }

        var items = new List<AiContextItem> { new(AiContextKind.TestResult, node.Label, text.ToString()) };
        if (node.Uri is { } uri && DocumentUris.ToPath(uri) is { } path && File.Exists(path))
        {
            items.Add(new AiContextItem(AiContextKind.CurrentFile, Path.GetFileName(path), OpenTextOf(path) ?? await File.ReadAllTextAsync(path).ConfigureAwait(true)) { Path = path, Language = Languages.LanguageIdOf(path) });
        }

        await AiChat.AskAsync(AiActions.ExplainTestQuestion, items).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ the editor

    /// <summary>Fix with AI: the diagnostic at the caret (or the first on its line), fixed through the preview.</summary>
    public async Task FixDiagnosticWithAiAsync()
    {
        if (!await EnsureAiAsync().ConfigureAwait(true) || ActiveEditor?.Controller is not { } controller)
        {
            return;
        }

        var caret = controller.Editor.CaretOffset;
        var line = controller.Model.Text.GetLineByOffset(caret);
        var here = controller.Markers.At(caret);
        var diagnostic = (here.Count > 0 ? here[0] : null)
            ?? Languages.Diagnostics.For(controller.Model.Path).Where(d => d.Range.Start.Line == line.LineNumber - 1).OrderBy(d => d.Severity).FirstOrDefault();
        if (diagnostic is null)
        {
            ShowMessage(Localizer.Get("msg.ai.noDiagnosticHere", "There's no error or warning at the caret."));
            return;
        }

        var model = controller.Model;
        var text = model.Text.Text;
        var version = model.Version;
        ShowMessage(Localizer.Format("msg.ai.fixing", "Asking AI to fix {0}…", diagnostic.Code ?? diagnostic.Message));
        AiFix? fix;
        try
        {
            fix = await AiActions.FixAsync(Ai, model.Path, model.LanguageId, text, diagnostic.Range.Start.Line + 1, diagnostic.Code ?? "", diagnostic.Message, CancellationToken.None).ConfigureAwait(true);
        }
        catch (AiUnavailableException ex)
        {
            ReportAiFailure(ex);
            return;
        }

        if (fix is null)
        {
            ShowMessage(Localizer.Get("msg.ai.noFix", "No fix came back (or the file is excluded from AI)."));
            return;
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var end = fix.EndLine == lines.Length ? new Position(fix.EndLine - 1, lines[^1].Length) : new Position(fix.EndLine, 0);
        var newText = fix.NewText + (fix.EndLine == lines.Length ? "" : "\n");
        if (text.Contains("\r\n", StringComparison.Ordinal))
        {
            newText = newText.Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        var edit = new WorkspaceEdit([new TextDocumentEdit(new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(model.Path), version), [new TextEdit(new Codalus.LanguageServices.Protocol.Range(new Position(fix.StartLine - 1, 0), end), newText)])]);
        await PreviewAndApplyAsync(Localizer.Format("msg.ai.fixTitle", "Fix {0} with AI?", diagnostic.Code ?? diagnostic.Message), edit).ConfigureAwait(true);
    }

    /// <summary>Generate Tests with AI: tests for the selection (or the current file) in AI Chat, with the solution's test framework.</summary>
    public async Task GenerateTestsWithAiAsync()
    {
        if (!await EnsureAiAsync().ConfigureAwait(true))
        {
            return;
        }

        var framework = TestFrameworks.Detect(Solutions.Projects.Values.Where(p => p.IsTestProject).Select(p => p.Path));
        var (items, anchor) = GatherAiContext(new AiContextChoice(Selection: true, CurrentFile: ActiveEditor?.Controller?.Editor.SelectionLength is null or 0, OpenFiles: false, Errors: false, BuildOutput: false, Solution: true));
        await AiChat.AskAsync(AiActions.GenerateTestsQuestion.Replace("{0}", framework, StringComparison.Ordinal), items, anchor).ConfigureAwait(true);
    }

    /// <summary>The CPU report's summary and top functions in AI Chat.</summary>
    private async Task SummarizeProfileWithAiAsync(ProfilingReportViewModel report)
    {
        if (!await EnsureAiAsync().ConfigureAwait(true))
        {
            return;
        }

        var text = new StringBuilder();
        text.Append(report.Title).AppendLine();
        if (report.Summary is { Length: > 0 } summary)
        {
            text.AppendLine(summary);
        }

        text.AppendLine().AppendLine("Function | Self | Total | Module");
        foreach (var function in report.Functions.Take(40))
        {
            text.Append(function.Name).Append(" | ").Append(function.Exclusive).Append(" | ").Append(function.Inclusive).Append(" | ").AppendLine(function.Module);
        }

        await AiChat.AskAsync(AiActions.SummarizeProfileQuestion, [new AiContextItem(AiContextKind.Profile, report.Title, text.ToString())]).ConfigureAwait(true);
    }

    /// <summary>The lines around <paramref name="line"/> (1-based) of a file, as a selection item.</summary>
    private async Task<AiContextItem> LinesAroundAsync(string path, int line, int radius = 15)
    {
        var text = OpenTextOf(path) ?? await File.ReadAllTextAsync(path).ConfigureAwait(true);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = Math.Max(1, line - radius);
        var end = Math.Min(lines.Length, line + radius);
        return new AiContextItem(AiContextKind.Selection, Path.GetFileName(path), string.Join('\n', lines[(start - 1)..end])) { Path = path, StartLine = start, Language = Languages.LanguageIdOf(path) };
    }
}
