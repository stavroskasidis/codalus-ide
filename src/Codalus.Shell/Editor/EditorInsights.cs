using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Codalus.LanguageServices.Client;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.Editor;

/// <summary>
/// The editor's inline information (M12a): inlay hints, inline diagnostics at the end of lines, CodeLens on a row of its
/// own above its line (the end of the line before, after a line break: the line itself keeps its height), the
/// inheritance margin, indentation guides and bracket pair colors. Language-service data is requested for the viewport
/// (debounced, cancellable) and kept in segments that follow edits until the next response. A C# file's CodeLens rows
/// are kept empty from its first frame, where the text says the service will list them, until their counts arrive.
/// UI thread only.
/// </summary>
public sealed class EditorInsights : IDisposable
{
    private static readonly TimeSpan s_hintDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan s_lensDelay = TimeSpan.FromMilliseconds(700);
    private const int ViewportMarginLines = 40;

    /// <summary>The longest text whose CodeLens rows are estimated on the UI thread (about 3 ms); longer ones off it.</summary>
    private const int EstimateOnUiThreadLength = 512 * 1024;

    private readonly TextEditor _editor;
    private readonly Func<CodeDocument> _model;
    private readonly Func<LanguageHostClient?> _client;
    private readonly IEditorServices _services;
    private readonly InlineGenerator _generator;
    private readonly IndentGuideRenderer _guides;
    private readonly BracketPairColorizer _brackets;
    private readonly DispatcherTimer _hintTimer = new() { Interval = s_hintDelay };
    private readonly DispatcherTimer _lensTimer = new() { Interval = s_lensDelay };
    private readonly DispatcherTimer _symbolTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private CancellationTokenSource? _symbolCts;
    private TextSegmentCollection<HintSegment> _hints;
    private TextSegmentCollection<LensSegment> _lenses;
    private Dictionary<int, string> _diagnosticsByLine = [];
    private (int First, int Last) _hintRange = (-1, -1);
    private int _lensVersion = -1;

    // The CodeLens rows kept before the service has listed the document's lenses: where its declarations are, from the
    // text alone (CSharpLensScanner). Null once it has, or when none are expected.
    private TextSegmentCollection<TextSegment>? _estimates;
    private bool _lensesListed;
    private CancellationTokenSource? _estimateCts;

    // Our own CodeLens entries (deferred from M12a): Git history from blame and test outcomes, by one-based line.
    private IReadOnlyList<Codalus.Workbench.Git.BlameRange> _blame = [];
    private int _blameVersion = -1;
    private long _blameAt;
    private CancellationTokenSource? _blameCts;
    private Dictionary<int, string> _gitLenses = [];
    private Dictionary<int, string> _testLenses = [];
    private bool _testLensesQueued;
    private static readonly TimeSpan s_blameInterval = TimeSpan.FromSeconds(3);
    private CancellationTokenSource? _hintCts;
    private CancellationTokenSource? _lensCts;
    private CancellationTokenSource? _runLensCts;
    private bool _disposed;

    public EditorInsights(TextEditor editor, Func<CodeDocument> model, Func<LanguageHostClient?> client, IEditorServices services)
    {
        _editor = editor;
        _model = model;
        _client = client;
        _services = services;
        var document = editor.Document;
        _hints = new TextSegmentCollection<HintSegment>(document);
        _lenses = new TextSegmentCollection<LensSegment>(document);
        _generator = new InlineGenerator(this);
        _guides = new IndentGuideRenderer(editor, Brush);
        _brackets = new BracketPairColorizer(document, Brush, () => _model().LanguageId ?? string.Empty);
        Margin = new InheritanceMargin(this);

        var view = editor.TextArea.TextView;
        view.ElementGenerators.Add(_generator);
        view.BackgroundRenderers.Add(_guides);
        view.LineTransformers.Add(_brackets);
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            _ = RefreshViewportAsync();
        };
        _lensTimer.Tick += (_, _) =>
        {
            _lensTimer.Stop();
            _ = RefreshLensesAsync();
        };
        _symbolTimer.Tick += (_, _) =>
        {
            _symbolTimer.Stop();
            _ = RefreshSymbolsAsync();
        };
        view.ScrollOffsetChanged += OnScrolled;
        document.Changed += OnDocumentChanged;
        editor.TextArea.AddHandler(InputElement.KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        services.Options.PropertyChanged += OnOptionsChanged;
        services.ThemeChanged += OnThemeChanged;
        _reviews = new TextSegmentCollection<ReviewSegment>(document);
        services.ReviewThreadsChanged += OnReviewThreadsChanged;
        services.Tests.Changed += OnTestsChanged;
        SymbolsChanged += (_, _) => UpdateGitLenses();
        ApplyOptions();
        LoadReviewThreads();
        UpdateTestLenses();
        EstimateLenses();
    }

    // ------------------------------------------------------------------ CodeLens rows before the service answers

    /// <summary>Whether the rows of CodeLens the service hasn't listed yet are kept (tests).</summary>
    internal bool HasLensEstimates => _estimates is not null;

