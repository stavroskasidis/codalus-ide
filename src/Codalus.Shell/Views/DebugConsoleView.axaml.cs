using System.Collections.Concurrent;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Output;

namespace Codalus.Shell.Views;

/// <summary>
/// The Debug Console: the debug output channel (appends batched per dispatcher pass, like the Output window) and an
/// input line; Enter evaluates, Up and Down walk the history.
/// </summary>
public sealed partial class DebugConsoleView : UserControl, IPaneView
{
    private readonly ConcurrentQueue<string?> _pending = new();
    private OutputChannel? _channel;
    private int _flushScheduled;

    private readonly Controls.FollowTail _tail;

    public DebugConsoleView()
    {
        InitializeComponent();
        _tail = Controls.FollowTail.Attach(Editor);
        Input.KeyDown += (_, e) =>
        {
            if (DataContext is not DebugConsoleViewModel vm)
            {
                return;
            }

            switch (e.Key)
            {
                case Key.Enter:
                    _ = vm.SubmitAsync();
                    e.Handled = true;
                    break;
                case Key.Up:
                    vm.History(-1);
                    Input.CaretIndex = vm.Input.Length;
                    e.Handled = true;
                    break;
                case Key.Down:
                    vm.History(1);
                    Input.CaretIndex = vm.Input.Length;
                    e.Handled = true;
                    break;
            }
        };
    }

    /// <summary>The text shown (for tests).</summary>
    public string Text => Editor.Text;

    public void FocusPrimary() => Input.Focus();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Show((DataContext as DebugConsoleViewModel)?.Channel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Show(null);
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
            Editor.Text = _channel?.GetText() ?? string.Empty;
        }
        else if (batch.Length > 0)
        {
            Editor.AppendText(batch.ToString());
        }

        // FollowTail keeps the end in view while the user is there (M34a).
    }
}
