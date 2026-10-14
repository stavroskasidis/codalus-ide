using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Search;
using Codalus.LanguageServices.Client;
using Codalus.LanguageServices.Protocol;
using Codalus.Rpc;
using Codalus.Shell.Controls;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Git;

namespace Codalus.Shell.Editor;

/// <summary>Caret position and selection, for the status bar.</summary>
public sealed record CaretInfo(int Line, int Column, int SelectionLength, int Carets);

/// <summary>
/// Everything one editor view does on top of AvaloniaEdit: options and theme, semantic highlighting of the visible
/// range, squiggles, folding, completion, signature help, Quick Info, reference and bracket highlights, multiple
/// carets, find/replace and navigation. Language features go through the document's language service client; all of
/// it is asynchronous and cancellable, and responses for an older document version are dropped. UI thread only.
/// </summary>
public sealed partial class CodeEditorController : IDisposable
{
    private const int TokenMarginLines = 60;
    private static readonly TimeSpan s_editDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan s_scrollDelay = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan s_caretDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan s_hoverDelay = TimeSpan.FromMilliseconds(450);

    private readonly CodeEditorDocument _dockable;
    private readonly IEditorServices _services;
    private readonly Popup _hoverPopup;
    private readonly SemanticColorizer _colorizer = new();
    private readonly DiagnosticMarkers _markers;
    private readonly HighlightRenderer _highlights;
    private readonly FoldingManager _folding;
    private readonly SearchPanel _search;
    private readonly MultiCaretController _multiCaret;
    private readonly DispatcherTimer _hoverTimer;
    private readonly BreakpointMargin _breakpointMargin;
    private readonly DebugLineRenderer _debugLines;
    private readonly PerfTipRenderer _perfTip;
    private readonly Popup? _exceptionPopup;
    private StopInfo? _exceptionShownFor;
    private readonly GitChangeMargin _gitMargin = new();
    private readonly TestMargin _testMargin;
    private readonly HttpRequestMargin _httpMargin;
    private readonly BenchmarkMargin _benchmarkMargin;
    private readonly CoverageMargin _coverageMargin;
    private readonly HotLineMargin _hotLineMargin;
    private readonly EditorInsights _insights;
    private readonly BookmarkMargin _bookmarkMargin;
    private readonly DispatcherTimer _gitTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private BlameMargin? _blameMargin;
    private string? _indexText;
    private bool _indexLoaded;
    private CancellationTokenSource? _gitCts;
    private CancellationTokenSource? _tokensCts;
    private CancellationTokenSource? _foldingCts;
    private CancellationTokenSource? _highlightCts;
    private CancellationTokenSource? _completionCts;
    private CancellationTokenSource? _signatureCts;
    private CancellationTokenSource? _hoverCts;
    private CompletionWindow? _completion;
    private OverloadInsightWindow? _signature;
    private Point _hoverPoint;
    private DocumentOptions? _documentOptions;
    private readonly bool _attached;
    private bool _disposed;

    public CodeEditorController(TextEditor editor, CodeEditorDocument dockable, IEditorServices services, Popup hoverPopup, bool attach = true, Popup? exceptionPopup = null)
    {
        _exceptionPopup = exceptionPopup;
        Editor = editor;
        _dockable = dockable;
        _services = services;
        _hoverPopup = hoverPopup;
        var document = Model.Text;
        _markers = new DiagnosticMarkers(document);
        _highlights = new HighlightRenderer(document);

        editor.Document = document;
        editor.IsReadOnly = Model.IsReadOnly;
        var view = editor.TextArea.TextView;
        view.LineTransformers.Add(_colorizer);
        view.LineTransformers.Add(_markers);
        view.BackgroundRenderers.Add(_highlights);
        view.BackgroundRenderers.Add(_markers);
        _breakpointMargin = new BreakpointMargin(() => Model.Path, services.Debugger.Breakpoints, CurrentDebugLine, relocate: attach)
        {
            RunToLine = line => services.Debugger.State == DebugState.Stopped && !_disposed ? services.RunToLineAsync(Model.Path, line) : null,
            ShowMenu = (line, anchor) => services.ShowBreakpointMenu(Model.Path, line, anchor),
        };
        editor.TextArea.LeftMargins.Insert(0, _breakpointMargin);
        _testMargin = new TestMargin(() => Model.Path, services.Tests, services.ShowTestMenu);
        editor.TextArea.LeftMargins.Insert(1, _testMargin);
        _httpMargin = new HttpRequestMargin(() => Model.LanguageId == "http", line => services.SendHttpRequestAsync(Model.Path, Model.Text.Text, line));
        editor.TextArea.LeftMargins.Insert(1, _httpMargin);
        _benchmarkMargin = new BenchmarkMargin(() => Model.Path, services.Benchmarks, services.RunBenchmarkAsync);
        editor.TextArea.LeftMargins.Insert(1, _benchmarkMargin);
        _bookmarkMargin = new BookmarkMargin(document, () => Model.Path, services.Bookmarks);
        editor.TextArea.LeftMargins.Insert(1, _bookmarkMargin);
        editor.TextArea.ContextRequested += OnContextRequested;
        _debugLines = new DebugLineRenderer(() => Model.Path, services.Debugger.Breakpoints, CurrentDebugLine);
        view.BackgroundRenderers.Insert(0, _debugLines);
        _perfTip = new PerfTipRenderer(CurrentDebugLine, () => services.Debugger.CurrentStop?.Elapsed, () => services.Options.PerfTips);
        view.BackgroundRenderers.Add(_perfTip);
        PinLayer = new PinnedDataTipLayer(editor, services, () => Model.Path, relocate: attach);
        services.Debugger.StateChanged += OnDebuggerChanged;
        services.Debugger.FrameChanged += OnDebuggerChanged;
        services.Debugger.Stopped += OnDebuggerStopped;
        editor.TextArea.LeftMargins.Add(_gitMargin);
        _coverageMargin = new CoverageMargin(() => Model.Path, services.Tests.Coverage);
        editor.TextArea.LeftMargins.Add(_coverageMargin);
        _hotLineMargin = new HotLineMargin(() => Model.Path, services.HotLines);
        editor.TextArea.LeftMargins.Insert(0, _hotLineMargin);
        _gitTimer.Tick += OnGitTimer;
        services.Git.IndexChanged += OnGitIndexChanged;
        document.TextChanged += OnGitTextChanged;
        _gitTimer.Start();
        _insights = new EditorInsights(editor, () => Model, () => Client, services);
        Suggestions = new InlineSuggestions(editor, () => Model, services, () => _completion is not null);
        editor.TextArea.LeftMargins.Insert(2, _insights.Margin);
        _folding = FoldingManager.Install(editor.TextArea);
        _search = SearchPanel.Install(editor);
        _multiCaret = new MultiCaretController(editor.TextArea);
        _hoverTimer = new DispatcherTimer { Interval = s_hoverDelay };
        _hoverTimer.Tick += OnHoverTimer;

        ApplyOptions();
        ApplyTheme();
        _services.Options.PropertyChanged += OnOptionsChanged;
        _services.ThemeChanged += OnThemeChanged;
        _services.Diagnostics.Changed += OnDiagnosticsChanged;
        Model.ContentChanged += OnContentChanged;
        Model.Reloaded += OnReloaded;
        Model.PropertyChanged += OnModelPropertyChanged;
        document.Changing += OnDocumentChanging;
        view.ScrollOffsetChanged += OnViewChanged;
        view.VisualLinesChanged += OnViewChanged;
        editor.TextArea.Caret.PositionChanged += OnCaretChanged;
        editor.TextArea.SelectionChanged += OnCaretChanged;
        editor.TextArea.TextEntering += OnTextEntering;
        editor.TextArea.TextEntered += OnTextEntered;
        // Tunnel: with extra carets, Backspace/Delete/Enter must be seen before AvaloniaEdit handles them.
        editor.TextArea.AddHandler(InputElement.KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        editor.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        view.PointerMoved += OnPointerMoved;
        view.PointerExited += OnPointerExited;
        view.PointerPressed += OnPointerPressed;
        _multiCaret.Changed += (_, _) => OnCaretChanged(this, EventArgs.Empty);

        UpdateDiagnostics();
        UpdateGrammar();
        RefreshAll();
        Ui.Post(ShowExceptionHelperIfStopped);
        _attached = attach;
        if (attach)
        {
            dockable.Attach(this);
        }
    }

    public TextEditor Editor { get; }

    /// <summary>AI inline suggestions (M28b).</summary>
    internal InlineSuggestions Suggestions { get; }

    /// <summary>The test margin (tests and the headless UI test).</summary>
    public TestMargin TestMargin => _testMargin;

    /// <summary>The send glyphs of a .http file (M15a).</summary>
    public HttpRequestMargin HttpMargin => _httpMargin;

    /// <summary>The benchmark glyphs (M18b).</summary>
    public BenchmarkMargin BenchmarkMargin => _benchmarkMargin;

    /// <summary>The coverage coloring (tests and the headless UI test).</summary>
    public CoverageMargin CoverageMargin => _coverageMargin;

    /// <summary>The profiler's hot lines (M18; tests).</summary>
    public HotLineMargin HotLineMargin => _hotLineMargin;

    /// <summary>The Git changes of the gutter (the overview ruler).</summary>
    internal IReadOnlyList<Workbench.Git.LineChange> GitChanges => _gitMargin.Changes;

    /// <summary>The bookmark glyphs (tests).</summary>
    public BookmarkMargin BookmarkMargin => _bookmarkMargin;

    /// <summary>Inlay hints, CodeLens, the inheritance margin, guides and bracket colors (M12a).</summary>
    public EditorInsights Insights => _insights;

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // Right-clicking outside the selection moves the caret there first, as in other editors.
        if (e.TryGetPosition(Editor.TextArea.TextView, out var point)
            && Editor.TextArea.TextView.GetPosition(point + Editor.TextArea.TextView.ScrollOffset) is { } position)
        {
            var offset = Model.Text.GetOffset(position.Location);
            if (Editor.SelectionLength == 0 || offset < Editor.SelectionStart || offset > Editor.SelectionStart + Editor.SelectionLength)
            {
                Editor.TextArea.Caret.Offset = offset;
            }
        }

        _services.ShowEditorContextMenu(this, Editor.TextArea);
        e.Handled = true;
    }