    /// <summary>The CodeLens state, for a test's failure message.</summary>
    internal string DescribeLenses() =>
        $"listed {_lensesListed} (version {_lensVersion} of {Model.Version}), {_lenses.Count} lenses, {_estimates?.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no"} estimates, "
        + $"CodeLens {Options.CodeLens}, expected {_services.ExpectsCodeLens(Model)}, language {Model.LanguageId}";

    /// <summary>
    /// Keeps the room of the CodeLens rows the service will list until it does: the declarations
    /// <see cref="CSharpLensScanner"/> finds in the text, as the classic IDE does from its syntax tree. Opening a file
    /// (or restoring it at startup, before the service has loaded the solution), the code doesn't move down when the
    /// lenses arrive; their rows stay empty until the counts do. A long text is scanned off the UI thread.
    /// </summary>
    private void EstimateLenses()
    {
        var had = ClearEstimates();
        if (_lensesListed || !Options.CodeLens || !_services.ExpectsCodeLens(Model))
        {
            if (had)
            {
                _editor.TextArea.TextView.Redraw();
                Updated?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        var document = _editor.Document;
        if (document.TextLength <= EstimateOnUiThreadLength)
        {
            SetEstimates(CSharpLensScanner.Scan(document.Text));
            return;
        }

        _ = EstimateOffThreadAsync(document.CreateSnapshot(), Model.Version, Restart(ref _estimateCts).Token);
    }

    private async Task EstimateOffThreadAsync(AvaloniaEdit.Document.ITextSource snapshot, int version, CancellationToken cancellationToken)
    {
        try
        {
            var offsets = await Task.Run(() => CSharpLensScanner.Scan(snapshot.Text), cancellationToken).ConfigureAwait(true);
            if (!cancellationToken.IsCancellationRequested && !_disposed && !_lensesListed && Model.Version == version)
            {
                SetEstimates(offsets);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SetEstimates(IReadOnlyList<int> offsets)
    {
        var document = _editor.Document;
        var estimates = new TextSegmentCollection<TextSegment>(document);
        foreach (var offset in offsets.Where(o => o <= document.TextLength))
        {
            estimates.Add(new TextSegment { StartOffset = offset, Length = 0 });
        }

        _estimates = estimates;
        _editor.TextArea.TextView.Redraw();
        Updated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops the estimated rows (the service listed the lenses, or won't); returns whether there were any.</summary>
    private bool ClearEstimates()
    {
        _estimateCts?.Cancel();
        if (_estimates is null)
        {
            return false;
        }

        _estimates.Disconnect(_editor.Document);
        _estimates = null;
        return true;
    }

    /// <summary>
    /// The document's text was replaced by its file's (changed outside the editor): the lenses' segments collapsed with
    /// the old text, so the new text's rows are estimated again until the service lists its lenses.
    /// </summary>
    public void Reloaded()
    {
        if (_lensesListed)
        {
            _lenses.Disconnect(_editor.Document);
            _lenses = new TextSegmentCollection<LensSegment>(_editor.Document);
            _lensesListed = false;
            _lensVersion = -1;
        }

        EstimateLenses();
    }

    // ------------------------------------------------------------------ Git and test CodeLens (deferred from M12a)

    /// <summary>Our CodeLens entries at a one-based line: Git history and the test outcome (tests).</summary>
    internal IReadOnlyList<string> ExtraLenses(int line)
    {
        var parts = new List<string>();
        if (Options.CodeLens && Options.TestCodeLens && _testLenses.TryGetValue(line, out var test))
        {
            parts.Add(test);
        }

        if (Options.CodeLens && Options.GitCodeLens && _gitLenses.TryGetValue(line, out var git))
        {
            parts.Add(git);
        }

        return parts;
    }

    private void OnTestsChanged(object? sender, EventArgs e)
    {
        if (_testLensesQueued)
        {
            return;
        }

        _testLensesQueued = true;
        Ui.Post(() =>
        {
            _testLensesQueued = false;
            if (!_disposed)
            {
                UpdateTestLenses();
            }
        });
    }

    /// <summary>The test outcomes by the line their test (or class) starts on; the innermost wins a line.</summary>
    private void UpdateTestLenses()
    {
        var lenses = new Dictionary<int, string>();
        if (Options.CodeLens && Options.TestCodeLens)
        {
            foreach (var node in _services.Tests.InDocument(DocumentUris.FromPath(Model.Path)).OrderBy(n => n.Range!.EndLine - n.Range.StartLine))
            {
                if (LensDetails.Tests(node) is { } text)
                {
                    lenses.TryAdd(node.Range!.StartLine + 1, text);
                }
            }
        }

        if (!lenses.OrderBy(p => p.Key).SequenceEqual(_testLenses.OrderBy(p => p.Key)))
        {
            _testLenses = lenses;
            _editor.TextArea.TextView.Redraw();
            Updated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Blames the document again when it changed: at once when it's saved, else at most every few seconds (with its
    /// unsaved text). Files outside a repository have no Git lenses.
    /// </summary>
    private async Task RefreshBlameAsync()
    {
        var model = Model;
        if (!Options.CodeLens || !Options.GitCodeLens || _blameVersion == model.Version || _services.Git.RepositoryFor(model.Path) is null
            || model.IsModified && System.Diagnostics.Stopwatch.GetElapsedTime(_blameAt) < s_blameInterval)
        {
            return;
        }

        var token = Restart(ref _blameCts).Token;
        var version = model.Version;
        _blameAt = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var blame = await _services.Git.BlameInRepositoryAsync(model.Path, model.IsModified ? model.Text.Text : null, token).ConfigureAwait(true);
            if (token.IsCancellationRequested || _disposed || model.Version != version)
            {
                return;
            }

            _blame = blame;
            _blameVersion = version;
            UpdateGitLenses();
        }
        catch (Exception ex) when (ex is Codalus.Workbench.Git.GitException or OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _blame = [];
        }
    }

    /// <summary>
    /// The history of each member with a CodeLens: its lines are its document symbol's range. Redraws only when they
    /// changed, or when <paramref name="lensesChanged"/> (new CodeLens entries to show): the document symbols of every
    /// open arrive while the first semantic tokens are being computed, and a file outside a repository has none.
    /// </summary>
    private void UpdateGitLenses(bool lensesChanged = false)
    {
        var lenses = new Dictionary<int, string>();
        if (Options.CodeLens && Options.GitCodeLens && _blame.Count > 0)
        {
            var symbols = Flatten(Symbols).ToList();
            var now = DateTimeOffset.Now;
            foreach (var line in _lenses.Select(l => _editor.Document.GetLineByOffset(l.StartOffset).LineNumber).Distinct())
            {
                var symbol = symbols.Where(s => s.SelectionRange.Start.Line == line - 1).MinBy(s => s.Range.End.Line - s.Range.Start.Line);
                if (symbol is not null && LensDetails.Git(_blame, symbol.Range.Start.Line + 1, symbol.Range.End.Line + 1, now) is { } text)
                {
                    lenses[line] = text;
                }
            }
        }

        if (!lensesChanged && lenses.Count == _gitLenses.Count && lenses.All(p => _gitLenses.TryGetValue(p.Key, out var text) && text == p.Value))
        {
            return;
        }

        _gitLenses = lenses;
        _editor.TextArea.TextView.Redraw();
        Updated?.Invoke(this, EventArgs.Empty);

        static IEnumerable<DocumentSymbol> Flatten(IEnumerable<DocumentSymbol> level) =>
            level.SelectMany(s => Flatten(s.Children ?? []).Prepend(s));
    }

    // ------------------------------------------------------------------ review comments (M13b)

    private readonly TextSegmentCollection<ReviewSegment> _reviews;

    /// <summary>The review threads shown at the end of lines: one-based line and the threads there (tests).</summary>
    internal IReadOnlyList<(int Line, IReadOnlyList<Workbench.Hosting.EditorReviewThread> Threads)> ReviewLines =>
        [.. _reviews.Select(r => (_editor.Document.GetLineByOffset(r.StartOffset).LineNumber, r.Threads))];

    private void OnReviewThreadsChanged(object? sender, EventArgs e) => Ui.Post(LoadReviewThreads);

    /// <summary>Places the review threads of the current branch's pull request on this file's lines (they follow edits).</summary>
    private void LoadReviewThreads()
    {
        if (_disposed)
        {
            return;
        }

        _reviews.Clear();
        var document = _editor.Document;
        foreach (var group in _services.ReviewThreadsFor(_model().Path).Where(t => t.Line >= 1 && t.Line <= document.LineCount).GroupBy(t => t.Line))
        {
            var line = document.GetLineByNumber(group.Key);
            _reviews.Add(new ReviewSegment([.. group]) { StartOffset = line.Offset, Length = 0 });
        }

        _editor.TextArea.TextView.Redraw();
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private sealed class ReviewSegment(IReadOnlyList<Workbench.Hosting.EditorReviewThread> threads) : TextSegment
    {
        public IReadOnlyList<Workbench.Hosting.EditorReviewThread> Threads { get; } = threads;

        /// <summary>"bo: Rename this? (+2)": the first comment of the first thread, and how many more there are.</summary>
        public string Summary
        {
            get
            {
                var first = Threads[0].Comments.Count > 0 ? Threads[0].Comments[0] : null;
                var more = Threads.Sum(t => t.Comments.Count) - 1;
                var body = (first?.Body ?? string.Empty).ReplaceLineEndings(" ").Trim();
                if (body.Length > 60)
                {
                    body = body[..57] + "…";
                }

                return $"◆ {first?.Author}: {body}{(more > 0 ? $" (+{more.ToString(System.Globalization.CultureInfo.InvariantCulture)})" : string.Empty)}";
            }
        }
    }

    /// <summary>The inheritance margin (added to the editor's left margins by the controller).</summary>
    public InheritanceMargin Margin { get; }

    /// <summary>Inlay hints by document offset (tests).</summary>
    internal IReadOnlyList<(int Offset, string Label)> Hints => [.. _hints.Select(h => (h.StartOffset, h.Label))];

    /// <summary>The inline text of a one-based line: inline diagnostics, review comments and its CodeLens, above it or at its end (tests).</summary>
    internal string? EndOfLineText(int line)
    {
        var documentLine = _editor.Document.GetLineByNumber(line);
        var parts = new[] { _generator.EndOfLine(documentLine)?.Text, _generator.LensAbove(documentLine)?.Text }.OfType<string>().ToList();
        return parts.Count == 0 ? null : string.Join("    ", parts);
    }

    /// <summary>The CodeLens row shown above a one-based line, or null (none, or at the line's end when the line before is folded away) (tests).</summary>
    internal string? LensRowAbove(int line) => _generator.LensAbove(_editor.Document.GetLineByNumber(line))?.Text.Trim();

    /// <summary>Whether the first CodeLens of the row above a one-based line has its command yet, or null without one (tests).</summary>
    internal bool? LensResolvedAbove(int line) => _generator.LensAbove(_editor.Document.GetLineByNumber(line))?.Lens?.Resolved;

    /// <summary>
    /// A visual line's height without the CodeLens row that may end it (the next line's): what a line background covers.
    /// </summary>
    public static double CodeHeight(VisualLine line) =>
        line.TextLines.Count > 1 && line.Elements.Count > 0 && line.Elements[^1] is LensRowElement ? line.Height - line.TextLines[^1].Height : line.Height;

    /// <summary>Whether a document offset is inside a folded section (set by the controller, which owns the foldings).</summary>
    internal Func<int, bool>? IsFoldedAway { get; set; }

    /// <summary>Raised when hints, lenses or the margin changed (tests).</summary>
    public event EventHandler? Updated;

    /// <summary>
    /// The document's symbols (namespaces, types, members), nested, from the language service; empty without one.
    /// Feeds the navigation bar, sticky scroll and the Document Outline.
    /// </summary>
    public IReadOnlyList<DocumentSymbol> Symbols { get; private set; } = [];

    /// <summary>The document version <see cref="Symbols"/> describe.</summary>
    public int SymbolsVersion { get; private set; } = -1;

    /// <summary>Raised on the UI thread when <see cref="Symbols"/> changed.</summary>
    public event EventHandler? SymbolsChanged;

    /// <summary>The innermost symbols containing a position, outermost first (navigation bar, sticky scroll, outline).</summary>
    public IReadOnlyList<DocumentSymbol> SymbolPath(Position position)
    {
        var path = new List<DocumentSymbol>();
        var level = Symbols;
        while (level.FirstOrDefault(s => Contains(s.Range, position)) is { } symbol)
        {
            path.Add(symbol);
            level = symbol.Children ?? [];
        }

        return path;
    }

    private static bool Contains(LanguageServices.Protocol.Range range, Position position) =>
        (position.Line > range.Start.Line || (position.Line == range.Start.Line && position.Character >= range.Start.Character))
        && (position.Line < range.End.Line || (position.Line == range.End.Line && position.Character <= range.End.Character));

    private async Task RefreshSymbolsAsync()
    {
        if (_disposed || _client() is not { Capabilities.DocumentSymbolProvider: true } client)
        {
            return;
        }

        var token = Restart(ref _symbolCts).Token;
        var model = Model;
        var version = model.Version;
        try
        {
            var symbols = await client.DocumentSymbolsAsync(model.Uri, token).ConfigureAwait(true);
            if (token.IsCancellationRequested || _disposed || model.Version != version)
            {
                return;
            }

            Symbols = symbols;
            SymbolsVersion = version;
            SymbolsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Text.Json.JsonException or Codalus.Rpc.JsonRpcConnectionLostException or Codalus.Rpc.JsonRpcRemoteException or ObjectDisposedException or InvalidOperationException)
        {
            RetryAfterFailure(ex, _hintTimer);
        }
    }

    private CodeDocument Model => _model();

    private EditorOptions Options => _services.Options.For(Model.LanguageId);

    private IBrush Brush(string key) =>
        _editor.TryFindResource(key, _editor.ActualThemeVariant, out var resource) && resource is IBrush brush ? brush : Brushes.Gray;

    private void OnThemeChanged(object? sender, EventArgs e) => _editor.TextArea.TextView.Redraw();

    private void OnOptionsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        ApplyOptions();
        Refresh();
    }

    private void ApplyOptions()
    {
        UpdateTestLenses();
        _guides.Enabled = Options.IndentGuides;
        _brackets.Enabled = Options.BracketPairColorization;
        Margin.IsVisible = Options.InheritanceMargin;
        _editor.TextArea.TextView.Redraw();
    }

    /// <summary>Re-requests everything (the language service started or couldn't, the document reloaded, options changed).</summary>
    public void Refresh()
    {
        _hintRange = (-1, -1);
        _lensVersion = -1;
        if (!_lensesListed)
        {
            EstimateLenses();
        }

        Restart(_hintTimer);
        Restart(_lensTimer);
        Restart(_symbolTimer);
    }

    /// <summary>The diagnostics changed: the inline messages follow.</summary>
    public void UpdateDiagnostics(IReadOnlyList<Diagnostic> diagnostics)
    {
        var byLine = new Dictionary<int, string>();
        if (Options.InlineDiagnostics)
        {
            foreach (var diagnostic in diagnostics.Where(d => d.Severity <= DiagnosticSeverity.Warning).OrderBy(d => d.Severity))
            {
                byLine.TryAdd(diagnostic.Range.Start.Line + 1, diagnostic.Message.Split('\n')[0]);
            }
        }

        if (byLine.Count != 0 || _diagnosticsByLine.Count != 0)
        {
            _diagnosticsByLine = byLine;
            _editor.TextArea.TextView.Redraw();
        }

        // New diagnostics mean a finished analysis: hints asked for before the project loaded may exist now.
        if (_hints.Count == 0)
        {
            _hintRange = (-1, -1);
            Restart(_hintTimer);
        }
    }

    /// <summary>Moves the inheritance glyphs and bracket colors back in line with the language order (after a grammar installs its colorizer).</summary>
    public void KeepColorizerLast()
    {
        var transformers = _editor.TextArea.TextView.LineTransformers;
        if (transformers.Count > 0 && transformers[^1] != _brackets)
        {
            transformers.Remove(_brackets);
            transformers.Add(_brackets);
        }
    }

    private static void Restart(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    private void OnScrolled(object? sender, EventArgs e)
    {
        var (first, last) = VisibleLines();
        if (first < _hintRange.First || last > _hintRange.Last || Margin.NeedsRange(first, last))
        {
            Restart(_hintTimer);
        }

        if (_lenses.Any(l => !l.Resolved))
        {
            Restart(_lensTimer);
        }
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        _hintRange = (-1, -1);
        _emptyHints = 0;
        Margin.Invalidate();
        Restart(_hintTimer);
        Restart(_lensTimer);
        Restart(_symbolTimer);
    }

    private (int First, int Last) VisibleLines()
    {
        var view = _editor.TextArea.TextView;
        if (view.VisualLinesValid && view.VisualLines.Count > 0)
        {
            return (view.VisualLines[0].FirstDocumentLine.LineNumber, view.VisualLines[^1].LastDocumentLine.LineNumber);
        }

        // The visual lines are built again at the next layout (a redraw invalidates them: a lens refresh's own, right
        // before it resolves). The lines in view are still known from the scroll position and the height: the first 60
        // lines were the lines above the view, scrolled down, and nothing in view got its counts until the next scroll.
        var lines = _editor.Document.LineCount;
        if (view.Bounds.Height <= 0)
        {
            return (1, Math.Min(lines, 60));
        }

        var first = view.GetDocumentLineByVisualTop(view.VerticalOffset)?.LineNumber ?? 1;
        var last = view.GetDocumentLineByVisualTop(view.VerticalOffset + view.Bounds.Height)?.LineNumber ?? lines;
        return (first, last);
    }

    // ------------------------------------------------------------------ requests

    private async Task RefreshViewportAsync()
    {
        if (_disposed)
        {
            return;
        }

        var token = Restart(ref _hintCts).Token;
        var (visibleFirst, visibleLast) = VisibleLines();
        var first = Math.Max(1, visibleFirst - ViewportMarginLines);
        var last = Math.Min(_editor.Document.LineCount, visibleLast + ViewportMarginLines);
        var client = _client();
        var model = Model;
        var version = model.Version;
        var range = new LanguageServices.Protocol.Range(new Position(first - 1, 0), new Position(last, 0));
        try
        {
            InlayHint[] hints = [];
            if (client?.Capabilities?.InlayHintProvider == true && (Options.InlayParameterNames || Options.InlayTypes))
            {
                hints = await client.InlayHintsAsync(model.Uri, range, token).ConfigureAwait(true);
            }

            InheritanceMarginItem[]? margin = null;
            if (client is not null && Options.InheritanceMargin && model.LanguageId == ShellHost.CSharpLanguageId && Margin.NeedsRange(visibleFirst, visibleLast))
            {
                margin = await client.InheritanceMarginAsync(model.Uri, range, token).ConfigureAwait(true);
            }

            if (token.IsCancellationRequested || _disposed || model.Version != version)
            {
                return;
            }

            var collection = new TextSegmentCollection<HintSegment>(_editor.Document);
            foreach (var hint in hints.Where(h => h.Kind == InlayHintKind.Parameter ? Options.InlayParameterNames : h.Kind != InlayHintKind.Type || Options.InlayTypes))
            {
                var offset = model.OffsetAt(hint.Position);
                collection.Add(new HintSegment(hint.Label, hint.PaddingLeft == true, hint.PaddingRight == true) { StartOffset = offset, Length = 0 });
            }

            _hints.Disconnect(_editor.Document);
            _hints = collection;
            _hintRange = (first, last);
            if (hints.Length == 0 && client?.Capabilities?.InlayHintProvider == true && _emptyHints < 3)
            {
                // Asked before the language service had the document's semantics (it just opened, the project is
                // still loading): ask again a few times rather than wait for the next edit or diagnostics.
                _emptyHints++;
                DispatcherTimer.RunOnce(() =>
                {
                    if (!_disposed && _hints.Count == 0)
                    {
                        _hintRange = (-1, -1);
                        Restart(_hintTimer);
                    }
                }, TimeSpan.FromSeconds(_emptyHints));
            }
            if (margin is not null)
            {
                Margin.Set(margin, first, last);
            }

            _editor.TextArea.TextView.Redraw();
            Updated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Text.Json.JsonException or Codalus.Rpc.JsonRpcConnectionLostException or Codalus.Rpc.JsonRpcRemoteException or ObjectDisposedException or InvalidOperationException)
        {
            RetryAfterFailure(ex, _lensTimer);
        }
    }

    private async Task RefreshLensesAsync()
    {
        if (_disposed || _client() is not { Capabilities.CodeLensProvider: { } options } client || !Options.CodeLens)
        {
            if (!_disposed && !Options.CodeLens)
            {
                // Turned on again, the rows are kept from the text at once.
                _lensesListed = false;
                if (_lenses.Count > 0 | ClearEstimates())
                {
                    _lenses.Disconnect(_editor.Document);
                    _lenses = new TextSegmentCollection<LensSegment>(_editor.Document);
                    _editor.TextArea.TextView.Redraw();
                }
            }
            else if (!_disposed && _client() is { Capabilities: { CodeLensProvider: null } } && ClearEstimates())
            {
                _editor.TextArea.TextView.Redraw(); // a service without CodeLens
            }

            return;
        }

        var token = Restart(ref _lensCts).Token;
        var model = Model;
        var version = model.Version;
        try
        {
            if (_lensVersion != version)
            {
                var lenses = await client.CodeLensesAsync(model.Uri, token).ConfigureAwait(true);
                if (token.IsCancellationRequested || _disposed || model.Version != version)
                {
                    return;
                }

                // Keep the previous titles until the new counts arrive, so lenses don't flicker while typing: those still
                // waiting for their counts too (out of view, a refresh before), or the next refresh dropped their rows.
                var previous = _lenses.Where(l => l.Title is not null).GroupBy(l => (_editor.Document.GetLineByOffset(l.StartOffset).LineNumber, l.Lens.Data?.GetRawText()?.Split(',')[^1]))
                    .ToDictionary(g => g.Key, g => g.First().Title);
                var collection = new TextSegmentCollection<LensSegment>(_editor.Document);
                foreach (var lens in lenses)
                {
                    var offset = model.OffsetAt(lens.Range.Start);
                    var segment = new LensSegment(lens) { StartOffset = offset, Length = 0 };
                    if (lens.Command is { } command)
                    {
                        segment.Resolve(command);
                    }
                    else if (previous.TryGetValue((_editor.Document.GetLineByOffset(offset).LineNumber, lens.Data?.GetRawText()?.Split(',')[^1]), out var title))
                    {
                        segment.Title = title;
                    }

                    collection.Add(segment);
                }

                _lenses.Disconnect(_editor.Document);
                _lenses = collection;
                _lensVersion = version;
                _lensesListed = true;
                ClearEstimates();
                UpdateGitLenses(lensesChanged: true);
                _ = RefreshBlameAsync();
            }

            if (!options.ResolveProvider)
            {
                _editor.TextArea.TextView.Redraw();
                return;
            }

            var (first, last) = VisibleLines();
            var start = _editor.Document.GetLineByNumber(Math.Max(1, first - 5)).Offset;
            var end = _editor.Document.GetLineByNumber(Math.Min(_editor.Document.LineCount, last + 5)).EndOffset;
            foreach (var segment in _lenses.FindOverlappingSegments(start, end - start).Where(l => !l.Resolved).ToList())
            {
                var resolved = await client.ResolveCodeLensAsync(segment.Lens, token).ConfigureAwait(true);
                if (token.IsCancellationRequested || _disposed || model.Version != version)
                {
                    return;
                }

                if (resolved.Command is { } command)
                {
                    segment.Resolve(command);
                    _editor.TextArea.TextView.Redraw(_editor.Document.GetLineByOffset(segment.StartOffset));
                }
            }

            Updated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Text.Json.JsonException or Codalus.Rpc.JsonRpcConnectionLostException or Codalus.Rpc.JsonRpcRemoteException or ObjectDisposedException or InvalidOperationException)
        {
            RetryAfterFailure(ex, _symbolTimer);
        }
    }

    private int _failures;

    /// <summary>Hint requests in a row that came back empty (reset by an edit).</summary>
    private int _emptyHints;

    /// <summary>
    /// A request failed (the service restarting, or not ready yet): try again later, a few times, so the editor doesn't
    /// stay without hints until the next edit. Cancellations are not failures.
    /// </summary>
    private void RetryAfterFailure(Exception ex, DispatcherTimer timer)
    {
        if (ex is OperationCanceledException || _disposed || _failures >= 5)
        {
            return;
        }

        _failures++;
        _services.Log($"{Model.Name}: inline information request failed ({ex.GetType().Name}: {ex.Message}); retrying");
        _hintRange = (-1, -1);
        _lensVersion = -1;
        DispatcherTimer.RunOnce(() => Restart(timer), TimeSpan.FromSeconds(_failures));
    }

    private static CancellationTokenSource Restart(ref CancellationTokenSource? cts)
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = new CancellationTokenSource();
        return cts;
    }

    // ------------------------------------------------------------------ caret: hints are not caret stops

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Left or Key.Right) || _hints.Count == 0)
        {
            return;
        }

        // A hint sits at a document offset with two visual columns (before and after it): skip the extra caret stop.
        var caret = _editor.TextArea.Caret;
        if (_editor.TextArea.Selection.Length == 0 && _hints.FindSegmentsContaining(caret.Offset).Any(h => h.StartOffset == caret.Offset))
        {
            var target = e.Key == Key.Right ? caret.Offset + 1 : caret.Offset - 1;
            if (target >= 0 && target <= _editor.Document.TextLength && _editor.Document.GetLineByOffset(target) == _editor.Document.GetLineByOffset(caret.Offset))
            {
                caret.Offset = target;
                caret.BringCaretToView();
                e.Handled = true;
            }
        }
    }

    // ------------------------------------------------------------------ actions

    private void RunLens(LensSegment lens) => _ = RunLensAsync(lens);

    private async Task RunLensAsync(LensSegment lens)
    {
        // A refresh's lenses keep their titles but get their commands one request at a time, near the viewport: one
        // clicked before its turn is resolved now, or the click did nothing (Linux CI, run 40706281162).
        if (!lens.Resolved)
        {
            if (_client() is not { } client)
            {
                return;
            }

            var version = Model.Version;
            var token = Restart(ref _runLensCts).Token;
            try
            {
                var resolved = await client.ResolveCodeLensAsync(lens.Lens, token).ConfigureAwait(true);
                if (token.IsCancellationRequested || _disposed || Model.Version != version || resolved.Command is not { } resolvedCommand)
                {
                    return;
                }

                lens.Resolve(resolvedCommand);
                if (_lenses.Contains(lens))
                {
                    _editor.TextArea.TextView.Redraw(_editor.Document.GetLineByOffset(lens.StartOffset));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or System.Text.Json.JsonException or Codalus.Rpc.JsonRpcConnectionLostException or Codalus.Rpc.JsonRpcRemoteException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
        }

        if (lens.Command is not { Arguments: [_, var at, ..] } command || string.IsNullOrEmpty(command.CommandId))
        {
            return;
        }

        var position = System.Text.Json.JsonSerializer.Deserialize(at, ProtocolJsonContext.Default.Position);
        if (position is null)
        {
            return;
        }

        _editor.TextArea.Caret.Offset = Model.OffsetAt(position);
        _services.RunCommand(command.CommandId);
    }

    private void ShowTargets(InheritanceMarginItem item, Control anchor)
    {
        var menu = new ContextMenu();
        foreach (var group in item.Targets.GroupBy(t => t.Relation))
        {
            if (menu.Items.Count > 0)
            {
                menu.Items.Add(new Separator());
            }

            menu.Items.Add(Controls.MenuEntry.Caption(RelationTitle(group.Key)));
            foreach (var target in group)
            {
                var entry = Controls.MenuEntry.Create(target.DisplayName, "go-to");
                entry.IsEnabled = target.Location is not null;
                if (target.Location is { } location)
                {
                    entry.Click += (_, _) => _services.OpenLocations([location], target.DisplayName);
                }

                menu.Items.Add(entry);
            }
        }

        menu.Open(anchor);
    }

    internal static string RelationTitle(string relation) => relation switch
    {
        InheritanceRelations.Implements => "Implements",
        InheritanceRelations.ImplementedBy => "Implemented by",
        InheritanceRelations.Overrides => "Overrides",
        InheritanceRelations.OverriddenBy => "Overridden by",
        InheritanceRelations.Inherits => "Inherits from",
        InheritanceRelations.InheritedBy => "Derived types",
        _ => relation,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hintTimer.Stop();
        _lensTimer.Stop();
        _symbolTimer.Stop();
        foreach (var cts in new[] { _hintCts, _lensCts, _runLensCts, _symbolCts, _blameCts, _estimateCts })
        {
            cts?.Cancel();
            cts?.Dispose();
        }

        _estimates?.Disconnect(_editor.Document);

        var view = _editor.TextArea.TextView;
        view.ElementGenerators.Remove(_generator);
        view.BackgroundRenderers.Remove(_guides);
        view.LineTransformers.Remove(_brackets);
        view.ScrollOffsetChanged -= OnScrolled;
        _editor.Document.Changed -= OnDocumentChanged;
        _brackets.Detach();
        _hints.Disconnect(_editor.Document);
        _lenses.Disconnect(_editor.Document);
        _reviews.Disconnect(_editor.Document);
        _services.Options.PropertyChanged -= OnOptionsChanged;
        _services.ThemeChanged -= OnThemeChanged;
        _services.ReviewThreadsChanged -= OnReviewThreadsChanged;
        _services.Tests.Changed -= OnTestsChanged;
    }

    // ------------------------------------------------------------------ segments

    private sealed class HintSegment(string label, bool paddingLeft, bool paddingRight) : TextSegment
    {
        public string Label { get; } = label;

        public string Text => (paddingLeft ? " " : string.Empty) + Label + (paddingRight ? " " : string.Empty);
    }

    private sealed class LensSegment(CodeLens lens) : TextSegment
    {
        public CodeLens Lens { get; } = lens;

        public Command? Command { get; private set; }

        public string? Title { get; set; }

        public bool Resolved => Command is not null;

        public void Resolve(Command command)
        {
            Command = command;
            Title = command.Title;
        }
    }

    // ------------------------------------------------------------------ rendering

    private sealed record EndOfLine(string Text, LensSegment? Lens)
    {
        /// <summary>Review threads on the line (M13b): clicking the text shows them.</summary>
        public ReviewSegment? Review { get; init; }
    }

    /// <summary>A line's CodeLens titles (<paramref name="Text"/>), and the indentation that lines them up with its code.</summary>
    private sealed record LensRow(string Indent, string Text, LensSegment? Lens);

    /// <summary>
    /// Inserts the hints (zero-length elements) and, at the end of a line, its own end-of-line text (diagnostic, review
    /// comments) and the next line's CodeLens on a row of its own after a line break.
    /// </summary>
    private sealed class InlineGenerator(EditorInsights owner) : VisualLineElementGenerator
    {
        public EndOfLine? EndOfLine(DocumentLine line)
        {
            var parts = new List<string>();
            var review = owner._reviews.FindSegmentsContaining(line.Offset).FirstOrDefault(r => r.StartOffset >= line.Offset && r.StartOffset <= line.EndOffset)
                ?? owner._reviews.FindOverlappingSegments(line.Offset, line.Length).FirstOrDefault();
            if (review is not null)
            {
                parts.Add(review.Summary);
            }

            if (owner._diagnosticsByLine.TryGetValue(line.LineNumber, out var message))
            {
                parts.Add(message);
            }

            // A line's CodeLens goes above it; at its end only when there is no line before it to show it after (and
            // only with text: there it takes no room to keep).
            LensSegment? first = null;
            if (!CanShowAbove(line) && Lenses(line) is { Text.Length: > 0 } lens)
            {
                first = lens.Lens;
                parts.Add(lens.Text);
            }

            return parts.Count == 0 ? null : new EndOfLine("    " + string.Join("    ", parts), first) { Review = review };
        }

        /// <summary>The CodeLens row shown above <paramref name="line"/> (after the end of the line before), or null.</summary>
        public LensRow? LensAbove(DocumentLine line) => CanShowAbove(line) ? Lenses(line) : null;

        /// <summary>
        /// A line's CodeLens row: its titles, or none yet (an empty row, as tall as one with titles) while its lenses
        /// wait for their counts or the service hasn't listed them yet (estimated). The row's room never changes when
        /// the counts arrive, nor when one resolves to nothing ("-").
        /// </summary>
        private LensRow? Lenses(DocumentLine line)
        {
            if (!owner.Options.CodeLens)
            {
                return null;
            }

            var lenses = owner._lenses.FindOverlappingSegments(line.Offset, line.Length);
            var titles = lenses.Where(l => l.Title is not null && l.Title != "-").Select(l => l.Title!).Concat(owner.ExtraLenses(line.LineNumber)).ToList();
            if (titles.Count == 0 && lenses.Count == 0 && owner._estimates?.FindOverlappingSegments(line.Offset, line.Length).Count is not > 0)
            {
                return null;
            }

            // The code's indentation, tabs as the columns they take, so the row starts where the code does.
            var indent = new System.Text.StringBuilder();
            var tab = Math.Max(1, owner._editor.Options.IndentationSize);
            foreach (var c in owner._editor.Document.GetText(line.Offset, line.Length))
            {
                if (c == ' ')
                {
                    indent.Append(' ');
                }
                else if (c == '\t')
                {
                    indent.Append(' ', tab - (indent.Length % tab));
                }
                else
                {
                    break;
                }
            }

            var first = lenses.FirstOrDefault(l => l.Title is not null && l.Title != "-") ?? lenses.FirstOrDefault();
            return new LensRow(indent.ToString(), string.Join(" | ", titles), first);
        }

        /// <summary>A line's CodeLens shows above it when the line before is there to end with it (not folded away).</summary>
        private bool CanShowAbove(DocumentLine line) =>
            line.PreviousLine is { } previous && owner.IsFoldedAway?.Invoke(previous.EndOffset) != true;

        public override int GetFirstInterestedOffset(int startOffset)
        {
            var line = CurrentContext.VisualLine.LastDocumentLine;
            var hint = owner._hints.FindFirstSegmentWithStartAfter(startOffset);
            var hintOffset = hint is not null && hint.StartOffset <= line.EndOffset ? hint.StartOffset : -1;
            var eolOffset = startOffset <= line.EndOffset && (EndOfLine(line) is not null || line.NextLine is { } next && LensAbove(next) is not null) ? line.EndOffset : -1;
            return hintOffset < 0 ? eolOffset : eolOffset < 0 ? hintOffset : Math.Min(hintOffset, eolOffset);
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var line = CurrentContext.Document.GetLineByOffset(offset);
            var hint = owner._hints.FindFirstSegmentWithStartAfter(offset);
            var hintText = hint is not null && hint.StartOffset == offset ? hint.Text : null;
            var atEnd = offset == line.EndOffset;
            var eol = atEnd ? EndOfLine(line) : null;
            var below = atEnd && line.NextLine is { } next ? LensAbove(next) : null;
            if (hintText is null && eol is null && below is null)
            {
                return null;
            }

            if (hintText is not null && eol is null && below is null)
            {
                return new InlineTextElement(hintText, owner.Brush("Shell.Editor.InlayHint.Foreground"), owner.Brush("Shell.Editor.InlayHint.Background"), null);
            }

            var text = (hintText ?? string.Empty) + (eol?.Text ?? string.Empty);
            var color = eol?.Review is not null ? owner.Brush("Shell.Info")
                : owner._diagnosticsByLine.ContainsKey(line.LineNumber) ? owner.Brush("Shell.Warning")
                : eol?.Lens is not null ? owner.Brush("Shell.Editor.CodeLens.Foreground")
                : owner.Brush("Shell.Editor.InlayHint.Foreground");
            Action? click = eol?.Review is { } review ? () => owner._services.ShowReviewThreads(review.Threads, owner._editor.TextArea)
                : eol?.Lens is { } lens ? () => owner.RunLens(lens)
                : null;
            if (below is null)
            {
                return new InlineTextElement(text, color, null, click);
            }

            Action? lensClick = below.Lens is { } nextLens ? () => owner.RunLens(nextLens) : null;
            return new LensRowElement(owner, text, color, click, below, lensClick);
        }
    }

    /// <summary>
    /// The end of a line's text and the next line's CodeLens row: the line's own end-of-line text (if any), a line break,
    /// the next line's indentation and its CodeLens, smaller, muted and in the UI's proportional font (compact, as in the
    /// classic IDE). None of it is part of the document or takes the caret; the next line itself keeps its height (no
    /// taller caret or selection), and the row clicks run its first lens. A row without titles yet holds a space in the
    /// titles' font, so it is exactly as tall as the row they fill.
    /// </summary>
    private sealed class LensRowElement : VisualLineElement
    {
        private readonly EditorInsights _owner;
        private readonly string _text;
        private readonly IBrush _color;
        private readonly Action? _click;
        private readonly LensRow _row;
        private readonly string _titles;
        private readonly Action? _lensClick;

        public LensRowElement(EditorInsights owner, string text, IBrush color, Action? click, LensRow row, Action? lensClick)
            : base(text.Length + 1 + row.Indent.Length + Math.Max(1, row.Text.Length), 0)
        {
            _owner = owner;
            _text = text;
            _color = color;
            _click = click;
            _row = row;
            _titles = row.Text.Length > 0 ? row.Text : " ";
            _lensClick = lensClick;
        }

        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            var at = startVisualColumn - VisualColumn;
            if (at < _text.Length)
            {
                return Run(_text[at..], _color, small: true, ui: false);
            }

            if (at == _text.Length)
            {
                return new TextEndOfLine(1);
            }

            at -= _text.Length + 1;
            return at < _row.Indent.Length
                ? Run(_row.Indent[at..], _color, small: false, ui: false)
                : Run(_titles[(at - _row.Indent.Length)..], _owner.Brush("Shell.Editor.CodeLens.Foreground"), small: true, ui: true);
        }

        private TextCharacters Run(string text, IBrush foreground, bool small, bool ui)
        {
            var properties = new VisualLineElementTextRunProperties(TextRunProperties);
            properties.SetForegroundBrush(foreground);
            if (ui)
            {
                properties.SetTypeface(new Typeface(FontFamily.Default));
            }

            if (small)
            {
                properties.SetFontRenderingEmSize(TextRunProperties.FontRenderingEmSize * 0.9);
            }

            return new TextCharacters(text, properties);
        }

        public override int GetNextCaretPosition(int visualColumn, AvaloniaEdit.Document.LogicalDirection direction, CaretPositioningMode mode) => -1;

        public override bool IsWhitespace(int visualColumn) => false;

        /// <summary>The click that the pointer's row takes: the line's own text on the first, the CodeLens on the one below.</summary>
        private Action? ClickAt(PointerEventArgs e)
        {
            var view = _owner._editor.TextArea.TextView;
            var y = e.GetPosition(view).Y + view.ScrollOffset.Y;
            if (view.GetVisualLineFromVisualTop(y) is not { } visualLine)
            {
                return null;
            }

            var textLine = visualLine.GetTextLineByVisualYPosition(y);
            return visualLine.TextLines.IndexOf(textLine) > 0 ? _lensClick : _click;
        }

        protected override void OnQueryCursor(PointerEventArgs e)
        {
            if (ClickAt(e) is not null && e.Source is InputElement element)
            {
                element.Cursor = new Cursor(StandardCursorType.Hand);
                e.Handled = true;
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed && ClickAt(e) is { } click)
            {
                click();
                e.Handled = true;
            }
        }
    }

    /// <summary>Text that isn't part of the document: no caret stops, smaller and muted, optionally clickable.</summary>
    private sealed class InlineTextElement(string text, IBrush foreground, IBrush? background, Action? click) : FormattedTextElement(text, 0)
    {
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            TextRunProperties.SetForegroundBrush(foreground);
            if (background is not null)
            {
                TextRunProperties.SetBackgroundBrush(background);
            }

            TextRunProperties.SetFontRenderingEmSize(TextRunProperties.FontRenderingEmSize * 0.9);
            return base.CreateTextRun(startVisualColumn, context);
        }

        public override int GetNextCaretPosition(int visualColumn, AvaloniaEdit.Document.LogicalDirection direction, CaretPositioningMode mode) => -1;

        protected override void OnQueryCursor(PointerEventArgs e)
        {
            if (click is not null && e.Source is InputElement element)
            {
                element.Cursor = new Cursor(StandardCursorType.Hand);
                e.Handled = true;
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (click is not null && e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
            {
                click();
                e.Handled = true;
            }
        }
    }

    /// <summary>
    /// The inheritance margin: a glyph on lines declaring types and members with bases or derived symbols (↑ implements
    /// or overrides, ↓ implemented or overridden, both). Clicking lists the targets.
    /// </summary>
    public sealed class InheritanceMargin : AbstractMargin
    {
        private const double Size = 14;
        private readonly EditorInsights _owner;
        private Dictionary<int, InheritanceMarginItem> _byLine = [];
        private (int First, int Last) _range = (-1, -1);

        internal InheritanceMargin(EditorInsights owner)
        {
            _owner = owner;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        /// <summary>Items by one-based line (tests).</summary>
        internal IReadOnlyDictionary<int, InheritanceMarginItem> Items => _byLine;

        internal bool NeedsRange(int first, int last) => IsVisible && (first < _range.First || last > _range.Last);

        internal void Invalidate() => _range = (-1, -1);

        internal void Set(IReadOnlyList<InheritanceMarginItem> items, int first, int last)
        {
            _byLine = items.GroupBy(i => i.Line + 1).ToDictionary(g => g.Key, g => g.First());
            _range = (first, last);
            InvalidateMeasure();
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize) => new(IsVisible ? Size + 2 : 0, 0);

        protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
        {
            if (oldTextView is not null)
            {
                oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
            }

            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView is not null)
            {
                newTextView.VisualLinesChanged += OnVisualLinesChanged;
            }
        }

        private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
            if (TextView is not { VisualLinesValid: true } view || _byLine.Count == 0)
            {
                return;
            }

            var pen = new Pen(_owner.Brush("Shell.Info"), 1.4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            foreach (var line in view.VisualLines)
            {
                if (!_byLine.TryGetValue(line.FirstDocumentLine.LineNumber, out var item))
                {
                    continue;
                }

                var top = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineTop) - view.VerticalOffset;
                var height = line.TextLines[0].Height;
                var center = new Point(2 + (Size / 2), top + (height / 2));
                var up = item.Relations.Any(r => r is InheritanceRelations.Implements or InheritanceRelations.Overrides or InheritanceRelations.Inherits);
                var down = item.Relations.Any(r => r is InheritanceRelations.ImplementedBy or InheritanceRelations.OverriddenBy or InheritanceRelations.InheritedBy);
                var r = Math.Min(Size, height) / 2 - 2;
                if (up)
                {
                    // ▲ outline: has a base.
                    var dx = down ? -3 : 0;
                    context.DrawLine(pen, new Point(center.X + dx, center.Y - r), new Point(center.X + dx, center.Y + r));
                    context.DrawLine(pen, new Point(center.X + dx - 3, center.Y - r + 3), new Point(center.X + dx, center.Y - r));
                    context.DrawLine(pen, new Point(center.X + dx + 3, center.Y - r + 3), new Point(center.X + dx, center.Y - r));
                }

                if (down)
                {
                    var dx = up ? 3 : 0;
                    context.DrawLine(pen, new Point(center.X + dx, center.Y - r), new Point(center.X + dx, center.Y + r));
                    context.DrawLine(pen, new Point(center.X + dx - 3, center.Y + r - 3), new Point(center.X + dx, center.Y + r));
                    context.DrawLine(pen, new Point(center.X + dx + 3, center.Y + r - 3), new Point(center.X + dx, center.Y + r));
                }
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (TextView is not { } view || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }

            var position = e.GetPosition(view);
            if (view.GetVisualLineFromVisualTop(position.Y + view.VerticalOffset) is { } line
                && _byLine.TryGetValue(line.FirstDocumentLine.LineNumber, out var item))
            {
                e.Handled = true;
                _owner.ShowTargets(item, this);
            }
        }
    }
}

/// <summary>Vertical lines at each indentation level (blank lines take the indentation of the lines around them).</summary>
internal sealed class IndentGuideRenderer(TextEditor editor, Func<string, IBrush> brush) : IBackgroundRenderer
{
    public bool Enabled { get; set; } = true;

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!Enabled || !textView.VisualLinesValid || editor.WordWrap)
        {
            return;
        }

        var document = textView.Document;
        var indentSize = Math.Max(1, editor.Options.IndentationSize);
        var tabSize = Math.Max(1, editor.Options.IndentationSize);
        var width = textView.WideSpaceWidth;
        var pen = new Pen(brush("Shell.Editor.Guide"), 1);
        foreach (var line in textView.VisualLines)
        {
            var documentLine = line.FirstDocumentLine;
            var indent = Indentation(document, documentLine, tabSize);
            if (indent < 0)
            {
                // Blank: continue the guides of the surrounding block.
                var previous = documentLine.PreviousLine;
                var next = documentLine.NextLine;
                while (previous is not null && Indentation(document, previous, tabSize) < 0)
                {
                    previous = previous.PreviousLine;
                }

                while (next is not null && Indentation(document, next, tabSize) < 0)
                {
                    next = next.NextLine;
                }

                indent = Math.Min(previous is null ? 0 : Indentation(document, previous, tabSize), next is null ? 0 : Indentation(document, next, tabSize));
            }

            var top = line.VisualTop - textView.VerticalOffset;
            var bottom = top + line.Height;
            for (var column = indentSize; column < indent; column += indentSize)
            {
                var x = Math.Round(column * width - textView.HorizontalOffset) + 0.5;
                if (x > 0)
                {
                    drawingContext.DrawLine(pen, new Point(x, top), new Point(x, bottom));
                }
            }
        }
    }

    /// <summary>The visual width of the line's leading whitespace, or -1 for a blank line.</summary>
    private static int Indentation(TextDocument document, DocumentLine line, int tabSize)
    {
        var width = 0;
        for (var offset = line.Offset; offset < line.EndOffset; offset++)
        {
            switch (document.GetCharAt(offset))
            {
                case ' ':
                    width++;
                    break;
                case '\t':
                    width += tabSize - (width % tabSize);
                    break;
                default:
                    return width;
            }
        }

        return -1;
    }
}

/// <summary>
/// Colors (), [] and {} by nesting depth, skipping strings and comments (C-style lexing: <c>//</c>, <c>/* */</c>,
/// quotes, C# verbatim strings). The depth at each line start is cached and recomputed from the first changed line.
/// </summary>
internal sealed class BracketPairColorizer : DocumentColorizingTransformer
{
    private static readonly string[] s_brushes = ["Shell.Editor.Bracket1", "Shell.Editor.Bracket2", "Shell.Editor.Bracket3"];
    private static readonly HashSet<string> s_languages = new(StringComparer.Ordinal)
    {
        "csharp", "json", "jsonc", "javascript", "typescript", "javascriptreact", "typescriptreact", "css", "scss", "less",
        "java", "c", "cpp", "go", "rust", "fsharp", "powershell", "razor", "aspnetcorerazor", "kotlin", "swift", "dart", "php",
    };

    private readonly TextDocument _document;
    private readonly Func<string, IBrush> _brush;
    private readonly Func<string> _language;
    private readonly List<LineState> _states = [new(0, Lex.Code)];

    private enum Lex
    {
        Code,
        BlockComment,
        VerbatimString,
    }

    private readonly record struct LineState(int Depth, Lex Mode);

    public BracketPairColorizer(TextDocument document, Func<string, IBrush> brush, Func<string> language)
    {
        _document = document;
        _brush = brush;
        _language = language;
        document.Changing += OnChanging;
    }

    public bool Enabled { get; set; } = true;

    public void Detach() => _document.Changing -= OnChanging;

    private void OnChanging(object? sender, DocumentChangeEventArgs e)
    {
        var line = _document.GetLineByOffset(Math.Min(e.Offset, _document.TextLength)).LineNumber;
        if (_states.Count > line)
        {
            _states.RemoveRange(line, _states.Count - line);
        }
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (!Enabled || !s_languages.Contains(_language()) || line.Length > 2000)
        {
            return;
        }

        var state = StateAt(line.LineNumber);
        var brushes = s_brushes.Select(_brush).ToArray();
        Scan(line, state, (offset, depth) =>
        {
            var b = brushes[((depth % brushes.Length) + brushes.Length) % brushes.Length];
            ChangeLinePart(offset, offset + 1, element => element.TextRunProperties.SetForegroundBrush(b));
        });
    }

    /// <summary>The lexer state at the start of a one-based line (computed forward from the last cached line).</summary>
    private LineState StateAt(int lineNumber)
    {
        while (_states.Count < lineNumber)
        {
            var line = _document.GetLineByNumber(_states.Count);
            _states.Add(Scan(line, _states[^1], null));
        }

        return _states[lineNumber - 1];
    }

    private LineState Scan(DocumentLine line, LineState state, Action<int, int>? bracket)
    {
        var depth = state.Depth;
        var mode = state.Mode;
        var end = line.EndOffset;
        for (var i = line.Offset; i < end; i++)
        {
            var c = _document.GetCharAt(i);
            var next = i + 1 < end ? _document.GetCharAt(i + 1) : '\0';
            switch (mode)
            {
                case Lex.BlockComment:
                    if (c == '*' && next == '/')
                    {
                        mode = Lex.Code;
                        i++;
                    }

                    continue;
                case Lex.VerbatimString:
                    if (c == '"')
                    {
                        if (next == '"')
                        {
                            i++;
                        }
                        else
                        {
                            mode = Lex.Code;
                        }
                    }

                    continue;
            }

            switch (c)
            {
                case '/' when next == '/':
                    i = end;
                    break;
                case '/' when next == '*':
                    mode = Lex.BlockComment;
                    i++;
                    break;
                case '@' when next == '"':
                    mode = Lex.VerbatimString;
                    i++;
                    break;
                case '"' or '\'':
                    // A regular string or character literal ends on the line.
                    for (i++; i < end; i++)
                    {
                        var s = _document.GetCharAt(i);
                        if (s == '\\')
                        {
                            i++;
                        }
                        else if (s == c)
                        {
                            break;
                        }
                    }

                    break;
                case '(' or '[' or '{':
                    bracket?.Invoke(i, depth);
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth = Math.Max(0, depth - 1);
                    bracket?.Invoke(i, depth);
                    break;
            }
        }

        return new LineState(depth, mode);
    }
}
