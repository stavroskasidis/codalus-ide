using Avalonia.Controls;
using DaedalusStudio.Shell.Controls;

namespace DaedalusStudio.Shell.Views;

public sealed partial class PluginsView : UserControl, IPaneView
{
    public PluginsView() => InitializeComponent();

    public void FocusPrimary() => List.Focus();
}