    public CodeDocument Model => _dockable.Model;

    public SemanticColorizer Colorizer => _colorizer;

    public DiagnosticMarkers Markers => _markers;

    public HighlightRenderer Highlights => _highlights;

    public FoldingManager Folding => _folding;

    public MultiCaretController MultiCaret => _multiCaret;

    public SearchPanel Search => _search;

    public bool IsCompletionOpen => _completion is not null;

    internal CompletionWindow? CompletionWindow => _completion;

    internal OverloadInsightWindow? SignatureWindow => _signature;

    public bool IsSignatureHelpOpen => _signature is not null;

    public CaretInfo Caret
    {
        get
        {
            var caret = Editor.TextArea.Caret;
            return new CaretInfo(caret.Line, caret.Column, Editor.TextArea.Selection.Length, 1 + _multiCaret.ExtraCarets.Count);
        }
    }

    /// <summary>Raised when the caret or selection moves.</summary>
    public event EventHandler? CaretChanged;

    /// <summary>Raised when semantic tokens were applied (first highlight, benchmarks).</summary>
    public event EventHandler? TokensApplied;

    private LanguageHostClient? Client => _services.Client(Model);

    private CancellationTokenSource Restart(ref CancellationTokenSource? cts)
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = new CancellationTokenSource();
        return cts;
    }

    // ------------------------------------------------------------------ options and theme

    private void OnOptionsChanged(object? sender, PropertyChangedEventArgs e) => ApplyOptions();

    private void ApplyOptions()
    {
        var options = _services.Options;
        var editorOptions = Editor.Options;
        editorOptions.IndentationSize = _documentOptions?.IndentSize ?? options.TabSize;
        editorOptions.ConvertTabsToSpaces = _documentOptions?.InsertSpaces ?? options.InsertSpaces;
        editorOptions.ShowSpaces = options.RenderWhitespace;
        editorOptions.ShowTabs = options.RenderWhitespace;
        editorOptions.HighlightCurrentLine = options.HighlightCurrentLine;
        editorOptions.ShowColumnRulers = options.Rulers.Count > 0;
        editorOptions.ColumnRulerPositions = [.. options.Rulers];
        editorOptions.EnableHyperlinks = false;
        editorOptions.EnableEmailHyperlinks = false;
        editorOptions.AllowScrollBelowDocument = true;
        Editor.WordWrap = options.WordWrap;
        Editor.ShowLineNumbers = options.LineNumbers;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    private AvaloniaEdit.TextMate.TextMate.Installation? _textMate;

    /// <summary>The TextMate grammar scope coloring this document, or null (tests).</summary>
    public string? GrammarScope { get; private set; }

    /// <summary>The TextMate model of a grammar-colored document (tests).</summary>
    internal AvaloniaEdit.TextMate.TextMate.Installation? TextMate => _textMate;

    /// <summary>The distinct text colors on a rendered line (smoke test, benchmarks).</summary>
    internal int LineColors(int line)
    {
        var view = Editor.TextArea.TextView;
        view.EnsureVisualLines();
        return view.GetVisualLine(line) is { } visual ? Colors(visual) : 0;
    }

    /// <summary>Whether the grammar has colored a visible line yet (benchmarks: the TextMate equivalent of <see cref="TokensApplied"/>).</summary>
    internal bool GrammarColored()
    {
        var view = Editor.TextArea.TextView;
        view.EnsureVisualLines();
        return GrammarScope is not null && view.VisualLinesValid && view.VisualLines.Any(l => Colors(l) >= 2);
    }

    private static int Colors(VisualLine line) =>
        line.Elements.Select(e => e.TextRunProperties.ForegroundBrush is Avalonia.Media.ISolidColorBrush brush ? brush.Color : default).Distinct().Count();

    /// <summary>Colors the document with its language's TextMate grammar (ADR 0016), if it has one.</summary>
    private void UpdateGrammar()
    {
        var scope = _services.TextMate.ScopeFor(Model.LanguageId);
        if (scope == GrammarScope)
        {
            return;
        }

        GrammarScope = scope;
        if (scope is null)
        {
            _textMate?.Dispose();
            _textMate = null;
            return;
        }

        try
        {
            if (_textMate is null)
            {
                _textMate = AvaloniaEdit.TextMate.TextMate.InstallTextMate(Editor, _services.TextMate, initCurrentDocument: true, ex => _services.Log("TextMate: " + ex.Message));
                _textMate.SetTheme(_services.TextMate.ThemeFor(_services.Theme.Id));
            }

            _textMate.SetGrammar(scope);
            _insights.KeepColorizerLast();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Log($"TextMate: {Model.Name}: {ex.Message}");
            _textMate?.Dispose();
            _textMate = null;
            GrammarScope = null;
        }
    }

    private void ApplyTheme()
    {
        var theme = _services.Theme;
        _colorizer.Theme = theme;
        _markers.Theme = theme;
        _highlights.Theme = theme;
        Editor.TextArea.SelectionBrush = theme.Selection;
        Editor.TextArea.TextView.CurrentLineBackground = theme.CurrentLine;
        Editor.TextArea.TextView.CurrentLineBorder = new Avalonia.Media.Pen(Avalonia.Media.Brushes.Transparent, 0);
        _textMate?.SetTheme(_services.TextMate.ThemeFor(theme.Id));
        Editor.TextArea.TextView.Redraw();
    }

    // ------------------------------------------------------------------ document changes

    private void OnDocumentChanging(object? sender, DocumentChangeEventArgs e) => _colorizer.OnDocumentChanging(e, Model.Text);

    private void OnContentChanged(object? sender, CodeDocumentChangedEventArgs e)
    {
        ScheduleTokens(s_editDelay);
        ScheduleFolding(TimeSpan.FromMilliseconds(500));
        _highlights.Set(HighlightRenderer.Kind.Read, []);
        _highlights.Set(HighlightRenderer.Kind.Write, []);
        _coverageMargin.MarkEdited();
        if (_dockable.IsPreview)
        {
            _dockable.IsPreview = false; // editing keeps a preview tab open
        }
    }

    private void OnReloaded(object? sender, EventArgs e) => RefreshAll();

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CodeDocument.LanguageId))
        {
            UpdateGrammar();
            RefreshAll();
            _httpMargin.Refresh();
        }
    }

    /// <summary>Requests everything again: after the language service (re)started, or the document was reloaded.</summary>
    public void RefreshAll()
    {
        // Highlighting the visible lines first; folding (a walk of the whole file) and options when it's done.
        _refreshPending = true;
        ScheduleTokens(TimeSpan.Zero);
        _insights.Refresh();
    }

    private bool _refreshPending;

    private void AfterTokens()
    {
        if (_refreshPending)
        {
            _refreshPending = false;
            ScheduleFolding(TimeSpan.Zero);
            _ = LoadDocumentOptionsAsync();
        }
    }

    private async Task LoadDocumentOptionsAsync()
    {
        if (Client is not { } client)
        {
            return;
        }

        try
        {
            _documentOptions = await client.DocumentOptionsAsync(Model.Uri, CancellationToken.None).ConfigureAwait(true);
            if (!_disposed)
            {
                ApplyOptions();
            }
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            Report(ex);
        }
    }

    /// <summary>Failed language requests go to the Language Services log (cancellations are expected and dropped).</summary>
    private void Report(Exception ex)
    {
        if (ex is not OperationCanceledException)
        {
            _services.Log($"{Model.Name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is OperationCanceledException or JsonRpcConnectionLostException or JsonRpcRemoteException or InvalidOperationException;

    // ------------------------------------------------------------------ semantic highlighting

    private void OnViewChanged(object? sender, EventArgs e)
    {
        LightbulbChanged?.Invoke(this, EventArgs.Empty);
        if (!_colorizer.Covers(VisibleLines()))
        {
            ScheduleTokens(s_scrollDelay);
        }
    }

    private void ScheduleTokens(TimeSpan delay) => _ = TokensAsync(delay, Restart(ref _tokensCts).Token);

    private async Task TokensAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(true);
            }

            if (Client is not { } client)
            {
                AfterTokens(); // no semantic tokens: folding and options still need their turn
                return;
            }

            if (client.Capabilities?.SemanticTokensProvider is not { } provider)
            {
                AfterTokens(); // a server without semantic tokens (the TextMate grammar colors the file)
                return;
            }

            var version = Model.Version;
            var (first, last) = VisibleLines();
            var start = Math.Max(0, first - TokenMarginLines);
            var end = Math.Min(Model.Text.LineCount, last + TokenMarginLines);
            if (!provider.Range)
            {
                (start, end) = (0, Model.Text.LineCount);
            }

            var range = new LanguageServices.Protocol.Range(new Position(start, 0), new Position(end, 0));
            var tokens = await client.SemanticTokensAsync(Model.Uri, provider.Range ? range : null, cancellationToken).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested || version != Model.Version || _disposed)
            {
                return; // stale: a newer edit is on its way
            }

            _colorizer.Apply(start, end, SemanticColorizer.Remap(tokens.Data, provider.Legend.TokenTypes));
            Editor.TextArea.TextView.Redraw();
            TokensApplied?.Invoke(this, EventArgs.Empty);
            AfterTokens();
        }
        catch (Exception ex) when (IsTransient(ex) && ex is not OperationCanceledException)
        {
            AfterTokens();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Zero-based, end-exclusive range of visible lines.</summary>
    public (int First, int Last) VisibleLines()
    {
        var view = Editor.TextArea.TextView;
        if (!view.VisualLinesValid || view.VisualLines.Count == 0)
        {
            return (0, Math.Min(Model.Text.LineCount, 100));
        }

        return (view.VisualLines[0].FirstDocumentLine.LineNumber - 1, view.VisualLines[^1].LastDocumentLine.LineNumber);
    }

    // ------------------------------------------------------------------ folding

    private void ScheduleFolding(TimeSpan delay) => _ = FoldingAsync(delay, Restart(ref _foldingCts).Token);

    private async Task FoldingAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(true);
            }

            if (Client is not { Capabilities.FoldingRangeProvider: true } client)
            {
                // No language server folding: XML elements, markers and indentation (ADR 0016).
                var version0 = Model.Version;
                var snapshot = Model.Text.CreateSnapshot();
                var configuration = Configuration;
                var languageId = Model.LanguageId;
                var local = await Task.Run(() => LanguageFeatures.Foldings(new AvaloniaEdit.Document.TextDocument(snapshot), languageId, configuration), cancellationToken).ConfigureAwait(true);
                if (!cancellationToken.IsCancellationRequested && version0 == Model.Version && !_disposed)
                {
                    _folding.UpdateFoldings(local, -1);
                }

                return;
            }

            var version = Model.Version;
            var ranges = await client.FoldingRangesAsync(Model.Uri, cancellationToken).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested || version != Model.Version || _disposed)
            {
                return;
            }

            ApplyFoldings(ranges);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            Report(ex);
        }
    }

    public void ApplyFoldings(IReadOnlyList<FoldingRange> ranges)
    {
        var document = Model.Text;
        var foldings = new List<NewFolding>();
        foreach (var range in ranges)
        {
            if (range.StartLine + 1 > document.LineCount || range.EndLine + 1 > document.LineCount || range.EndLine <= range.StartLine)
            {
                continue;
            }

            var startLine = document.GetLineByNumber(range.StartLine + 1);
            var endLine = document.GetLineByNumber(range.EndLine + 1);
            var start = Math.Min(startLine.EndOffset, startLine.Offset + (range.StartCharacter ?? startLine.Length));
            if (range.Kind is "comment" or "imports" or "region")
            {
                start = startLine.Offset + (startLine.Length - document.GetText(startLine.Offset, startLine.Length).TrimStart().Length); // fold the whole first line
            }

            foldings.Add(new NewFolding(start, endLine.EndOffset) { Name = range.CollapsedText ?? "…" });
        }

        foldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        _folding.UpdateFoldings(foldings, -1);
    }

    /// <summary>Toggles the innermost folding at the caret (Ctrl+M Ctrl+M).</summary>
    public void ToggleFoldAtCaret()
    {
        var offset = Editor.CaretOffset;
        var line = Model.Text.GetLineByOffset(offset);
        var folding = _folding.GetFoldingsContaining(offset).LastOrDefault()
                      ?? _folding.GetFoldingsAt(line.Offset).FirstOrDefault()
                      ?? _folding.AllFoldings.FirstOrDefault(f => f.StartOffset >= line.Offset && f.StartOffset <= line.EndOffset);
        if (folding is not null)
        {
            folding.IsFolded = !folding.IsFolded;
        }
    }

    /// <summary>Collapses (true) or expands (false) every folding; Ctrl+M Ctrl+O / Ctrl+M Ctrl+L.</summary>
    public void SetAllFolded(bool folded)
    {
        foreach (var folding in _folding.AllFoldings)
        {
            folding.IsFolded = folded;
        }
    }

    // ------------------------------------------------------------------ diagnostics

    private void OnDiagnosticsChanged(object? sender, string path)
    {
        if (SolutionPaths.Comparer.Equals(path, Model.Path))
        {
            Ui.Post(UpdateDiagnostics);
        }
    }

    private void UpdateDiagnostics()
    {
        if (_disposed)
        {
            return;
        }

        var diagnostics = _services.Diagnostics.For(Model.Path);
        _markers.Set(diagnostics, Model.OffsetAt);
        _insights.UpdateDiagnostics(diagnostics);
        Editor.TextArea.TextView.Redraw();
        LightbulbChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ caret: brackets, references, signature help

    private void OnCaretChanged(object? sender, EventArgs e)
    {
        CaretChanged?.Invoke(this, EventArgs.Empty);
        LightbulbChanged?.Invoke(this, EventArgs.Empty);
        var brackets = HighlightRenderer.MatchBracket(Model.Text, Editor.CaretOffset);
        _highlights.Set(HighlightRenderer.Kind.Bracket, brackets is { } b ? [(b.Open, b.Open + 1), (b.Close, b.Close + 1)] : []);
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        _ = DocumentHighlightsAsync(Restart(ref _highlightCts).Token);
        if (_signature is not null)
        {
            _ = SignatureHelpAsync(null, retrigger: true);
        }
    }

    private async Task DocumentHighlightsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(s_caretDelay, cancellationToken).ConfigureAwait(true);
            if (Client is not { } client || Editor.TextArea.Selection.Length > 0)
            {
                return;
            }

            var version = Model.Version;
            var highlights = await client.DocumentHighlightsAsync(Model.Uri, Model.PositionAt(Editor.CaretOffset), cancellationToken).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested || version != Model.Version || _disposed)
            {
                return;
            }

            (int, int) Span(DocumentHighlight h) => (Model.OffsetAt(h.Range.Start), Model.OffsetAt(h.Range.End));
            _highlights.Set(HighlightRenderer.Kind.Read, highlights.Where(h => h.Kind != DocumentHighlightKind.Write).Select(Span));
            _highlights.Set(HighlightRenderer.Kind.Write, highlights.Where(h => h.Kind == DocumentHighlightKind.Write).Select(Span));
            Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            Report(ex);
        }
    }

    // ------------------------------------------------------------------ typing: completion and signature help

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        // Typing over a selection or with extra carets is handled by the multi-caret controller first.
        if (_multiCaret.ExtraCarets.Count > 0 && e.Text is { Length: > 0 } text && !e.Handled)
        {
            _multiCaret.Type(text);
            e.Handled = true;
            return;
        }

        // Typing the closing character of an auto-closed pair moves over it.
        if (e.Text is { Length: 1 } typed && _services.Options.AutoClosingBrackets && Editor.SelectionLength == 0
            && Editor.CaretOffset < Model.Text.TextLength && Model.Text.GetCharAt(Editor.CaretOffset) == typed[0]
            && Configuration?.AutoClosingPairs.Any(p => p.Close == typed && (p.Open != p.Close || _autoClosed?.Offset == Editor.CaretOffset)) == true)
        {
            Editor.CaretOffset++;
            _autoClosed = null;
            e.Handled = true;
        }
    }

    /// <summary>The last auto-inserted closing quote (typing it moves over it), following edits.</summary>
    private AvaloniaEdit.Document.TextAnchor? _autoClosed;

    /// <summary>The document's language configuration (comments, brackets, folding markers).</summary>
    public Workbench.Languages.LanguageConfiguration? Configuration => _services.TextMate.ConfigurationFor(Model.LanguageId);

    /// <summary>After an opening character of an auto-closing pair, inserts its closing one (VS Code's "languageDefined").</summary>
    private void AutoClose(string text)
    {
        if (!_services.Options.AutoClosingBrackets || _multiCaret.ExtraCarets.Count > 0 || Configuration is not { } configuration
            || configuration.AutoClosingPairs.FirstOrDefault(p => p.Open.Length == 1 && p.Open == text) is not { } pair)
        {
            return;
        }

        var caret = Editor.CaretOffset;
        var document = Model.Text;
        var next = caret < document.TextLength ? document.GetCharAt(caret) : '\n';
        if (!(char.IsWhiteSpace(next) || ")]};,>".Contains(next, StringComparison.Ordinal)))
        {
            return; // only before whitespace, the end of the line or a closing character
        }

        if (pair.Open == pair.Close)
        {
            // Quotes: not after a word character (it's an apostrophe), not inside a string or comment.
            var previous = caret >= 2 ? document.GetCharAt(caret - 2) : ' ';
            var line = document.GetLineByOffset(caret);
            var before = document.GetText(line.Offset, caret - 1 - line.Offset);
            if (char.IsLetterOrDigit(previous) || before.Count(c => c == pair.Open[0]) % 2 == 1)
            {
                return;
            }
        }

        if (pair.NotIn.Contains("comment") && configuration.LineComment is { Length: > 0 } comment)
        {
            var line = document.GetLineByOffset(caret);
            if (document.GetText(line.Offset, caret - line.Offset).Contains(comment, StringComparison.Ordinal))
            {
                return;
            }
        }

        document.Insert(caret, pair.Close);
        Editor.CaretOffset = caret;
        _autoClosed = document.CreateAnchor(caret);
    }

    /// <summary>Comments or uncomments the selected lines (or the caret's line) with the language's comment syntax.</summary>
    public void Comment(CommentAction action)
    {
        if (Model.IsReadOnly)
        {
            return;
        }

        var document = Model.Text;
        var start = document.GetLineByOffset(Editor.SelectionStart).LineNumber;
        var endOffset = Editor.SelectionStart + Editor.SelectionLength;
        var end = document.GetLineByOffset(endOffset).LineNumber;
        if (Editor.SelectionLength > 0 && end > start && document.GetLineByNumber(end).Offset == endOffset)
        {
            end--; // a selection ending at the start of a line doesn't include it
        }

        if (!LanguageFeatures.CommentLines(document, start, end, Configuration, action))
        {
            _services.ShowMessage("This language has no comments.");
        }
    }

    private string WordBeforeCaret()
    {
        var caret = Editor.CaretOffset;
        var start = caret;
        while (start > 0 && (char.IsLetterOrDigit(Model.Text.GetCharAt(start - 1)) || Model.Text.GetCharAt(start - 1) is '_' or '-'))
        {
            start--;
        }

        return Model.Text.GetText(start, caret - start);
    }

    /// <summary>
    /// Word completion from the document, for languages without a language server's completion. The words are
    /// counted off the UI thread (<see cref="WordIndex"/>, refreshed at most every two seconds while typing) and
    /// completed with the words of the caret's line, so typing in large files stays within budget.
    /// </summary>
    public void CompleteWords() => _ = CompleteWordsAsync(Restart(ref _wordsCts).Token);

    private static readonly TimeSpan WordIndexRefresh = TimeSpan.FromSeconds(2);
    private CancellationTokenSource? _wordsCts;
    private volatile WordIndex? _wordIndex;
    private volatile Task<WordIndex>? _wordIndexTask;
    private long _wordIndexBuilt;

    private async Task CompleteWordsAsync(CancellationToken cancellationToken)
    {
        var caret = Editor.CaretOffset;
        var document = Model.Text;
        var start = caret;
        while (start > 0 && (char.IsLetterOrDigit(document.GetCharAt(start - 1)) || document.GetCharAt(start - 1) is '_' or '-'))
        {
            start--;
        }

        var prefix = document.GetText(start, caret - start);
        if (prefix.Length == 0)
        {
            return;
        }

        var version = Model.Version;
        var index = _wordIndex;
        if (index is null || (index.Version != version && _wordIndexTask is null && System.Diagnostics.Stopwatch.GetElapsedTime(_wordIndexBuilt) > WordIndexRefresh))
        {
            // Rebuilt in the background; until then the stale index plus the current line answer.
            var snapshot = document.CreateSnapshot();
            var build = _wordIndexTask = Task.Run(() => WordIndex.Build(snapshot, version, CancellationToken.None), CancellationToken.None);
            _ = build.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    _wordIndex = t.Result;
                }

                _wordIndexBuilt = System.Diagnostics.Stopwatch.GetTimestamp();
                _wordIndexTask = null;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            index ??= await build.ConfigureAwait(true);
        }

        var line = document.GetLineByOffset(caret);
        var words = index.Complete(prefix, document.GetText(line.Offset, line.Length).Remove(start - line.Offset, caret - start));
        if (words.Count == 0 || _completion is not null || _disposed || cancellationToken.IsCancellationRequested
            || Model.Version != version || Editor.CaretOffset != caret)
        {
            return;
        }

        var window = new CompletionWindow(Editor.TextArea) { StartOffset = start, EndOffset = caret, CloseAutomatically = true };
        foreach (var word in words)
        {
            window.CompletionList.CompletionData.Add(new CompletionData(
                new LanguageServices.Protocol.CompletionItem(word) { Kind = LanguageServices.Protocol.CompletionItemKind.Text },
                Task.FromResult,
                (data, segment) => document.Replace(segment.Offset, segment.Length, data.Item.Label)));
        }

        window.Closed += (_, _) =>
        {
            if (_completion == window)
            {
                _completion = null;
            }
        };
        _completion = window;
        window.Show();
        window.CompletionList.SelectItem(prefix);
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text is { Length: 1 } entered)
        {
            AutoClose(entered);
            if (entered == "/" && AtDocCommentStart())
            {
                _ = DocCommentAsync();
                return;
            }
        }

        if (e.Text is { Length: > 0 } wordStart && Client?.Capabilities?.CompletionProvider is null && _completion is null
            && (char.IsLetter(wordStart[^1]) || wordStart[^1] == '_') && Model.LanguageId is not (null or "plaintext"))
        {
            // No language server completion: offer the document's words once a word is three letters long.
            var caret = Editor.CaretOffset;
            if (caret >= 3 && char.IsLetter(Model.Text.GetCharAt(caret - 2)) && char.IsLetter(Model.Text.GetCharAt(caret - 3))
                && (caret == 3 || !char.IsLetterOrDigit(Model.Text.GetCharAt(caret - 4))))
            {
                CompleteWords();
            }
        }

        if (e.Text is not { Length: 1 } text || Client is not { Capabilities: { } capabilities })
        {
            return;
        }

        var c = text[0];
        if (capabilities.SignatureHelpProvider is { } signature)
        {
            if (signature.TriggerCharacters.Contains(text))
            {
                _ = SignatureHelpAsync(text, retrigger: _signature is not null);
            }
            else if (signature.RetriggerCharacters.Contains(text) && _signature is not null)
            {
                _ = SignatureHelpAsync(text, retrigger: true);
            }
        }

        if (_services.Options.FormatOnType && !Model.IsReadOnly && _multiCaret.ExtraCarets.Count == 0
            && capabilities.DocumentOnTypeFormattingProvider is { } onType
            && (onType.FirstTriggerCharacter == text || onType.MoreTriggerCharacter?.Contains(text) == true))
        {
            _ = FormatOnTypeAsync(text);
        }

        if (capabilities.CompletionProvider is { } completion)
        {
            var isTrigger = completion.TriggerCharacters.Contains(text) || (Model.LanguageId == ShellHost.RazorLanguageId && text is "<" or "\"" or "/" or "=")
                || (Model.LanguageId is ShellHost.AxamlLanguageId or ShellHost.MauiXamlLanguageId && text is "{" or "/" or "=" or "|");
            if (_completion is null && isTrigger)
            {
                ShowCompletion(text);
            }
            else if (_completion is null && (char.IsLetter(c) || c == '_'))
            {
                // A server that had nothing complete for this word isn't asked again while it grows (as VS Code).
                if (WordStart(Editor.CaretOffset) != _noCompletionWordStart || Model.Version != _noCompletionVersion + 1)
                {
                    ShowCompletion();
                }
                else
                {
                    _noCompletionVersion = Model.Version;
                    if (WordBeforeCaret().Length >= 3)
                    {
                        CompleteWords();
                    }
                }
            }
            else if (_completion is not null && c == '.')
            {
                _completion.Close();
                ShowCompletion(text);
            }
        }
    }

    private async Task FormatOnTypeAsync(string ch)
    {
        try
        {
            if (Client is not { } client)
            {
                return;
            }

            var version = Model.Version;
            var options = new FormattingOptions(Editor.Options.IndentationSize, Editor.Options.ConvertTabsToSpaces);
            var edits = await client.FormatOnTypeAsync(Model.Uri, Model.PositionAt(Editor.CaretOffset), ch, options, CancellationToken.None).ConfigureAwait(true);
            if (edits.Length > 0 && version == Model.Version && !_disposed && _completion is null)
            {
                Model.ApplyEdits(edits);
            }
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            Report(ex);
        }
    }

    /// <summary>The line (1-based) where the lightbulb shows: the caret's, when it has a diagnostic; else null.</summary>
    public int? LightbulbLine
    {
        get
        {
            var line = Model.Text.GetLineByOffset(Editor.CaretOffset);
            return _markers.Segments.Any(s => s.StartOffset <= line.EndOffset && s.EndOffset >= line.Offset) ? line.LineNumber : null;
        }
    }

    /// <summary>The lightbulb may have moved (caret, diagnostics, scrolling).</summary>
    public event EventHandler? LightbulbChanged;

    /// <summary>Opens completion at the caret (Ctrl+Space, or a typed trigger character).</summary>
    private int _noCompletionWordStart = -1;
    private int _noCompletionVersion;

    /// <summary>The start of the identifier that ends at <paramref name="offset"/>.</summary>
    private int WordStart(int offset)
    {
        var document = Model.Text;
        while (offset > 0 && (char.IsLetterOrDigit(document.GetCharAt(offset - 1)) || document.GetCharAt(offset - 1) == '_'))
        {
            offset--;
        }

        return offset;
    }

    public void ShowCompletion(string? triggerCharacter = null) => _ = CompletionAsync(triggerCharacter, Restart(ref _completionCts).Token);

    private async Task CompletionAsync(string? triggerCharacter, CancellationToken cancellationToken)
    {
        try
        {
            if (Client is not { } client || Model.IsReadOnly)
            {
                _services.Log($"{Model.Name}: completion not requested ({(Model.IsReadOnly ? "read-only" : "no language service client yet")})");
                return;
            }

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var requestedAt = Editor.CaretOffset;
            var context = triggerCharacter is null
                ? new CompletionContext(CompletionTriggerKind.Invoked, null)
                : new CompletionContext(CompletionTriggerKind.TriggerCharacter, triggerCharacter);
            var position = Model.PositionAt(requestedAt);
            var request = client.CompletionAsync(Model.Uri, position, context, cancellationToken);
            var (embedded, embeddedList) = await EmbeddedCompletionAsync(requestedAt, position, context, cancellationToken).ConfigureAwait(true);
            var list = await request.ConfigureAwait(true);
            var embeddedItems = new HashSet<LanguageServices.Protocol.CompletionItem>(ReferenceEqualityComparer.Instance);
            if (embeddedList is { Items.Length: > 0 })
            {
                // HTML in Razor (ADR 0027): the HTML server's elements and attributes replace the same names from the C# host.
                var labels = embeddedList.Items.Select(i => i.Label).ToHashSet(StringComparer.Ordinal);
                embeddedItems.UnionWith(embeddedList.Items);
                list = list with { IsIncomplete = list.IsIncomplete || embeddedList.IsIncomplete, Items = [.. list.Items.Where(i => !labels.Contains(i.Label)), .. embeddedList.Items] };
            }

            if (_services.ExtraCompletions(Model, requestedAt) is { Count: > 0 } extra)
            {
                var labels = extra.Select(i => i.Label).ToHashSet(StringComparer.Ordinal);
                list = list with { Items = [.. list.Items.Where(i => !labels.Contains(i.Label)), .. extra] };
            }
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) is { TotalSeconds: > 2 } slow)
            {
                _services.Log($"{Model.Name}: completion took {slow.TotalSeconds:0.0} s");
            }

            // Snippets whose prefix starts with the typed word count too: they show even when the server has nothing.
            var typed = WordBeforeCaret();
            var snippets = SnippetItems(triggerCharacter).Where(s => typed.Length == 0 || s.Label.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).ToList();
            if (list.Items.Length == 0 && snippets.Count == 0 && !list.IsIncomplete && triggerCharacter is null && Model.LanguageId != ShellHost.CSharpLanguageId)
            {
                _noCompletionWordStart = WordStart(requestedAt);
                _noCompletionVersion = Model.Version;
            }

            if (list.Items.Length == 0 && snippets.Count == 0 && !cancellationToken.IsCancellationRequested && !_disposed && _completion is null
                && Model.LanguageId != ShellHost.CSharpLanguageId && typed.Length >= 3)
            {
                // The server has nothing here (e.g. YAML without a schema): the document's words (ADR 0016).
                CompleteWords();
                return;
            }

            if (cancellationToken.IsCancellationRequested || _disposed || (list.Items.Length == 0 && snippets.Count == 0) || _completion is not null)
            {
                _services.Log($"{Model.Name}: completion not shown ({list.Items.Length} items, cancelled {cancellationToken.IsCancellationRequested}, open {_completion is not null})");
                return;
            }

            var start = list.ItemDefaults?.EditRange is { } range ? Model.OffsetAt(range.Start) : WordStart(requestedAt);
            var caret = Editor.CaretOffset;
            if (caret < start || caret - start > 200)
            {
                return; // the caret moved away meanwhile
            }

            var window = new CompletionWindow(Editor.TextArea)
            {
                StartOffset = start,
                EndOffset = caret,
                CloseWhenCaretAtBeginning = triggerCharacter is null || triggerCharacter == ".",
                CloseAutomatically = true,
            };
            Task<LanguageServices.Protocol.CompletionItem> Resolve(LanguageServices.Protocol.CompletionItem item) =>
                item.Data is null && item.Kind == CompletionItemKind.Snippet && item.InsertTextFormat == InsertTextFormat.Snippet
                    ? Task.FromResult(item) // our snippets: nothing to resolve
                    : embedded is not null && embeddedItems.Contains(item)
                        ? embedded.Client.ResolveCompletionAsync(item, CancellationToken.None)
                        : client.ResolveCompletionAsync(item, CancellationToken.None);
            foreach (var item in list.Items.OrderBy(i => i.SortText ?? i.Label, StringComparer.Ordinal).Concat(SnippetItems(triggerCharacter)))
            {
                window.CompletionList.CompletionData.Add(new CompletionData(item, Resolve, Commit));
            }

            window.CompletionList.SelectionChanged += (_, _) => (window.CompletionList.SelectedItem as CompletionData)?.Resolve();
            window.Closed += (_, _) =>
            {
                if (_completion == window)
                {
                    _completion = null;
                }
            };
            _completion = window;
            window.Show();
            void Select()
            {
                // Filters by the typed text and picks the best match (CompletionData.Priority breaks ties).
                if (caret > start && Editor.CaretOffset >= caret)
                {
                    window.CompletionList.SelectItem(Model.Text.GetText(start, Editor.CaretOffset - start));
                }

                if (window.CompletionList.SelectedItem is null && list.Items.FirstOrDefault(i => i.Preselect == true) is { } preselected)
                {
                    window.CompletionList.SelectedItem = window.CompletionList.CompletionData.OfType<CompletionData>().First(d => d.Item == preselected);
                }
            }

            Select();

            // Again once the list is laid out: a selection made before its template applied doesn't stick (M34a).
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_completion == window)
                {
                    Select();
                }
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            Report(ex);
        }
    }

    /// <summary>The embedded language's completion at the caret (HTML at a Razor file's markup); nothing elsewhere or when it fails.</summary>
    private async Task<(EmbeddedRequest? Request, LanguageServices.Protocol.CompletionList? List)> EmbeddedCompletionAsync(int offset, Position position, CompletionContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (await _services.EmbeddedAsync(Model, offset).ConfigureAwait(true) is not { } embedded)
            {
                return (null, null);
            }

            return (embedded, await embedded.Client.CompletionAsync(embedded.Uri, position, context, cancellationToken).ConfigureAwait(true));
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            _services.Log($"{Model.Name}: embedded completion failed ({ex.Message})");
            return (null, null);
        }
    }

    /// <summary>The language's snippets as completion items (not after a trigger character such as <c>.</c>).</summary>
    private IEnumerable<LanguageServices.Protocol.CompletionItem> SnippetItems(string? triggerCharacter)
    {
        if (triggerCharacter is not null && triggerCharacter != " ")
        {
            return [];
        }

        return _services.Snippets.For(Model.LanguageId).SelectMany(s => s.Prefixes.Select(prefix => new LanguageServices.Protocol.CompletionItem(prefix)
        {
            Kind = CompletionItemKind.Snippet,
            Detail = s.Name,
            InsertText = s.Body,
            InsertTextFormat = InsertTextFormat.Snippet,
            FilterText = prefix,
            SortText = prefix,
            Documentation = MarkupContent.Markdown((s.Description is { Length: > 0 } d ? d + "\n\n" : string.Empty) + "```\n" + Codalus.Workbench.Snippets.SnippetParser.ToPlainText(s.Body) + "\n```"),
        }));
    }

    /// <summary>Inserts a snippet at the caret, or around the selection (Surround With).</summary>
    public void InsertSnippet(string body, bool surround)
    {
        if (Model.IsReadOnly)
        {
            return;
        }

        var configuration = Configuration;
        var caret = Editor.CaretOffset;
        SnippetEngine.Insert(Editor.TextArea, body, caret, 0, SnippetVariables(configuration), keepSelection: surround);
        Editor.TextArea.Focus();
    }

    private Func<string, string?> SnippetVariables(Workbench.Languages.LanguageConfiguration? configuration) =>
        SnippetEngine.Variables(Model.Path, Editor.TextArea, configuration?.LineComment, configuration?.BlockComment is { } block ? (block.Start, block.End) : null);

    private void Commit(CompletionData data, ISegment segment)
    {
        var resolved = data.ResolvedWithin(TimeSpan.FromMilliseconds(300));
        if ((resolved ?? data.Item) is { InsertTextFormat: InsertTextFormat.Snippet } snippet)
        {
            // A snippet (ours, or a server's): its text goes through the snippet engine, with tab stops (M12b).
            var body = snippet.TextEdit?.NewText ?? snippet.InsertText ?? snippet.Label;
            var start = segment.Offset;
            var length = segment.Length;
            if (snippet.TextEdit is { } edit)
            {
                start = Model.OffsetAt(edit.Range.Start);
                length = Math.Max(0, Math.Max(segment.EndOffset, Model.OffsetAt(edit.Range.End)) - start);
            }

            SnippetEngine.Insert(Editor.TextArea, body, start, length, SnippetVariables(Configuration));
            return;
        }

        var document = Model.Text;
        using (document.RunUpdate())
        {
            // The item's edit replaces its range, which may start before the word being typed (CSS's "::after" over
            // "::af"), through what was typed since the request.
            var textEdit = resolved?.TextEdit ?? data.Item.TextEdit;
            var text = textEdit?.NewText ?? data.Item.InsertText ?? data.Item.Label;
            var replaceStart = segment.Offset;
            var replaceLength = segment.Length;
            if (textEdit is { } itemEdit && Model.OffsetAt(itemEdit.Range.Start) is var editStart && editStart <= segment.Offset)
            {
                replaceStart = editStart;
                replaceLength = Math.Max(segment.EndOffset, Model.OffsetAt(itemEdit.Range.End)) - editStart;
            }

            document.Replace(replaceStart, replaceLength, text);
            var caretAfter = replaceStart + text.Length;

            // Additional edits refer to the text when completion was requested; they sit before the completed span
            // (usings at the top), so applying them last-first from the end keeps their positions valid.
            foreach (var edit in (resolved?.AdditionalTextEdits ?? []).OrderByDescending(e => e.Range.Start.Line).ThenByDescending(e => e.Range.Start.Character))
            {
                var start = Model.OffsetAt(edit.Range.Start);
                var end = Model.OffsetAt(edit.Range.End);
                if (end <= segment.Offset)
                {
                    document.Replace(start, end - start, edit.NewText);
                    caretAfter += edit.NewText.Length - (end - start);
                }
            }

            Editor.CaretOffset = Math.Clamp(caretAfter, 0, document.TextLength);
        }
    }

    /// <summary>Opens (or updates) signature help at the caret (Ctrl+Shift+Space, or a typed trigger).</summary>
    public void ShowSignatureHelp() => _ = SignatureHelpAsync(null, retrigger: false);

    private async Task SignatureHelpAsync(string? triggerCharacter, bool retrigger)
    {
        var cancellationToken = Restart(ref _signatureCts).Token;
        try
        {
            if (Client is not { } client)
            {
                return;
            }

            var context = new SignatureHelpContext(triggerCharacter is null ? 1 : 2, triggerCharacter, retrigger);
            var help = await client.SignatureHelpAsync(Model.Uri, Model.PositionAt(Editor.CaretOffset), context, cancellationToken).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested || _disposed)
            {
                return;
            }

            if (help is null || help.Signatures.Length == 0)
            {
                _signature?.Close();
                return;
            }

            if (_signature is { } open)
            {
                var keep = open.Provider?.SelectedIndex ?? 0;
                open.Provider = new SignatureProvider(help) { SelectedIndex = help.Signatures.Length > keep && retrigger && triggerCharacter is null ? keep : help.ActiveSignature };
                return;
            }

            var window = new OverloadInsightWindow(Editor.TextArea) { Provider = new SignatureProvider(help) };
            window.Closed += (_, _) =>
            {
                if (_signature == window)
                {
                    _signature = null;
                }
            };
            _signature = window;
            window.Show();
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            Report(ex);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        TrackCopy(e);
        if (e.Key == Key.Escape)
        {
            if (_hoverPopup.IsOpen)
            {
                _hoverPopup.IsOpen = false;
                e.Handled = true;
            }
            else if (_multiCaret.ExtraCarets.Count > 0)
            {
                _multiCaret.Clear();
                e.Handled = true;
            }
        }
        else if (_multiCaret.ExtraCarets.Count > 0 && _multiCaret.HandleKey(e))
        {
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ Quick Info

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(Editor.TextArea.TextView);
        if (_hoverPopup.IsOpen && Math.Abs(point.X - _hoverPoint.X) + Math.Abs(point.Y - _hoverPoint.Y) < 8)
        {
            return;
        }

        _hoverPoint = point;
        _hoverPopup.IsOpen = false;
        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    private void OnPointerExited(object? sender, PointerEventArgs e) => _hoverTimer.Stop();

    private void OnHoverTimer(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();
        var view = Editor.TextArea.TextView;
        if (view.GetPosition(_hoverPoint + view.ScrollOffset) is not { } position)
        {
            return;
        }

        var offset = Model.Text.GetOffset(position.Location);
        _ = ShowQuickInfoAsync(offset, _hoverPoint);
    }

    /// <summary>Quick Info at the caret (Ctrl+K Ctrl+I).</summary>
    public void ShowQuickInfoAtCaret()
    {
        var view = Editor.TextArea.TextView;
        var location = view.GetVisualPosition(Editor.TextArea.Caret.Position, VisualYPosition.LineBottom) - view.ScrollOffset;
        _ = ShowQuickInfoAsync(Editor.CaretOffset, location);
    }

    private async Task ShowQuickInfoAsync(int offset, Point at)
    {
        var cancellationToken = Restart(ref _hoverCts).Token;
        try
        {
            var parts = new List<string>();
            foreach (var diagnostic in _markers.At(offset))
            {
                parts.Add($"**{diagnostic.Code}**: {Escape(diagnostic.Message)}");
            }

            var dataTip = await DataTipAsync(offset, cancellationToken).ConfigureAwait(true);
            if (dataTip is null && Client is { } client)
            {
                var hover = await client.HoverAsync(Model.Uri, Model.PositionAt(offset), cancellationToken).ConfigureAwait(true);
                if (hover?.Contents.Value is not { Length: > 0 } && Model.LanguageId == ShellHost.RazorLanguageId
                    && await _services.EmbeddedAsync(Model, Math.Min(offset + 1, Model.Text.TextLength)).ConfigureAwait(true) is { } embedded)
                {
                    hover = await embedded.Client.HoverAsync(embedded.Uri, Model.PositionAt(offset), cancellationToken).ConfigureAwait(true);
                }

                if (hover?.Contents.Value is { Length: > 0 } markdown)
                {
                    parts.Insert(0, markdown);
                }
            }

            if (cancellationToken.IsCancellationRequested || _disposed || (parts.Count == 0 && dataTip is null))
            {
                return;
            }

            Control content = parts.Count > 0 ? MarkdownRenderer.Render(string.Join("\n\n---\n\n", parts), _services.OpenLink) : new Panel();
            if (dataTip is not null)
            {
                // The data tip is a tree that expands and edits values (ADR 0021); diagnostics stay above it.
                var tip = new Views.DataTipView { DataContext = dataTip };
                LastDataTip = dataTip;
                content = parts.Count > 0 ? new StackPanel { Spacing = 6, Children = { content, tip } } : tip;
            }

            _hoverPopup.Child = new Border
            {
                Classes = { "editorHover" },
                MaxWidth = 640,
                Child = content,
            };
            _hoverPopup.PlacementTarget = Editor.TextArea.TextView;
            _hoverPopup.Placement = PlacementMode.AnchorAndGravity;
            _hoverPopup.PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft;
            _hoverPopup.PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight;
            _hoverPopup.PlacementRect = new Rect(at.X, at.Y + 4, 1, 1);
            _hoverPopup.IsOpen = true;
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            Report(ex);
        }
    }

    // ------------------------------------------------------------------ git

    public GitChangeMargin GitMargin => _gitMargin;

    public BlameMargin? Blame => _blameMargin;

    private void OnGitIndexChanged(object? sender, EventArgs e) => Ui.Post(() =>
    {
        _indexLoaded = false;
        if (!_disposed)
        {
            _gitTimer.Stop();
            _gitTimer.Start();
        }
    });

    private void OnGitTextChanged(object? sender, EventArgs e)
    {
        _gitTimer.Stop();
        _gitTimer.Start();
    }

    private void OnGitTimer(object? sender, EventArgs e)
    {
        _gitTimer.Stop();
        _ = UpdateGitMarkersAsync();
    }

    /// <summary>Compares the editor's text with the file's index version and updates the change markers.</summary>
    public async Task UpdateGitMarkersAsync()
    {
        var git = _services.Git;

        // The file's own repository: a solution can span several (M13b); only the open one has a status.
        if (_disposed || Model.IsReadOnly || git.RepositoryFor(Model.Path) is not { } root)
        {
            _gitMargin.Changes = [];
            return;
        }

        var cancellationToken = Restart(ref _gitCts).Token;
        try
        {
            if (!_indexLoaded)
            {
                _indexText = await git.ShowInRepositoryAsync(Model.Path, ":", cancellationToken).ConfigureAwait(true);
                _indexLoaded = true;
            }

            var status = root == git.Root ? git.Status?.Files.FirstOrDefault(f => Workbench.Projects.SolutionService.PathComparer.Equals(f.Path, Model.Path)) : null;
            var current = Model.Text.Text;
            var baseline = _indexText ?? (status is { IsUntracked: true } ? string.Empty : null);
            if (baseline is null)
            {
                _gitMargin.Changes = []; // ignored or outside the repository
                return;
            }

            var changes = await Task.Run(() => LineDiff.Changes(LineDiff.Lines(baseline), LineDiff.Lines(current)), cancellationToken).ConfigureAwait(true);
            if (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                _gitMargin.Changes = changes;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (GitException ex)
        {
            Report(ex);
        }
    }

    /// <summary>Shows or hides blame annotations (blaming the editor's current text).</summary>
    public async Task ToggleBlameAsync()
    {
        if (_blameMargin is not null)
        {
            Editor.TextArea.LeftMargins.Remove(_blameMargin);
            _blameMargin = null;
            return;
        }

        if (_services.Git.RepositoryFor(Model.Path) is null)
        {
            _services.ShowMessage("This file is not in a git repository.");
            return;
        }

        try
        {
            var ranges = await _services.Git.BlameInRepositoryAsync(Model.Path, Model.Text.Text).ConfigureAwait(true);
            _blameMargin = new BlameMargin { Ranges = ranges };
            Editor.TextArea.LeftMargins.Insert(0, _blameMargin);
        }
        catch (GitException ex)
        {
            _services.ShowMessage("Blame failed: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ debugging

    /// <summary>The current statement's line in this document while the debugger is stopped here.</summary>
    public int? CurrentDebugLine()
    {
        var debugger = _services.Debugger;
        return debugger.State == DebugState.Stopped && debugger.SelectedFrame is { Source.Path: { } path } frame
               && Workbench.Projects.SolutionService.PathComparer.Equals(path, Model.Path)
            ? frame.Line
            : null;
    }

    public BreakpointMargin BreakpointMargin => _breakpointMargin;

    /// <summary>The pinned data tips over this editor (M14b), an overlay the view stacks on it.</summary>
    public PinnedDataTipLayer PinLayer { get; }

    private void OnDebuggerStopped(object? sender, StopInfo e)
    {
        OnDebuggerChanged(sender, EventArgs.Empty);
        Ui.Post(ShowExceptionHelperIfStopped);
    }

    private void OnDebuggerChanged(object? sender, EventArgs e) => Ui.Post(() =>
    {
        if (_disposed)
        {
            return;
        }

        // Closed once the program moves on, even when it stopped again before this ran (Run to Cursor, a step).
        if (_exceptionPopup is { IsOpen: true } popup
            && (_services.Debugger.State != DebugState.Stopped || !ReferenceEquals(_services.Debugger.CurrentStop, _exceptionShownFor)))
        {
            popup.IsOpen = false;
        }

        _breakpointMargin.InvalidateVisual();
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    });

    /// <summary>The exception helper, when the program stopped on an exception at a line of this document.</summary>
    public bool IsExceptionHelperOpen => _exceptionPopup?.IsOpen == true;

    private void ShowExceptionHelperIfStopped()
    {
        var debugger = _services.Debugger;
        if (_disposed || _exceptionPopup is not { } popup || debugger.CurrentStop is not { Reason: "exception" } stop || ReferenceEquals(stop, _exceptionShownFor)
            || CurrentDebugLine() is not { } line || line > Model.Text.LineCount)
        {
            return;
        }

        _exceptionShownFor = stop;
        var view = Editor.TextArea.TextView;
        view.EnsureVisualLines();
        var position = view.GetVisualPosition(new TextViewPosition(line, Model.Text.GetLineByNumber(line).Length + 1), VisualYPosition.LineBottom) - view.ScrollOffset;
        popup.Child = ExceptionHelper.Build(stop, debugger.ExceptionSettings, _services, () => popup.IsOpen = false, Editor);
        popup.PlacementTarget = view;
        popup.Placement = PlacementMode.AnchorAndGravity;
        popup.PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft;
        popup.PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight;
        popup.PlacementRect = new Rect(Math.Max(0, position.X - 40), Math.Max(0, position.Y + 2), 1, 1);
        popup.IsOpen = true;
    }

    /// <summary>The expression a data tip evaluates: the identifier at <paramref name="offset"/> with the member accesses before it.</summary>
    public string? ExpressionAt(int offset)
    {
        var text = Model.Text;
        static bool IsIdentifier(char c) => char.IsLetterOrDigit(c) || c == '_';
        if (offset >= text.TextLength || !IsIdentifier(text.GetCharAt(offset)))
        {
            if (offset == 0 || !IsIdentifier(text.GetCharAt(offset - 1)))
            {
                return null;
            }

            offset--;
        }

        var end = offset;
        while (end < text.TextLength && IsIdentifier(text.GetCharAt(end)))
        {
            end++;
        }

        var start = offset;
        while (true)
        {
            while (start > 0 && IsIdentifier(text.GetCharAt(start - 1)))
            {
                start--;
            }

            if (start > 1 && text.GetCharAt(start - 1) == '.' && IsIdentifier(text.GetCharAt(start - 2)))
            {
                start--;
                continue;
            }

            break;
        }

        var expression = text.GetText(start, end - start);
        return expression.Length == 0 || char.IsDigit(expression[0]) ? null : expression;
    }

    /// <summary>The last data tip shown (tests).</summary>
    public ViewModels.DataTipViewModel? LastDataTip { get; private set; }

    /// <summary>Shows the data tip (or Quick Info) for the expression at <paramref name="offset"/>.</summary>
    public Task ShowDataTipAsync(int offset)
    {
        var view = Editor.TextArea.TextView;
        var location = view.GetVisualPosition(new TextViewPosition(Model.Text.GetLocation(offset)), VisualYPosition.LineBottom) - view.ScrollOffset;
        return ShowQuickInfoAsync(offset, location);
    }

    /// <summary>A data tip: the expression at <paramref name="offset"/> evaluated as an expandable, editable tree.</summary>
    private async Task<ViewModels.DataTipViewModel?> DataTipAsync(int offset, CancellationToken cancellationToken)
    {
        var debugger = _services.Debugger;
        if (debugger.State != DebugState.Stopped || ExpressionAt(offset) is not { } expression)
        {
            return null;
        }

        try
        {
            var result = await debugger.EvaluateAsync(expression, "hover", cancellationToken).ConfigureAwait(true);
            var line = Model.Text.GetLineByOffset(offset).LineNumber;
            return new ViewModels.DataTipViewModel(debugger, expression, result)
            {
                PinToSource = Model.IsReadOnly ? null : () =>
                {
                    debugger.PinnedTips.Pin(Model.Path, line, expression, result.Result);
                    _hoverPopup.IsOpen = false;
                },
            };
        }
        catch (Exception ex) when (ex is Codalus.Debugging.DapException or Codalus.Debugging.DapConnectionLostException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Escape(string text) => text.Replace("*", "\\*", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal);

    // ------------------------------------------------------------------ navigation

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _hoverPopup.IsOpen = false;
        var properties = e.GetCurrentPoint(Editor.TextArea.TextView).Properties;
        var modifiers = e.KeyModifiers;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        var view = Editor.TextArea.TextView;
        if (view.GetPosition(e.GetPosition(view) + view.ScrollOffset) is not { } position)
        {
            return;
        }

        var offset = Model.Text.GetOffset(position.Location);
        var primaryModifier = modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta);
        if (primaryModifier && modifiers.HasFlag(KeyModifiers.Alt))
        {
            // Ctrl+Alt+Click: add a caret (Alt+drag is AvaloniaEdit's box selection).
            _multiCaret.AddCaret(offset);
            e.Handled = true;
        }
        else if (primaryModifier)
        {
            // Ctrl+Click: Go to Definition.
            Editor.CaretOffset = offset;
            _ = GoToDefinitionAsync();
            e.Handled = true;
        }
        else if (_multiCaret.ExtraCarets.Count > 0)
        {
            _multiCaret.Clear();
        }
    }

    public Task GoToDefinitionAsync() => NavigateAsync(implementation: false);

    public Task GoToImplementationAsync() => NavigateAsync(implementation: true);

    private async Task NavigateAsync(bool implementation)
    {
        if (Client is not { } client)
        {
            _services.ShowMessage("No language service for this file.");
            return;
        }

        try
        {
            var position = Model.PositionAt(Editor.CaretOffset);
            var locations = implementation
                ? await client.ImplementationAsync(Model.Uri, position, CancellationToken.None).ConfigureAwait(true)
                : await client.DefinitionAsync(Model.Uri, position, CancellationToken.None).ConfigureAwait(true);
            if (locations.Length == 0)
            {
                _services.ShowMessage(implementation ? "No implementations found." : "Cannot navigate to the symbol under the caret.");
                return;
            }

            _services.OpenLocations(locations, implementation ? "Go to Implementation" : "Go to Definition");
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            Report(ex);
        }
    }

    /// <summary>Moves the caret to an LSP position and scrolls it into view, centered.</summary>
    public void Navigate(Position position)
    {
        var offset = Model.OffsetAt(position);
        Editor.TextArea.ClearSelection();
        Editor.CaretOffset = offset;
        var location = Model.Text.GetLocation(offset);
        Editor.ScrollTo(location.Line, location.Column);
        Dispatcher.UIThread.Post(() => Editor.TextArea.TextView.MakeVisible(Editor.TextArea.Caret.CalculateCaretRectangle()), DispatcherPriority.Background);
        Editor.TextArea.Focus();
    }

    // ------------------------------------------------------------------ teardown

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _textMate?.Dispose();
        _insights.Dispose();
        _gitTimer.Stop();
        _gitCts?.Cancel();
        _gitCts?.Dispose();
        _services.Git.IndexChanged -= OnGitIndexChanged;
        Model.Text.TextChanged -= OnGitTextChanged;
        Editor.TextArea.LeftMargins.Remove(_gitMargin);
        if (_blameMargin is not null)
        {
            Editor.TextArea.LeftMargins.Remove(_blameMargin);
        }

        if (_attached)
        {
            _dockable.Detach(this);
        }
        foreach (var cts in new[] { _tokensCts, _foldingCts, _highlightCts, _completionCts, _signatureCts, _hoverCts, _wordsCts })
        {
            cts?.Cancel();
            cts?.Dispose();
        }

        _hoverTimer.Stop();
        _completion?.Close();
        _signature?.Close();
        _services.Options.PropertyChanged -= OnOptionsChanged;
        _services.ThemeChanged -= OnThemeChanged;
        _services.Diagnostics.Changed -= OnDiagnosticsChanged;
        Model.ContentChanged -= OnContentChanged;
        Model.Reloaded -= OnReloaded;
        Model.PropertyChanged -= OnModelPropertyChanged;
        Model.Text.Changing -= OnDocumentChanging;
        FoldingManager.Uninstall(_folding);
        _search.Uninstall();
        _multiCaret.Dispose();
        var view = Editor.TextArea.TextView;
        view.LineTransformers.Remove(_colorizer);
        view.LineTransformers.Remove(_markers);
        view.BackgroundRenderers.Remove(_highlights);
        view.BackgroundRenderers.Remove(_markers);
        view.BackgroundRenderers.Remove(_debugLines);
        Editor.TextArea.LeftMargins.Remove(_breakpointMargin);
        _breakpointMargin.Detach();
        Editor.TextArea.LeftMargins.Remove(_testMargin);
        _testMargin.Detach();
        Editor.TextArea.LeftMargins.Remove(_benchmarkMargin);
        _benchmarkMargin.Detach();
        _bookmarkMargin.Detach();
        Editor.TextArea.LeftMargins.Remove(_coverageMargin);
        _coverageMargin.Detach();
        Editor.TextArea.LeftMargins.Remove(_hotLineMargin);
        _hotLineMargin.Detach();
        Editor.TextArea.ContextRequested -= OnContextRequested;
        _services.Debugger.StateChanged -= OnDebuggerChanged;
        _services.Debugger.FrameChanged -= OnDebuggerChanged;
        _services.Debugger.Stopped -= OnDebuggerStopped;
        PinLayer.Detach();
        view.ScrollOffsetChanged -= OnViewChanged;
        view.VisualLinesChanged -= OnViewChanged;
        view.PointerMoved -= OnPointerMoved;
        view.PointerExited -= OnPointerExited;
        view.PointerPressed -= OnPointerPressed;
        Editor.TextArea.Caret.PositionChanged -= OnCaretChanged;
        Editor.TextArea.SelectionChanged -= OnCaretChanged;
        Editor.TextArea.TextEntering -= OnTextEntering;
        Editor.TextArea.TextEntered -= OnTextEntered;
        Editor.TextArea.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        Suggestions.Dispose();
    }
}

/// <summary>Path comparison of the platform (case-insensitive on Windows and macOS).</summary>
internal static class SolutionPaths
{
    public static StringComparer Comparer => Codalus.Workbench.Projects.SolutionService.PathComparer;
}
