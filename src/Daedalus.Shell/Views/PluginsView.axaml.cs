using Avalonia.Controls;
using Daedalus.Shell.Controls;

namespace Daedalus.Shell.Views;

public sealed partial class PluginsView : UserControl, IPaneView
{
    public PluginsView() => InitializeComponent();

    public void FocusPrimary() => List.Focus();
}
