using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// A commit's tab: the selected file's diff (a <see cref="DiffView"/>, made again for each file) and the details pane,
/// whose changes are rows that expand and collapse as a tree's (<see cref="TreeRows"/>, M57).
/// </summary>
public sealed partial class GitCommitView : UserControl, IPaneView
{
    private readonly EditorFont? _font;
    private readonly GitCommitViewModel? _viewModel;

    public GitCommitView()
    {
        InitializeComponent();
    }

    public GitCommitView(GitCommitViewModel viewModel, EditorFont font)
        : this()
    {
        _font = font;
        _viewModel = viewModel;
        DataContext = viewModel;
        TreeRows.Attach(Changes, () => viewModel.Changes);
        ShowDiff();

        // The view model lives with the tab and the view is made again for it: only an attached view listens. Back in view,
        // the selected row is brought into view, as it was left (the list doesn't scroll to its selection by itself).
        AttachedToVisualTree += (_, _) =>
        {
            viewModel.PropertyChanged += OnViewModelChanged;
            viewModel.RevealRequested += OnRevealRequested;
            ShowDiff();
            Dispatcher.UIThread.Post(() =>
            {
                if (viewModel.SelectedRow is { } row && this.IsAttachedToVisualTree())
                {
                    Changes.ScrollIntoView(row);
                }
            }, DispatcherPriority.Loaded);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            viewModel.PropertyChanged -= OnViewModelChanged;
            viewModel.RevealRequested -= OnRevealRequested;
        };
    }

    /// <summary>The diff shown, if any (tests).</summary>
    internal DiffView? DiffView => DiffHost.Content as DiffView;

    public void FocusPrimary() => Changes.Focus();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GitCommitViewModel.Diff))
        {
            ShowDiff();
        }
    }

    private void OnRevealRequested(GitChangeRow row) => TreeRows.Reveal(Changes, row, focus: Changes.IsKeyboardFocusWithin);

    private void ShowDiff()
    {
        var diff = _viewModel?.Diff;
        if (DiffHost.Content is DiffView { } shown && ReferenceEquals(shown.ViewModel, diff))
        {
            return;
        }

        DiffHost.Content = diff is not null && _font is not null ? new DiffView(diff, _font) : null;
    }

    private void OnAction(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string action } && _viewModel is { } vm)
        {
            _ = vm.RunAsync(action);
        }
    }

    private void OnMenuAction(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string action } && _viewModel is { } vm)
        {
            _ = vm.RunAsync(action);
        }
    }

    private void OnExplain(object? sender, RoutedEventArgs e) => _ = _viewModel?.ExplainAsync();
}
