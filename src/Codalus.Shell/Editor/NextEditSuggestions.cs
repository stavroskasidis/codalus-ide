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
/// AI next-edit suggestions in one editor (M28b, ADR 0062): the editor keeps the last few edits; when typing pauses,
/// <see cref="AiNextEdit"/> predicts the next one nearby. Its lines show highlighted with the new text at their end; Tab
/// applies it (and the applied edit can lead to the next suggestion), Esc or any other edit dismisses it.
/// </summary>
public sealed class NextEditSuggestions : IDisposable
{
    private static readonly TimeSpan s_delay = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan s_mergeWindow = TimeSpan.FromSeconds(4);
    private readonly TextEditor _editor;
    private readonly Func<CodeDocument> _model;
    private readonly Func<AiNextEdit?> _nextEdit;
    private readonly Func<bool> _isBusy;
    private readonly Func<IBrush> _brush;
    private readonly DispatcherTimer _timer = new() { Interval = s_delay };
    private readonly List<(AiRecentEdit Edit, DateTime At)> _recent = [];
    private readonly Highlight _highlight;
    private readonly HintGenerator _generator;
    private CancellationTokenSource? _cts;
    private (int Line, string Old)? _changing;
    private bool _applying;

    public NextEditSuggestions(TextEditor editor, Func<CodeDocument> model, Func<AiNextEdit?> nextEdit, Func<bool> isBusy, Func<IBrush> brush)
    {
        _editor = editor;
        _model = model;
        _nextEdit = nextEdit;
        _isBusy = isBusy;
        _brush = brush;
        _highlight = new Highlight(this);
        _generator = new HintGenerator(this);
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _ = RequestAsync();
        };
        editor.TextArea.TextView.BackgroundRenderers.Add(_highlight);
        editor.TextArea.TextView.ElementGenerators.Add(_generator);
        editor.Document.Changing += OnChanging;
        editor.Document.Changed += OnChanged;
    }

    /// <summary>The suggestion shown, or null.</summary>
    public AiEditSuggestion? Suggestion { get; private set; }

    /// <summary>The edits kept for the next request (tests).</summary>
    public IReadOnlyList<AiRecentEdit> RecentEdits => [.. _recent.Select(r => r.Edit)];

    public event EventHandler? Changed;

    private void OnChanging(object? sender, DocumentChangeEventArgs e)
    {
        var document = _editor.Document;
        var first = document.GetLineByOffset(e.Offset);
        var last = document.GetLineByOffset(Math.Min(document.TextLength, e.Offset + e.RemovalLength));
        _changing = (first.LineNumber, document.GetText(first.Offset, last.EndOffset - first.Offset));
    }

    private void OnChanged(object? sender, DocumentChangeEventArgs e)
    {
        Dismiss();
        if (_changing is not { } changing)
        {
            return;
        }

        _changing = null;
        var document = _editor.Document;
        var first = document.GetLineByNumber(Math.Min(changing.Line, document.LineCount));
        var last = document.GetLineByOffset(Math.Min(document.TextLength, e.Offset + e.InsertionLength));
        var newText = document.GetText(first.Offset, last.EndOffset - first.Offset);
        var now = DateTime.UtcNow;

        // Typing a word is many changes on one line: they make one edit.
        if (_recent.Count > 0 && _recent[^1].Edit.Line == changing.Line && now - _recent[^1].At < s_mergeWindow && !_applying)
        {
            _recent[^1] = (_recent[^1].Edit with { NewText = newText }, now);
        }
        else
        {
            _recent.Add((new AiRecentEdit(changing.Line, changing.Old, newText), now));
            if (_recent.Count > 5)
            {
                _recent.RemoveAt(0);
            }
        }

        if (_recent[^1].Edit.OldText == _recent[^1].Edit.NewText)
        {
            _recent.RemoveAt(_recent.Count - 1);
        }

        if (_nextEdit()?.IsEnabled == true)
        {
            _timer.Stop();
            _timer.Start();
        }
    }

    /// <summary>Tab applies the suggestion, Esc dismisses it; true when it handled the key.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (Suggestion is null || e.KeyModifiers != KeyModifiers.None)
        {
            return false;
        }

        if (e.Key == Key.Tab && !_isBusy())
        {
            Apply();
            return true;
        }

        if (e.Key == Key.Escape)
        {
            Dismiss();
            return true;
        }

        return false;
    }

    /// <summary>Replaces the suggested lines with the new text, as one undo step, and puts the caret at its end.</summary>
    public void Apply()
    {
        if (Suggestion is not { } suggestion)
        {
            return;
        }

        var document = _editor.Document;
        if (suggestion.EndLine > document.LineCount)
        {
            Dismiss();
            return;
        }

        var first = document.GetLineByNumber(suggestion.StartLine);
        var last = document.GetLineByNumber(suggestion.EndLine);
        if (document.GetText(first.Offset, last.EndOffset - first.Offset) != suggestion.OldText)
        {
            Dismiss();
            return;
        }

        var newText = document.Text.Contains("\r\n", StringComparison.Ordinal) ? suggestion.NewText.Replace("\n", "\r\n", StringComparison.Ordinal) : suggestion.NewText;
        Suggestion = null;
        _applying = true;
        try
        {
            document.Replace(first.Offset, last.EndOffset - first.Offset, newText);
            _editor.CaretOffset = first.Offset + newText.Length;
        }
        finally
        {
            _applying = false;
        }

        Redraw();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dismiss()
    {
        _timer.Stop();
        _cts?.Cancel();
        if (Suggestion is null)
        {
            return;
        }

        Suggestion = null;
        Redraw();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Asks for the next edit (the timer's tick; tests call it directly).</summary>
    internal async Task RequestAsync()
    {
        if (_nextEdit() is not { IsEnabled: true } nextEdit || _recent.Count == 0 || _isBusy() || _editor.TextArea.Selection.Length > 0)
        {
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var document = _editor.Document;
        var version = document.Version;
        var caretLine = document.GetLineByOffset(_editor.CaretOffset).LineNumber;
        var model = _model();
        AiEditSuggestion? suggestion;
        try
        {
            suggestion = await nextEdit.SuggestAsync(model.Path, model.LanguageId, document.Text, caretLine, RecentEdits, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (suggestion is null || cts.IsCancellationRequested || !Equals(document.Version, version) || _isBusy())
        {
            return;
        }

        // Don't suggest undoing what was just typed.
        if (_recent.Count > 0 && suggestion.NewText == _recent[^1].Edit.OldText)
        {
            return;
        }

        Suggestion = suggestion;
        Redraw();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Redraw() => _editor.TextArea.TextView.Redraw();

    public void Dispose()
    {
        _timer.Stop();
        _cts?.Cancel();
        _cts?.Dispose();
        _editor.TextArea.TextView.BackgroundRenderers.Remove(_highlight);
        _editor.TextArea.TextView.ElementGenerators.Remove(_generator);
        _editor.Document.Changing -= OnChanging;
        _editor.Document.Changed -= OnChanged;
    }

    /// <summary>The suggested lines' background.</summary>
    private sealed class Highlight(NextEditSuggestions owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (owner.Suggestion is not { } suggestion || suggestion.EndLine > textView.Document.LineCount)
            {
                return;
            }

            var brush = owner._brush();
            var fill = brush is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, 0.15) : brush;
            foreach (var line in textView.VisualLines)
            {
                var number = line.FirstDocumentLine.LineNumber;
                if (number >= suggestion.StartLine && number <= suggestion.EndLine)
                {
                    var top = line.VisualTop - textView.ScrollOffset.Y;
                    drawingContext.FillRectangle(fill, new Rect(0, top, textView.Bounds.Width, line.Height));
                }
            }
        }
    }

    /// <summary>The new text after the suggestion's last line.</summary>
    private sealed class HintGenerator(NextEditSuggestions owner) : VisualLineElementGenerator
    {
        private DocumentLine? Target() =>
            owner.Suggestion is { } suggestion && suggestion.EndLine <= CurrentContext.Document.LineCount ? CurrentContext.Document.GetLineByNumber(suggestion.EndLine) : null;

        public override int GetFirstInterestedOffset(int startOffset)
        {
            var line = CurrentContext.VisualLine.LastDocumentLine;
            return Target() is { } target && target == line && startOffset <= target.EndOffset ? target.EndOffset : -1;
        }

        public override VisualLineElement? ConstructElement(int offset)
        {
            if (owner.Suggestion is not { } suggestion || Target() is not { } target || offset != target.EndOffset)
            {
                return null;
            }

            var lines = suggestion.NewText.Split('\n');
            var text = "    ⇥ " + lines[0].Trim() + (lines.Length > 1 ? $" (+{lines.Length - 1})" : "");
            return new HintElement(text, owner._brush());
        }
    }

    private sealed class HintElement(string text, IBrush brush) : FormattedTextElement(text, 0)
    {
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            TextRunProperties.SetForegroundBrush(brush);
            return base.CreateTextRun(startVisualColumn, context);
        }

        public override int GetNextCaretPosition(int visualColumn, AvaloniaEdit.Document.LogicalDirection direction, CaretPositioningMode mode) => -1;
    }
}
