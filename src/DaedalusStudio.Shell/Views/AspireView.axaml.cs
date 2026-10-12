using Avalonia.Controls;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>The Aspire window: the toolbar acts on the selected resource; a double-click opens its endpoint.</summary>
public sealed partial class AspireView : UserControl, IPaneView
{
    public AspireView()
    {
        InitializeComponent();
        Controls.FollowTail.Attach(LogText); // the console output follows new lines (M34a)
        AspireViewModel? Vm() => DataContext as AspireViewModel;
        Dashboard.Click += (_, _) => Vm()?.OpenDashboard();
        Start.Click += (_, _) => _ = Vm()?.StartAsync();
        Stop.Click += (_, _) => _ = Vm()?.StopAsync();
        Restart.Click += (_, _) => _ = Vm()?.RestartAsync();
        Browse.Click += (_, _) => Vm()?.OpenEndpoint();
        ResourceList.DoubleTapped += (_, _) => Vm()?.OpenEndpoint();
    }

    public void FocusPrimary() => ResourceList.Focus();
}
