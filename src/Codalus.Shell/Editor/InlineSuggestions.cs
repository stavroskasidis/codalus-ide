using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Codalus.Shell.Editor;

/// <summary>
/// AI inline suggestions (M28b): after a pause in typing, the text the model proposes at the caret is shown as ghost text
/// (its first line, with the count of the others). Tab accepts it; typing, moving the caret or Esc dismisses it. The
/// request is asynchronous and cancelled by the next keystroke, so typing never waits for it (budget 2).
/// </summary>
internal sealed class InlineSuggestions : IDisposable
{
    /// <summary>The pause after which a suggestion is asked for.</summary>
    internal static TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(600);

    private readonly TextEditor _editor;
    private readonly Func<CodeDocument> _model;
    private readonly IEditorServices _services;
    private readonly Func<bool> _completionOpen;
    private readonly DispatcherTimer _timer;
    private readonly GhostGenerator _generator;
    private CancellationTokenSource? _request;
    private bool _inserting;

    public InlineSuggestions(TextEditor editor, Func<CodeDocument> model, IEditorServices services, Func<bool> completionOpen)
    {
        _editor = editor;
        _model = model;
        _services = services;
        _completionOpen = completionOpen;
        _generator = new GhostGenerator(this);
        _timer = new DispatcherTimer { Interval = Delay };
        _timer.Tick += OnTimer;
        editor.TextArea.TextView.ElementGenerators.Add(_generator);
        editor.Document.Changed += OnDocumentChanged;
        editor.TextArea.Caret.PositionChanged += OnCaretChanged;
        editor.TextArea.AddHandler(InputElement.KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>The suggestion shown, and where (tests).</summary>
    internal (string Text, int Offset)? Shown => Ghost is { } ghost ? (ghost, Offset) : null;

    internal string? Ghost { get; private set; }

    internal int Offset { get; private set; } = -1;

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_inserting)
        {
            return;
        }

        Dismiss();
        _timer.Stop();
        _timer.Interval = Delay;
        _timer.Start();
    }

    private void OnCaretChanged(object? sender, EventArgs e)
    {
        if (Ghost is not null && _editor.CaretOffset != Offset)
        {
            Dismiss();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Ghost is null)
        {
            return;
        }

        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.None)
        {
            Accept();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Dismiss();
            e.Handled = true;
        }
    }

    private async void OnTimer(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_editor.SelectionLength > 0 || _completionOpen() || _editor.IsReadOnly)
        {
            return;
        }

        _request?.Cancel();
        using var request = _request = new CancellationTokenSource();
        var offset = _editor.CaretOffset;
        string? text;
        try
        {
            text = await _services.SuggestInlineAsync(_model(), offset, request.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (_request == request)
            {
                _request = null;
            }
        }

        // Shown only where it was asked for: an edit cancels the request (OnDocumentChanged), and the caret may have moved.
        if (!request.IsCancellationRequested && !string.IsNullOrEmpty(text) && _editor.CaretOffset == offset)
        {
            Show(text, offset);
        }
    }

    internal void Show(string text, int offset)
    {
        Ghost = text;
        Offset = offset;
        _editor.TextArea.TextView.Redraw();
    }

    /// <summary>Inserts the suggestion at the caret.</summary>
    internal void Accept()
    {
        if (Ghost is not { } text)
        {
            return;
        }

        var offset = Offset;
        Dismiss();
        _inserting = true;
        try
        {
            _editor.Document.Insert(offset, text);
            _editor.CaretOffset = offset + text.Length;
        }
        finally
        {
            _inserting = false;
        }
    }

    internal void Dismiss()
    {
        _request?.Cancel();
        if (Ghost is null)
        {
            return;
        }

        Ghost = null;
        Offset = -1;
        _editor.TextArea.TextView.Redraw();
    }

    public void Dispose()
    {
        _timer.Stop();
        _request?.Cancel();
        _editor.TextArea.TextView.ElementGenerators.Remove(_generator);
        _editor.Document.Changed -= OnDocumentChanged;
        _editor.TextArea.Caret.PositionChanged -= OnCaretChanged;
        _editor.TextArea.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
    }

    /// <summary>The ghost text: a zero-width element at the suggestion's offset, which the caret can't stop in.</summary>
    private sealed class GhostGenerator(InlineSuggestions owner) : VisualLineElementGenerator
    {
        public override int GetFirstInterestedOffset(int startOffset)
        {
            var line = CurrentContext.VisualLine.LastDocumentLine;
            return owner.Ghost is not null && owner.Offset >= startOffset && owner.Offset <= line.EndOffset ? owner.Offset : -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            if (owner.Ghost is not { } ghost || offset != owner.Offset)
            {
                return null;
            }

            var lines = ghost.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var shown = lines.Length > 1 ? $"{lines[0]}  … +{lines.Length - 1} line{(lines.Length == 2 ? "" : "s")} (Tab)" : lines[0];
            return new GhostElement(shown, owner._editor.TextArea.Foreground);
        }
    }

    private sealed class GhostElement(string text, IBrush? foreground) : FormattedTextElement(text, 0)
    {
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            if (foreground is ISolidColorBrush solid)
            {
                TextRunProperties.SetForegroundBrush(new SolidColorBrush(solid.Color, 0.45));
            }

            TextRunProperties.SetTypeface(new Typeface(TextRunProperties.Typeface.FontFamily, FontStyle.Italic));
            return base.CreateTextRun(startVisualColumn, context);
        }

        public override int GetNextCaretPosition(int visualColumn, AvaloniaEdit.Document.LogicalDirection direction, CaretPositioningMode mode) => -1;
    }
}
