using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Codalus.Shell.Controls;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>A commit's tab: the selected file's diff (a <see cref="DiffView"/>, made again for each file) and the details pane.</summary>
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
        viewModel.PropertyChanged += OnViewModelChanged;
        ShowDiff();
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

    private void ShowDiff() =>
        DiffHost.Content = _viewModel?.Diff is { } diff && _font is not null ? new DiffView(diff, _font) : null;

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
