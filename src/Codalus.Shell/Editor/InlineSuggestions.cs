using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Codalus.Workbench.Ai;

namespace Codalus.Shell.Editor;

/// <summary>
/// AI inline suggestions in one editor (M28b, ADR 0062): after typing pauses at the end of a line, the suggestion from
/// <see cref="AiCompletions"/> shows in gray at the caret (every line of it, the following lines pushed down). Tab accepts
/// it, Ctrl+Right (Alt+Right on macOS) accepts its next word, Esc or moving away dismisses it, and typing what it shows
/// keeps the rest. Asking never blocks typing: a newer keystroke cancels the request.
/// </summary>
public sealed class InlineSuggestions : IDisposable
{
    private readonly TextEditor _editor;
    private readonly Func<CodeDocument> _model;
    private readonly Func<AiCompletions?> _completions;
    private readonly Func<bool> _isCompletionOpen;
    private readonly Func<IBrush> _brush;
    private readonly DispatcherTimer _timer = new();
    private readonly GhostGenerator _generator;
    private CancellationTokenSource? _cts;
    private bool _typed;
    private bool _applying;

    public InlineSuggestions(TextEditor editor, Func<CodeDocument> model, Func<AiCompletions?> completions, Func<bool> isCompletionOpen, Func<IBrush> brush)
    {
        _editor = editor;
        _model = model;
        _completions = completions;
        _isCompletionOpen = isCompletionOpen;
        _brush = brush;
        _generator = new GhostGenerator(this);
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _ = RequestAsync();
        };
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
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>Asks for a suggestion when the caret is at the end of a line (or before closing characters) without a selection.</summary>
    internal async Task RequestAsync()
    {
        if (_completions() is not { IsEnabled: true } completions || _editor.TextArea.Selection.Length > 0 || _isCompletionOpen() || _editor.IsReadOnly)
        {
            return;
        }

        var document = _editor.Document;
        var caret = _editor.CaretOffset;
        var line = document.GetLineByOffset(caret);
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

        Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var version = document.Version;
        var model = _model();
        var request = new AiCompletionRequest(model.Path, model.LanguageId, document.GetText(0, caret), document.GetText(caret, document.TextLength - caret));
        string? suggestion;
        try
        {
            suggestion = await completions.CompleteAsync(request, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (suggestion is null || cts.IsCancellationRequested || !Equals(document.Version, version) || _editor.CaretOffset != caret)
        {
            return;
        }

        Show(caret, suggestion);
    }

    public void Dispose()
    {
        Cancel();
        _editor.TextArea.TextView.ElementGenerators.Remove(_generator);
        _editor.TextArea.TextEntered -= OnTextEntered;
        _editor.Document.Changed -= OnDocumentChanged;
        _editor.TextArea.Caret.PositionChanged -= OnCaretChanged;
    }

    /// <summary>Puts the ghost text at the suggestion's offset.</summary>
    private sealed class GhostGenerator(InlineSuggestions owner) : VisualLineElementGenerator
    {
        public override int GetFirstInterestedOffset(int startOffset)
        {
            var line = CurrentContext.VisualLine.LastDocumentLine;
            return owner.Text is not null && owner.Offset >= startOffset && owner.Offset <= line.EndOffset ? owner.Offset : -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            if (owner.Text is not { } text || offset != owner.Offset)
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
