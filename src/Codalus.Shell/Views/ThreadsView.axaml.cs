using Avalonia.Controls;
using Avalonia.Input;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>The Threads window: double-click or Enter switches to the thread.</summary>
public sealed partial class ThreadsView : UserControl, IPaneView
{
    public ThreadsView()
    {
        InitializeComponent();
        ShownTracker.Track(this); // refreshed only while shown (M56)
        ThreadMenu.Opening += OnMenuOpening;
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

    /// <summary>Freeze and Thaw are offered by what the debugger and the thread allow (the Codalus debugger freezes threads, M58).</summary>
    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var vm = DataContext as ThreadsViewModel;
        var row = List.SelectedItem as ThreadRow;
        FreezeItem.IsEnabled = vm?.CanFreeze == true && row is { IsFrozen: false };
        ThawItem.IsEnabled = vm?.CanFreeze == true && row is { IsFrozen: true };
        if (row is null)
        {
            e.Cancel = true;
        }
    }

    private void OnThreadAction(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string action } || DataContext is not ThreadsViewModel vm || List.SelectedItem is not ThreadRow row)
        {
            return;
        }

        _ = action switch
        {
            "freeze" => vm.FreezeAsync(row, frozen: true),
            "thaw" => vm.FreezeAsync(row, frozen: false),
            _ => vm.ActivateAsync(row),
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
