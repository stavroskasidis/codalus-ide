using System.Text.Json;
using Avalonia.Headless.XUnit;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Ai.Ide;

namespace Codalus.Shell.Tests;

/// <summary>
/// M60's code-change tools for agents (D52) on samples/LanguageFeatures: Rename, the lightbulb's code actions, formatting
/// and Replace in Files, each an action applied through the IDE's edit path. An open file changes in its editor as one
/// undo step and stays unsaved; a closed file is written; a change that would touch a file excluded from AI is refused
/// whole, and Replace in Files leaves those files out.
/// </summary>
public sealed class IdeToolsCodeChangeShellTests
{
    private static async Task<IdeToolResult> CallAsync(ShellFixture f, string tool, string json = "{}") =>
        await f.Shell.IdeToolAsync(tool, JsonElement.Parse(json), "Claude", CancellationToken.None);

    private static int LineOf(string text, string marker) =>
        Array.FindIndex(text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'), l => l.Contains(marker, StringComparison.Ordinal)) + 1;

    [AvaloniaFact]
    public async Task Agents_rename_apply_fixes_format_and_replace_through_the_ides_edit_path()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("LanguageFeatures");
        File.WriteAllText(Path.Combine(folder, ".codalusignore"), "src/App/Json.cs\n");
        var shapesPath = Path.Combine(folder, "src", "Lib", "Shapes.cs");
        var programPath = Path.Combine(folder, "src", "App", "Program.cs");
        var reporterPath = Path.Combine(folder, "src", "App", "Reporter.cs");
        var jsonPath = Path.Combine(folder, "src", "App", "Json.cs");
        var json = File.ReadAllText(jsonPath);
        File.WriteAllText(reporterPath, File.ReadAllText(reporterPath).Replace("public static void Run()", "public static void Run( )", StringComparison.Ordinal));
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "LanguageFeatures.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: "ready", Projects: 2 }, seconds: 180);

        // Rename in an open file: its three edits in the editor, unsaved, undone by one Undo.
        var shapes = await f.Shell.OpenFileAsync(shapesPath, preview: false);
        await ShellFixture.PumpUntil(() => shapes?.Controller is not null);
        var original = shapes!.Model.Text.Text;
        var renamed = await CallAsync(f, "rename_symbol", $$"""{"path":"src/Lib/Shapes.cs","line":{{LineOf(original, "public double Radius")}},"name":"Radius","newName":"Size"}""");
        Assert.False(renamed.IsError, renamed.Text);
        Assert.Equal("Renamed 'Radius' to 'Size'.\nChanged 1 file:\n- src/Lib/Shapes.cs: 3 edits (in its open editor, unsaved; one undo step)", renamed.Text);
        Assert.Contains("Size * Size", shapes.Model.Text.Text, StringComparison.Ordinal);
        Assert.True(shapes.Model.IsModified);
        Assert.DoesNotContain("Size", File.ReadAllText(shapesPath), StringComparison.Ordinal);
        shapes.Model.Text.UndoStack.Undo();
        Assert.Equal(original, shapes.Model.Text.Text);

        // Across files: the open one in its editor, the closed one written.
        var scale = await CallAsync(f, "rename_symbol", $$"""{"path":"src/Lib/Shapes.cs","line":{{LineOf(original, "public static double Scale(IShape")}},"name":"Scale","newName":"ScaleArea"}""");
        Assert.False(scale.IsError, scale.Text);
        Assert.Contains("- src/App/Program.cs: 1 edit (written to disk)", scale.Text, StringComparison.Ordinal);
        Assert.Contains("- src/Lib/Shapes.cs: 1 edit (in its open editor", scale.Text, StringComparison.Ordinal);
        Assert.Contains("Geometry.ScaleArea(circle, 3)", File.ReadAllText(programPath), StringComparison.Ordinal);
        Assert.Contains("public static double ScaleArea(IShape shape", shapes.Model.Text.Text, StringComparison.Ordinal);

        // A rename that would reach a file excluded from AI is refused whole, naming it.
        var program = File.ReadAllText(programPath);
        var person = await CallAsync(f, "rename_symbol", $$"""{"path":"src/App/Program.cs","line":{{LineOf(program, "new Person")}},"name":"Person","newName":"Human"}""");
        Assert.True(person.IsError, person.Text);
        Assert.Equal("Refused: the change would edit files excluded from AI: src/App/Json.cs. Nothing was changed.", person.Text);
        Assert.Equal(program, File.ReadAllText(programPath));
        Assert.Equal(json, File.ReadAllText(jsonPath));

        // A fix: listed at its place, then applied by its title, in the open editor.
        var editor = await f.Shell.OpenFileAsync(programPath, preview: false);
        await ShellFixture.PumpUntil(() => editor?.Controller is not null);
        editor!.Model.Text.Insert(editor.Model.Text.TextLength, "\nvar sb = new StringBuilder();\n");
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Diagnostics.For(programPath).Any(d => d.Code == "CS0246"), seconds: 60);
        var line = LineOf(editor.Model.Text.Text, "new StringBuilder");
        var actions = await CallAsync(f, "get_code_actions", $$"""{"path":"src/App/Program.cs","line":{{line}},"name":"StringBuilder"}""");
        Assert.False(actions.IsError, actions.Text);
        Assert.Contains("[quickfix] using System.Text;", actions.Text, StringComparison.Ordinal);
        var fixedIt = await CallAsync(f, "apply_code_action", $$"""{"path":"src/App/Program.cs","line":{{line}},"name":"StringBuilder","title":"using System.Text;"}""");
        Assert.False(fixedIt.IsError, fixedIt.Text);
        Assert.StartsWith("Applied 'using System.Text;'.\nChanged 1 file:\n- src/App/Program.cs:", fixedIt.Text, StringComparison.Ordinal);
        Assert.StartsWith("using System.Text;", editor.Model.Text.Text, StringComparison.Ordinal);
        var unknown = await CallAsync(f, "apply_code_action", $$"""{"path":"src/App/Program.cs","line":{{line + 1}},"title":"No such fix"}""");
        Assert.True(unknown.IsError);
        Assert.StartsWith("No ", unknown.Text, StringComparison.Ordinal);

