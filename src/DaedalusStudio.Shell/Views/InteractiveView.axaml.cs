using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Output;
using LspCompletionItem = DaedalusStudio.LanguageServices.Protocol.CompletionItem;

namespace DaedalusStudio.Shell.Views;

/// <summary>
/// The C# Interactive window (ADR 0015). The input is a small C# editor: tokens, completion and signature help come
/// from the REPL host, which knows the submissions so far. Enter at the end runs a complete submission (Ctrl+Enter
/// always runs, Shift+Enter adds a line), Alt+Up and Alt+Down recall history, Escape clears the input.
/// </summary>
public sealed partial class InteractiveView : UserControl, IPaneView
{
    private readonly ConcurrentQueue<string?> _pending = new();
    private readonly SemanticColorizer _colorizer = new();
    private readonly DispatcherTimer _tokensTimer;
    private InteractiveViewModel? _vm;
    private OutputChannel? _channel;
    private int _flushScheduled;
    private bool _syncing;
    private CompletionWindow? _completion;
    private OverloadInsightWindow? _signature;
    private int _tokensGeneration;
    private int _completionGeneration;
    private int _signatureGeneration;

    private readonly Controls.FollowTail _tail;

    public InteractiveView()
    {
        InitializeComponent();
        _tail = Controls.FollowTail.Attach(TranscriptEditor);
        InputEditor.TextArea.TextView.LineTransformers.Add(_colorizer);
        InputEditor.Options.EnableHyperlinks = false;
        InputEditor.Options.EnableEmailHyperlinks = false;
        InputEditor.Options.ConvertTabsToSpaces = true;
        InputEditor.Options.IndentationSize = 4;
        _tokensTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _tokensTimer.Tick += (_, _) =>
        {
            _tokensTimer.Stop();
            _ = RefreshTokensAsync();
        };
        InputEditor.TextChanged += (_, _) => OnInputChanged();
        InputEditor.TextArea.TextEntered += (_, e) => OnTextEntered(e.Text ?? string.Empty);
        InputEditor.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>The transcript text (for tests).</summary>
    public string Text => TranscriptEditor.Text;

    /// <summary>The input editor (for tests).</summary>
    internal AvaloniaEdit.TextEditor Input => InputEditor;

    internal CompletionWindow? CompletionWindow => _completion;

    public void FocusPrimary() => InputEditor.TextArea.Focus();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
        }

