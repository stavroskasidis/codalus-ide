using Avalonia.Controls;
using Avalonia.Interactivity;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>The Processes window (M14b).</summary>
public sealed partial class ProcessesView : UserControl
{
    public ProcessesView() => InitializeComponent();

    private ProcessesViewModel? ViewModel => DataContext as ProcessesViewModel;

    private void OnAttach(object? sender, RoutedEventArgs e) => ViewModel?.Run("debug.attach");

    private void OnDetach(object? sender, RoutedEventArgs e) => ViewModel?.Run("debug.detachAll");

    private void OnTerminate(object? sender, RoutedEventArgs e) => ViewModel?.Run("debug.stop");
}
