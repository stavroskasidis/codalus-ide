using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Codalus.Shell.Editor;
using Codalus.Workbench.Snippets;

namespace Codalus.Shell.Tests;

/// <summary>M12b in the editor: snippets.</summary>
public class EditingDepthTests
{
    private static async Task<CodeEditorController> OpenAsync(ShellFixture f, string path)
    {
        var document = await f.Shell.OpenFileAsync(path);
        Assert.NotNull(document);
        await ShellFixture.PumpUntil(() => document.Controller is not null);
        return document.Controller!;
    }

    private static string Snippet(ShellFixture f, string name) => f.Shell.Workbench.Snippets.For("csharp").Single(s => s.Name == name).Body;

    [AvaloniaFact]
    public async Task Editing_commands_history_autorecover_and_format_on_save()
    {
        await using var f = ShellFixture.Create();
        var folder = await f.OpenLanguageFeaturesAsync();
        var shapesPath = Path.Combine(folder, "src", "Lib", "Shapes.cs");
        var editor = await OpenAsync(f, shapesPath);
        var text = editor.Model.Text;
        int LineOf(string marker) => text.GetLineByOffset(text.Text.IndexOf(marker, StringComparison.Ordinal)).LineNumber;

        // Duplicate a line, then move it up and down.
        editor.Editor.CaretOffset = text.Text.IndexOf("public double Radius", StringComparison.Ordinal);
        editor.Editor.TextArea.Focus();
        f.Shell.Run("editor.action.duplicateSelection");
        Assert.Equal(2, text.Text.Split("public double Radius").Length - 1);
        Assert.Equal(LineOf("public double Radius") + 1, editor.Caret.Line);
        var before = editor.Caret.Line;
        f.Shell.Run("editor.action.moveLinesUpAction");
        Assert.Equal(before - 1, editor.Caret.Line);
        f.Shell.Run("editor.action.moveLinesDownAction");
        Assert.Equal(before, editor.Caret.Line);
        editor.Editor.Undo();
        editor.Editor.Undo();
        editor.Editor.Undo();
        Assert.Equal(1, text.Text.Split("public double Radius").Length - 1);

        // Expand Selection goes outwards through the syntax; Shrink goes back.
        editor.Editor.CaretOffset = text.Text.IndexOf("Radius * Radius", StringComparison.Ordinal) + 2;
        await editor.ExpandSelectionAsync();
        Assert.Equal("Radius", editor.Editor.SelectedText);
        await editor.ExpandSelectionAsync();
        Assert.Equal("System.Math.PI * Radius", editor.Editor.SelectedText);
        await editor.ExpandSelectionAsync();
        Assert.Equal("System.Math.PI * Radius * Radius", editor.Editor.SelectedText);
        editor.ShrinkSelection();
        Assert.Equal("System.Math.PI * Radius", editor.Editor.SelectedText);
        editor.Editor.TextArea.ClearSelection();

        // Typing /// above a method writes its documentation comment.
        var describe = text.GetLineByNumber(LineOf("public override string Describe(int precision) => \"Circle"));
        text.Insert(describe.Offset, "    //\n");
        editor.Editor.CaretOffset = describe.Offset + "    //".Length;
        editor.Editor.TextArea.PerformTextInput("/");
        await ShellFixture.PumpUntil(() => text.Text.Contains("/// <param name=\"precision\"></param>", StringComparison.Ordinal), seconds: 30);
        Assert.Contains("/// <returns></returns>", text.Text, StringComparison.Ordinal);
        Assert.Equal(LineOf("/// <param name=\"precision\"></param>") - 2, editor.Caret.Line); // on the summary's empty line

        // Bookmarks: toggled on the caret's line, moved by an edit above, visited with Next.
        var circleLine = LineOf("public sealed class Circle");
        editor.Editor.CaretOffset = text.GetLineByNumber(circleLine).Offset;
        f.Shell.Run("bookmarks.toggle");
        await ShellFixture.PumpUntil(() => editor.BookmarkMargin.Lines.SequenceEqual([circleLine]));
        text.Insert(0, "// moved\n");
        await ShellFixture.PumpUntil(() => f.Shell.Bookmarks.For(shapesPath).Single().Line == circleLine + 1);
        editor.Editor.CaretOffset = 0;
        await f.Shell.NextBookmarkAsync(1);
        Assert.Equal(circleLine + 1, editor.Caret.Line);
        Assert.Single(f.Shell.BookmarkList.Rows);
        text.Remove(0, "// moved\n".Length);

        // The Task List collects TODO and HACK comments, unsaved text included.
        text.Insert(0, "// TODO: tidy the shapes\n/* HACK keep for now */\n");
        await f.Shell.TaskList.RefreshAsync();
        Assert.Contains(f.Shell.TaskList.Rows, r => r is { Token: "TODO", Text: "tidy the shapes", Line: 1 } && r.Path == shapesPath);
        Assert.Contains(f.Shell.TaskList.Rows, r => r is { Token: "HACK", Text: "keep for now", Line: 2 });
        f.Shell.TaskList.Filter = "tidy";
        Assert.Single(f.Shell.TaskList.Rows);
        text.Remove(0, "// TODO: tidy the shapes\n/* HACK keep for now */\n".Length);

        // The clipboard ring keeps copies.
        editor.Editor.Select(text.Text.IndexOf("IShape", StringComparison.Ordinal), "IShape".Length);
        f.Press(Key.C, RawInputModifiers.Control);
        Assert.Equal("IShape", f.Shell.ClipboardRing[0]);

        // Format on save; each save is kept in the local history.
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.FormatOnSaveSetting, true, Workbench.Settings.SettingScope.User, TestContext.Current.CancellationToken);
        text.Insert(text.TextLength, "\ninternal static class Spacing{public static int  Value=1 ;}\n");
        await f.Shell.SaveActiveDocumentAsync();
        Assert.Contains("public static int Value = 1;", await File.ReadAllTextAsync(shapesPath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        text.Insert(text.TextLength, "// second\n");
        await f.Shell.SaveActiveDocumentAsync();
        await ShellFixture.PumpUntil(() => f.Shell.History.Entries(shapesPath).Count == 2);
        var first = f.Shell.History.Entries(shapesPath)[1];
        ShellHost.RestoreFromHistory(editor.Model, first.Read());
        Assert.DoesNotContain("// second", text.Text, StringComparison.Ordinal);
        Assert.True(editor.Model.IsModified);

        // AutoRecover: modified documents are backed up, and a clean exit removes the backups.
        f.Shell.BackUpModifiedDocuments();
        var backups = Path.Combine(f.UserData, "state", "backups");
        await ShellFixture.PumpUntil(() => Directory.Exists(backups) && Directory.EnumerateFiles(backups).Any());
        f.Shell.DeleteAllBackups();
        Assert.False(Directory.Exists(backups));
    }

    [AvaloniaFact]
    public async Task The_dialog_checklist_reorders_and_returns_the_checked_items()
    {
        var dialogs = new ViewModels.DialogViewModel();
        var showing = dialogs.ShowAsync(new ViewModels.DialogRequest("Change Signature", "Reorder", ["OK", "Cancel"])
        {
            Choices = [new("a", "int a", true), new("b", "int b", true), new("c", "int c", true)],
            Reorderable = true,
        });
        Assert.True(dialogs.HasChoices);
        dialogs.SelectedChoice = dialogs.Choices[2];
        dialogs.MoveChoice(-1); // c before b
        dialogs.Choices.Single(c => c.Id == "a").IsChecked = false;
        dialogs.Accept();
        var result = await showing;
        Assert.Equal(["c", "b"], result.Checked);
    }

    [Fact]
    public void Json_becomes_classes()
    {
        var code = Infrastructure.JsonClassGenerator.Generate("""
            { "id": 1, "name": "Ada", "big": 9999999999, "score": 1.5, "tags": ["a"], "created_at": "2026-10-10T12:00:00Z",
              "orders": [ { "id": 1, "total": 2 }, { "id": 2, "total": 2.5, "note": null } ] }
            """, "Root");

        Assert.Contains("public class Root", code, StringComparison.Ordinal);
        Assert.Contains("public int Id { get; set; }", code, StringComparison.Ordinal);
        Assert.Contains("public long Big { get; set; }", code, StringComparison.Ordinal);
        Assert.Contains("public double Score { get; set; }", code, StringComparison.Ordinal);
        Assert.Contains("public List<string> Tags { get; set; }", code, StringComparison.Ordinal);
        Assert.Contains("[System.Text.Json.Serialization.JsonPropertyName(\"created_at\")]", code, StringComparison.Ordinal);
        Assert.Contains("public DateTimeOffset CreatedAt { get; set; }", code, StringComparison.Ordinal);
        Assert.Contains("public List<Order> Orders { get; set; }", code, StringComparison.Ordinal);
        Assert.Contains("public class Order", code, StringComparison.Ordinal);
        Assert.Contains("public double Total { get; set; }", code, StringComparison.Ordinal); // the widest of 2 and 2.5
    }

    [AvaloniaFact]
    public async Task Snippets_insert_with_fields_surround_selections_and_come_from_completion_and_imports()
    {
        await using var f = ShellFixture.Create();
        var folder = await f.OpenLanguageFeaturesAsync();
        var editor = await OpenAsync(f, Path.Combine(folder, "src", "Lib", "Shapes.cs"));
        var text = editor.Model.Text;

        // prop: the type field is selected; typing replaces it, Tab goes to the name, Enter ends at $0.
        var anchor = text.Text.IndexOf("public static double Scale(IShape", StringComparison.Ordinal);
        editor.Editor.CaretOffset = anchor;
        editor.Editor.TextArea.Focus();
        editor.InsertSnippet(Snippet(f, "prop"), surround: false);
        ShellFixture.Pump();
        f.Type("string");
        f.Press(Key.Tab);
        f.Type("Name");
        f.Press(Key.Enter);
        Assert.Contains("public string Name { get; set; }", text.Text, StringComparison.Ordinal);

        // propfull mirrors the type and the field name.
        editor.Editor.CaretOffset = text.Text.IndexOf("public static double Scale(IShape", StringComparison.Ordinal);
        editor.InsertSnippet(Snippet(f, "propfull"), surround: false);
        ShellFixture.Pump();
        f.Type("long");
        f.Press(Key.Enter);
        Assert.Contains("private long myVar;", text.Text, StringComparison.Ordinal);
        Assert.Contains("public long MyProperty", text.Text, StringComparison.Ordinal);

        // Surround With: the selected statement inside an if.
        var target = "shape.Area() * factor";
        var start = text.Text.IndexOf(target, StringComparison.Ordinal);
        editor.Editor.Select(start, target.Length);
        editor.InsertSnippet(Snippet(f, "region"), surround: true);
        ShellFixture.Pump();
        f.Press(Key.Enter);
        Assert.Matches(@"#region MyRegion\s+shape\.Area\(\) \* factor\s+#endregion", text.Text);
        editor.Editor.Undo();

        // Completion offers snippets after the language service's items; committing one inserts it with its fields.
        var line = text.GetLineByOffset(text.Text.IndexOf("public static double Scale(IShape", StringComparison.Ordinal));
        editor.Editor.CaretOffset = line.Offset;
        f.Type("cw");
        editor.ShowCompletion();
        await ShellFixture.PumpUntil(() => editor.CompletionWindow?.CompletionList.CompletionData.Any(d => d.Text == "cw") == true, seconds: 90);
        editor.CompletionWindow!.CompletionList.SelectItem("cw");
        await ShellFixture.PumpUntil(() => (editor.CompletionWindow?.CompletionList.SelectedItem as CompletionData)?.Item.Kind == LanguageServices.Protocol.CompletionItemKind.Snippet);
        f.Press(Key.Enter);
        await ShellFixture.PumpUntil(() => text.Text.Contains("Console.WriteLine();", StringComparison.Ordinal));

        // Visual Studio snippets import into the user's snippets.
        f.Shell.ImportSnippets(SnippetRegistry.ParseVisualStudio("""
            <CodeSnippets xmlns="http://schemas.microsoft.com/VisualStudio/2005/CodeSnippet">
              <CodeSnippet Format="1.0.0">
                <Header><Title>Guard</Title><Shortcut>guard</Shortcut></Header>
                <Snippet>
                  <Declarations><Literal><ID>arg</ID><Default>value</Default></Literal></Declarations>
                  <Code Language="csharp"><![CDATA[ArgumentNullException.ThrowIfNull($arg$);$end$]]></Code>
                </Snippet>
              </CodeSnippet>
            </CodeSnippets>
            """, "user"));
        var guard = f.Shell.Workbench.Snippets.For("csharp").Single(s => s.Name == "Guard");
        Assert.Equal(["guard"], guard.Prefixes);
        Assert.True(File.Exists(Path.Combine(f.UserData, "config", "snippets", "csharp.json")));
    }
}
