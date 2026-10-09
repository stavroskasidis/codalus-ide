using Avalonia.Controls;
using Avalonia.Input;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>The Call Stack window: double-click or Enter selects a frame and opens its source.</summary>
public sealed partial class CallStackView : UserControl, IPaneView
{
    public CallStackView()
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
        if (DataContext is CallStackViewModel vm && List.SelectedItem is FrameRow row)
        {
            _ = vm.ActivateAsync(row);
        }
    }

    public void FocusPrimary() => List.Focus();
}
