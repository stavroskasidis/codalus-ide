using Avalonia.Headless.XUnit;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>The .resx resource editor (deferred from M11).</summary>
public sealed class ResxEditorTests
{
    [AvaloniaFact]
    public async Task Resource_files_open_as_a_table_that_edits_and_saves_their_strings()
    {
        await using var f = ShellFixture.Create();
        var folder = Path.Combine(f.UserData, "resx");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Strings.resx");
        var empty = Codalus.Workbench.CustomTools.ResxFile.Empty();
        empty.Set("Greeting", "Hello", "Shown on start");
        empty.Set("Farewell", "Bye", null);
        var text = empty.Save().Replace("</root>", "  <data name=\"Logo\" type=\"System.Resources.ResXFileRef, System.Windows.Forms\">\n    <value>Logo.png;System.Byte[], mscorlib</value>\n  </data>\n</root>", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);

        // Opening a .resx file shows the resource editor: strings editable, the image reference read-only.
        Assert.Null(await f.Shell.OpenFileAsync(path));
        var document = Assert.IsType<ResxEditorDocument>(f.Shell.Layout.ActiveDocument);
        await ShellFixture.PumpUntil(() => document.Rows.Count == 3);
        Assert.Equal(["Greeting", "Farewell", "Logo"], document.Rows.Select(r => r.Name));
        Assert.True(document.Rows[2].IsReadOnly);
        Assert.False(document.IsModified);

        // Edit, rename, add and remove, then save.
        document.Rows[0].Value = "Hello, world";
        document.Rows[1].Name = "Goodbye";
        document.AddCommand.Execute(null);
        document.Selected!.Name = "Title";
        document.Selected.Value = "Codalus";
        Assert.True(document.IsModified);
        document.Filter = "Title";
        Assert.Single(document.Visible);
        document.Filter = string.Empty;
        await f.Shell.SaveActiveDocumentAsync();
        Assert.False(document.IsModified);

        var saved = Codalus.Workbench.CustomTools.ResxFile.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Entries;
        Assert.Equal(["Greeting", "Goodbye", "Logo", "Title"], saved.Select(e => e.Name));
        Assert.Equal("Hello, world", saved[0].Value);
        Assert.Equal("Shown on start", saved[0].Comment);
        Assert.Equal("Codalus", saved[3].Value);

        // Duplicate names don't save.
        document.Rows[3].Name = "Greeting";
        await f.Shell.SaveActiveDocumentAsync();
        Assert.True(document.HasError);
        Assert.Contains("two resources named Greeting", document.Error, StringComparison.Ordinal);
        document.Rows[3].Name = "Title";
        document.Selected = document.Rows[3];
        document.RemoveCommand.Execute(null);
        await f.Shell.SaveActiveDocumentAsync();
        Assert.DoesNotContain("Title", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), StringComparison.Ordinal);

        // View Code opens the XML in the text editor.
        document.ViewCodeCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == path);
        Assert.Contains("<data name=\"Goodbye\"", f.Shell.ActiveEditor!.Model.Text.Text, StringComparison.Ordinal);
    }
}
