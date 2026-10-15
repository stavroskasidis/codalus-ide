using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>Find and Replace (the floating window) and its results in Find Results, on a folder of plain files.</summary>
public class FindInFilesTests
{
    [Fact]
    public void File_types_include_by_name_and_exclude_by_path_below_the_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "solution");
        string In(params string[] parts) => Path.Combine([root, .. parts]);

        var all = new FileTypeFilter(FindInFilesOptions.DefaultFileTypes);
        Assert.True(all.Matches(In("src", "App", "Program.cs"), root));
        Assert.True(all.Matches(In("bin.cs"), root));
        Assert.False(all.Matches(In("src", "App", "bin", "Debug", "App.dll"), root));
        Assert.False(all.Matches(In("obj", "project.assets.json"), root));
        Assert.False(all.Matches(In(".git", "config"), root));
        Assert.True(all.Matches(In(".editorconfig"), root)); // a dot file, not a dot folder

        var csharp = new FileTypeFilter("*.cs; *.razor, !*Generated*");
        Assert.True(csharp.Matches(In("A.cs"), root));
        Assert.True(csharp.Matches(In("Pages", "Index.razor"), root));
        Assert.False(csharp.Matches(In("A.json"), root));
        Assert.False(csharp.Matches(In("A.Generated.cs"), root));
        Assert.True(new FileTypeFilter(string.Empty).Matches(In("anything"), root));

        var options = new FindInFilesOptions("value", MatchCase: true, LookIn: FindScopes.CurrentProject, FileTypes: "*.cs");
        Assert.Equal("Find all \"value\", Match case, Current project, \"*.cs\"", options.Describe("Find all"));
    }

    [AvaloniaFact]
    public async Task Find_and_replace_floats_over_the_documents_and_fills_find_results()
    {
        await using var f = ShellFixture.Create();
        var folder = Directory.CreateTempSubdirectory("codalus-find-").FullName;
        try
        {
            var one = Path.Combine(folder, "a", "One.cs");
            var two = Path.Combine(folder, "a", "Two.txt");
            var four = Path.Combine(folder, "b", "Four.md");
            var ignored = Path.Combine(folder, "bin", "Debug", "Three.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(one)!);
            Directory.CreateDirectory(Path.GetDirectoryName(four)!);
            Directory.CreateDirectory(Path.GetDirectoryName(ignored)!);
            Directory.CreateDirectory(Path.Combine(folder, ".git"));
            await File.WriteAllTextAsync(one, "class One { int value; int Value => value; }\n    // value again\n");
            await File.WriteAllTextAsync(two, "Value\nnothing\n");
            await File.WriteAllTextAsync(four, "A VALUE.\n");
            await File.WriteAllTextAsync(ignored, "value\n");
            await File.WriteAllTextAsync(Path.Combine(folder, ".git", "config"), "value\n");

            // Ctrl+Shift+F shows the window on Find in Files; the Find Results window has no search box of its own.
            var find = f.Shell.FindReplace;
            var results = f.Shell.FindResults;
            f.Shell.Run("workbench.action.findInFiles");
            await ShellFixture.PumpUntil(() => find.IsOpen && f.Window.GetVisualDescendants().OfType<FindReplaceView>().Any(v => v.IsEffectivelyVisible));
            Assert.False(find.IsReplace);

            // An invalid regular expression keeps the window open with the reason.
            find.Query = "(";
            find.UseRegex = true;
            await find.FindAllAsync();
            Assert.True(find.IsOpen);
            Assert.StartsWith("Invalid regular expression", find.Error, StringComparison.Ordinal);
            find.UseRegex = false;

            // Find All: a row per matching line, bin/ and dot folders left out, and the window closes.
            find.Query = "value";
            find.LookIn = folder;
            NavigationDepthTests.Screenshot(f, "find-and-replace");
            await find.FindAllAsync();
            NavigationDepthTests.Screenshot(f, "find-results");
            Assert.False(find.IsOpen);
            Assert.Equal([one, two, four], results.Rows.Where(r => r.IsHeader).Select(r => r.Path));
            var first = results.Rows.First(r => !r.IsHeader);
            Assert.Equal((1, 16, 5), (first.Line, first.Column, first.Length));
            Assert.Equal(15, first.Highlights!.Count); // the line's three matches
            Assert.Equal("// value again", results.Rows[2].Code);
            Assert.Equal([3, 4, 5, 6, 7], results.Rows[2].Highlights); // trimmed text, highlights moved with it
            Assert.Contains("Matching lines: 4", results.Summary, StringComparison.Ordinal);
            Assert.Contains("Matching files: 3", results.Summary, StringComparison.Ordinal);
            Assert.StartsWith("Find all \"value\", " + folder, results.Title, StringComparison.Ordinal);
            Assert.Equal("value", find.QueryHistory[0]);

            // Modify Find shows the window again; Find Next opens each match selected, then Skip File moves on.
            results.ModifyFindCommand.Execute(null);
            Assert.True(find.IsOpen);
            await find.FindNextAsync(backwards: false);
            await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Controller is { } c && c.Model.Path == one && c.Editor.SelectedText == "value");
            Assert.Equal(1, f.Shell.ActiveEditor!.Controller!.Caret.Line);
            await find.SkipFileAsync();
            await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Controller?.Model.Path == two);
            await find.FindNextAsync(backwards: true);
            await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Controller is { } c && c.Model.Path == one && c.Caret.Line == 2);

            // Replace, one match at a time: whole words, case matched; each line's matches in turn.
            f.Shell.Run("workbench.action.replaceInFiles");
            Assert.True(find.IsReplace);
            NavigationDepthTests.Screenshot(f, "replace-in-files");
            find.MatchCase = true;
            find.WholeWord = true;
            find.ReplaceWith = "amount";
            await find.ReplaceAsync(); // a new search: goes to the first match
            await find.ReplaceAsync();
            var editor = f.Shell.ActiveEditor!.Controller!;
            Assert.Equal("class One { int amount; int Value => value; }", editor.Model.Text.GetText(editor.Model.Text.GetLineByNumber(1)));
            Assert.Equal("value", editor.Editor.SelectedText); // the line's next match
            await find.ReplaceAsync();
            await find.ReplaceAsync();
            Assert.Equal("class One { int amount; int Value => amount; }\n    // amount again\n", editor.Model.Text.Text);
            Assert.All(results.Rows.Where(r => !r.IsHeader), r => Assert.True(r.IsReplaced));
            Assert.Contains("int value;", await File.ReadAllTextAsync(one), StringComparison.Ordinal); // open documents stay unsaved

            // Replace All, after a confirmation: open documents in the editor, the others on disk; bin/ untouched.
            find.MatchCase = false;
            find.WholeWord = false;
            find.Query = "value";
            find.ReplaceWith = "Total";
            var replacing = find.ReplaceAllAsync();
            await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
            f.Shell.Dialogs.Accept();
            await replacing;
            Assert.Equal("class One { int amount; int Total => amount; }\n    // amount again\n", editor.Model.Text.Text);
            Assert.Equal("Total\nnothing\n", (await f.Shell.OpenFileAsync(two))!.Model.Text.Text); // opened by Skip File
            Assert.Equal("Value\nnothing\n", await File.ReadAllTextAsync(two));
            Assert.Equal("A Total.\n", await File.ReadAllTextAsync(four));
            Assert.Equal("value\n", await File.ReadAllTextAsync(ignored));
            Assert.StartsWith("Replace all \"value\"", results.Title, StringComparison.Ordinal);
            Assert.Contains("Occurrences replaced: 3", results.Summary, StringComparison.Ordinal);
            await f.Shell.OpenFileAsync(one);
            Assert.Equal("class One { int amount; int Total => amount; }", results.Rows.First(r => r.Path == one && !r.IsHeader).Code);

            // Regular expressions replace with their groups; Current document is the active editor only.
            find.UseRegex = true;
            find.Query = @"(\w+) again";
            find.ReplaceWith = "$1 later";
            find.LookIn = FindScopes.CurrentDocument;
            replacing = find.ReplaceAllAsync();
            await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
            f.Shell.Dialogs.Accept();
            await replacing;
            Assert.EndsWith("// amount later\n", editor.Model.Text.Text, StringComparison.Ordinal);

            // Append results keeps the earlier ones.
            find.UseRegex = false;
            find.Query = "amount";
            await find.FindAllAsync();
            Assert.Single(results.Rows, r => r.IsHeader);
            find.Query = "class";
            find.AppendResults = true;
            await find.FindAllAsync();
            Assert.Equal(2, results.Rows.Count(r => r.IsHeader));

            // Escape closes the window.
            f.Shell.FindInFiles();
            await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<FindReplaceView>().Any(v => v.IsEffectivelyVisible));
            find.CloseCommand.Execute(null);
            Assert.False(find.IsOpen);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
