using System.Text;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;
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
    private const string Chunk = "f(a[1],{b:2});";

    /// <summary>A minified script's text of exactly <paramref name="length"/> characters.</summary>
    private static string Filler(int length) => string.Concat(Enumerable.Repeat(Chunk, (length / Chunk.Length) + 1))[..length];

    /// <summary>The first line as the editor draws it now.</summary>
    private static VisualLine FirstLine(CodeEditorController editor)
    {
        var view = editor.Editor.TextArea.TextView;
        view.EnsureVisualLines();
        return view.GetOrConstructVisualLine(editor.Model.Text.GetLineByNumber(1));
    }

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
        await ShellFixture.PumpUntil(() => editor.LongLineCuts > 0);

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

        // Typing at the start keeps the cut where it is: the marker hides one more character (with the caret elsewhere; one
        // pushed into the hidden part has the line drawn up to it, as the caret test shows).
        editor.Editor.CaretOffset = editor.Model.Text.GetLineByNumber(2).Offset;
        editor.Editor.Document.Insert(0, "x");
        view.EnsureVisualLines();
        Assert.Equal(line.Length + 1 - LongLineCutter.MaxRendered, view.GetVisualLine(1)!.Elements[^1].DocumentLength);
        await editor.Model.SaveAsync(TestContext.Current.CancellationToken);
        Assert.Equal("x" + line + "\nvar second = 2;\n", await File.ReadAllTextAsync(editor.Model.Path, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task The_caret_never_stays_in_the_hidden_part_of_a_long_line()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;
        var line = Filler(210_000);
        var editor = await OpenAsync(f, "app.min.js", line + "\nvar second = 2;\n");
        var document = editor.Model.Text;
        var textArea = editor.Editor.TextArea;
        textArea.Focus();

        // Put far into the hidden part going forward (Go To, a diagnostic): it goes on to the line's end, and what's typed
        // lands there, not in the text nobody sees.
        editor.Editor.CaretOffset = 50_000;
        Assert.Equal(line.Length, editor.Editor.CaretOffset);
        textArea.PerformTextInput("x");
        Assert.Equal(line + "x", document.GetText(document.GetLineByNumber(1)));
        Assert.Equal(line.Length + 1, editor.Editor.CaretOffset);

        // Going back: to the furthest the line is drawn to, and the line is drawn up to it, the caret at the marker.
        editor.Editor.CaretOffset = 50_000;
        Assert.Equal(LongLineCutter.MaxDrawnToCaret, editor.Editor.CaretOffset);
        var drawn = FirstLine(editor);
        Assert.Equal(LongLineCutter.MaxDrawnToCaret + 1, drawn.VisualLength);
        Assert.Equal(line.Length + 1 - LongLineCutter.MaxDrawnToCaret, Assert.IsType<LongLineCutter.Marker>(drawn.Elements[^1]).DocumentLength);

        // Away from the line, it's cut where it usually is; typing at the marker shows what's typed, in order.
        editor.Editor.CaretOffset = document.GetLineByNumber(2).Offset;
        Assert.Equal(LongLineCutter.MaxRendered + 1, FirstLine(editor).VisualLength);
        editor.Editor.CaretOffset = LongLineCutter.MaxRendered;
        textArea.PerformTextInput("a");
        textArea.PerformTextInput("b");
        Assert.Equal("ab", document.GetText(LongLineCutter.MaxRendered, 2));
        Assert.Equal(LongLineCutter.MaxRendered + 2, editor.Editor.CaretOffset);
        drawn = FirstLine(editor);
        Assert.Equal(LongLineCutter.MaxRendered + 3, drawn.VisualLength);
        Assert.Equal(document.GetLineByNumber(1).Length - LongLineCutter.MaxRendered - 2, drawn.Elements[^1].DocumentLength);

        // Back on the line's start, the line stays drawn that far while the caret is on it, then the usual cut again.
        editor.Editor.CaretOffset = 0;
        Assert.Equal(LongLineCutter.MaxRendered + 3, FirstLine(editor).VisualLength);
        editor.Editor.CaretOffset = document.GetLineByNumber(2).Offset;
        Assert.Equal(LongLineCutter.MaxRendered + 1, FirstLine(editor).VisualLength);
    }

    [AvaloniaFact]
    public async Task Find_shows_a_match_just_past_the_cut_and_passes_the_far_ones_both_ways()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;
        var line = Filler(15_000) + "MARK" + Filler(34_996) + "MARK" + Filler(159_996); // at 15,000 and at 50,000
        var editor = await OpenAsync(f, "find.min.js", line + "\nvar second = 2;\n");
        editor.Editor.CaretOffset = 0;

        // Just past the cut: the line is drawn up to the match, which is selected as anywhere else.
        editor.Search.Open();
        editor.Search.Reactivate(); // typed into the widget, which has the focus
        ShellFixture.Pump();
        editor.Search.SearchPattern = "MARK";
        await ShellFixture.PumpUntil(() => editor.Editor.SelectionLength > 0); // searched off the UI thread, a moment after the last key
        Assert.Equal((15_000, 4), (editor.Editor.SelectionStart, editor.Editor.SelectionLength));
        Assert.Equal(15_004, editor.Editor.CaretOffset);
        Assert.Equal(15_005, FirstLine(editor).VisualLength);

        // Far past it: Find Next goes on to the line's end, with nothing selected in the hidden text.
        editor.Search.FindNext();
        ShellFixture.Pump();
        Assert.Equal(line.Length, editor.Editor.CaretOffset);
        Assert.Equal(0, editor.Editor.SelectionLength);

        // Find Previous from there: back to where the line can be drawn to, then on to the match before.
        editor.Search.FindPrevious();
        ShellFixture.Pump();
        Assert.Equal(LongLineCutter.MaxDrawnToCaret, editor.Editor.CaretOffset);
        Assert.Equal(0, editor.Editor.SelectionLength);
        Assert.Equal(LongLineCutter.MaxDrawnToCaret + 1, FirstLine(editor).VisualLength);
        editor.Search.FindPrevious();
        ShellFixture.Pump();
        Assert.Equal((15_000, 4), (editor.Editor.SelectionStart, editor.Editor.SelectionLength));
        editor.Search.Close();
    }

    [AvaloniaFact]
    public async Task The_line_drawn_to_the_caret_stays_drawn_after_a_change_that_rebuilds_it()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;
        var text = new StringBuilder();
        for (var i = 1; i <= 6_000; i++)
        {
            text.Append(i == 10 ? Filler(15_000) : $"var v{i} = {i};").Append('\n');
        }

        var editor = await OpenAsync(f, "rebuilt.js", text.ToString());
        var document = editor.Model.Text;
        var view = editor.Editor.TextArea.TextView;
        editor.Editor.TextArea.Focus();
        editor.Editor.CaretOffset = document.GetLineByNumber(10).Offset + 12_000;
        DrawnToCaret();
        var caret = CaretX();

        // A group from line 9 into line 10 (a format, a rename): AvaloniaEdit replaces the span, which deletes line 10's
        // DocumentLine for a new one. The caret's line and column stay (what's before it keeps its length), so it doesn't move.
        var lineTen = document.GetLineByNumber(10);
        DocumentEdits.Apply(document, [(document.GetLineByNumber(9).Offset, 0, "/**/"), .. LineTenEdits()]);
        Assert.True(lineTen.IsDeleted);
        DrawnToCaret();
        Assert.Equal(caret, CaretX());

        // A group from line 1 to the end replaces all of the text, which rebuilds every line but the first; and its undo.
        var edits = new List<(int Offset, int Length, string Text)>();
        for (var i = 1; i <= 6_000; i++)
        {
            if (i == 10)
            {
                edits.AddRange(LineTenEdits());
            }
            else
            {
                edits.Add((document.GetLineByNumber(i).Offset, 0, "/**/"));
            }
        }

        var last = document.GetLineByNumber(document.LineCount);
        DocumentEdits.Apply(document, edits);
        Assert.NotSame(last, document.GetLineByNumber(document.LineCount)); // the whole text replaced, not the edits' span
        DrawnToCaret();
        Assert.Equal(caret, CaretX());
        document.UndoStack.Undo();
        DrawnToCaret();
        Assert.Equal(caret, CaretX());

        // Away from line 10, the usual cut again.
        editor.Editor.CaretOffset = document.GetLineByNumber(11).Offset;
        view.EnsureVisualLines();
        Assert.Equal(LongLineCutter.MaxRendered + 1, view.GetOrConstructVisualLine(document.GetLineByNumber(10)).VisualLength);

        // Every 500 characters of line 10 up to the caret, one replaced by another (the caret's column stays), then inserts
        // after it to the line's end.
        List<(int Offset, int Length, string Text)> LineTenEdits()
        {
            var line = document.GetLineByNumber(10);
            var list = new List<(int Offset, int Length, string Text)>();
            for (var column = 0; column < 12_000; column += 500)
            {
                list.Add((line.Offset + column, 1, "x"));
            }

            for (var column = 12_500; column < line.Length; column += 500)
            {
                list.Add((line.Offset + column, 0, "/**/"));
            }

            return list;
        }

        // Line 10 is drawn up to the caret at column 12,000: no marker before it.
        void DrawnToCaret()
        {
            Assert.Equal(new TextLocation(10, 12_001), editor.Editor.TextArea.Caret.Location);
            view.EnsureVisualLines();
            var drawn = view.GetOrConstructVisualLine(document.GetLineByNumber(10));
            Assert.Equal(12_001, drawn.VisualLength);
            Assert.IsType<LongLineCutter.Marker>(drawn.Elements[^1]);
        }

        double CaretX() => view.GetVisualPosition(editor.Editor.TextArea.Caret.Position, VisualYPosition.LineMiddle).X;
    }

    [AvaloniaFact]
    public async Task A_surrogate_pair_at_the_cut_is_kept_whole_where_another_colorizer_split_the_line()
    {
        await using var f = ShellFixture.Create();
        var line = Filler(LongLineCutter.MaxRendered - 1) + "\U0001F600" + Filler(200_000); // the pair at 9,999 and 10,000
        var editor = await OpenAsync(f, "emoji.txt", line + "\n");
        var view = editor.Editor.TextArea.TextView;

        // A colorizer before the cut ends an element at the pair (as a squiggle, a selection or a token can).
        view.LineTransformers.Insert(view.LineTransformers.IndexOf(view.LineTransformers.OfType<LongLineCutter>().Single()), new ColorFrom(LongLineCutter.MaxRendered - 1));
        view.Redraw();
        var drawn = FirstLine(editor);
        Assert.Equal(LongLineCutter.MaxRendered, drawn.VisualLength); // cut before the pair, not drawn whole
        Assert.Equal(line.Length - LongLineCutter.MaxRendered + 1, Assert.IsType<LongLineCutter.Marker>(drawn.Elements[^1]).DocumentLength);
    }

    [AvaloniaFact]
    public async Task A_surrogate_pair_across_an_element_boundary_at_the_cut_is_kept_whole()
    {
        await using var f = ShellFixture.Create();
        var line = Filler(LongLineCutter.MaxRendered - 1) + "\U0001F600" + Filler(200_000);
        var editor = await OpenAsync(f, "emoji.txt", line + "\n");
        var view = editor.Editor.TextArea.TextView;
        view.LineTransformers.Insert(view.LineTransformers.IndexOf(view.LineTransformers.OfType<LongLineCutter>().Single()), new ColorFrom(LongLineCutter.MaxRendered));
        view.Redraw();
        var drawn = FirstLine(editor);
        Assert.Equal(LongLineCutter.MaxRendered, drawn.VisualLength);
        Assert.False(char.IsHighSurrogate(editor.Model.Text.GetCharAt(drawn.Elements[^2].RelativeTextOffset + drawn.Elements[^2].DocumentLength - 1)));
    }

    /// <summary>Colors each line from a column on, which splits its text there.</summary>
    private sealed class ColorFrom(int column) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length > column)
            {
                ChangeLinePart(line.Offset + column, line.EndOffset, e => e.TextRunProperties.SetForegroundBrush(Brushes.Red));
            }
        }
    }

    [AvaloniaFact]
    public async Task A_large_file_opens_without_colors_folding_or_its_language_service()
    {
        await using var f = ShellFixture.Create();
        var services = (IEditorServices)f.Shell;
        var text = new StringBuilder("<root>\n");
        for (var i = 0; i <= CodeDocument.LargeFileLines / 3; i++)
        {
            text.Append("  <item>\n    <v>").Append(i).Append("</v>\n  </item>\n");
        }

        text.Append("</root>\n");
        var large = await OpenAsync(f, "large.xml", text.ToString());
        Assert.True(large.Model.IsLarge);
        Assert.Null(large.GrammarScope);
        Assert.False(f.Shell.Languages.IsOpen(large.Model.Path)); // no didOpen: the text never went to the markup service
        Assert.True(large.Editor.FindAncestorOfType<CodeEditorView>()!.ShowsLargeFileBar);

        // A file under the limits, the same language: colors, folding and the service.
        var small = await OpenAsync(f, "small.xml", "<root>\n  <item>\n    <v>1</v>\n  </item>\n</root>\n");
        Assert.False(small.Model.IsLarge);
        Assert.Equal("text.xml", small.GrammarScope);
        Assert.True(f.Shell.Languages.IsOpen(small.Model.Path));
        Assert.False(small.Editor.FindAncestorOfType<CodeEditorView>()!.ShowsLargeFileBar);
        await ShellFixture.PumpUntil(() => small.Folding.AllFoldings.Any() && services.Client(small.Model) is not null, seconds: 20);
        Assert.True(small.FoldingPasses > 0);
        Assert.Null(services.Client(large.Model)); // the service runs, and still gets no requests for it

        // The large one never folds, not even after an edit (its XML would have hundreds of thousands of foldings).
        large.Model.Text.Insert(0, "<!-- -->");
        ShellFixture.Pump();
        Assert.Equal(0, large.FoldingPasses);
        Assert.Empty(large.Folding.AllFoldings);

        // CodeLens isn't expected for a large C# file, as it is for a small one.
        var folder = Path.Combine(f.UserData, "cs");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "Large.cs"), string.Concat(Enumerable.Repeat("// line\n", CodeDocument.LargeFileLines + 1)), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folder, "Small.cs"), "class A { }\n", TestContext.Current.CancellationToken);
        using var largeCs = await CodeDocument.OpenAsync(Path.Combine(folder, "Large.cs"), TestContext.Current.CancellationToken);
        using var smallCs = await CodeDocument.OpenAsync(Path.Combine(folder, "Small.cs"), TestContext.Current.CancellationToken);
        largeCs.LanguageId = smallCs.LanguageId = "csharp";
        Assert.True(largeCs.IsLarge);
        Assert.False(services.ExpectsCodeLens(largeCs));
        Assert.True(services.ExpectsCodeLens(smallCs));
    }

    /// <summary>The longest any input may wait while a large document goes to its language service (M57's tight budget).</summary>
    private const double LanguageServiceBudget = 100;

    /// <summary>
    /// The smallest window: each frame the headless renderer draws costs the UI thread what the frame's pixels cost (35 ms
    /// at 1280 by 800 in a 4-core container), which a budget for the language service's work shouldn't measure.
    /// </summary>
    private static void SmallWindow(ShellFixture f)
    {
        f.Window.Width = f.Window.MinWidth;
        f.Window.Height = f.Window.MinHeight;
        ShellFixture.Pump();
    }

    private static async Task PumpForAsync(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        await ShellFixture.PumpUntil(() => DateTime.UtcNow >= until, seconds: 120);
    }

    /// <summary>The LSP conformance server (tests/Codalus.LspConformance.Server), which logs what it gets to a file.</summary>
    internal static string ConformanceServer => Path.Combine(ShellFixture.RepoRoot, "tests", "Codalus.LspConformance.Server", "bin",
        typeof(EditorLargeTextTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(a => a.Key == "CodalusConfiguration").Value!, Codalus.Tests.RepoBuild.TargetFramework, "codalus-lsp-conformance.dll");

    /// <summary>The conformance server as the language service of <c>.conf</c> files, logging to <paramref name="log"/>.</summary>
    private static void RegisterConformanceServer(ShellFixture f, string log, params string[] options)
    {
        f.Shell.Workbench.LanguageDefinitions.Register(new Workbench.Languages.LanguageDefinition("conf", "Conformance") { Extensions = [".conf"] });
        f.Shell.Workbench.LanguageServers.Register(new Workbench.Languages.LanguageServiceDescriptor("conformance", "Conformance", [], null, () => Rpc.ProcessLaunchInfo.FromPath(ConformanceServer, ["--log", log, .. options]))
        {
            Languages = ["conf"],
        });
    }

    [AvaloniaFact]
    public async Task A_document_just_under_the_limits_goes_to_the_markup_service_without_holding_the_ui_thread()
    {
        // 19 MB of XML that is mostly '<' (each one six bytes of JSON): the didOpen made the whole text a string and
        // encoded it on the UI thread (M56's TODO); now the UI thread only queues it (M57).
        await using var f = ShellFixture.Create();
        SmallWindow(f);
        var services = (IEditorServices)f.Shell;
        var small = await OpenAsync(f, "warm.xml", "<root><item/></root>\n");
        await ShellFixture.PumpUntil(() => services.Client(small.Model) is not null, seconds: 60); // the markup service runs

        // The '<'s in character data: one element, so the service's own answers (symbols, foldings) stay small.
        const string Line = "<<a <b <c <d <e <f <g <h <i <j <k <l <m <n <o <p <q <r <s <t <u <v <w <x <y <z <<<<<<<<<<\n";
        var text = "<root><![CDATA[\n" + string.Concat(Enumerable.Repeat(Line, 19_000_000 / Line.Length)) + "]]></root>\n";
        Assert.InRange(text.Length, 18_000_000, CodeDocument.LargeFileCharacters - 1);
        var path = Path.Combine(f.UserData, "big.xml");
        var document = new TextDocument(text);
        var snapshot = document.CreateSnapshot();

        // The language service's part of the editor's open, as Track does it: from the UI thread, with the service
        // running, so nothing before the didOpen yields (the rest of the open is M56's, measured there).
        using var monitor = new UiStallMonitor();
        Assert.True(await f.Shell.Languages.OpenAsync(new Workbench.Languages.LanguageDocument(path, "xml", () => (snapshot.Text, 1))));
        var client = services.Client(small.Model)!;
        await client.HoverAsync(LanguageServices.Protocol.DocumentUris.FromPath(path), new LanguageServices.Protocol.Position(0, 1), TestContext.Current.CancellationToken); // after the didOpen
        await PumpForAsync(1000);
        monitor.AssertResponsive("a 19 MB XML document's didOpen", LanguageServiceBudget);
        await f.Shell.Languages.CloseAsync(path);
    }

    [AvaloniaFact]
    public async Task Typing_in_a_large_file_under_a_server_that_syncs_whole_texts_never_holds_the_ui_thread()
    {
        // A server that syncs whole documents (TextDocumentSyncKind.Full) gets the text at each key: it was made a string
        // and encoded on the UI thread, about 15 MB per key here; now it is read from the snapshot on the writer loop.
        await using var f = ShellFixture.Create();
        SmallWindow(f);
        var log = Path.Combine(f.UserData, "conformance.log");
        RegisterConformanceServer(f, log);

        var line = "key = " + new string('v', 54) + "\n";
        var text = string.Concat(Enumerable.Repeat(line, 250_000)); // 15 MB, under both limits
        var editor = await OpenAsync(f, "big.conf", text);
        Assert.False(editor.Model.IsLarge);
        var services = (IEditorServices)f.Shell;
        await ShellFixture.PumpUntil(() => services.Client(editor.Model) is not null, seconds: 60);
        Assert.Equal(LanguageServices.Protocol.TextDocumentSyncKind.Full, services.Client(editor.Model)!.Capabilities!.TextDocumentSync);

        using var monitor = new UiStallMonitor();
        async Task TypeAsync()
        {
            editor.Editor.CaretOffset = 0;
            for (var i = 0; i < 20; i++)
            {
                editor.Editor.TextArea.PerformTextInput("z");
                ShellFixture.Pump();
            }

            await PumpForAsync(300);
        }

        await TypeAsync(); // for the JIT
        monitor.Reset();
        await TypeAsync();
        monitor.AssertResponsive("typing 20 keys in a 15 MB file under a whole-text server", LanguageServiceBudget);

        // The server ends on the editor's text, at its version, whatever was replaced on the way.
        var client = services.Client(editor.Model)!;
        await client.HoverAsync(editor.Model.Uri, new LanguageServices.Protocol.Position(0, 0), TestContext.Current.CancellationToken);
        Assert.Equal(editor.Model.Text.Text, await File.ReadAllTextAsync(log + ".text", TestContext.Current.CancellationToken));
        var versions = File.ReadAllLines(log).Where(l => l.StartsWith("didChange ", StringComparison.Ordinal)).Select(l => int.Parse(l.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(versions.Order().Distinct(), versions);
        Assert.Equal(editor.Model.Version, versions[^1]);
        TestContext.Current.TestOutputHelper?.WriteLine($"{versions.Count} whole texts for 40 keys; the longest wait {monitor.Report().Describe()}");
    }

    /// <summary>
    /// A document edited while its didOpen is still queued: the didOpen, read when written, carries the editor's snapshot
    /// with that snapshot's own version (taken together under the lock), and the edits queued before it was written are
    /// skipped, so an incremental server neither applies them twice nor goes back (M57 review).
    /// </summary>
    [AvaloniaFact]
    public async Task Edits_made_while_a_didopen_is_queued_reach_an_incremental_server_once_at_their_versions()
    {
        await using var f = ShellFixture.Create();
        var log = Path.Combine(f.UserData, "conformance.log");
        RegisterConformanceServer(f, log, "--incremental");
        var services = (IEditorServices)f.Shell;
        var warm = await OpenAsync(f, "warm.conf", "warm = 1\n");
        await ShellFixture.PumpUntil(() => services.Client(warm.Model) is not null, seconds: 60);

        // The connection's writer is held in another document's didOpen, so the editor's didOpen and its edits queue.
        using var gate = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Path.Combine(f.UserData, "holder.conf");
        var held = f.Shell.Languages.OpenAsync(new Workbench.Languages.LanguageDocument(holder, "conf", () =>
        {
            entered.TrySetResult();
            gate.Wait(TimeSpan.FromSeconds(30));
            return ("holder = 1\n", 1);
        }));
        await ShellFixture.PumpUntil(() => entered.Task.IsCompleted);

        var editor = await OpenAsync(f, "edited.conf", "a = 1\n");
        editor.Editor.CaretOffset = 0;
        foreach (var key in "xyz")
        {
            editor.Editor.TextArea.PerformTextInput(key.ToString());
        }

        Assert.Equal(4, editor.Model.Version);
        gate.Set();
        await ShellFixture.PumpUntil(() => held.IsCompleted);
        Assert.True(await held);

        // One more edit after the didOpen went out: the server applies it to the text the didOpen carried.
        editor.Editor.TextArea.PerformTextInput("!");
        var client = services.Client(editor.Model)!;
        var hover = client.HoverAsync(editor.Model.Uri, new LanguageServices.Protocol.Position(0, 0), TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => hover.IsCompleted);
        await hover;
        Assert.Equal(editor.Model.Text.Text, await File.ReadAllTextAsync(log + ".text", TestContext.Current.CancellationToken));
        var lines = File.ReadAllLines(log).SkipWhile(l => l != "didOpen 1 11").Skip(1).ToList(); // after the holder's
        Assert.Equal(["didOpen 4 9", "didChange 5 10 1", "hover"], lines);
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

    [Fact]
    public void Themes_are_read_once_each_until_the_languages_change()
    {
        var languages = new Workbench.Languages.LanguageRegistry();
        var textMate = new TextMateSupport(languages);
        textMate.ThemeFor("dark");
        textMate.ThemeFor("dark");
        textMate.GetDefaultTheme();
        Assert.Equal(1, textMate.ThemeReads);
        textMate.ThemeFor("light");
        textMate.ThemeFor("dark");
        Assert.Equal(2, textMate.ThemeReads);

        // A plugin's grammar or language may bring its own: read again after a change.
        languages.Register(new Workbench.Languages.LanguageDefinition("sample", "Sample"));
        textMate.ThemeFor("dark");
        Assert.Equal(3, textMate.ThemeReads);
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

        // Shown again: its thread starts again, and an edit made while hidden is tokenized again (the key's "a" moved from
        // column 3 to 7, where the line's old tokens have the number).
        first.Editor.Document.Insert(0, "[1, ");
        ShellFixture.Pump();
        Assert.Contains("constant.numeric.json", first.GrammarScopesAt(1, 7) ?? []); // hidden: not tokenized again yet
        await f.Shell.OpenFileAsync(first.Model.Path, preview: false, null);
        await ShellFixture.PumpUntil(() => first.GrammarTokenizing && !third.GrammarTokenizing);
        await ShellFixture.PumpUntil(() => first.GrammarScopesAt(1, 7)?.Any(s => s.StartsWith("support.type.property-name", StringComparison.Ordinal)) == true, seconds: 20);
    }
}
