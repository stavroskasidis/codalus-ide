using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

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
        Assert.Equal("C#", f.Shell.StatusBar.Right.Single(i => i.Item.Id == "codalus.editor.language").Text);
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
        try
        {
            // The first request loads Roslyn's completion providers, which takes long on a busy machine (the whole suite
            // runs in parallel). Asking again would cancel it, so wait for it; ask once more only if it came back empty.
            try
            {
                await ShellFixture.PumpUntil(() => program.CompletionWindow is { } w && w.CompletionList.CompletionData.Count > 0, seconds: 90);
            }
            catch (Exception) when (f.Shell.Workbench.Output.GetOrCreate("codalus.languages", "Language Services").GetText().Contains("completion not shown", StringComparison.Ordinal))
            {
                f.Shell.Run("editor.action.triggerSuggest");
                await ShellFixture.PumpUntil(() => program.CompletionWindow is { } w && w.CompletionList.CompletionData.Count > 0, seconds: 60);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Assert.Fail($"No completion window. Client: {f.Shell.Languages.RunningClient("csharp") is not null}. Text near caret: '{program.Model.Text.GetText(Math.Max(0, program.Editor.CaretOffset - 20), 20)}'. Log:\n{f.Shell.Workbench.Output.GetOrCreate("codalus.languages", "Language Services").GetText()}");
        }
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
    public async Task Redraws_while_a_large_file_is_classified_keep_its_first_semantic_tokens_request()
    {
        // file_highlight_ms (docs/benchmarks/net11-release-gate.md): every redraw during an open (CodeLens, inlay hints,
        // Git lenses arriving) cancelled the first semantic tokens request and sent another, so a large file's first
        // colors waited for the last redraw. A count of requests, not a time: the first one is the one applied.
        await using var f = ShellFixture.Create();
        var folder = Directory.CreateDirectory(Path.Combine(f.UserData, "large")).FullName;
        var source = new StringBuilder("namespace Large;\n\n");
        for (var i = 0; i < 2500; i++)
        {
            source.Append(System.Globalization.CultureInfo.InvariantCulture, $$"""
                public sealed class Item{{i}}
                {
                    private readonly List<string> _names = new();
                    public int Count => _names.Count;
                    public string Describe(int index) => $"{index}: {_names[index % _names.Count]}";
                    public void Add(string name) => _names.Add(name.Trim());
                }

                """);
        }

        var path = Path.Combine(folder, "Large.cs");
        await File.WriteAllTextAsync(path, "using System.Collections.Generic;\n" + source, TestContext.Current.CancellationToken);
        var editor = await OpenAsync(f, path);
        int? sentWhenApplied = null;
        editor.TokensApplied += (_, _) => sentWhenApplied ??= editor.TokenRequests;
        await ShellFixture.PumpUntil(() => editor.TokenRequests > 0 || editor.Colorizer.HasTokens, seconds: 60);
        var sent = editor.TokenRequests;
        for (var i = 0; i < 20 && !editor.Colorizer.HasTokens; i++)
        {
            editor.Editor.TextArea.TextView.Redraw();
            ShellFixture.Pump();
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        await ShellFixture.PumpUntil(() => editor.Colorizer.HasTokens, seconds: 120);
        Assert.Equal(sent, sentWhenApplied);
    }

    [AvaloniaFact]
    public async Task Documents_reopen_with_their_solution()
    {
        await using var f = ShellFixture.Create();
        var folder = await f.OpenLanguageFeaturesAsync();
        var shapes = await OpenAsync(f, Path.Combine(folder, "src", "Lib", "Shapes.cs"));
        MoveCaret(shapes, "class Circle");
        await OpenAsync(f, Path.Combine(folder, "src", "App", "Program.cs"));

        await f.Shell.CloseSolutionAsync();
        Assert.Empty(f.Shell.Editors);

        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "LanguageFeatures.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Editors.Count() == 2);
        Assert.Equal("Program.cs", f.Shell.ActiveEditor?.Model.Name);

        // Background tabs get their view (and the remembered caret) when shown.
        var reopened = f.Shell.Editors.Single(e => e.Model.Name == "Shapes.cs");
        f.Shell.Layout.OpenDocument(reopened);
        await ShellFixture.PumpUntil(() => reopened.Controller is not null);
        Assert.Equal(shapes.Caret.Line, reopened.Controller!.Caret.Line);
    }

    [AvaloniaFact]
    public async Task Rename_code_actions_formatting_search_and_navigation()
    {
        await using var f = ShellFixture.Create();
        var folder = await f.OpenLanguageFeaturesAsync();
        var shapesPath = Path.Combine(folder, "src", "Lib", "Shapes.cs");
        var programPath = Path.Combine(folder, "src", "App", "Program.cs");

        // Rename with preview: Radius → Size, previewed, applied in the editor (the closed Program.cs is untouched: no uses).
        var shapes = await OpenAsync(f, shapesPath);
        MoveCaret(shapes, "Radius { get; }", 1);
        var renaming = f.Shell.RenameSymbolAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen && f.Shell.Dialogs.HasInput);
        f.Shell.Dialogs.InputText = "Size";
        f.Shell.Dialogs.Accept();
        try
        {
            await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen && f.Shell.Dialogs.HasDetails, seconds: 60);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Seen twice under load (Windows, macOS): say what the shell and the language service did instead.
            var log = f.Shell.Workbench.Output.GetOrCreate("codalus.languages").GetText();
            throw new InvalidOperationException(
                $"No rename preview. Rename task: {renaming.Status} {renaming.Exception?.GetBaseException().Message}; status bar: '{f.Shell.StatusBar.Message}'; " +
                $"dialog open: {f.Shell.Dialogs.IsOpen}; language status: {f.Shell.Languages.Status(ShellHost.CSharpLanguageId)}. Log:\n{log[Math.Max(0, log.Length - 4000)..]}", ex);
        }

        Assert.Contains(f.Shell.Dialogs.Details, l => l.IsAdded && l.Text.Contains("Size * Size", StringComparison.Ordinal));
        f.Shell.Dialogs.Accept();
        await renaming;
        Assert.Contains("public double Size { get; }", shapes.Model.Text.Text, StringComparison.Ordinal);
        Assert.True(shapes.Model.IsModified);

        // Find All References into the Find Results.
        MoveCaret(shapes, "class Circle", "class C".Length);
        await f.Shell.FindReferencesAsync();
        Assert.Contains(f.Shell.FindResults.Rows, r => r.IsHeader && r.Path == programPath);
        Assert.Contains("References to 'Circle'", f.Shell.FindResults.Summary, StringComparison.Ordinal);

        // Find in Files.
        f.Shell.FindInFiles();
        f.Shell.FindReplace.Query = "JsonSerializer";
        await f.Shell.FindReplace.FindAllAsync();
        Assert.Contains(f.Shell.FindResults.Rows, r => !r.IsHeader && r.Path == programPath);

        // The lightbulb: an unknown type gets "using System.Text;" after a preview.
        var program = await OpenAsync(f, programPath);
        program.Model.Text.Insert(program.Model.Text.TextLength, "\nvar sb = new StringBuilder();\n");
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Diagnostics.For(programPath).Any(d => d.Code == "CS0246"), seconds: 60);
        MoveCaret(program, "new StringBuilder", "new S".Length);
        var fixing = f.Shell.ShowCodeActionsAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Palette.IsOpen, seconds: 60);
        f.Shell.Palette.Accept(f.Shell.Palette.Rows.First(r => r.Label.Contains("using System.Text", StringComparison.Ordinal)));
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen && f.Shell.Dialogs.HasDetails, seconds: 60);
        f.Shell.Dialogs.Accept();
        await fixing;
        Assert.StartsWith("using System.Text;", program.Model.Text.Text, StringComparison.Ordinal);

        // Format Document.
        program.Model.Text.Insert(program.Model.Text.TextLength, "int  x=1 ;\n");
        await f.Shell.FormatAsync(selectionOnly: false);
        Assert.Contains("int x = 1;", program.Model.Text.Text, StringComparison.Ordinal);

        // Go To All (Code Search, M47) finds a type and opens it.
        await f.Shell.GoToAllAsync();
        f.Shell.CodeSearch.Query = "Squar";
        await ShellFixture.PumpUntil(() => f.Shell.CodeSearch.Rows.Any(r => r.Result is { Name: "Square", Kind: Workbench.Search.CodeSearchKind.Type }), seconds: 30);
        f.Shell.CodeSearch.SelectedIndex = f.Shell.CodeSearch.Rows.IndexOf(f.Shell.CodeSearch.Rows.First(r => r.Result is { Name: "Square", Kind: Workbench.Search.CodeSearchKind.Type }));
        await f.Shell.CodeSearch.AcceptAsync();
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Name is "Shapes.cs" or "Square.Part.cs");

        // Go to Definition into a referenced assembly opens decompiled, read-only code.
        program = (await f.Shell.OpenFileAsync(programPath))!.Controller ?? await OpenAsync(f, programPath);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Controller is not null);
        program = f.Shell.ActiveEditor!.Controller!;
        MoveCaret(program, "Console.WriteLine", 2);
        await program.GoToDefinitionAsync();
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.ReadOnlyLabel == "decompiled", seconds: 60);
        Assert.Contains("class Console", f.Shell.ActiveEditor!.Model.Text.Text, StringComparison.Ordinal);
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
        Assert.Equal("UTF-8 with BOM", f.Shell.StatusBar.Right.Single(i => i.Item.Id == "codalus.editor.encoding").Text);
        Assert.Equal("CRLF", f.Shell.StatusBar.Right.Single(i => i.Item.Id == "codalus.editor.eol").Text);
        Assert.Equal("Plain Text", f.Shell.StatusBar.Right.Single(i => i.Item.Id == "codalus.editor.language").Text);

        editor.Model.Text.Insert(editor.Model.Text.TextLength, "three\r\n");
        await f.Shell.SaveActiveDocumentAsync();
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        Assert.Equal("one\r\ntwo\r\nthree\r\n", Encoding.UTF8.GetString(bytes[3..]));

        await f.Shell.Workbench.Settings.SetAsync(EditorOptions.MinimapKey, false, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => !editor.Editor.FindAncestorOfType<CodeEditorView>()!.Minimap.IsVisible);
    }
}
