using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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

    /// <summary>
    /// The searches some typing may start: one when it pauses, one for each real pause between keys (a key on a loaded
    /// machine can take longer than the search's delay), and one for each of the search's longest waits it lasted. The
    /// test allowed a fixed 5 for 20 keys, which assumed they took about 2 s: during the whole suite, three test projects
    /// at a time, they took longer and the search ran 12 times, as it should.
    /// </summary>
    private sealed class Typing
    {
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private TimeSpan _last;
        private int _pauses;

        public void Key()
        {
            var now = _clock.Elapsed;
            if (_last > TimeSpan.Zero && now - _last >= FindWidget.SearchDelay)
            {
                _pauses++;
            }

            _last = now;
        }

        public int MostSearches => 1 + _pauses + (int)(_clock.Elapsed / FindWidget.SearchMaxDelay);
    }

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
            ShellFixture.Pump(); // each step is a user action of its own, measured alone: not the four in one block

            // A frame draws the visible carets only, and builds no line for the others.
            ShellFixture.RenderFrame(f.Window);
            var visibleStart = view.VisualLines[0].FirstDocumentLine.Offset;
            var visibleEnd = view.VisualLines[^1].LastDocumentLine.EndOffset;
            var visible = editor.MultiCaret.ExtraCarets.Count(s => s.Caret >= visibleStart && s.Caret <= visibleEnd);
            Assert.InRange(visible, 1, 100);
            Assert.Equal(visible, editor.MultiCaret.DrawnCarets);
            Assert.Null(view.GetVisualLine(1));
            Assert.Null(view.GetVisualLine(25_001));
            ShellFixture.Pump();

            // Typing replaces every selection in one document change, undone in one step.
            changes = 0;
            editor.Editor.TextArea.PerformTextInput("X");
            Assert.Equal(1, changes);
            Assert.Equal(0, Occurrences(document.Text, "marker"));
            Assert.Equal(20_000, Occurrences(document.Text, "call(X,"));
            ShellFixture.Pump();
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
            find.Reactivate(); // typed into the widget: its first match is selected
            find.SearchPattern = string.Empty;
            await ShellFixture.PumpUntil(() => !find.IsSearching && find.MatchCount == 0);
            editor.Editor.CaretOffset = 0;
            var searches = find.SearchesRun;
            var typing = new Typing();
            foreach (var typed in s_typedQueries)
            {
                typing.Key();
                find.SearchPattern = typed;
                ShellFixture.Pump();
            }

            await ShellFixture.PumpUntil(() => !find.IsSearching && find.MatchCount > 0, seconds: 60);
            Assert.Equal(FindWidget.MaxMatches, find.MatchCount);
            Assert.True(find.IsCapped);
            Assert.InRange(find.SearchesRun - searches, 1, typing.MostSearches); // once per pause in typing, not once per key
            Assert.Equal((4, 4), (editor.Editor.SelectionStart, editor.Editor.SelectionLength)); // the first match is selected
            Assert.EndsWith("+", find.Status);
            Assert.NotEmpty(find.MatchLines);

            // Typing in the document: the highlights follow the edits, and one search runs when the typing pauses.
            searches = find.SearchesRun;
            editor.Editor.TextArea.ClearSelection();
            editor.Editor.CaretOffset = editor.Model.Text.GetLineByNumber(10).Offset;
            typing = new Typing();
            for (var i = 0; i < 20; i++)
            {
                typing.Key();
                editor.Editor.TextArea.PerformTextInput("z");
                ShellFixture.Pump();
            }

            await ShellFixture.PumpUntil(() => !find.IsSearching, seconds: 60);
            Assert.InRange(find.SearchesRun - searches, 1, typing.MostSearches);
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
        var stepped = find.MatchesStepped;
        find.FindPrevious();
        var previous = document.GetLineByNumber(24_999);
        Assert.Equal((previous.Offset + "x24998 = ".Length, 3), (editor.Editor.SelectionStart, editor.Editor.SelectionLength));
        Assert.InRange(find.MatchesStepped - stepped, 1, 1_000); // the ones near the caret, not the 4,998 from the last counted

        // Replace Next past them replaces the selected match (it reselected it) and goes on to the next one.
        find.ReplaceNext();
        Assert.Equal("x24998 = barbaz;", document.GetText(document.GetLineByNumber(24_999)));
        Assert.Equal((line.Offset + "x24999 = ".Length, 3), (editor.Editor.SelectionStart, editor.Editor.SelectionLength));
        editor.Editor.Undo();
        Assert.Equal(text, document.Text);

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

    [Fact]
    public void A_cancelled_search_stops_even_when_its_matches_are_skipped()
    {
        // Whole words: one kept match, then 100,000 skipped. The stop was asked only when the kept count was a multiple of
        // 256, so a cancelled search went on over the whole text.
        var text = "foo " + string.Concat(Enumerable.Repeat("foox ", 100_000));
        var asked = 0;
        var regex = FindSearch.RegexOf(new FindQuery("foo", MatchCase: true, WholeWords: true, UseRegex: false), TimeSpan.FromSeconds(10));
        Assert.Null(FindSearch.Matches(text, regex, wholeWords: true, FindWidget.MaxMatches, stop: () => ++asked > 1));
        Assert.Equal(2, asked);
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

        // Opened as a list, the item's resolve starts when it's selected: held until the commit is done.
        var gate = new TaskCompletionSource();
        Task<LspCompletionItem> Held(LspCompletionItem _) => Task.Run(async () =>
        {
            await gate.Task;
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
        editor.OpenCompletion(Typed().StartOffset, [editor.NewCompletionData(item, Held, commitCharacters: ";")]);
        Assert.True(editor.IsCompletionOpen);
        editor.Editor.TextArea.PerformTextInput(";");
        Assert.False(editor.IsCompletionOpen);
        Assert.Contains("{ List<>; }", document.Text, StringComparison.Ordinal);
        gate.SetResult();
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
        editor.Editor.Undo();
        editor.Editor.Undo();
        Assert.Equal(Text, document.Text);

        // Committed by a character that another handler then took (it wasn't typed): the commit's offsets are stale at the
        // next key, so the resolve is dropped there rather than applied after it.
        void TakeSemicolon(object? sender, TextInputEventArgs e) => e.Handled |= e.Text == ";";
        editor.Editor.TextArea.TextEntering += TakeSemicolon;
        editor.Editor.CaretOffset = Typed().EndOffset;
        gate = new TaskCompletionSource();
        editor.OpenCompletion(Typed().StartOffset, [editor.NewCompletionData(item, Held, commitCharacters: ";")]);
        editor.Editor.TextArea.PerformTextInput(";");
        editor.Editor.TextArea.TextEntering -= TakeSemicolon;
        Assert.Contains("{ List<> }", document.Text, StringComparison.Ordinal);
        editor.Editor.TextArea.PerformTextInput("x");
        gate.SetResult();
        await WaitAsync(400);
        Assert.Contains("{ List<>x }", document.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("using", document.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_late_resolve_replaces_from_where_its_edit_starts_and_keeps_the_items_text_without_one()
    {
        // The C# service's items have no edit of their own; an override's resolve replaces "override ToS" with the whole
        // declaration. Committed (Tab) before it came, the late edit must start where its range does, not at the typed word.
        await using var f = ShellFixture.Create();
        const string Text = "class C\n{\n    override ToS\n}\n";
        var editor = await OpenAsync(f, "override.txt", Text);
        var document = editor.Model.Text;
        var typedFrom = Text.IndexOf("ToS", StringComparison.Ordinal);
        TextSegment Typed() => new() { StartOffset = typedFrom, Length = 3 };
        Func<LspCompletionItem, Task<LspCompletionItem>> Slow(LspCompletionItem resolved) => _ => Task.Run(async () =>
        {
            await Task.Delay(50);
            return resolved;
        });
        var item = new LspCompletionItem("ToString()") { InsertText = "ToString", Data = JsonElement.Parse("1") };
        var declaration = new LspCompletionItem("ToString()")
        {
            TextEdit = new TextEdit(new Range(new Position(2, 4), new Position(2, 16)), "public override string ToString() => base.ToString();"),
        };

        editor.Editor.CaretOffset = Typed().EndOffset;
        editor.NewCompletionData(item, Slow(declaration)).Complete(Typed());
        Assert.Contains("    override ToString\n", document.Text, StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => document.Text.Contains("=>", StringComparison.Ordinal));
        Assert.Equal("class C\n{\n    public override string ToString() => base.ToString();\n}\n", document.Text);
        editor.Editor.Undo();
        Assert.Equal(Text, document.Text);

        // A resolve with no edit (only a using): the item's own text stays, not the resolve's label.
        var usingOnly = new LspCompletionItem("ToString() (resolved)")
        {
            AdditionalTextEdits = [new TextEdit(new Range(new Position(0, 0), new Position(0, 0)), "using System;\n")],
        };
        editor.Editor.CaretOffset = Typed().EndOffset;
        editor.NewCompletionData(item, Slow(usingOnly)).Complete(Typed());
        await ShellFixture.PumpUntil(() => document.Text.StartsWith("using System;", StringComparison.Ordinal));
        Assert.Equal("using System;\nclass C\n{\n    override ToString\n}\n", document.Text);
    }

    [AvaloniaFact]
    public async Task Insert_next_matching_caret_goes_on_past_the_wrap_to_every_match()
    {
        await using var f = ShellFixture.Create();
        const string Text = "word 1\nword 2\nword 3\nword 4\n";
        var editor = await OpenAsync(f, "next.txt", Text);
        int Line(int number) => editor.Model.Text.GetLineByNumber(number).Offset;
        editor.Editor.CaretOffset = Line(3) + 1;
        editor.MultiCaret.SelectNextOccurrence(); // selects the word
        for (var i = 0; i < 3; i++)
        {
            editor.MultiCaret.SelectNextOccurrence(); // line 4, then wrapping around: line 1, line 2
        }

        Assert.Equal([Line(1), Line(2), Line(4)], editor.MultiCaret.ExtraCarets.Select(s => s.Start));
        editor.MultiCaret.SelectNextOccurrence(); // every match taken: nothing more
        Assert.Equal(3, editor.MultiCaret.ExtraCarets.Count);
    }

    /// <summary>Opens Quick Find on <paramref name="pattern"/>, typed into it (it has the focus), and waits for its results.</summary>
    private static async Task<FindWidget> FindAsync(CodeEditorController editor, string pattern, bool replace = false)
    {
        var find = editor.Search;
        find.IsReplaceMode = replace;
        find.Open();
        find.Reactivate();
        ShellFixture.Pump();
        find.SearchPattern = pattern;
        await ShellFixture.PumpUntil(() => !find.IsSearching && find.Status.Length > 0, seconds: 30);
        return find;
    }

    [AvaloniaFact]
    public async Task A_search_ending_while_the_user_types_in_the_text_never_selects_a_match()
    {
        // A query with no match yet, then the word typed into the document: the search the edit starts must not select it
        // (and move the caret), or the next key replaces it.
        await using var f = ShellFixture.Create();
        var editor = await OpenAsync(f, "typing.txt", "first line\nsecond line\n");
        var find = await FindAsync(editor, "zebra");
        Assert.Equal(0, find.MatchCount);
        Assert.Equal("No matches found", find.Status);

        editor.Editor.TextArea.Focus();
        editor.Editor.CaretOffset = editor.Model.Text.TextLength;
        foreach (var c in "zebra")
        {
            editor.Editor.TextArea.PerformTextInput(c.ToString());
        }

        var caret = editor.Editor.CaretOffset;
        await ShellFixture.PumpUntil(() => !find.IsSearching && find.MatchCount == 1, seconds: 30);
        Assert.Equal((caret, 0), (editor.Editor.CaretOffset, editor.Editor.SelectionLength));

        // Typed into the widget again: the first match is selected.
        find.Reactivate();
        ShellFixture.Pump();
        find.SearchPattern = "line";
        await ShellFixture.PumpUntil(() => !find.IsSearching && find.MatchCount == 2, seconds: 30);
        Assert.Equal(("line", 4), (editor.Editor.SelectedText, editor.Editor.SelectionLength));
    }

    [AvaloniaFact]
    public async Task Quick_find_keys_go_between_matches_replace_and_close()
    {
        await using var f = ShellFixture.Create();
        const string Text = "foo one\nfoo two\nfoo three\n";
        var editor = await OpenAsync(f, "keys.txt", Text);
        var document = editor.Model.Text;
        var find = await FindAsync(editor, "foo", replace: true);
        int Line(int number) => document.GetLineByNumber(number).Offset;
        Assert.Equal(Line(1), editor.Editor.SelectionStart);
        Assert.Equal("1 of 3", find.Status);

        f.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(Line(2), editor.Editor.SelectionStart);
        f.Window.KeyPress(Key.F3, RawInputModifiers.None, PhysicalKey.F3, null);
        Assert.Equal(Line(3), editor.Editor.SelectionStart);
        f.Window.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, null);
        Assert.Equal(Line(2), editor.Editor.SelectionStart);

        // Replace Next: the selected match is replaced and the next one selected; with none selected, it selects first.
        find.ReplacePattern = "bar";
        f.Window.KeyPress(Key.R, RawInputModifiers.Alt, PhysicalKey.R, null);
        Assert.Equal("foo one\nbar two\nfoo three\n", document.Text);
        Assert.Equal(Line(3), editor.Editor.SelectionStart);
        editor.Editor.TextArea.ClearSelection();
        editor.Editor.CaretOffset = 0;
        find.ReplaceNext();
        Assert.Equal("foo one\nbar two\nfoo three\n", document.Text);
        Assert.Equal((0, 3), (editor.Editor.SelectionStart, editor.Editor.SelectionLength));
        find.ReplaceNext();
        Assert.Equal("bar one\nbar two\nfoo three\n", document.Text);

        // Escape in the box closes it; Ctrl+F's widget closes on Escape in the text too.
        f.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(find.IsOpen);
        find.Open();
        editor.Editor.TextArea.Focus();
        f.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(find.IsOpen);
    }

    [AvaloniaFact]
    public async Task An_invalid_or_runaway_regular_expression_says_so_without_hanging()
    {
        await using var f = ShellFixture.Create();
        var editor = await OpenAsync(f, "regex.txt", new string('a', 40) + "\n");
        var find = editor.Search;
        find.UseRegex = true;
        await FindAsync(editor, "(");
        Assert.Equal("Invalid regular expression", find.Status);

        // Nested quantifiers that backtrack for ever: the search stops at its timeout, off the UI thread.
        using var monitor = new UiStallMonitor();
        find.SearchPattern = "(a+)+b";
        await ShellFixture.PumpUntil(() => !find.IsSearching && find.Status != "Invalid regular expression", seconds: 30);
        Assert.Equal("The expression takes too long", find.Status);
        monitor.AssertResponsive("a runaway regular expression in Quick Find", Budget);
    }
}
