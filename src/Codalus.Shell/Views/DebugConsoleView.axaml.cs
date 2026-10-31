using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Output;

namespace Codalus.Shell.Views;

/// <summary>
/// The Debug Console: the debug output channel (appends batched per dispatcher pass and colored, like the Output window,
/// M34h) and an input line; Enter evaluates, Up and Down walk the history.
/// </summary>
public sealed partial class DebugConsoleView : UserControl, IPaneView
{
    private readonly OutputPresenter _presenter;
    private readonly Controls.FollowTail _tail;

    public DebugConsoleView()
    {
        InitializeComponent();
        _presenter = new OutputPresenter(Editor);
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
        _presenter.Pause(); // shown again unchanged, the console isn't read again (M56)
    }

    private void Show(OutputChannel? channel)
    {
        _presenter.Show(channel);

        // FollowTail keeps the end in view while the user is there (M34a).
        _tail.Reset();
    }
}
