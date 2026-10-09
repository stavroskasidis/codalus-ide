using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Output;

namespace DaedalusStudio.Shell.Views;

/// <summary>
/// Shows one output channel in a read-only, virtualized text editor. Appends arrive on any thread; they are
/// queued and applied in one batch per dispatcher pass, so a chatty channel can't flood the UI thread.
/// </summary>
public sealed partial class OutputView : UserControl, IPaneView
{
    private readonly ConcurrentQueue<string?> _pending = new();
    private OutputViewModel? _viewModel;
    private OutputChannel? _channel;
    private int _flushScheduled;

    public OutputView() => InitializeComponent();

    /// <summary>The text currently shown (for tests).</summary>
    public string Text => Editor.Text;

    public void FocusPrimary() => Editor.TextArea.Focus();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _viewModel = DataContext as OutputViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
            Show(_viewModel.Selected);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        Show(null);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OutputViewModel.Selected))
        {
            Show(_viewModel?.Selected);
        }
    }

    private void Show(OutputChannel? channel)
    {
        if (_channel is not null)
        {
            _channel.Appended -= OnAppended;
        }

        _channel = channel;
        _pending.Clear();
        Editor.Text = channel?.GetText() ?? string.Empty;
        if (channel is not null)
        {
            channel.Appended += OnAppended;
        }

        ScrollToEnd();
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
        Volatile.Write(ref _flushScheduled, 0);
        var appended = new StringBuilder();
        var reload = false;
        while (_pending.TryDequeue(out var text))
        {
            if (text is null)
            {
                reload = true;
                appended.Clear();
            }
            else
            {
                appended.Append(text);
            }
        }

        if (reload)
        {
            Editor.Text = _channel?.GetText() ?? string.Empty;
        }
        else if (appended.Length > 0)
        {
            Editor.Document.Insert(Editor.Document.TextLength, appended.ToString());
        }

        ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        if (_viewModel is not { ScrollLock: true })
        {
            Editor.ScrollToEnd();
        }
    }
}
