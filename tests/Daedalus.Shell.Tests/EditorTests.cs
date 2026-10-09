using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Daedalus.LanguageServices.Protocol;
using Daedalus.Shell.Editor;
using Daedalus.Shell.ViewModels;
using Daedalus.Workbench.Settings;

namespace Daedalus.Shell.Tests;

/// <summary>The editor with the real build host and C# language service on samples/LanguageFeatures.</summary>
public class EditorTests
{
    private static async Task<CodeEditorController> OpenAsync(ShellFixture f, string path, bool preview = false)
    {
        var document = await f.Shell.OpenFileAsync(path, preview);
        Assert.NotNull(document);
        await ShellFixture.PumpUntil(() => document.Controller is not null);
        return document.Controller!;
    }

    private static void MoveCaret(CodeEditorController editor, string marker, int delta = 0)
    {
        var offset = editor.Model.Text.Text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(offset >= 0, marker);
        editor.Editor.CaretOffset = offset + delta;
        editor.Editor.TextArea.Focus();
        ShellFixture.Pump();
    }

    [AvaloniaFact]
    public async Task Csharp_files_get_highlighting_folding_diagnostics_and_the_error_list()
    {
        await using var f = ShellFixture.Create();
        var folder = await f.OpenLanguageFeaturesAsync();

        var shapes = await OpenAsync(f, Path.Combine(folder, "src", "Lib", "Shapes.cs"));
        await ShellFixture.PumpUntil(() => shapes.Colorizer.HasTokens && shapes.Folding.AllFoldings.Any());
        Assert.Contains(shapes.Folding.AllFoldings, folding => folding.Title == "Squares");
        Assert.Equal("C#", f.Shell.StatusBar.Right.Single(i => i.Item.Id == "daedalus.editor.language").Text);
        Assert.Contains(f.Window.GetVisualDescendants().OfType<Minimap>(), m => m.IsEffectivelyVisible);

        var reporter = await OpenAsync(f, Path.Combine(folder, "src", "App", "Reporter.cs"));
        await ShellFixture.PumpUntil(() => reporter.Markers.Segments.Any(s => s.Diagnostic.Code == "CA2200"), seconds: 60);
        await ShellFixture.PumpUntil(() => f.Shell.ErrorList.Rows.Any(r => r.Code == "CA2200"));
        var row = f.Shell.ErrorList.Rows.First(r => r.Code == "CA2200");
        Assert.Equal("App", row.Project);
        Assert.Equal("Reporter.cs", row.File);

        // Typing an error shows a squiggle and an Error List entry; undoing removes them.
        MoveCaret(reporter, "Console.WriteLine(int.Parse(");
        reporter.Editor.TextArea.PerformTextInput("int broken = \"x\"; ");
        await ShellFixture.PumpUntil(() => f.Shell.ErrorList.Rows.Any(r => r.Code == "CS0029"), seconds: 60);
        reporter.Editor.Undo();
        await ShellFixture.PumpUntil(() => f.Shell.ErrorList.Rows.All(r => r.Code != "CS0029"), seconds: 60);

        // The Error List opens the location.
        await f.Shell.ErrorList.OpenAsync(row);
        Assert.Equal(row.Line, f.Shell.ActiveEditor!.Controller!.Caret.Line);
    }

    [AvaloniaFact]
    public async Task Completion_signature_help_and_navigation_work_in_the_editor()
    {
        await using var f = ShellFixture.Create();
        var folder = await f.OpenLanguageFeaturesAsync();
        var program = await OpenAsync(f, Path.Combine(folder, "src", "App", "Program.cs"));

        // Typing "." after a variable opens member completion; Enter commits the selection.
        MoveCaret(program, "circle.Describe(2)", "circle".Length);
        program.Model.Text.Remove(program.Editor.CaretOffset, ".Describe".Length);
        program.Editor.TextArea.PerformTextInput(".");
        await ShellFixture.PumpUntil(() => program.CompletionWindow is { } w && w.CompletionList.CompletionData.Count > 0, seconds: 60); // the first request loads Roslyn's completion providers
        Assert.Contains(program.CompletionWindow!.CompletionList.CompletionData, d => d.Text == "Radius");
        program.Editor.TextArea.PerformTextInput("Desc");
        ShellFixture.Pump();
        program.CompletionWindow!.CompletionList.RequestInsertion(EventArgs.Empty);
        await ShellFixture.PumpUntil(() => program.CompletionWindow is null);
        Assert.Contains("circle.Describe(2)", program.Model.Text.Text, StringComparison.Ordinal);

        // Signature help after "(" and ",".
        MoveCaret(program, "Geometry.Scale(circle, 3)", "Geometry.Scale(circle,".Length);
        program.ShowSignatureHelp();
        await ShellFixture.PumpUntil(() => program.SignatureWindow is not null);
        Assert.Equal(2, program.SignatureWindow!.Provider!.Count);

        // F12 on Circle opens Shapes.cs at the class; Navigate Backward returns.
        MoveCaret(program, "new Circle", "new C".Length);
        f.Shell.Run("editor.action.revealDefinition");
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Name == "Shapes.cs" && f.Shell.ActiveEditor.Controller is not null);
        var shapes = f.Shell.ActiveEditor!.Controller!;
        Assert.Contains("class Circle", shapes.Model.Text.GetText(shapes.Model.Text.GetLineByOffset(shapes.Editor.CaretOffset)), StringComparison.Ordinal);
        await f.Shell.NavigateBackAsync();
        Assert.Equal("Program.cs", f.Shell.ActiveEditor?.Model.Name);

