using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Output;

namespace Codalus.Shell.Views;

/// <summary>
/// Shows one output channel in a read-only, virtualized text editor, its lines colored by what they report and by the
/// processes' ANSI colors (M34h, <see cref="OutputPresenter"/>). Appends arrive on any thread; they are decoded there
/// and applied in one batch per dispatcher pass, so a chatty channel can't flood the UI thread.
/// </summary>
public sealed partial class OutputView : UserControl, IPaneView
{
    private readonly OutputPresenter _presenter;
    private readonly FollowTail _tail;
    private OutputViewModel? _viewModel;

    public OutputView()
    {
        InitializeComponent();
        _presenter = new OutputPresenter(Editor);
        _tail = FollowTail.Attach(Editor);
    }

    /// <summary>Whether the view sticks to the end of the output (tests).</summary>
    public bool IsFollowing => _tail.IsFollowing;

    /// <summary>The text currently shown, without escape sequences (for tests).</summary>
    public string Text => Editor.Text;

    /// <summary>What colors the text (tests).</summary>
    internal OutputPresenter Coloring => _presenter;

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
        else if (e.PropertyName == nameof(OutputViewModel.ScrollLock))
        {
            _tail.Enabled = _viewModel is not { ScrollLock: true };
        }
    }

    private void Show(OutputChannel? channel)
    {
        _presenter.Show(channel);
        _tail.Enabled = _viewModel is not { ScrollLock: true };
        _tail.Reset();

        // FollowTail scrolls once layout knows the new end, if the user is still at the end.
    }
}
