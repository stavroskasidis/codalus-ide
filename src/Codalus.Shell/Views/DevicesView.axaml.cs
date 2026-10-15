using Avalonia.Controls;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>The Devices window (M20a): devices, emulators and simulators for .NET MAUI apps.</summary>
public sealed partial class DevicesView : UserControl, IPaneView
{
    public DevicesView()
    {
        InitializeComponent();
        DevicesViewModel? Vm() => DataContext as DevicesViewModel;
        RefreshButton.Click += (_, _) => _ = Vm()?.RefreshAsync();
        StartButton.Click += (_, _) => _ = Vm()?.StartAsync();
        UseButton.Click += (_, _) => Vm()?.Use();
        InstallButton.Click += (_, _) => _ = Vm()?.InstallAndroidAsync();
        DeviceList.DoubleTapped += (_, _) =>
        {
            if (Vm() is { } vm)
            {
                if (vm.CanStart)
                {
                    _ = vm.StartAsync();
                }
                else
                {
                    vm.Use();
                }
            }
        };
    }

    public void FocusPrimary() => DeviceList.Focus();
}
