using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Editor;

/// <summary>
/// AI inline suggestions in one editor (M28b, ADR 0062): after typing pauses at the end of a line, or anywhere on demand
/// (Trigger Inline Suggestion, Alt+\), the suggestion from <see cref="AiCompletions"/> shows in gray at the caret (every
/// line of it, the following lines pushed down). Tab accepts it, Ctrl+Right (Alt+Right on macOS) accepts its next word,
/// Esc or moving away dismisses it, and typing what it shows keeps the rest. Asking never blocks typing: a newer keystroke
/// cancels the request. While a request is out, gray dots at the caret say it's thinking (an agent can take seconds).
/// </summary>
public sealed class InlineSuggestions : IDisposable
{
    private readonly TextEditor _editor;
    private readonly Func<CodeDocument> _model;
    private readonly Func<AiCompletions?> _completions;
    private readonly Func<bool> _isCompletionOpen;
    private readonly Func<IBrush> _brush;
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _thinking = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly Action<string>? _message;
    private readonly GhostGenerator _generator;
    private DateTime _thinkingFrom;
    private int _dots;
    private CancellationTokenSource? _cts;
    private bool _typed;
    private bool _applying;

    /// <summary>How long an automatic request waits before the dots show: a provider's usually answers first, without a flicker.</summary>
    public static TimeSpan ThinkingDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <param name="message">Shows a short message (the status bar): an on-demand request that found nothing says so.</param>
    public InlineSuggestions(TextEditor editor, Func<CodeDocument> model, Func<AiCompletions?> completions, Func<bool> isCompletionOpen, Func<IBrush> brush, Action<string>? message = null)
    {
        _editor = editor;
        _model = model;
        _completions = completions;
        _isCompletionOpen = isCompletionOpen;
        _brush = brush;
        _message = message;
        _generator = new GhostGenerator(this);
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _ = RequestAsync();
        };
        _thinking.Tick += (_, _) => OnThinkingTick();
        editor.TextArea.TextView.ElementGenerators.Add(_generator);
        editor.TextArea.TextEntered += OnTextEntered;
        editor.Document.Changed += OnDocumentChanged;
        editor.TextArea.Caret.PositionChanged += OnCaretChanged;
    }

    /// <summary>The offset the suggestion is shown at, or -1.</summary>
    public int Offset { get; private set; } = -1;

    /// <summary>The suggestion shown, or null.</summary>
    public string? Text { get; private set; }

    public bool IsVisible => Text is not null;

    /// <summary>The offset the thinking dots are at while a request is out, or -1 (none, or not shown yet).</summary>
    public int ThinkingOffset { get; private set; } = -1;

    public bool IsThinking => ThinkingOffset >= 0;

    /// <summary>Raised when a suggestion shows or goes (tests).</summary>
    public event EventHandler? Changed;

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (_completions()?.IsEnabled != true)
        {
            return;
        }

        _typed = true;
        _timer.Interval = _completions()!.Delay;
        _timer.Stop();
        _timer.Start();
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_applying)
        {
            return;
        }

        Cancel();
        if (Text is not { } text)
        {
            return;
        }

        // Typing what the suggestion shows keeps the rest of it.
        if (e.RemovalLength == 0 && e.Offset == Offset && e.InsertedText.Text is { Length: > 0 } inserted && text.StartsWith(inserted, StringComparison.Ordinal) && text.Length > inserted.Length)
        {
            Offset += inserted.Length;
            Text = text[inserted.Length..];
            Redraw();
            return;
        }

        Dismiss();
    }

    private void OnCaretChanged(object? sender, EventArgs e)
    {
        if (Text is not null && _editor.CaretOffset != Offset)
        {
            Dismiss();
        }

        // Moving away from a request that is out: its answer would be for a place the caret left, so it stops, dots too.
        if (_pendingThinking >= 0 && _editor.CaretOffset != _pendingThinking)
        {
            Cancel();
        }

        if (!_typed)
        {
            _timer.Stop();
        }

        _typed = false;
    }

    /// <summary>Handles Tab, Esc and the accept-a-word key while a suggestion shows; true when it did.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (Text is null)
        {
            return false;
        }

        var wordKey = OperatingSystem.IsMacOS() ? KeyModifiers.Alt : KeyModifiers.Control;
        switch (e.Key)
        {
            case Key.Tab when e.KeyModifiers == KeyModifiers.None && !_isCompletionOpen():
                Accept();
                return true;
            case Key.Escape when e.KeyModifiers == KeyModifiers.None:
                Dismiss();
                return true;
            case Key.Right when e.KeyModifiers == wordKey:
                AcceptWord();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Inserts the whole suggestion, as one undo step.</summary>
    public void Accept()
    {
        if (Text is { } text)
        {
            Insert(text);
        }
    }

    /// <summary>Inserts the suggestion up to the end of its next word; the rest stays shown.</summary>
    public void AcceptWord()
    {
        if (Text is not { } text)
        {
            return;
        }

        var end = 0;
        while (end < text.Length && char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        if (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
        {
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
            {
                end++;
            }
        }
        else if (end < text.Length)
        {
            end++;
        }

        var word = text[..end];
        var rest = text[end..];
        var offset = Offset;
        Insert(word);
        if (rest.Length > 0)
        {
            Show(offset + word.Length, rest);
        }
    }

    private void Insert(string text)
    {
        var offset = Offset;
        Dismiss();
        _applying = true;
        try
        {
            _editor.Document.Insert(offset, text);
            _editor.CaretOffset = offset + text.Length;
        }
        finally
        {
            _applying = false;
        }
    }

    public void Dismiss()
    {
        Cancel();
        if (Text is null)
        {
            return;
        }

        Text = null;
        Offset = -1;
        Redraw();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Show(int offset, string text)
    {
        Offset = offset;
        Text = text;
        Redraw();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Redraw() => _editor.TextArea.TextView.Redraw();

    private void Cancel()
    {
        _timer.Stop();
        StopThinking();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Asks for a suggestion at the caret now (Trigger Inline Suggestion): anywhere in a line, and with automatic
    /// suggestions off too, as long as AI can answer. The dots show at once; finding nothing says so.
    /// </summary>
    public Task TriggerAsync() => RequestAsync(onDemand: true);

    /// <summary>
    /// Asks for a suggestion: after typing pauses, when the caret is at the end of a line (or before closing characters)
    /// without a selection; on demand, wherever the caret is.
    /// </summary>
    internal async Task RequestAsync(bool onDemand = false)
    {
        if (_completions() is not { } completions || !(onDemand ? completions.CanCompleteOnDemand : completions.IsEnabled)
            || _editor.TextArea.Selection.Length > 0 || _isCompletionOpen() || _editor.IsReadOnly)
        {
            if (onDemand && _completions() is { CanCompleteOnDemand: false })
            {
                _message?.Invoke(Localizer.Get("msg.editor.inlineSuggestionNoAi", "Inline suggestions need AI assistance: turn it on and choose a provider, or connect an agent (Tools → AI)."));
            }

            return;
        }

        var document = _editor.Document;
        var caret = _editor.CaretOffset;
        var line = document.GetLineByOffset(caret);
        if (!onDemand)
        {
            var rest = document.GetText(caret, line.EndOffset - caret);
            if (rest.Any(c => !char.IsWhiteSpace(c) && c is not (')' or ']' or '}' or ';' or ',' or '"' or '\'' or '>')))
            {
                return;
            }

            if (document.GetText(line.Offset, caret - line.Offset).Trim().Length == 0 && line.LineNumber > 1 && document.GetLineByNumber(line.LineNumber - 1).Length == 0)
            {
                // An empty line after an empty line: nothing to go on.
                return;
            }
        }

        Dismiss();

        Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var version = document.Version;
        var model = _model();
        var request = new AiCompletionRequest(model.Path, model.LanguageId, document.GetText(0, caret), document.GetText(caret, document.TextLength - caret));
        StartThinking(caret, onDemand ? TimeSpan.Zero : ThinkingDelay);
        string? suggestion;
        try
        {
            suggestion = await completions.CompleteAsync(request, cts.Token, onDemand).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (_cts == cts)
            {
                StopThinking();
            }
        }

        if (cts.IsCancellationRequested || !Equals(document.Version, version) || _editor.CaretOffset != caret)
        {
            return;
        }

        if (suggestion is null)
        {
            if (onDemand)
            {
                _message?.Invoke(Localizer.Get("msg.editor.noInlineSuggestion", "No suggestion here (the AI Output channel says why when a request failed)."));
            }

            return;
        }

        Show(caret, suggestion);
    }

    /// <summary>Shows the thinking dots at <paramref name="offset"/> after <paramref name="after"/>, animated until the request ends.</summary>
    private void StartThinking(int offset, TimeSpan after)
    {
        StopThinking();
        ThinkingOffset = -1;
        _thinkingFrom = DateTime.UtcNow + after;
        _pendingThinking = offset;
        _dots = 0;
        if (after <= TimeSpan.Zero)
        {
            OnThinkingTick();
        }

        _thinking.Start();
    }

    private int _pendingThinking = -1;

    private void OnThinkingTick()
    {
        if (_pendingThinking < 0 || DateTime.UtcNow < _thinkingFrom)
        {
            return;
        }

        ThinkingOffset = _pendingThinking;
        _dots = (_dots % 3) + 1;
        Redraw();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void StopThinking()
    {
        _thinking.Stop();
        _pendingThinking = -1;
        if (ThinkingOffset >= 0)
        {
            ThinkingOffset = -1;
            Redraw();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The thinking dots as they are now (one to three), or null.</summary>
    internal string? ThinkingText => ThinkingOffset >= 0 ? new string('·', Math.Max(1, _dots)) : null;

    public void Dispose()
    {
        Cancel();
        _thinking.Stop();
        _editor.TextArea.TextView.ElementGenerators.Remove(_generator);
        _editor.TextArea.TextEntered -= OnTextEntered;
        _editor.Document.Changed -= OnDocumentChanged;
        _editor.TextArea.Caret.PositionChanged -= OnCaretChanged;
    }

    /// <summary>Puts the ghost text at the suggestion's offset.</summary>
    private sealed class GhostGenerator(InlineSuggestions owner) : VisualLineElementGenerator
    {
        /// <summary>Where the ghost goes: the suggestion's offset, or the thinking dots' while there is no suggestion.</summary>
        private (int Offset, string? Text) Ghost => owner.Text is { } text ? (owner.Offset, text) : (owner.ThinkingOffset, owner.ThinkingText);

        public override int GetFirstInterestedOffset(int startOffset)
        {
            var line = CurrentContext.VisualLine.LastDocumentLine;
            var (at, text) = Ghost;
            return text is not null && at >= startOffset && at <= line.EndOffset ? at : -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            var (at, text) = Ghost;
            if (text is null || offset != at)
            {
                return null;
            }

            var line = CurrentContext.Document.GetLineByOffset(offset);
            var before = CurrentContext.Document.GetText(line.Offset, offset - line.Offset);
            return new GhostElement(text, before, owner._brush(), CurrentContext.TextView.DefaultLineHeight, CurrentContext.TextView.Options.IndentationSize);
        }
    }

    /// <summary>The suggestion: no part of the document, no caret stops, drawn in gray over as many lines as it has.</summary>
    private sealed class GhostElement(string text, string before, IBrush brush, double lineHeight, int tabSize) : VisualLineElement(1, 0)
    {
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
            new GhostRun(text, before, brush, TextRunProperties, lineHeight, tabSize);

        public override int GetNextCaretPosition(int visualColumn, AvaloniaEdit.Document.LogicalDirection direction, CaretPositioningMode mode) => -1;
    }

    /// <summary>
    /// The first line at the caret; the others below it from the line's start (they carry their own indentation), so the
    /// visual line is as tall as the suggestion.
    /// </summary>
    private sealed class GhostRun : DrawableTextRun
    {
        private readonly TextLayout[] _lines;
        private readonly double _lineHeight;
        private readonly double _indent;

        public GhostRun(string text, string before, IBrush brush, TextRunProperties properties, double lineHeight, int tabSize)
        {
            Properties = properties;
            var typeface = properties.Typeface;
            var size = properties.FontRenderingEmSize;
            _lineHeight = lineHeight;
            var tab = new string(' ', Math.Max(1, tabSize));
            var ghost = new Typeface(typeface.FontFamily, FontStyle.Italic, typeface.Weight);
            _lines = [.. text.Split('\n').Select(l => new TextLayout(l.Replace("\t", tab, StringComparison.Ordinal), ghost, size, brush))];
            using var prefix = new TextLayout(before.Replace("\t", tab, StringComparison.Ordinal), typeface, size, brush);
            _indent = prefix.WidthIncludingTrailingWhitespace;
        }

        /// <summary>The editor's properties: the line reads their baseline alignment.</summary>
        public override TextRunProperties? Properties { get; }

        public override Size Size => new(_lines[0].WidthIncludingTrailingWhitespace, _lineHeight * _lines.Length);

        public override double Baseline => _lines[0].Baseline;

        public override void Draw(DrawingContext drawingContext, Point origin)
        {
            _lines[0].Draw(drawingContext, origin);
            for (var i = 1; i < _lines.Length; i++)
            {
                _lines[i].Draw(drawingContext, new Point(origin.X - _indent, origin.Y + (i * _lineHeight)));
            }
        }
    }
}