        // Source-generated code opens read-only.
        MoveCaret(program, "Default.Person", "Default.".Length);
        await program.GoToDefinitionAsync();
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.IsReadOnly == true);
        Assert.Contains("AppJsonContext", f.Shell.ActiveEditor!.Model.Text.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Preview_tabs_splits_and_external_changes()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("LanguageFeatures");
        var lib = Path.Combine(folder, "src", "Lib");

        // A preview replaces the previous preview; editing promotes it.
        var first = await f.Shell.OpenFileAsync(Path.Combine(lib, "Shapes.cs"), preview: true);
        Assert.True(first!.IsPreview);
        var second = await f.Shell.OpenFileAsync(Path.Combine(lib, "Conditional.cs"), preview: true);
        await ShellFixture.PumpUntil(() => second!.Controller is not null);
        Assert.DoesNotContain(f.Shell.Editors, e => e.Model.Name == "Shapes.cs");
        second!.Controller!.Editor.Document.Insert(0, "// edited\n");
        Assert.False(second.IsPreview);
        await f.Shell.OpenFileAsync(Path.Combine(lib, "Square.Part.cs"), preview: true);
        Assert.Equal(2, f.Shell.Editors.Count());

        // Split right: a second view of the same text; closing it doesn't ask to save.
        f.Shell.Layout.OpenDocument(second);
        f.Shell.SplitActiveEditor(below: false);
        var views = f.Shell.Editors.Where(e => e.Model == second.Model).ToList();
        Assert.Equal(2, views.Count);
        f.Shell.Layout.CloseDocument(views[1]);
        ShellFixture.Pump();
        Assert.Single(f.Shell.Editors, e => e.Model == second.Model);
        Assert.False(f.Shell.Dialogs.IsOpen);

        // An unmodified document reloads when the file changes on disk.
        var square = f.Shell.Editors.Single(e => e.Model.Name == "Square.Part.cs");
        await File.WriteAllTextAsync(Path.Combine(lib, "Square.Part.cs"), "namespace Lib;\n// changed outside\n");
        await ShellFixture.PumpUntil(() => square.Model.Text.Text.Contains("changed outside", StringComparison.Ordinal), seconds: 10);
        Assert.False(square.Model.IsModified);
    }

    [AvaloniaFact]
    public async Task Multiple_carets_type_together_and_undo_as_one_step()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("LanguageFeatures");
        var editor = await OpenAsync(f, Path.Combine(folder, "src", "Lib", "Square.Part.cs"));
        var text = editor.Model.Text;
        editor.Editor.CaretOffset = text.GetLineByNumber(3).Offset;
        editor.Editor.TextArea.Focus();
        editor.MultiCaret.AddCaretVertically(+1);
        editor.MultiCaret.AddCaretVertically(+1);
        Assert.Equal(2, editor.MultiCaret.ExtraCarets.Count);

        editor.Editor.TextArea.PerformTextInput("X");
        Assert.Equal('X', text.GetCharAt(text.GetLineByNumber(3).Offset));
        Assert.Equal('X', text.GetCharAt(text.GetLineByNumber(4).Offset));
        Assert.Equal('X', text.GetCharAt(text.GetLineByNumber(5).Offset));
        Assert.True(editor.MultiCaret.HandleKey(new KeyEventArgs { Key = Key.Back }));
        Assert.DoesNotContain("X", text.Text, StringComparison.Ordinal);
        editor.Editor.TextArea.PerformTextInput("Y");
        editor.Editor.Undo();
        Assert.DoesNotContain("Y", text.Text, StringComparison.Ordinal);
        editor.MultiCaret.Clear();
        Assert.Empty(editor.MultiCaret.ExtraCarets);

        // Shift+Alt+. adds the next occurrence of the word at the caret.
        editor.Editor.CaretOffset = text.Text.IndexOf("Side", StringComparison.Ordinal) + 1;
        editor.MultiCaret.SelectNextOccurrence(); // selects the word
        editor.MultiCaret.SelectNextOccurrence(); // adds the next "Side"
        Assert.Single(editor.MultiCaret.ExtraCarets);
    }

    [AvaloniaFact]
    public async Task Files_keep_their_encoding_and_line_endings()
    {
        await using var f = ShellFixture.Create();
        var path = Path.Combine(f.UserData, "crlf.txt");
        await File.WriteAllBytesAsync(path, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("one\r\ntwo\r\n")]);
        var editor = await OpenAsync(f, path);
        Assert.Equal("UTF-8 with BOM", f.Shell.StatusBar.Right.Single(i => i.Item.Id == "daedalus.editor.encoding").Text);
        Assert.Equal("CRLF", f.Shell.StatusBar.Right.Single(i => i.Item.Id == "daedalus.editor.eol").Text);
        Assert.Equal("Plain Text", f.Shell.StatusBar.Right.Single(i => i.Item.Id == "daedalus.editor.language").Text);

        editor.Model.Text.Insert(editor.Model.Text.TextLength, "three\r\n");
        await f.Shell.SaveActiveDocumentAsync();
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        Assert.Equal("one\r\ntwo\r\nthree\r\n", Encoding.UTF8.GetString(bytes[3..]));

        await f.Shell.Workbench.Settings.SetAsync(EditorOptions.MinimapKey, false, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => !editor.Editor.FindAncestorOfType<CodeEditorView>()!.Minimap.IsVisible);
    }
}
