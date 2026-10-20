using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Plugins.Protocol;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>
/// Declarative views and custom editors (plugin API 1.0, M22a) with samples/plugins/TodoBoard: a <c>*.todos.json</c>
/// file opens in the plugin's board, its events reach the plugin, its new text saves, and its tool window follows.
/// </summary>
public sealed class DeclarativeViewTests
{

    [AvaloniaFact]
    public async Task A_custom_editor_shows_the_plugins_view_and_saves_its_text()
    {
        await using var f = ShellFixture.Create(builtInPlugins: Path.Combine(ShellFixture.RepoRoot, "samples", "plugins", "TodoBoard", "bin", ShellFixture.Configuration, "net10.0"));
        var file = Path.Combine(f.UserData, "chores.todos.json");
        File.WriteAllText(file, """[ { "title": "Water the plants", "done": false }, { "title": "Buy milk", "done": true } ]""");

        // The file opens in the board, not the text editor.
        Assert.Null(await f.Shell.OpenFileAsync(file));
        await ShellFixture.PumpUntil(() => f.Shell.Layout.ActiveDocument is CustomEditorDocument, seconds: 60);
        var document = (CustomEditorDocument)f.Shell.Layout.ActiveDocument!;
        Assert.Equal("Todo Board", document.Editor.DisplayName);
        await ShellFixture.PumpUntil(() => f.Shell.Plugins.GetView(document.ViewId) is not null, seconds: 60);

        // Rendered with the shell's controls: the table has the file's two items.
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<Controls.DeclarativeView>().Any(v => v.Root is not null));
        var list = f.Window.GetVisualDescendants().OfType<ListBox>().First(l => Avalonia.Automation.AutomationProperties.GetName(l) == "items");
        Assert.Equal(2, list.ItemCount);

        // Selecting the first item and checking it: the plugin gives the file's new text, and the document is modified.
        list.SelectedIndex = 0;
        await ShellFixture.PumpUntil(() => Text(f, document.ViewId).Contains("Done: Water the plants", StringComparison.Ordinal));
        await f.Shell.Plugins.SendViewEventAsync(new ViewEventParams(document.ViewId, "done", "change", "true", null), CancellationToken.None);
        await ShellFixture.PumpUntil(() => document.IsModified);
        Assert.Contains("2 of 2 done", Text(f, document.ViewId), StringComparison.Ordinal);

        // A new item from the text box.
        await f.Shell.Plugins.SendViewEventAsync(new ViewEventParams(document.ViewId, "new", "change", "Call the vet", null), CancellationToken.None);
        await ShellFixture.PumpUntil(() => Text(f, document.ViewId).Contains("2 of 3 done", StringComparison.Ordinal));
        await ShellFixture.PumpUntil(() => document.PendingText?.Contains("Call the vet", StringComparison.Ordinal) == true);

        // Saved: the file has the plugin's text.
        await f.Shell.SaveActiveDocumentAsync();
        Assert.False(document.IsModified);
        var saved = File.ReadAllText(file);
        Assert.Contains("\"Call the vet\"", saved, StringComparison.Ordinal);
        Assert.Contains("\"done\": true", saved, StringComparison.Ordinal);

        // The plugin's tool window (kind "view") lists the open board.
        Assert.Contains("chores.todos.json", Text(f, "todoboard.summary"), StringComparison.Ordinal);

        // Reopen With Text Editor: the same file as text; the board's view goes.
        await f.RunAsync(ShellHost.ReopenWithTextEditorCommand);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == file);
        await ShellFixture.PumpUntil(() => f.Shell.Plugins.GetView(document.ViewId) is null);
        await ShellFixture.PumpUntil(() => !Text(f, "todoboard.summary").Contains("chores.todos.json", StringComparison.Ordinal));
    }

    /// <summary>A view's texts, its table cells included, in one string.</summary>
    private static string Text(ShellFixture f, string viewId)
    {
        static IEnumerable<string> Walk(ViewNodeDto? node) => node is null ? [] :
            new[] { node.Text, node.Value }.Where(t => t is not null).Select(t => t!)
                .Concat((node.Rows ?? []).SelectMany(r => r))
                .Concat((node.Children ?? []).SelectMany(Walk));
        return string.Join(" | ", Walk(f.Shell.Plugins.GetView(viewId)));
    }
}
