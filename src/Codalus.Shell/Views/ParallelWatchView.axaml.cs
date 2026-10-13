using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>Parallel Watch (M14b): Enter or Evaluate runs the expression on every thread.</summary>
public sealed partial class ParallelWatchView : UserControl
{
    public ParallelWatchView() => InitializeComponent();

    private void OnEvaluate(object? sender, RoutedEventArgs e) => _ = (DataContext as ParallelWatchViewModel)?.RefreshAsync();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = (DataContext as ParallelWatchViewModel)?.RefreshAsync();
        }
    }
}
