using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// The Memory window (M58): the program's memory from an address or an expression's object, a page at a time, and
/// bytes to write there.
/// </summary>
public sealed partial class MemoryView : UserControl, IPaneView
{
    public MemoryView()
    {
        InitializeComponent();
        ShownTracker.Track(this); // read only while shown, as the other debugger windows (M56)
    }

    private MemoryViewModel? Model => DataContext as MemoryViewModel;

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = Model?.GoAsync();
            e.Handled = true;
        }
    }

    private void OnBytesKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = Model?.WriteAsync();
            e.Handled = true;
        }
    }

    private void OnGo(object? sender, RoutedEventArgs e) => _ = Model?.GoAsync();

    private void OnPrevious(object? sender, RoutedEventArgs e) => _ = Model?.PreviousPageAsync();

    private void OnNext(object? sender, RoutedEventArgs e) => _ = Model?.NextPageAsync();

    private void OnWrite(object? sender, RoutedEventArgs e) => _ = Model?.WriteAsync();

    public void FocusPrimary() => Address.Focus();
}
