using Codalus.Shell.Infrastructure;
using Codalus.Shell.Views;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// Custom editors (plugin API 1.0, M22a): a file whose name a plugin's <c>customEditors</c> claim opens in a document
/// showing the plugin's declarative view of it. The plugin's new text makes it modified; Save writes it; Reopen With
/// Text Editor opens the file as text.
/// </summary>
public sealed partial class ShellHost
{
    public const string ReopenWithTextEditorCommand = "workbench.reopenWithTextEditor";

    private void InitializeCustomEditors()
    {
        Layout.DocumentClosed += (_, document) =>
        {
            if (document is CustomEditorDocument closed)
            {
                Plugins.CloseCustomEditor(closed.ViewId);
            }
        };
        Plugins.CustomEditorTextChanged += (_, change) => Ui.Post(() =>
        {
            if (Layout.Documents.OfType<CustomEditorDocument>().FirstOrDefault(d => d.ViewId == change.ViewId) is { } document)
            {
                document.SetText(change.Text);
            }
        });
        Workbench.Commands.Register(
            new CommandDescriptor(ReopenWithTextEditorCommand, "Reopen With Text Editor", ContributionSource.Shell)
            {
                Category = "View",
                Enablement = Codalus.Workbench.Context.WhenClause.Parse("activeDocumentKind == customEditor"),
            },
            async (_, _) =>
            {
                await Ui.RunAsync(async () =>
                {
                    if (Layout.ActiveDocument is CustomEditorDocument document)
                    {
                        var path = document.Path;
                        Layout.CloseDocument(document);
                        await OpenFileAsync(path, textEditor: true).ConfigureAwait(true);
                    }
                }).ConfigureAwait(false);
                return null;
            });
    }

    /// <summary>Opens <paramref name="path"/> in <paramref name="editor"/>; false (after saying why) when its plugin couldn't.</summary>
    private async Task<bool> OpenInCustomEditorAsync(string path, Workbench.Plugins.ContributedCustomEditor editor, bool preview)
    {
        if (Layout.FindDocument<CustomEditorDocument>(CustomEditorDocument.IdFor(path)) is { } open)
        {
            Layout.OpenDocument(open, preview, focus: !preview);
            return true;
        }

        try
        {
            var text = await File.ReadAllTextAsync(path).ConfigureAwait(true);
            var viewId = await Plugins.OpenCustomEditorAsync(editor, path, text, CancellationToken.None).ConfigureAwait(true);
            var document = new CustomEditorDocument(path, viewId, editor, Plugins);
            document.CloseRequested += (_, _) => _ = ConfirmCloseAsync(document);
            Layout.OpenDocument(document, preview, focus: !preview);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.customEditor.couldNotOpen", "{0} couldn't open in {1}: {2}", Path.GetFileName(path), editor.DisplayName, ex.Message));
            return false;
        }
    }

    private async Task<bool> SaveCustomEditorAsync(CustomEditorDocument document)
    {
        try
        {
            await document.SaveAsync().ConfigureAwait(true);
            StatusBar.ShowMessage(Localizer.Format("msg.editor.saved", "Saved {0}", document.Title), TimeSpan.FromSeconds(3));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.customEditor.couldNotSave", "{0} couldn't be saved: {1}", document.Title, ex.Message));
            return false;
        }
    }
}
