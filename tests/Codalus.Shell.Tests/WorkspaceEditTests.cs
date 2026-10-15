using Avalonia.Headless.XUnit;
using Codalus.LanguageServices.Protocol;

namespace Codalus.Shell.Tests;

/// <summary>
/// Deferred work: a workspace edit's renamed and deleted files (Rename File to Match Type, LSP's file operations), after
/// its text edits; an open file is saved, renamed and reopened; a deleted one is closed.
/// </summary>
public sealed class WorkspaceEditTests
{
    [AvaloniaFact]
    public async Task Renamed_and_deleted_files_follow_the_text_edits()
    {
        await using var f = ShellFixture.Create();
        var folder = Path.Combine(Path.GetTempPath(), "codalus-edits-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var widget = Path.Combine(folder, "Widget.cs");
            var obsolete = Path.Combine(folder, "Obsolete.cs");
            await File.WriteAllTextAsync(widget, "class Gadget { }\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(obsolete, "// old\n", TestContext.Current.CancellationToken);
            var open = await f.Shell.OpenFileAsync(widget, preview: false, null);
            await ShellFixture.PumpUntil(() => open?.Controller is not null);

            // The edit changes the open file's text, renames it to Gadget.cs, and deletes Obsolete.cs (closed).
            var gadget = Path.Combine(folder, "Gadget.cs");
            var edit = new WorkspaceEdit([new TextDocumentEdit(new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(widget), null),
                [new TextEdit(new LanguageServices.Protocol.Range(new Position(0, 0), new Position(0, 0)), "// moved\n")])])
            {
                ResourceOperations =
                [
                    new ResourceOperation(ResourceOperationKinds.Rename, DocumentUris.FromPath(widget), DocumentUris.FromPath(gadget)),
                    new ResourceOperation(ResourceOperationKinds.Delete, DocumentUris.FromPath(obsolete)),
                ],
            };
            Assert.True(await f.Shell.ApplyWorkspaceEditAsync(edit));

            Assert.False(File.Exists(widget));
            Assert.Equal("// moved\nclass Gadget { }\n", await File.ReadAllTextAsync(gadget, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(obsolete));
            Assert.DoesNotContain(f.Shell.Editors, e => e.Model.Path == widget);
            Assert.Contains(f.Shell.Editors, e => e.Model.Path == gadget);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
