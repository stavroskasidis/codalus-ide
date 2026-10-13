using Avalonia.Controls;
using Codalus.Shell.Controls;

namespace Codalus.Shell.Views;

public sealed partial class PluginsView : UserControl, IPaneView
{
    public PluginsView() => InitializeComponent();

    public void FocusPrimary() => List.Focus();
}
