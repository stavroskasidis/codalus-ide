using Avalonia.Controls;
using Avalonia.Interactivity;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>The Processes window (M14b).</summary>
public sealed partial class ProcessesView : UserControl
{
    public ProcessesView()
    {
        InitializeComponent();
        ShownTracker.Track(this); // refreshed only while shown (M56)
        ProcessMenu.Opening += OnMenuOpening;
    }

    /// <summary>Terminate Process and Detach Process (#547) apply to the selected row's program, when it still runs.</summary>
    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (List.SelectedItem is not ProcessRow row || !row.CanTerminate)
        {
            e.Cancel = true;
            return;
        }

        TerminateItem.IsEnabled = row.CanTerminate;
        DetachItem.IsEnabled = row.CanDetach;
    }

    private void OnProcessAction(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string action } && List.SelectedItem is ProcessRow row && ViewModel is { } vm)
        {
            _ = vm.EndAsync(row, detach: action == "detach");
        }
    }

    private ProcessesViewModel? ViewModel => DataContext as ProcessesViewModel;

    private void OnAttach(object? sender, RoutedEventArgs e) => ViewModel?.Run("debug.attach");

    private void OnDetach(object? sender, RoutedEventArgs e) => ViewModel?.Run("debug.detachAll");

    private void OnTerminate(object? sender, RoutedEventArgs e) => ViewModel?.Run("debug.stop");
}
