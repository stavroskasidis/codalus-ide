using Avalonia.Controls;
using Avalonia.Input;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>The Threads window: double-click or Enter switches to the thread.</summary>
public sealed partial class ThreadsView : UserControl, IPaneView
{
    public ThreadsView()
    {
        InitializeComponent();
        List.DoubleTapped += (_, _) => Activate();
        List.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Activate();
                e.Handled = true;
            }
        };
    }

    private void Activate()
    {
        if (DataContext is ThreadsViewModel vm && List.SelectedItem is ThreadRow row)
        {
            _ = vm.ActivateAsync(row);
        }
    }

    public void FocusPrimary() => List.Focus();
}
