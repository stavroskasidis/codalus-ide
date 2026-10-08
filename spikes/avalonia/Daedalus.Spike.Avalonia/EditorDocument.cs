using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Daedalus.Diagnostics.BenchEvents;
using Daedalus.Rpc;
using Dock.Model.Mvvm.Controls;
using TextEdit = Daedalus.LanguageServices.Protocol.TextEdit;

namespace Daedalus.Spike.Avalonia;

/// <summary>An open editor tab. The text model outlives the view, which Dock may recreate.</summary>
public sealed class EditorDocument : Document
{
    internal EditorDocument(ShellViewModel shell, string path, string text)
    {
        Id = path;
        Title = System.IO.Path.GetFileName(path);
        Path = path;
        Text = new TextDocument(text);
        Controller = new EditorController(shell, this);
    }

    public string Path { get; }

    public TextDocument Text { get; }

    internal EditorController Controller { get; }
}

/// <summary>
/// Keeps the host in sync with edits and drives semantic highlighting for the visible range.
/// Everything here runs on the UI thread; host calls are asynchronous and cancellable, and
/// stale responses (older document version) are discarded.
/// </summary>
internal sealed class EditorController : IDisposable
{
    private const int MarginLines = 60;
    private static readonly TimeSpan s_editDebounce = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan s_scrollDebounce = TimeSpan.FromMilliseconds(30);

    private readonly ShellViewModel _shell;
    private readonly EditorDocument _document;
    private Task _sendChain = Task.CompletedTask;
    private CancellationTokenSource? _highlightCts;
    private TextView? _textView;
    private readonly TaskCompletionSource _firstHighlight = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public EditorController(ShellViewModel shell, EditorDocument document)
    {
        _shell = shell;
        _document = document;
        document.Text.Changed += OnDocumentChanged;
    }

    public int Version { get; private set; } = 1;

    /// <summary>Completes once the visible range first shows semantic highlighting.</summary>
    public Task FirstHighlight => _firstHighlight.Task;

    public SemanticColorizer Colorizer { get; } = new();

    /// <summary>Binds to the text view of the (possibly recreated) editor view.</summary>
    public void Attach(TextView textView)
    {
        if (_textView is { } previous)
        {
            previous.ScrollOffsetChanged -= OnViewChanged;
            previous.VisualLinesChanged -= OnViewChanged;
            previous.LineTransformers.Remove(Colorizer);
        }

        _textView = textView;
        textView.LineTransformers.Add(Colorizer);
        textView.ScrollOffsetChanged += OnViewChanged;
        textView.VisualLinesChanged += OnViewChanged;
        ScheduleHighlight(TimeSpan.Zero);
    }

    private void OnViewChanged(object? sender, EventArgs e)
    {
        if (!Colorizer.Covers(VisibleRange()))
        {
            ScheduleHighlight(s_scrollDebounce);
        }
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        Version++;
        Colorizer.OnDocumentChanged(e, _document.Text);
        var version = Version;
        var edit = new TextEdit(e.Offset, e.RemovalLength, e.InsertedText.Text);
        if (_shell.Host is { } host)
        {
            // Chained so that edits reach the host strictly in order.
            _sendChain = _sendChain.ContinueWith(
                _ => host.DidChangeAsync(_document.Path, version, [edit], CancellationToken.None),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap()
                .ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        }

        ScheduleHighlight(s_editDebounce);
    }

    public void ScheduleHighlight(TimeSpan delay)
    {
        _highlightCts?.Cancel();
        _highlightCts?.Dispose();
        var cts = _highlightCts = new CancellationTokenSource();
        _ = HighlightAsync(delay, cts.Token);
    }

    private async Task HighlightAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (_shell.Host is not { } host || _textView is null)
        {
            return;
        }

        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(true);
            }

            var (first, last) = VisibleRange();
            var start = Math.Max(0, first - MarginLines);
            var end = Math.Min(_document.Text.LineCount, last + MarginLines);
            await _sendChain.ConfigureAwait(true);
            var tokens = await host.GetSemanticTokensRangeAsync(_document.Path, start, end, cancellationToken).ConfigureAwait(true);
            if (tokens.Version != Version || cancellationToken.IsCancellationRequested)
            {
                return; // Stale: a newer edit is on its way.
            }

            Colorizer.Apply(start, end, tokens.Data);
            _textView.Redraw();
            if (!_firstHighlight.Task.IsCompleted)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    BenchEvents.Emit(BenchEventNames.HighlightReady);
                    _firstHighlight.TrySetResult();
                }, DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is JsonRpcConnectionLostException or JsonRpcRemoteException)
        {
            // The supervisor restarts the host and triggers a fresh highlight pass.
        }
    }

    /// <summary>Zero-based, end-exclusive range of visible lines.</summary>
    private (int First, int Last) VisibleRange()
    {
        if (_textView is not { VisualLinesValid: true } view || view.VisualLines.Count == 0)
        {
            return (0, Math.Min(_document.Text.LineCount, 100));
        }

        return (view.VisualLines[0].FirstDocumentLine.LineNumber - 1, view.VisualLines[^1].LastDocumentLine.LineNumber);
    }

    public void Dispose()
    {
        _document.Text.Changed -= OnDocumentChanged;
        _highlightCts?.Cancel();
        _highlightCts?.Dispose();
        _highlightCts = null;
    }
}
