using Avalonia.Controls;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>The Ports window (M21): forwarded and listening ports of the remote machine.</summary>
public sealed partial class RemotePortsView : UserControl, IPaneView
{
    public RemotePortsView()
    {
        InitializeComponent();
        RemotePortsViewModel? Vm() => DataContext as RemotePortsViewModel;
        Forward.Click += (_, _) => _ = Vm()?.ForwardAsync();
        Stop.Click += (_, _) => Vm()?.Stop();
        Browse.Click += (_, _) => Vm()?.OpenInBrowser();
        PortList.DoubleTapped += (_, _) => Vm()?.OpenInBrowser();
    }

    public void FocusPrimary() => PortList.Focus();
}
