using System.Text;
using System.Text.Json;
using Codalus.Plugins.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Testing;

namespace Codalus.Shell;

/// <summary>
/// AI actions on existing surfaces (M28c): each is a command the user runs, so each use is opted into. They gather their
/// own context (a problem, an exception, a failing test, the staged changes) through the same data boundary as the
/// chat (<see cref="AiContext"/>) and ask in the chat, except the commit message, which streams into Git Changes.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>Lines of code around a problem, an exception's stop or a test, sent with the question.</summary>
    private const int AiContextLines = 15;

    internal const string CommitMessagePrompt =
        "Write a git commit message for these staged changes: a summary line of at most 72 characters in the imperative mood, "
        + "then, only if it helps, a blank line and a short body. Reply with the message only, no code fences.";

    private void InitializeAiActions()
    {
        void Add(string id, string title, string when, Func<Task> run) =>
            Workbench.Commands.Register(new CommandDescriptor(id, title, ContributionSource.Shell) { Category = "AI", Enablement = WhenClause.Parse(when) }, async (_, _) =>
            {
                await Ui.RunAsync(run).ConfigureAwait(false);
                return null;
            });
        Add("ai.explainProblem", "Ask AI About This Problem", "aiReady && editorTextFocus", ExplainProblemWithAiAsync);
        Add("ai.writeTests", "Write Tests with AI", "aiReady && editorHasSelection", WriteTestsWithAiAsync);
        Add("ai.explainException", "Explain the Exception with AI", "aiReady && debugStopped", ExplainExceptionWithAiAsync);
        Add("ai.explainTestFailure", "Explain the Test Failure with AI", "aiReady", ExplainTestFailureWithAiAsync);
        Add("ai.draftCommitMessage", "Draft Commit Message with AI", "aiReady && gitRepository", DraftCommitMessageWithAiAsync);
        var shell = ContributionSource.Shell;
        Workbench.Menus.Add(
        [
            new MenuItemRule(MenuLocations.EditorContext, "ai.explainProblem", shell) { Group = "9-ai", Order = 1, When = WhenClause.Parse("aiReady") },
            new MenuItemRule(MenuLocations.EditorContext, "ai.writeTests", shell) { Group = "9-ai", Order = 2, When = WhenClause.Parse("aiReady && editorHasSelection") },
            new MenuItemRule(MenuLocations.MenuBar("debug"), "ai.explainException", shell) { Group = "6-inspect", Order = 90, When = WhenClause.Parse("aiReady") },
            new MenuItemRule(MenuLocations.MenuBar("git"), "ai.draftCommitMessage", shell) { Group = "commit", Order = 90, When = WhenClause.Parse("aiReady") },
            new MenuItemRule(MenuLocations.MenuBar("test"), "ai.explainTestFailure", shell) { Group = "9-ai", Order = 1, When = WhenClause.Parse("aiReady") },
        ]);
    }

    /// <summary>Opens the chat and asks there, with attachments taken through the data boundary.</summary>
    private async Task AskInChatAsync(string question, IEnumerable<(string Kind, string? Path, string Content, string Label)> context, AiTarget? target = null)
    {
        if (!AiEnabled || AiProvider.Length == 0 || AiModel.Length == 0)
        {
            Layout.ShowToolWindow(AiChatWindow, focus: true);
            return;
        }

        var boundary = new AiContext(Solutions.Solution?.Directory);
        var attachments = new List<AiAttachment>();
        var refused = new List<string>();
        foreach (var (kind, path, content, label) in context)
        {
            if (path is null)
            {
                attachments.Add(new AiAttachment(kind, label, content));
                continue;
            }

            var (attachment, why) = await boundary.AttachAsync(kind, path, content, label, CancellationToken.None).ConfigureAwait(true);
            if (attachment is not null)
            {
                attachments.Add(attachment);
            }
            else if (why is not null && !refused.Contains(why))
            {
                refused.Add(why);
            }
        }

        Layout.ShowToolWindow(AiChatWindow, focus: false);
        await SendInChatAsync(question, attachments, refused, target).ConfigureAwait(true);
    }

    /// <summary>The lines around <paramref name="line"/> (zero-based) of a text, numbered, and their range.</summary>
    private static (string Text, int First, int Last) Around(string text, int line, int lines = AiContextLines)
    {
        var all = text.Split('\n');
        var first = Math.Clamp(line - lines, 0, Math.Max(0, all.Length - 1));
        var last = Math.Clamp(line + lines, 0, Math.Max(0, all.Length - 1));
        var numbered = new StringBuilder();
        for (var i = first; i <= last; i++)
        {
            numbered.Append(i + 1).Append(": ").Append(all[i].TrimEnd('\r')).Append('\n');
        }

        return (numbered.ToString(), first, last);
    }

    private async Task ExplainProblemWithAiAsync()
    {
        if (ActiveEditor is not { Controller: { } controller } editor)
        {
            return;
        }

        var line = controller.Editor.TextArea.Caret.Line - 1;
        var diagnostics = Languages.Diagnostics.For(editor.Model.Path) ?? [];
        var problem = diagnostics.Where(d => d.Range.Start.Line <= line && d.Range.End.Line >= line).OrderBy(d => d.Severity).FirstOrDefault()
            ?? diagnostics.OrderBy(d => Math.Abs(d.Range.Start.Line - line)).FirstOrDefault();
        if (problem is null)
        {
            StatusBar.ShowMessage("There's no problem at the caret.", TimeSpan.FromSeconds(4));
            return;
        }

        var (code, first, last) = Around(editor.Model.Text.Text, problem.Range.Start.Line);
        var name = Path.GetFileName(editor.Model.Path);
        await AskInChatAsync(
            $"Explain this problem and how to fix it: {(problem.Code is { } c ? c + ": " : string.Empty)}{problem.Message} (line {problem.Range.Start.Line + 1}).",
            [("selection", editor.Model.Path, code, $"{name}, lines {first + 1}–{last + 1}")]).ConfigureAwait(true);
    }

    private async Task WriteTestsWithAiAsync()
    {
        if (ActiveEditor is not { Controller: { } controller } editor || controller.Editor.SelectionLength == 0)
        {
            return;
        }

        var framework = Solutions.Projects.Values.Where(p => p.IsTestProject).Select(p => p.TestPlatform).FirstOrDefault();
        await AskInChatAsync(
            "Write unit tests for this code" + (framework is null ? "." : $" (the solution's test projects use {framework}).") + " Cover the normal cases and the edge cases.",
            [("selection", editor.Model.Path, controller.Editor.SelectedText, $"The selection in {Path.GetFileName(editor.Model.Path)}")]).ConfigureAwait(true);
    }

    private async Task ExplainExceptionWithAiAsync()
    {
        if (Debugger.CurrentStop is not { Exception: { } exception })
        {
            StatusBar.ShowMessage("The program isn't stopped on an exception.", TimeSpan.FromSeconds(4));
            return;
        }

        var details = exception.Details;
        var text = new StringBuilder()
            .Append(details?.FullTypeName ?? exception.ExceptionId).Append(": ").Append(details?.Message ?? exception.Description).Append('\n')
            .Append(details?.StackTrace ?? string.Empty);
        for (var inner = details?.InnerException?.FirstOrDefault(); inner is not null; inner = inner.InnerException?.FirstOrDefault())
        {
            text.Append("\n---> ").Append(inner.FullTypeName ?? inner.TypeName).Append(": ").Append(inner.Message).Append('\n').Append(inner.StackTrace);
        }

        var context = new List<(string, string?, string, string)> { ("exception", null, text.ToString(), "The exception") };
        if (Debugger.SelectedFrame is { Source.Path: { } path } frame && File.Exists(path))
        {
            var (code, first, last) = Around(await File.ReadAllTextAsync(path).ConfigureAwait(true), frame.Line - 1);
            context.Add(("file", path, code, $"{Path.GetFileName(path)}, lines {first + 1}–{last + 1} (stopped at {frame.Line})"));
        }

        await AskInChatAsync("Explain this exception: why it happened here, and how to fix it.", context).ConfigureAwait(true);
    }

    private async Task ExplainTestFailureWithAiAsync()
    {
        var node = TestExplorer?.Selected?.Group.Node;
        if (node?.Result is not { } result || !result.Outcome.IsFailure())
        {
            StatusBar.ShowMessage("Select a failed test in the Test Explorer first.", TimeSpan.FromSeconds(4));
            return;
        }

        var failure = new StringBuilder();
        foreach (var message in result.Messages)
        {
            failure.Append(message.Message).Append('\n');
            if (message.Expected is not null || message.Actual is not null)
            {
                failure.Append("Expected: ").Append(message.Expected).Append("\nActual: ").Append(message.Actual).Append('\n');
            }

            if (message.StackTrace is { Length: > 0 } stack)
            {
                failure.Append(stack).Append('\n');
            }
        }

        var context = new List<(string, string?, string, string)> { ("test", null, failure.ToString(), $"The failure of {node.Label}") };
        if (node.Uri is { } uri && Codalus.LanguageServices.Protocol.DocumentUris.ToPath(uri) is { } path && File.Exists(path))
        {
            var (code, first, last) = Around(await File.ReadAllTextAsync(path).ConfigureAwait(true), node.Range?.StartLine ?? 0, 30);
            context.Add(("file", path, code, $"{Path.GetFileName(path)}, lines {first + 1}–{last + 1}"));
        }

        await AskInChatAsync($"Why does the test {node.Label} fail, and what should change: the test or the code it tests?", context).ConfigureAwait(true);
    }

    /// <summary>Streams a commit message for the staged changes into Git Changes' message box.</summary>
    private async Task DraftCommitMessageWithAiAsync()
    {
        if (!AiEnabled || AiProvider.Length == 0 || AiModel.Length == 0 || Git.Status is not { } status)
        {
            return;
        }

        var staged = GitChanges.Staged.Select(r => r.Status.Path).ToList();
        if (staged.Count == 0)
        {
            StatusBar.ShowMessage("Stage the changes to describe first.", TimeSpan.FromSeconds(4));
            return;
        }

        var boundary = new AiContext(Git.Root);
        var diff = new StringBuilder();
        var left = new List<string>();
        foreach (var path in staged)
        {
            var full = Path.IsPathRooted(path) ? path : Path.Combine(Git.Root ?? string.Empty, path);
            if (await boundary.RefusalAsync(full, CancellationToken.None).ConfigureAwait(true) is not null)
            {
                left.Add(Path.GetFileName(path));
                continue;
            }

            if (await Git.DiffAsync(path, staged: true).ConfigureAwait(true) is { IsBinary: false } file)
            {
                diff.Append("--- ").Append(path).Append('\n');
                foreach (var hunk in file.Hunks)
                {
                    diff.Append(hunk.Header).Append('\n');
                    foreach (var line in hunk.Lines)
                    {
                        diff.Append(line.Kind).Append(line.Text).Append('\n');
                    }
                }
            }

            if (diff.Length > 24_000)
            {
                diff.Append("…(the rest of the diff is left out)\n");
                break;
            }
        }

        var user = "Staged changes on " + (status.Branch ?? "the current branch") + ":\n" + diff
            + (left.Count > 0 ? "\nAlso changed (not shown): " + string.Join(", ", left) : string.Empty);
        var task = Workbench.Progress.Begin("Drafting the commit message with AI", "ai");
        var message = new StringBuilder();
        GitChanges.Message = string.Empty;
        try
        {
            await Plugins.ChatAsync(AiProvider, AiModel, [new AiMessageDto("system", CommitMessagePrompt), new AiMessageDto("user", user)], piece =>
            {
                lock (message)
                {
                    message.Append(piece);
                }

                Ui.Post(() =>
                {
                    lock (message)
                    {
                        GitChanges.Message = message.ToString().TrimStart();
                    }
                });
            }, CancellationToken.None, maxTokens: 400).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Codalus.Rpc.JsonRpcRemoteException or Codalus.Rpc.JsonRpcConnectionLostException)
        {
            Workbench.Notifications.Error("The commit message couldn't be drafted: " + ex.Message);
        }
        finally
        {
            task.End();
        }

        lock (message)
        {
            GitChanges.Message = message.ToString().Trim().Trim('`').Trim();
        }
    }
}
