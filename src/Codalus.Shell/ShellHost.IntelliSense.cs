using System.Globalization;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Context;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// What the editor's IntelliSense popups need from the shell: the keys their tooltips and buttons name, the context
/// keys the completion list's filter shortcuts depend on, and Quick Info's buttons: Fix (the top code fix), Other Fixes
/// (the code actions menu) and Add to Chat (AI Chat, through AiService, only while AI is on).
/// </summary>
public sealed partial class ShellHost
{
    string? IEditorServices.KeyLabel(string commandId) => KeyLabelOf(commandId);

    void IEditorServices.SetContextKey(string key, bool value) => Workbench.Context.Set(key, value);

    /// <summary>
    /// The key a command is bound to in the current scheme, as menus show it: the highest-precedence rule whose when-clause
    /// holds, the editor taken as focused (Quick Info shows while the pointer, not the focus, is in it).
    /// </summary>
    internal string? KeyLabelOf(string commandId)
    {
        var context = new EditorFocusedContext(Workbench.Context);
        var rules = Workbench.Keybindings.EffectiveRules;
        for (var i = rules.Count - 1; i >= 0; i--)
        {
            if (rules[i] is { Keys: { } keys } rule && rule.Command == commandId && rule.When.Evaluate(context))
            {
                return keys.ToLabel(Workbench.Platform);
            }
        }

        return null;
    }

    /// <summary>The context with the editor focused and its IntelliSense open, for the labels of keys that apply there.</summary>
    private sealed class EditorFocusedContext(IContextKeyReader context) : IContextKeyReader
    {
        public object? GetValue(string key) => key is "editorTextFocus" or "suggestWidgetVisible" ? true : context.GetValue(key);
    }

    Task IEditorServices.ApplyTopFixAsync(CodeEditorController editor, Diagnostic diagnostic) => ApplyTopFixAsync(editor, diagnostic);

    /// <summary>Alt+.: the top code fix for the diagnostic at the caret (or the first on its line).</summary>
    public Task ApplyTopFixAtCaretAsync()
    {
        if (ActiveEditor?.Controller is not { } controller || DiagnosticAtCaret(controller) is not { } diagnostic)
        {
            ShowMessage(Localizer.Get("msg.intelliSense.noDiagnosticHere", "There's no error or warning at the caret."));
            return Task.CompletedTask;
        }

        return ApplyTopFixAsync(controller, diagnostic);
    }

    private Diagnostic? DiagnosticAtCaret(CodeEditorController controller)
    {
        var caret = controller.Editor.CaretOffset;
        var here = controller.Markers.At(caret);
        var line = controller.Model.Text.GetLineByOffset(caret).LineNumber - 1;
        return here.Count > 0 ? here[0] : Languages.Diagnostics.For(controller.Model.Path).Where(d => d.Range.Start.Line == line).OrderBy(d => d.Severity).FirstOrDefault();
    }

    /// <summary>
    /// Applies the language service's top fix for <paramref name="diagnostic"/>: its preferred quick fix, else its first
    /// one, else the first refactoring there. Suppressions, severity changes and actions that ask first are left to the
    /// code actions menu.
    /// </summary>
    public async Task ApplyTopFixAsync(CodeEditorController editor, Diagnostic diagnostic)
    {
        if (((IEditorServices)this).Client(editor.Model) is not { } client || editor.Model.IsReadOnly)
        {
            return;
        }

        try
        {
            StatusBar.ShowMessage(Localizer.Get("msg.refactoring.lookingForCodeActions", "Looking for code actions…"));
            var actions = await client.CodeActionsAsync(editor.Model.Uri, diagnostic.Range, [diagnostic], CancellationToken.None).ConfigureAwait(true);
            StatusBar.ClearMessage();
            static bool Direct(CodeAction a) => !a.Title.EndsWith('…') && !a.Title.StartsWith("Suppress ", StringComparison.Ordinal) && !a.Title.StartsWith("Configure ", StringComparison.Ordinal);
            var top = actions.Where(a => a.Kind == CodeActionKind.QuickFix && Direct(a)).OrderBy(a => a.IsPreferred == true ? 0 : 1).FirstOrDefault()
                ?? actions.FirstOrDefault(a => a.Kind != CodeActionKind.QuickFix && Direct(a));
            if (top is null)
            {
                ShowMessage(Localizer.Format("msg.intelliSense.noFix", "No fix for {0}.", diagnostic.Code ?? diagnostic.Message));
                return;
            }

            var resolved = top.Edit is null ? await client.ResolveCodeActionAsync(top, CancellationToken.None).ConfigureAwait(true) : top;
            if (resolved.Edit is not { } edit || (edit.DocumentChanges.Length == 0 && edit.ResourceOperations.Length == 0))
            {
                ShowMessage(Localizer.Get("msg.intelliSense.fixMadeNoChanges", "The fix made no changes."));
                return;
            }

            if (await ApplyWorkspaceEditAsync(edit).ConfigureAwait(true))
            {
                StatusBar.ShowMessage(Localizer.Format("msg.intelliSense.applied", "Applied: {0}", top.Title), TimeSpan.FromSeconds(4));
            }
        }
        catch (Exception ex) when (ex is Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or OperationCanceledException)
        {
            StatusBar.ClearMessage();
            LanguageLog.AppendLine($"[code actions] the top fix failed: {ex.Message}");
        }
    }

    /// <summary>Quick Info's Other Fixes: the caret goes to the diagnostic and the code actions menu opens there.</summary>
    async Task IEditorServices.ShowCodeActionsAtAsync(CodeEditorController editor, Diagnostic diagnostic)
    {
        editor.Editor.TextArea.ClearSelection();
        editor.Editor.CaretOffset = editor.Model.OffsetAt(diagnostic.Range.Start);
        editor.Editor.TextArea.Focus();
        await ShowCodeActionsAsync().ConfigureAwait(true);
    }

    /// <summary>Quick Info's Add to Chat: asks AI Chat about the diagnostic, with the code around it (through AiService).</summary>
    async Task IEditorServices.AddDiagnosticToChatAsync(CodeEditorController editor, Diagnostic diagnostic)
    {
        if (!await EnsureAiAsync().ConfigureAwait(true))
        {
            return;
        }

        var path = editor.Model.Path;
        var line = diagnostic.Range.Start.Line + 1;
        var where = Path.GetFileName(path) + ":" + line.ToString(CultureInfo.InvariantCulture);
        var items = new List<AiContextItem>
        {
            new(AiContextKind.Diagnostics, Localizer.Get("msg.ai.diagnostic", "the error"), (diagnostic.Code is { Length: > 0 } code ? code + ": " : string.Empty) + diagnostic.Message + " (" + where + ")")
            {
                Path = path,
                StartLine = line,
            },
        };
        if (OpenTextOf(path) is not null || File.Exists(path))
        {
            items.Add(await LinesAroundAsync(path, line).ConfigureAwait(true));
        }

        await AiChat.AskAsync(AiActions.ExplainDiagnosticQuestion, items).ConfigureAwait(true);
    }
}
