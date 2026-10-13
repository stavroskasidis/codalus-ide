using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>A dump session (M14b): threads and stacks, and the heap with its objects.</summary>
public sealed partial class DumpView : UserControl
{
    public DumpView() => InitializeComponent();

    private DumpViewModel? ViewModel => DataContext as DumpViewModel;

    // Raised while the XAML is still loading, before the named fields are set: use the sender.
    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is TabControl { SelectedIndex: 1 } && ViewModel is { } viewModel)
        {
            _ = viewModel.LoadHeapAsync();
        }
    }

    private void OnFrameDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FrameList.SelectedItem is DumpFrameRow frame && ViewModel is { } viewModel)
        {
            _ = viewModel.OpenFrameAsync(frame);
        }
    }

    private void OnInstanceSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: string address } && ViewModel is { } viewModel)
        {
            _ = viewModel.InspectAsync(address);
        }
    }

    private void OnFieldDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FieldList.SelectedItem is DumpFieldRow { IsObject: true } field && ViewModel is { } viewModel)
        {
            _ = viewModel.InspectAsync(field.Field.Address);
        }
    }

    private void OnBack(object? sender, RoutedEventArgs e) => _ = ViewModel?.BackAsync();
}
