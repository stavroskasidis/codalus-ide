using System.Text;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using Codalus.Shell.Editor;
using Codalus.Workbench.FileSystem;

namespace Codalus.Shell.Tests;

/// <summary>
/// Large texts in the editor (M56, the UI never hangs): long lines are drawn up to a limit, large files open without the
/// work that grows with them, bracket colors far from what's known are lexed off the UI thread, foldings are capped, and
/// hidden editors give their grammar's thread back.
/// </summary>
public sealed class EditorLargeTextTests
{
    private static async Task<CodeEditorController> OpenAsync(ShellFixture f, string name, string text)
    {
        var path = Path.Combine(f.UserData, name);
        await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
        await f.Shell.OpenFileAsync(path, preview: false, null);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == path && f.Shell.ActiveEditor.Controller is not null);
        return f.Shell.ActiveEditor!.Controller!;
    }

    [AvaloniaFact]
    public async Task A_long_line_is_drawn_to_ten_thousand_characters_and_keeps_all_its_text()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;
        var line = string.Concat(Enumerable.Repeat("f(a[1],{b:2});", 15_000)); // 210,000 characters, a minified script's
        var editor = await OpenAsync(f, "bundle.min.js", line + "\nvar second = 2;\n");
        var view = editor.Editor.TextArea.TextView;
        await ShellFixture.PumpUntil(() => editor.LongLinesCut > 0);

        view.EnsureVisualLines();
        var visual = view.GetVisualLine(1)!;
        Assert.IsType<LongLineCutter.Marker>(visual.Elements[^1]);
        Assert.Equal(LongLineCutter.MaxRendered + 1, visual.VisualLength);
        Assert.Equal(line.Length - LongLineCutter.MaxRendered, visual.Elements[^1].DocumentLength);
        Assert.True(visual.TextLines.Sum(t => t.Length) <= LongLineCutter.MaxRendered + 2, $"formatted {visual.TextLines.Sum(t => t.Length)} characters");
        Assert.Equal(15, view.GetVisualLine(2)!.VisualLength); // a short line is drawn whole

        // The text is all there: the document, a selection of the line, and a save.
        Assert.Equal(line.Length, editor.Model.Text.GetLineByNumber(1).Length);
        editor.Editor.Select(0, line.Length);
        Assert.Equal(line, editor.Editor.SelectedText);

        // The caret steps over the marker to the line's end, and back.
        editor.Editor.TextArea.Focus();
        editor.Editor.Select(LongLineCutter.MaxRendered, 0);
        f.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        await ShellFixture.PumpUntil(() => editor.Editor.CaretOffset != LongLineCutter.MaxRendered);
        Assert.Equal(line.Length, editor.Editor.CaretOffset);
        f.Window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        await ShellFixture.PumpUntil(() => editor.Editor.CaretOffset != line.Length);
        Assert.Equal(LongLineCutter.MaxRendered, editor.Editor.CaretOffset);

        // Typing at the start keeps the cut where it is: the marker hides one more character.
        editor.Editor.Document.Insert(0, "x");
        view.EnsureVisualLines();
        Assert.Equal(line.Length + 1 - LongLineCutter.MaxRendered, view.GetVisualLine(1)!.Elements[^1].DocumentLength);
        await editor.Model.SaveAsync(TestContext.Current.CancellationToken);
        Assert.Equal("x" + line + "\nvar second = 2;\n", await File.ReadAllTextAsync(editor.Model.Path, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task A_large_file_opens_without_colors_folding_or_its_language_service()
    {
        await using var f = ShellFixture.Create();
        var text = new StringBuilder("{\n");
        for (var i = 0; i < CodeDocument.LargeFileLines; i++)
        {
            text.Append("  \"key").Append(i).Append("\": [1, 2],\n");
        }

        text.Append("  \"last\": 0\n}\n");
        var large = await OpenAsync(f, "large.json", text.ToString());
        Assert.True(large.Model.IsLarge);
        Assert.Null(large.GrammarScope);
        Assert.False(f.Shell.Languages.IsOpen(large.Model.Path)); // no didOpen: the text never went to the markup service
        Assert.True(large.Editor.FindAncestorOfType<CodeEditorView>()!.ShowsLargeFileBar);

        // A file under the limits, the same language: colors, folding and the service.
        var small = await OpenAsync(f, "small.json", "{\n  \"a\": [1, 2],\n  \"b\": {\n    \"c\": 3\n  }\n}\n");
        Assert.False(small.Model.IsLarge);
        Assert.Equal("source.json", small.GrammarScope);
        Assert.True(f.Shell.Languages.IsOpen(small.Model.Path));
        Assert.False(small.Editor.FindAncestorOfType<CodeEditorView>()!.ShowsLargeFileBar);
        await ShellFixture.PumpUntil(() => small.Folding.AllFoldings.Any(), seconds: 20);
        Assert.Empty(large.Folding.AllFoldings);
    }

    [AvaloniaFact]
    public async Task Bracket_colors_lex_far_lines_off_the_ui_thread_and_edits_keep_the_states_after_them()
    {
        // Lines that open and close brackets and block comments, so a state depends on every line before it.
        var lines = Enumerable.Range(0, 100_000).Select(i => (i % 4) switch
        {
            0 => "{ call(x) [",
            1 => "  /* ] */ y(",
            2 => "  ) ] /* {",
            _ => "  } */ }",
        });
        var document = new TextDocument(string.Join("\n", lines));
        var redraws = 0;
        var colorizer = new BracketPairColorizer(document, _ => Brushes.Red, () => "csharp", () => redraws++);

        // Near the known states: lexed on the UI thread, a line at a time.
        Assert.Equal(BracketPairColorizer.Lexed(document, 500), colorizer.StateAt(500));
        Assert.Equal(499, colorizer.ScannedLines);

        // Far past them: unknown at first (the default color), then the background pass has it and redraws.
        Assert.Null(colorizer.StateAt(99_000));
        Assert.Equal(499, colorizer.ScannedLines);
        await ShellFixture.PumpUntil(() => colorizer.BackgroundPasses == 1 && redraws > 0);
        Assert.Equal(BracketPairColorizer.Lexed(document, 99_000), colorizer.StateAt(99_000));

        // A keystroke near the top: lexed again until a state comes out as it was, one line here.
        var scanned = colorizer.ScannedLines;
        document.Insert(document.GetLineByNumber(10).Offset + 2, "z");
        Assert.Equal(BracketPairColorizer.Lexed(document, 99_000), colorizer.StateAt(99_000));
        Assert.True(colorizer.ScannedLines - scanned <= 2, $"lexed {colorizer.ScannedLines - scanned} lines");

        // Lines added and removed move the states after them.
        document.Insert(document.GetLineByNumber(20).Offset, "x /* ( */\n");
        Assert.Equal(BracketPairColorizer.Lexed(document, 99_001), colorizer.StateAt(99_001));
        document.Remove(document.GetLineByNumber(33).Offset, document.GetLineByNumber(37).Offset - document.GetLineByNumber(33).Offset);
        Assert.Equal(BracketPairColorizer.Lexed(document, 98_997), colorizer.StateAt(98_997));
        Assert.True(colorizer.ScannedLines - scanned < 20, $"lexed {colorizer.ScannedLines - scanned} lines");

        // An unclosed bracket changes every state after it: the far line waits for a background pass again.
        document.Insert(document.GetLineByNumber(40).Offset, "(");
        Assert.Null(colorizer.StateAt(98_997));
        Assert.True(colorizer.ScannedLines - scanned < 20 + BracketPairColorizer.SyncLines, $"lexed {colorizer.ScannedLines - scanned} lines");
        await ShellFixture.PumpUntil(() => colorizer.BackgroundPasses == 2);
        Assert.Equal(BracketPairColorizer.Lexed(document, 98_997), colorizer.StateAt(98_997));
        colorizer.Detach();
    }

    [AvaloniaFact]
    public async Task A_reload_replaces_only_what_changed_on_disk_one_read_at_a_time()
    {
        // A file system whose reads can be held and whose changes are announced by the test, not a watcher's timing.
        var files = new HeldFile("line 1\nline 2\n");
        var root = Path.Combine(Path.GetTempPath(), "codalus-reload-" + Guid.NewGuid().ToString("N"));
        using var mount = FileSystems.Mount(root, files);
        var path = Path.Combine(root, "app.log");
        using var document = await CodeDocument.OpenAsync(path, TestContext.Current.CancellationToken); // built off the UI thread, used on it
        await ShellFixture.PumpUntil(() => files.Stats > 0); // the state it opened with
        var changes = new List<CodeDocumentChangedEventArgs>();
        document.ContentChanged += (_, e) => changes.Add(e);

        // A log written to: the reload is what was appended.
        files.Write("line 1\nline 2\nline 3\n");
        files.Notify(path);
        await ShellFixture.PumpUntil(() => changes.Count == 1);
        var appended = Assert.Single(changes[0].Changes);
        Assert.Equal("line 3\n", appended.Text);
        Assert.Equal((2, 0, 2, 0), RangeOf(appended));

        // Written again while that file is read: no second read meanwhile, then one for what the first didn't have.
        files.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.Write("line 1\nLINE 2\nline 3\n");
        files.Notify(path);
        await ShellFixture.PumpUntil(() => files.Reading == 1);
        var stats = files.Stats;
        files.Write("line 1\nLINE 2\nline 3\nline 4\n");
        files.Notify(path);
        await ShellFixture.PumpUntil(() => files.Stats > stats); // the watcher's check came
        ShellFixture.Pump();
        Assert.Equal(1, files.Reading);
        var hold = files.Hold;
        files.Hold = null;
        hold.SetResult();
        await ShellFixture.PumpUntil(() => changes.Count == 3);
        Assert.Equal(1, files.MostReading);
        var middle = Assert.Single(changes[1].Changes);
        Assert.Equal(("LINE", (1, 0, 1, 4)), (middle.Text, RangeOf(middle)));
        var fourth = Assert.Single(changes[2].Changes);
        Assert.Equal(("line 4\n", (3, 0, 3, 0)), (fourth.Text, RangeOf(fourth)));
        Assert.Equal("line 1\nLINE 2\nline 3\nline 4\n", document.Text.Text);
        Assert.False(document.IsModified);

        // The same text: no change at all.
        await document.ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, changes.Count);

        // Typed into while a change on disk is read: that's a conflict, and what was typed stays.
        var conflicts = 0;
        document.ConflictDetected += (_, _) => conflicts++;
        files.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.Write("line 1\nLINE 2\nline 3\nline 4\nline 5\n");
        files.Notify(path);
        await ShellFixture.PumpUntil(() => files.Reading == 1);
        document.Text.Insert(0, "// mine\n");
        hold = files.Hold;
        files.Hold = null;
        hold.SetResult();
        await ShellFixture.PumpUntil(() => conflicts == 1);
        Assert.Equal("// mine\nline 1\nLINE 2\nline 3\nline 4\n", document.Text.Text);
        Assert.True(document.IsModified);
    }

    [AvaloniaFact]
    public async Task A_reload_of_new_line_endings_never_splits_a_line_break()
    {
        var files = new HeldFile("a\r\nb\r\nc\r\n");
        var root = Path.Combine(Path.GetTempPath(), "codalus-reload-" + Guid.NewGuid().ToString("N"));
        using var mount = FileSystems.Mount(root, files);
        var path = Path.Combine(root, "crlf.txt");
        using var document = await CodeDocument.OpenAsync(path, TestContext.Current.CancellationToken);
        var changes = new List<CodeDocumentChangedEventArgs>();
        document.ContentChanged += (_, e) => changes.Add(e);

        // CRLF to LF (git's autocrlf, a formatter): the change ends after a line's "\r\n", never between its two characters,
        // which a language service would take as the line's end and keep the "\r".
        files.Write("a\nb\nc\n");
        await document.ReloadAsync(TestContext.Current.CancellationToken);
        var change = Assert.Single(Assert.Single(changes).Changes);
        Assert.Equal(("\nb\nc\n", (0, 1, 3, 0)), (change.Text, RangeOf(change)));
        Assert.Equal("a\nb\nc\n", document.Text.Text);
    }

    [Theory]
    [InlineData("a\r\nb\r\n", "a\nb\n", 1, 5, "\nb\n")] // ends after the "\r\n", not between
    [InlineData("a\nb", "a\r\nb", 1, 0, "\r")] // an insertion before a "\n" splits nothing
    [InlineData("x\U0001F600y", "x\U0001F601y", 1, 2, "\U0001F601")] // starts before the pair, not inside it
    [InlineData("line 1\n", "line 1\nline 2\n", 7, 0, "line 2\n")]
    public void A_difference_never_starts_or_ends_inside_a_line_break_or_a_surrogate_pair(string before, string after, int start, int removed, string inserted)
    {
        Assert.Equal((start, removed, inserted), CodeDocument.Difference(before, after));
        Assert.Equal(after, before[..start] + inserted + before[(start + removed)..]);
    }

    private static (int, int, int, int) RangeOf(LanguageServices.Protocol.TextDocumentContentChangeEvent change) =>
        (change.Range!.Start.Line, change.Range.Start.Character, change.Range.End.Line, change.Range.End.Character);

    /// <summary>
    /// A remote file system of one file: reads can be held (<see cref="Hold"/>) and return what the file had when they
    /// began, and changes are announced by <see cref="Notify"/>.
    /// </summary>
    private sealed class HeldFile(string initial) : IFileSystemProvider
    {
        private readonly Lock _gate = new();
        private byte[] _content = Encoding.UTF8.GetBytes(initial);
        private DateTime _modified = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private Action<IReadOnlyList<FileChangeEvent>>? _changed;
        private int _stats;
        private int _reading;
        private int _mostReading;

        public TaskCompletionSource? Hold { get; set; }

        public int Stats => Volatile.Read(ref _stats);

        /// <summary>The reads under way now, and the most there were at once.</summary>
        public int Reading => Volatile.Read(ref _reading);

        public int MostReading => Volatile.Read(ref _mostReading);

        public bool IsRemote => true;

        public void Write(string text)
        {
            lock (_gate)
            {
                (_content, _modified) = (Encoding.UTF8.GetBytes(text), _modified.AddSeconds(1));
            }
        }

        public void Notify(string path) => _changed?.Invoke([new(path, "changed")]);

        public Task<FileEntryInfo> StatAsync(string path, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _stats);
            lock (_gate)
            {
                return Task.FromResult(new FileEntryInfo(true, false, _content.Length, _modified));
            }
        }

        public async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
        {
            byte[] content;
            lock (_gate)
            {
                content = _content;
            }

            var reading = Interlocked.Increment(ref _reading);
            InterlockedMax(ref _mostReading, reading);
            try
            {
                if (Hold is { } hold)
                {
                    await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                return content;
            }
            finally
            {
                Interlocked.Decrement(ref _reading);
            }
        }

        public Task WriteAllBytesAsync(string path, byte[] content, CancellationToken cancellationToken)
        {
            Write(Encoding.UTF8.GetString(content));
            return Task.CompletedTask;
        }

        public IDisposable Watch(string path, bool recursive, Action<IReadOnlyList<FileChangeEvent>> changed)
        {
            _changed = changed;
            return new Unwatch(this);
        }

        public Task<IReadOnlyList<FileSystemEntry>> ReadDirectoryAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RenameAsync(string source, string target, bool overwrite, CancellationToken cancellationToken) => throw new NotSupportedException();

        private static void InterlockedMax(ref int location, int value)
        {
            var current = Volatile.Read(ref location);
            while (value > current && Interlocked.CompareExchange(ref location, value, current) is var seen && seen != current)
            {
                current = seen;
            }
        }

        private sealed class Unwatch(HeldFile files) : IDisposable
        {
            public void Dispose() => files._changed = null;
        }
    }

    [Fact]
    public void Foldings_are_capped_at_five_thousand_outermost_first_keeping_the_collapsed_ones()
    {
        var xml = new StringBuilder("<root>\n");
        for (var i = 0; i < 20_000; i++)
        {
            xml.Append("  <item>\n    <v>").Append(i).Append("</v>\n  </item>\n");
        }

        xml.Append("</root>\n");
        var document = new TextDocument(xml.ToString());
        var all = LanguageFeatures.Foldings(document, "xml", null);
        Assert.Equal(20_001, all.Count);
        var last = all[^1];
        var capped = LanguageFeatures.CapFoldings(all, new HashSet<(int, int)> { (last.StartOffset, last.EndOffset) });
        Assert.Equal(LanguageFeatures.MaxFoldings + 1, capped.Count);
        Assert.Equal((all[0].StartOffset, all[0].EndOffset), (capped[0].StartOffset, capped[0].EndOffset)); // the root
        Assert.Equal(all[LanguageFeatures.MaxFoldings - 1].StartOffset, capped[^2].StartOffset); // then the first items
        Assert.Same(last, capped[^1]); // and the collapsed one past them
        Assert.True(capped.Zip(capped.Skip(1)).All(p => p.First.StartOffset <= p.Second.StartOffset));
        var few = all.Take(100).ToList();
        Assert.Same(few, LanguageFeatures.CapFoldings(few, new HashSet<(int, int)>())); // under the cap: as they are
    }

    [AvaloniaFact]
    public async Task Hidden_editors_stop_their_grammar_thread_and_grammars_are_read_once()
    {
        await using var f = ShellFixture.Create();
        var first = await OpenAsync(f, "a.json", "{ \"a\": 1 }\n");
        await ShellFixture.PumpUntil(() => first.GrammarTokenizing);
        var reads = f.Shell.TextMate.GrammarReads;
        Assert.True(reads > 0);

        var second = await OpenAsync(f, "b.json", "{ \"b\": 2 }\n");
        var third = await OpenAsync(f, "c.json", "{ \"c\": 3 }\n");
        await ShellFixture.PumpUntil(() => third.GrammarTokenizing && !first.GrammarTokenizing && !second.GrammarTokenizing);
        Assert.Equal(reads, f.Shell.TextMate.GrammarReads); // the same grammar, parsed once

        // Shown again: its thread starts again, and an edit made while hidden is colored.
        first.Editor.Document.Insert(0, "\"x\" ");
        await f.Shell.OpenFileAsync(first.Model.Path, preview: false, null);
        await ShellFixture.PumpUntil(() => first.GrammarTokenizing && !third.GrammarTokenizing);
        var view = first.Editor.TextArea.TextView;
        await ShellFixture.PumpUntil(() =>
        {
            view.EnsureVisualLines();
            return view.GetVisualLine(1) is { } line && line.Elements.Select(e => e.TextRunProperties.ForegroundBrush).Distinct().Count() >= 3;
        }, seconds: 20);
    }
}
