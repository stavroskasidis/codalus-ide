using System.Text.Json;
using Avalonia.Headless.XUnit;
using AvaloniaEdit.Document;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using LspCompletionItem = Codalus.LanguageServices.Protocol.CompletionItem;
using Range = Codalus.LanguageServices.Protocol.Range;

namespace Codalus.Shell.Tests;

/// <summary>
/// Editing at scale never hangs the UI (M56): Insert Carets at All Matches and typing at all of them, Quick Find and
/// Replace All on a common word, Format Document's thousands of edits, the overview ruler with thousands of problems, and
/// committing a completion while the language service is slow. Each asserts the counts that grew with the file (matches
/// searched, document changes, marks worked out), and the flows run under <see cref="UiStallMonitor"/>.
/// </summary>
public sealed class EditorEditingAtScaleTests
{
    /// <summary>The longest any input may wait, without the GC's pauses: a hang on a loaded runner, as in <see cref="EverydayResponsivenessTests"/>.</summary>
    private const double Budget = 1000;

    /// <summary>A word typed into Quick Find, a key at a time, then partly erased and typed again.</summary>
    private static readonly string[] s_typedQueries = ["i", "it", "ite", "item", "items", "item", "ite", "it", "i", "it", "ite", "item"];

    private static async Task<CodeEditorController> OpenAsync(ShellFixture f, string name, string text)
    {
        var path = Path.Combine(f.UserData, name);
        await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
        await f.Shell.OpenFileAsync(path, preview: false, null);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == path && f.Shell.ActiveEditor.Controller is not null);
        return f.Shell.ActiveEditor!.Controller!;
    }

    private static async Task WaitAsync(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        await ShellFixture.PumpUntil(() => DateTime.UtcNow >= until, seconds: 120);
    }

    /// <summary>Runs <paramref name="flow"/> once for the JIT, then again measured: no input may wait over the budget.</summary>
    private static async Task FlowAsync(UiStallMonitor monitor, string name, Func<Task> flow)
    {
        await flow();
        await WaitAsync(300);
        monitor.Reset();
        await flow();
        await WaitAsync(300);
        monitor.AssertResponsive(name, Budget);
    }

    private static int Occurrences(string text, string word)
    {
        var count = 0;
        for (var i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + word.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [AvaloniaFact]
    public async Task Carets_at_all_matches_take_every_match_draw_the_visible_ones_and_type_as_one_change()
    {
        // 100,000 lines, every fifth with the word: 20,000 matches. Inserting them read every caret twice per match (seconds),
        // then each caret blink built a line for every caret off screen; typing at them was 20,000 document changes.
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;
        var text = string.Concat(Enumerable.Range(0, 100_000).Select(i => i % 5 == 0 ? $"call(marker, {i});\n" : $"other({i});\n"));
        var editor = await OpenAsync(f, "carets.txt", text);
        var document = editor.Model.Text;
        var view = editor.Editor.TextArea.TextView;
        var changes = 0;
        document.Changed += (_, _) => changes++;

        using var monitor = new UiStallMonitor();
        await FlowAsync(monitor, "inserting carets at 20,000 matches, drawing them, typing at them and undoing", async () =>
        {
            editor.MultiCaret.Clear();
            editor.Editor.TextArea.ClearSelection();
            editor.Editor.CaretOffset = document.GetLineByNumber(50_001).Offset + "call(ma".Length; // in the middle's word
            editor.Editor.ScrollToLine(50_001);
            editor.MultiCaret.SelectNextOccurrence(all: true);

            // Every match, the ones before the primary too (it stopped after the first one there), in order.
            Assert.Equal(19_999, editor.MultiCaret.ExtraCarets.Count);
            Assert.True(editor.MultiCaret.ExtraCarets.Zip(editor.MultiCaret.ExtraCarets.Skip(1)).All(p => p.First.Start < p.Second.Start));
            Assert.Equal(document.GetLineByNumber(1).Offset + "call(".Length, editor.MultiCaret.ExtraCarets[0].Start);

            // A frame draws the visible carets only, and builds no line for the others.
            ShellFixture.RenderFrame(f.Window);
            var visibleStart = view.VisualLines[0].FirstDocumentLine.Offset;
            var visibleEnd = view.VisualLines[^1].LastDocumentLine.EndOffset;
            var visible = editor.MultiCaret.ExtraCarets.Count(s => s.Caret >= visibleStart && s.Caret <= visibleEnd);
            Assert.InRange(visible, 1, 100);
            Assert.Equal(visible, editor.MultiCaret.DrawnCarets);
            Assert.Null(view.GetVisualLine(1));
            Assert.Null(view.GetVisualLine(25_001));

            // Typing replaces every selection in one document change, undone in one step.
            changes = 0;
            editor.Editor.TextArea.PerformTextInput("X");
            Assert.Equal(1, changes);
            Assert.Equal(0, Occurrences(document.Text, "marker"));
            Assert.Equal(20_000, Occurrences(document.Text, "call(X,"));
            editor.Editor.Undo();
            Assert.Equal(2, changes);
            Assert.Equal(text, document.Text);
            await WaitAsync(50);
        });
    }

    [Fact]
    public void Edits_near_each_other_are_one_change_and_anchors_follow_each_edit()
    {
        // Format Document's edits: an indent and a rename on each of 2,000 lines, as one change with the anchors (bookmarks,
        // breakpoints) moved as when the edits are applied one by one.
        var text = string.Concat(Enumerable.Range(0, 2_000).Select(i => $"x{i};\n"));
        var batched = new TextDocument(text);
        var oneByOne = new TextDocument(text);
        var edits = new List<(int Offset, int Length, string Text)>();
        for (var number = 1; number < batched.LineCount; number++)
        {
            var line = batched.GetLineByNumber(number);
            edits.Add((line.Offset, 0, "    "));
            edits.Add((line.Offset, 1, "value"));
        }

        int[] marks = [.. Enumerable.Range(0, text.Length / 3).Select(i => i * 3)];
        var batchedAnchors = marks.Select(o => batched.CreateAnchor(o)).ToList();
        var oneByOneAnchors = marks.Select(o => oneByOne.CreateAnchor(o)).ToList();
        var changes = 0;
        batched.Changed += (_, _) => changes++;

        Assert.Equal(1, DocumentEdits.Apply(batched, edits));
        using (oneByOne.RunUpdate())
        {
            foreach (var (offset, length, replacement) in Enumerable.Reverse(edits))
            {
                oneByOne.Replace(offset, length, replacement);
            }
        }

        Assert.Equal(1, changes);
        Assert.Equal(oneByOne.Text, batched.Text);
        Assert.Equal(oneByOneAnchors.Select(a => (a.IsDeleted, a.IsDeleted ? -1 : a.Offset)), batchedAnchors.Select(a => (a.IsDeleted, a.IsDeleted ? -1 : a.Offset)));
        batched.UndoStack.Undo();
        Assert.Equal(text, batched.Text);
        Assert.Equal(marks, batchedAnchors.Select(a => a.Offset));

        // Edits far apart stay separate changes: one group would rewrite the text between them.
        var far = new TextDocument(new string('a', 10_000));
        Assert.Equal(2, DocumentEdits.Apply(far, [(0, 0, "b"), (DocumentEdits.MaxGap + 10, 0, "c")]));
    }

    [Fact]
    public void Lsp_edits_apply_in_order_and_inserts_at_one_position_keep_their_order()
    {
        using var document = CodeDocument.Create(Path.Combine(Path.GetTempPath(), "edits.cs"), "ab\ncd\n");
        var changes = 0;
        document.Text.Changed += (_, _) => changes++;
        Assert.True(document.ApplyEdits(
        [
            new TextEdit(new Range(new Position(1, 0), new Position(1, 1)), "C"),
            new TextEdit(new Range(new Position(0, 1), new Position(0, 1)), "1"),
            new TextEdit(new Range(new Position(0, 1), new Position(0, 1)), "2"),
            new TextEdit(new Range(new Position(0, 0), new Position(0, 0)), "  "),
        ]));
        Assert.Equal("  a12b\nCd\n", document.Text.Text);
        Assert.Equal(1, changes);
        Assert.False(document.ApplyEdits([new TextEdit(new Range(new Position(0, 0), new Position(0, 3)), "x"), new TextEdit(new Range(new Position(0, 2), new Position(0, 4)), "y")]));
        Assert.Equal("  a12b\nCd\n", document.Text.Text);
    }

    [Fact]
    public void Offsets_move_through_a_change_of_many_edits_as_each_would()
    {
        var text = string.Concat(Enumerable.Range(0, 3_000).Select(i => $"item{i}, "));
        var document = new TextDocument(text);
        DocumentChangeEventArgs? change = null;
        document.Changed += (_, e) => change = e;
        var edits = new List<(int Offset, int Length, string Text)>();
        for (var i = text.IndexOf("item", StringComparison.Ordinal); i >= 0; i = text.IndexOf("item", i + 4, StringComparison.Ordinal))
        {
            edits.Add((i, 4, i % 3 == 0 ? string.Empty : "element"));
        }

        DocumentEdits.Apply(document, edits);
        var random = new Random(56);
        void AssertMapped(DocumentChangeEventArgs e, int length)
        {
            var offsets = Enumerable.Range(0, length + 1).OrderBy(_ => random.Next()).ToArray();
            var expected = offsets.Select(o => e.GetNewOffset(o)).ToArray();
            DocumentEdits.Map(e, offsets);
            Assert.Equal(expected, offsets);
        }

        AssertMapped(change!, text.Length);

        // Undone: the map inverted, its entries last to first.
        var edited = document.TextLength;
        document.UndoStack.Undo();
        Assert.Equal(text, document.Text);
        AssertMapped(change!, edited);
    }

    [Fact]
    public void The_ruler_marks_each_pixel_row_once_with_errors_over_warnings()
    {
        var diagnostics = Enumerable.Range(0, 10_000)
            .Select(i => new Diagnostic(new Range(new Position(i * 10, 0), new Position(i * 10, 1)), i % 2 == 0 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error, null, null, "problem"))
            .Append(new Diagnostic(new Range(new Position(5, 0), new Position(5, 1)), DiagnosticSeverity.Information, null, null, "a hint"))
            .ToList();
        var marks = RulerMarks.ForProblems(diagnostics, lines: 100_000, top: 16, height: 500);

        Assert.InRange(marks.Marks.Count, 400, 501);
        Assert.Equal((5_000, 5_000), (marks.Errors, marks.Warnings));
        Assert.All(marks.Marks, m => Assert.True(m.Error)); // each row has an error and a warning: the error wins
        Assert.Equal(16, marks.Marks[0].Row);
        Assert.Equal([16.0, 266.0], RulerMarks.Rows([1, 1, 2, 50_001], lines: 100_000, top: 16, height: 500));
    }

    [AvaloniaFact]
    public async Task The_overview_ruler_works_out_problem_marks_when_they_change_not_at_each_frame()
    {
        // 10,000 problems: each keystroke's and caret move's frame sorted them all and looked up a brush for each.
        await using var f = ShellFixture.Create();
        var text = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"line {i}\n"));
        var editor = await OpenAsync(f, "problems.txt", text);
        var ruler = f.Shell.ActiveEditor!.View!.OverviewRuler;
        Diagnostic[] Problems(int count) => [.. Enumerable.Range(0, count).Select(i => new Diagnostic(new Range(new Position(i * 2, 0), new Position(i * 2, 4)), i % 3 == 0 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error, "CS0000", "csharp", "problem"))];
        f.Shell.Languages.Diagnostics.Set(editor.Model.Path, "csharp", null, Problems(10_000));
        await ShellFixture.PumpUntil(() => ruler.Health.Errors > 0);
        ShellFixture.RenderFrame(f.Window);
        var built = ruler.ProblemMarksBuilt;
        Assert.True(built > 0);

        for (var line = 1; line <= 50; line++)
        {
            editor.Editor.CaretOffset = editor.Model.Text.GetLineByNumber(line * 10).Offset;
            ShellFixture.RenderFrame(f.Window);
        }

        Assert.Equal(built, ruler.ProblemMarksBuilt);

        // New problems: worked out once more.
        f.Shell.Languages.Diagnostics.Set(editor.Model.Path, "csharp", null, Problems(5_000));
        await ShellFixture.PumpUntil(() => ruler.Health.Errors + ruler.Health.Warnings == 5_000);
        await ShellFixture.PumpUntil(() =>
        {
            ShellFixture.RenderFrame(f.Window);
            return ruler.ProblemMarksBuilt > built;
        });
        ShellFixture.RenderFrame(f.Window);
        Assert.InRange(ruler.ProblemMarksBuilt, built + 1, built + 2); // once for each change the store reported
    }

    [AvaloniaFact]
    public async Task Quick_find_counts_twenty_thousand_and_searches_off_the_ui_thread_once_per_pause()
    {
        // 100,000 lines with the word twice on each: AvaloniaEdit's search panel searched all 200,000 matches on the UI
        // thread at each key in its box and at each edit while it was open.
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;
        var text = string.Concat(Enumerable.Range(0, 100_000).Select(i => $"var item{i} = items[{i}];\n"));
        var editor = await OpenAsync(f, "find.txt", text);
        var find = editor.Search;
        find.Open();

        using var monitor = new UiStallMonitor();
        await FlowAsync(monitor, "typing a common word into Quick Find, then typing in the document with it open", async () =>
        {
            find.SearchPattern = string.Empty;
            await ShellFixture.PumpUntil(() => !find.IsSearching && find.MatchCount == 0);
            editor.Editor.CaretOffset = 0;
            var searches = find.SearchesRun;
            foreach (var typed in s_typedQueries)
            {
                find.SearchPattern = typed;
                ShellFixture.Pump();
            }

            await ShellFixture.PumpUntil(() => !find.IsSearching && find.MatchCount > 0, seconds: 60);
            Assert.Equal(FindWidget.MaxMatches, find.MatchCount);
            Assert.True(find.IsCapped);
            Assert.InRange(find.SearchesRun - searches, 1, 3); // once per pause in typing, not once per key
            Assert.Equal((4, 4), (editor.Editor.SelectionStart, editor.Editor.SelectionLength)); // the first match is selected
            Assert.EndsWith("+", find.Status);
            Assert.NotEmpty(find.MatchLines);

            // Typing in the document: the highlights follow the edits, and one search runs when the typing pauses.
            searches = find.SearchesRun;
            editor.Editor.TextArea.ClearSelection();
            editor.Editor.CaretOffset = editor.Model.Text.GetLineByNumber(10).Offset;
            for (var i = 0; i < 20; i++)
            {
                editor.Editor.TextArea.PerformTextInput("z");
                ShellFixture.Pump();
            }

            await ShellFixture.PumpUntil(() => !find.IsSearching, seconds: 60);
            Assert.InRange(find.SearchesRun - searches, 1, 5);
            Assert.Equal(FindWidget.MaxMatches, find.MatchCount);
            for (var undo = 0; undo < 20 && editor.Model.Text.Text != text; undo++)
            {
                editor.Editor.Undo();
            }

            Assert.Equal(text, editor.Model.Text.Text);
            await ShellFixture.PumpUntil(() => !find.IsSearching, seconds: 60);
        });
    }

    [AvaloniaFact]
    public async Task Find_goes_past_the_counted_matches_and_replace_all_replaces_every_one_as_one_change()
    {
        await using var f = ShellFixture.Create();
        var text = string.Concat(Enumerable.Range(0, 30_000).Select(i => $"x{i} = foo;\n"));
        var editor = await OpenAsync(f, "replace.txt", text);
        var document = editor.Model.Text;
        var find = editor.Search;
        find.Open();
        find.IsReplaceMode = true;
        find.SearchPattern = "foo";
        find.ReplacePattern = "barbaz";
        await ShellFixture.PumpUntil(() => !find.IsSearching && find.MatchCount == FindWidget.MaxMatches, seconds: 60);
        Assert.True(find.IsCapped);

        // Past the 20,000 counted: Find Next and Find Previous still go match to match.
        Assert.Equal(20_000, find.MatchLines.Count);
        var line = document.GetLineByNumber(25_000);
        editor.Editor.CaretOffset = line.Offset;
        find.FindNext();
        Assert.Equal((line.Offset + "x24999 = ".Length, 3), (editor.Editor.SelectionStart, editor.Editor.SelectionLength));
        find.FindPrevious();
        var previous = document.GetLineByNumber(24_999);
        Assert.Equal((previous.Offset + "x24998 = ".Length, 3), (editor.Editor.SelectionStart, editor.Editor.SelectionLength));

        // Replace All: every match, not only the counted ones, as one document change; a bookmark's anchor moves with
        // each edit before it; one undo step.
        var mark = document.CreateAnchor(document.GetLineByNumber(20_000).Offset);
        var changes = 0;
        document.Changed += (_, _) => changes++;
        await find.ReplaceAllAsync();
        Assert.Equal(1, changes);
        Assert.Equal(0, Occurrences(document.Text, "foo"));
        Assert.Equal(30_000, Occurrences(document.Text, "barbaz"));
        Assert.Equal(document.GetLineByNumber(20_000).Offset, mark.Offset);
        editor.Editor.Undo();
        Assert.Equal(text, document.Text);
    }

    [AvaloniaFact]
    public async Task Committing_never_waits_for_the_resolve_and_a_late_one_applies_only_while_nothing_was_typed()
    {
        // The UI thread waited up to 300 ms for the language service on each commit (M56). The resolve here answers from
        // another thread after 50 ms: committing doesn't wait for it, and it applies when it comes if nothing was typed.
        await using var f = ShellFixture.Create();
        const string Text = "class C\n{\n    void M() { Li }\n}\n";
        var editor = await OpenAsync(f, "complete.txt", Text);
        var document = editor.Model.Text;
        var resolved = new LspCompletionItem("List<>")
        {
            TextEdit = new TextEdit(new Range(new Position(2, 15), new Position(2, 17)), "List"),
            AdditionalTextEdits = [new TextEdit(new Range(new Position(0, 0), new Position(0, 0)), "using System.Collections.Generic;\n")],
        };
        var item = new LspCompletionItem("List<>") { Data = JsonElement.Parse("1") };
        Task<LspCompletionItem> Slow(LspCompletionItem _) => Task.Run(async () =>
        {
            await Task.Delay(50);
            return resolved;
        });
        TextSegment Typed() => new() { StartOffset = Text.IndexOf("Li }", StringComparison.Ordinal), Length = 2 };

        // Committed by Enter: the item's own text at once, then the resolve's edit and its using, undone in one step.
        editor.Editor.CaretOffset = Typed().EndOffset;
        editor.NewCompletionData(item, Slow).Complete(Typed());
        Assert.Contains("{ List<> }", document.Text, StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => document.Text.StartsWith("using System.Collections.Generic;", StringComparison.Ordinal));
        Assert.Contains("{ List }", document.Text, StringComparison.Ordinal);
        editor.Editor.Undo();
        Assert.Equal(Text, document.Text);

        // Committed by a character, which the editor then types: the late resolve still applies.
        editor.Editor.CaretOffset = Typed().EndOffset;
        var data = editor.NewCompletionData(item, Slow);
        data.CommitCharacter = ';';
        data.Complete(Typed());
        data.CommitCharacter = null;
        editor.Editor.TextArea.PerformTextInput(";");
        Assert.Contains("{ List<>; }", document.Text, StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => document.Text.StartsWith("using System.Collections.Generic;", StringComparison.Ordinal));
        Assert.Contains("{ List; }", document.Text, StringComparison.Ordinal);
        for (var undo = 0; undo < 3 && document.Text != Text; undo++)
        {
            editor.Editor.Undo(); // the late edits join the typed character's step
        }

        Assert.Equal(Text, document.Text);

        // Typed on before it came: dropped.
        editor.Editor.CaretOffset = Typed().EndOffset;
        editor.NewCompletionData(item, Slow).Complete(Typed());
        editor.Editor.TextArea.PerformTextInput("x");
        await WaitAsync(400);
        Assert.Contains("{ List<>x }", document.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("using", document.Text, StringComparison.Ordinal);
    }
}
