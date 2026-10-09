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
using Daedalus.LanguageServices.Client;
using Daedalus.LanguageServices.Protocol;
using Daedalus.Rpc;
using Daedalus.Shell.Controls;
using Daedalus.Shell.Infrastructure;

namespace Daedalus.Shell.Editor;

/// <summary>Caret position and selection, for the status bar.</summary>
public sealed record CaretInfo(int Line, int Column, int SelectionLength, int Carets);

/// <summary>
/// Everything one editor view does on top of AvaloniaEdit: options and theme, semantic highlighting of the visible
/// range, squiggles, folding, completion, signature help, Quick Info, reference and bracket highlights, multiple
/// carets, find/replace and navigation. Language features go through the document's language service client; all of
/// it is asynchronous and cancellable, and responses for an older document version are dropped. UI thread only.
/// </summary>
public sealed class CodeEditorController : IDisposable
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

    public CodeEditorController(TextEditor editor, CodeEditorDocument dockable, IEditorServices services, Popup hoverPopup, bool attach = true)
    {
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
        view.PointerMoved += OnPointerMoved;
        view.PointerExited += OnPointerExited;
        view.PointerPressed += OnPointerPressed;
        _multiCaret.Changed += (_, _) => OnCaretChanged(this, EventArgs.Empty);

        UpdateDiagnostics();
        RefreshAll();
        _attached = attach;
        if (attach)
        {
            dockable.Attach(this);
        }
    }

    public TextEditor Editor { get; }

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

    private void ApplyTheme()
    {
        var theme = _services.Theme;
        _colorizer.Theme = theme;
        _markers.Theme = theme;
        _highlights.Theme = theme;
        Editor.TextArea.SelectionBrush = theme.Selection;
        Editor.TextArea.TextView.CurrentLineBackground = theme.CurrentLine;
        Editor.TextArea.TextView.CurrentLineBorder = new Avalonia.Media.Pen(Avalonia.Media.Brushes.Transparent, 0);
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
            RefreshAll();
        }
    }

    /// <summary>Requests everything again: after the language service (re)started, or the document was reloaded.</summary>
    public void RefreshAll()
    {
        // Highlighting the visible lines first; folding (a walk of the whole file) and options when it's done.
        _refreshPending = true;
        ScheduleTokens(TimeSpan.Zero);
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
                return;
            }

            var version = Model.Version;
            var (first, last) = VisibleLines();
            var start = Math.Max(0, first - TokenMarginLines);
            var end = Math.Min(Model.Text.LineCount, last + TokenMarginLines);
            var range = new LanguageServices.Protocol.Range(new Position(start, 0), new Position(end, 0));
            var tokens = await client.SemanticTokensAsync(Model.Uri, range, cancellationToken).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested || version != Model.Version || _disposed)
            {
                return; // stale: a newer edit is on its way
            }

            _colorizer.Apply(start, end, tokens.Data);
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

            if (Client is not { } client)
            {
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

        _markers.Set(_services.Diagnostics.For(Model.Path), Model.OffsetAt);
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
        }
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
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
            var isTrigger = completion.TriggerCharacters.Contains(text);
            if (_completion is null && (isTrigger || char.IsLetter(c) || c == '_'))
            {
                ShowCompletion(isTrigger ? text : null);
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
    public void ShowCompletion(string? triggerCharacter = null) => _ = CompletionAsync(triggerCharacter, Restart(ref _completionCts).Token);

    private async Task CompletionAsync(string? triggerCharacter, CancellationToken cancellationToken)
    {
        try
        {
            if (Client is not { } client || Model.IsReadOnly)
            {
                return;
            }

            var requestedAt = Editor.CaretOffset;
            var context = triggerCharacter is null
                ? new CompletionContext(CompletionTriggerKind.Invoked, null)
                : new CompletionContext(CompletionTriggerKind.TriggerCharacter, triggerCharacter);
            var list = await client.CompletionAsync(Model.Uri, Model.PositionAt(requestedAt), context, cancellationToken).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested || _disposed || list.Items.Length == 0 || _completion is not null)
            {
                _services.Log($"{Model.Name}: completion not shown ({list.Items.Length} items, cancelled {cancellationToken.IsCancellationRequested}, open {_completion is not null})");
                return;
            }

            var start = list.ItemDefaults?.EditRange is { } range ? Model.OffsetAt(range.Start) : requestedAt;
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
            Task<LanguageServices.Protocol.CompletionItem> Resolve(LanguageServices.Protocol.CompletionItem item) => client.ResolveCompletionAsync(item, CancellationToken.None);
            foreach (var item in list.Items.OrderBy(i => i.SortText ?? i.Label, StringComparer.Ordinal))
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
            if (caret > start)
            {
                window.CompletionList.SelectItem(Model.Text.GetText(start, caret - start));
            }

            if (window.CompletionList.SelectedItem is null && list.Items.FirstOrDefault(i => i.Preselect == true) is { } preselected)
            {
                window.CompletionList.SelectedItem = window.CompletionList.CompletionData.OfType<CompletionData>().First(d => d.Item == preselected);
            }
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            Report(ex);
        }
    }

    /// <summary>Applies a completion item: the resolved edit (or the label), then additional edits such as a using.</summary>
    private void Commit(CompletionData data, ISegment segment)
    {
        var resolved = data.ResolvedWithin(TimeSpan.FromMilliseconds(300));
        var document = Model.Text;
        using (document.RunUpdate())
        {
            var text = resolved?.TextEdit?.NewText ?? data.Item.InsertText ?? data.Item.Label;
            document.Replace(segment.Offset, segment.Length, text);
            var caretAfter = segment.Offset + text.Length;

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

            if (Client is { } client)
            {
                var hover = await client.HoverAsync(Model.Uri, Model.PositionAt(offset), cancellationToken).ConfigureAwait(true);
                if (hover?.Contents.Value is { Length: > 0 } markdown)
                {
                    parts.Insert(0, markdown);
                }
            }

            if (cancellationToken.IsCancellationRequested || _disposed || parts.Count == 0)
            {
                return;
            }

            _hoverPopup.Child = new Border
            {
                Classes = { "editorHover" },
                MaxWidth = 640,
                Child = MarkdownRenderer.Render(string.Join("\n\n---\n\n", parts), _services.OpenLink),
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
        if (_attached)
        {
            _dockable.Detach(this);
        }
        foreach (var cts in new[] { _tokensCts, _foldingCts, _highlightCts, _completionCts, _signatureCts, _hoverCts })
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
    }
}

/// <summary>Path comparison of the platform (case-insensitive on Windows and macOS).</summary>
internal static class SolutionPaths
{
    public static StringComparer Comparer => Daedalus.Workbench.Projects.SolutionService.PathComparer;
}