        // Formatting: an open file in its editor (one undo step for every edit), a closed one on disk.
        editor.Model.Text.Insert(editor.Model.Text.TextLength, "int  x=1 ;\n");
        var unformatted = editor.Model.Text.Text;
        var formatted = await CallAsync(f, "format_document", """{"path":"src/App/Program.cs"}""");
        Assert.False(formatted.IsError, formatted.Text);
        Assert.Contains("int x = 1;", editor.Model.Text.Text, StringComparison.Ordinal);
        editor.Model.Text.UndoStack.Undo();
        Assert.Equal(unformatted, editor.Model.Text.Text);
        var closed = await CallAsync(f, "format_document", """{"path":"src/App/Reporter.cs"}""");
        Assert.False(closed.IsError, closed.Text);
        Assert.Contains("- src/App/Reporter.cs: 1 edit (written to disk)", closed.Text, StringComparison.Ordinal);
        Assert.Contains("public static void Run()", File.ReadAllText(reporterPath), StringComparison.Ordinal);
        Assert.Equal("src/App/Reporter.cs is formatted already: nothing changed.", (await CallAsync(f, "format_document", """{"path":"src/App/Reporter.cs"}""")).Text);

        // Excluded files can't be read for code actions or formatted.
        var excluded = await CallAsync(f, "format_document", """{"path":"src/App/Json.cs"}""");
        Assert.Equal((true, "'src/App/Json.cs' is excluded from AI."), (excluded.IsError, excluded.Text));
        Assert.True((await CallAsync(f, "get_code_actions", """{"path":"src/App/Json.cs","line":3}""")).IsError);

        // Replace in Files: the open file in its editor, the excluded file left out (and said so), Find Results filled.
        var replaced = await CallAsync(f, "replace_in_files", """{"find":"Person","replace":"Human","matchCase":true,"glob":"*.cs"}""");
        Assert.False(replaced.IsError, replaced.Text);
        Assert.StartsWith("Replaced 2 match(es) in 1 file(s). (1 file(s) excluded from AI weren't searched.)", replaced.Text, StringComparison.Ordinal);
        Assert.Contains("- src/App/Program.cs: 2 replacements (in its open editor, unsaved; one undo step)", replaced.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Json.cs", replaced.Text, StringComparison.Ordinal);
        Assert.Contains("new Human(\"Ada\"), AppJsonContext.Default.Human", editor.Model.Text.Text, StringComparison.Ordinal);
        Assert.Equal(json, File.ReadAllText(jsonPath));
        Assert.Contains(f.Shell.FindResults.Rows, r => !r.IsHeader && r.Path == programPath);
        Assert.StartsWith("No matches in ", (await CallAsync(f, "replace_in_files", """{"find":"NoSuchTextAnywhere","replace":"x"}""")).Text, StringComparison.Ordinal);

        Assert.Contains("IDE tool (Claude): rename_symbol", f.Shell.Workbench.Output.GetOrCreate(AiService.OutputChannelId).GetText(), StringComparison.Ordinal);
    }
}