        _vm = DataContext as InteractiveViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
            _colorizer.Theme = _vm.Colors;
            SetInput(_vm.Input);
            _vm.Warm();
        }

        if (TopLevel.GetTopLevel(this) is not null)
        {
            Show(_vm?.Transcript);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Show(_vm?.Transcript);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Show(null);
        _completion?.Close();
        _signature?.Close();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(InteractiveViewModel.Input):
                SetInput(_vm.Input);
                break;
            case nameof(InteractiveViewModel.Colors):
                _colorizer.Theme = _vm.Colors;
                InputEditor.TextArea.TextView.Redraw();
                break;
        }
    }

    private void SetInput(string text)
    {
        if (InputEditor.Text == text)
        {
            return;
        }

        _syncing = true;
        try
        {
            InputEditor.Text = text;
            InputEditor.CaretOffset = text.Length;
        }
        finally
        {
            _syncing = false;
        }

        OnInputChanged();
    }

    private void OnInputChanged()
    {
        if (!_syncing && _vm is not null)
        {
            _vm.Input = InputEditor.Text;
        }

        _colorizer.Clear();
        _tokensTimer.Stop();
        _tokensTimer.Start();
    }

    private async Task RefreshTokensAsync()
    {
        if (_vm is null || InputEditor.Text is not { Length: > 0 } code)
        {
            return;
        }

        try
        {
            using var timeout = Begin(ref _tokensGeneration, out var generation);
            var data = await _vm.Service.TokensAsync(code, timeout.Token).ConfigureAwait(true);
            if (IsCurrent(ref _tokensGeneration, generation) && InputEditor.Text == code)
            {
                _colorizer.Apply(0, InputEditor.Document.LineCount, data);
                InputEditor.TextArea.TextView.Redraw();
            }
        }
        catch (Exception ex) when (IsTransient(ex))
        {
        }
    }

    private async void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Enter when _completion is null && !shift && (ctrl || InputEditor.CaretOffset == InputEditor.Document.TextLength):
                e.Handled = true;
                _signature?.Close();
                if (!await _vm.SubmitAsync(force: ctrl).ConfigureAwait(true))
                {
                    InputEditor.TextArea.PerformTextInput("\n");
                }

                break;
            case Key.Up when alt:
            case Key.Down when alt:
                e.Handled = true;
                _vm.Navigate(e.Key == Key.Up ? -1 : 1);
                InputEditor.CaretOffset = InputEditor.Document.TextLength;
                break;
            case Key.Space when ctrl && !shift:
                e.Handled = true;
                _ = CompleteAsync();
                break;
            case Key.Space when ctrl && shift:
                e.Handled = true;
                _ = SignatureHelpAsync();
                break;
            case Key.Escape when _completion is null && _signature is null:
                e.Handled = true;
                _vm.Input = string.Empty;
                break;
        }
    }

    private void OnTextEntered(string text)
    {
        // Typed text usually arrives a character at a time; pasted or IME text all at once: its last character counts.
        if (text.Length == 0)
        {
            return;
        }

        switch (text[^1..])
        {
            case ".":
                _completion?.Close(); // the identifier's list (from its first letter) gives way to the members
                _ = CompleteAsync();
                break;
            case "(" or ",":
                _ = SignatureHelpAsync();
                break;
            case ")":
                _signature?.Close();
                break;
            default:
                if (_completion is null && text.Length == 1 && (char.IsLetter(text[0]) || text[0] == '_') && WordLength() == 1)
                {
                    _ = CompleteAsync(); // the start of an identifier
                }

                break;
        }
    }

    private int WordLength()
    {
        var offset = InputEditor.CaretOffset;
        var start = offset;
        var document = InputEditor.Document;
        while (start > 0 && (char.IsLetterOrDigit(document.GetCharAt(start - 1)) || document.GetCharAt(start - 1) == '_'))
        {
            start--;
        }

        return offset - start;
    }

    /// <summary>Opens completion at the caret (Ctrl+Space, a typed '.' or the first letter of a word).</summary>
    internal async Task CompleteAsync()
    {
        if (_vm is null)
        {
            return;
        }

        var code = InputEditor.Text;
        var caret = InputEditor.CaretOffset;
        try
        {
            using var timeout = Begin(ref _completionGeneration, out var generation);
            var result = await _vm.Service.CompletionAsync(code, caret, timeout.Token).ConfigureAwait(true);

            if (!IsCurrent(ref _completionGeneration, generation) || result.Items.Length == 0 || _completion is not null || InputEditor.CaretOffset < result.Start || InputEditor.Text.Length < caret)
            {
                return;
            }

            var window = new CompletionWindow(InputEditor.TextArea)
            {
                StartOffset = result.Start,
                EndOffset = InputEditor.CaretOffset,
                CloseWhenCaretAtBeginning = true,
                CloseAutomatically = true,
            };
            foreach (var item in result.Items)
            {
                window.CompletionList.CompletionData.Add(new CompletionData(item, Task.FromResult, Commit));
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
            if (InputEditor.CaretOffset > result.Start)
            {
                window.CompletionList.SelectItem(InputEditor.Document.GetText(result.Start, InputEditor.CaretOffset - result.Start));
            }
        }
        catch (Exception ex) when (IsTransient(ex))
        {
        }
    }

    private void Commit(CompletionData data, ISegment segment)
    {
        LspCompletionItem item = data.Item;
        var text = item.InsertText ?? item.Label;
        InputEditor.Document.Replace(segment.Offset, segment.Length, text);
        InputEditor.CaretOffset = segment.Offset + text.Length;
    }

    private async Task SignatureHelpAsync()
    {
        if (_vm is null)
        {
            return;
        }

        try
        {
            using var timeout = Begin(ref _signatureGeneration, out var generation);
            var help = await _vm.Service.SignatureHelpAsync(InputEditor.Text, InputEditor.CaretOffset, timeout.Token).ConfigureAwait(true);
            if (!IsCurrent(ref _signatureGeneration, generation))
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
                open.Provider = new SignatureProvider(help);
                return;
            }

            var window = new OverloadInsightWindow(InputEditor.TextArea) { Provider = new SignatureProvider(help) };
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
        }
    }

    /// <summary>A timeout for one request of a kind; <see cref="IsCurrent"/> tells whether a newer one of that kind superseded it.</summary>
    private static CancellationTokenSource Begin(ref int counter, out int generation)
    {
        generation = Interlocked.Increment(ref counter);
        return new CancellationTokenSource(TimeSpan.FromSeconds(30));
    }

    private static bool IsCurrent(ref int counter, int generation) => Volatile.Read(ref counter) == generation;

    private static bool IsTransient(Exception ex) =>
        ex is OperationCanceledException or DaedalusStudio.Rpc.JsonRpcConnectionLostException or DaedalusStudio.Rpc.JsonRpcRemoteException or InvalidOperationException;

    // ------------------------------------------------------------------ transcript

    private void Show(OutputChannel? channel)
    {
        if (ReferenceEquals(channel, _channel))
        {
            return;
        }

        if (_channel is not null)
        {
            _channel.Appended -= OnAppended;
        }

        _channel = channel;
        _pending.Clear();
        TranscriptEditor.Text = channel?.GetText() ?? string.Empty;
        if (channel is not null)
        {
            channel.Appended += OnAppended;
        }

        _tail.Reset();
    }

    private void OnAppended(object? sender, string? text)
    {
        _pending.Enqueue(text);
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
        {
            Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
        }
    }

    private void Flush()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);
        var batch = new StringBuilder();
        var reload = false;
        while (_pending.TryDequeue(out var text))
        {
            if (text is null)
            {
                reload = true;
                batch.Clear();
            }
            else
            {
                batch.Append(text);
            }
        }

        if (reload)
        {
            TranscriptEditor.Text = _channel?.GetText() ?? string.Empty;
        }
        else if (batch.Length > 0)
        {
            TranscriptEditor.AppendText(batch.ToString());
        }

        // FollowTail keeps the end in view while the user is there (M34a).
    }
}
