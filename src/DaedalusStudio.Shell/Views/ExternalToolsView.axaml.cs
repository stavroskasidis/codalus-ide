using Avalonia.Controls;
using DaedalusStudio.Shell.Controls;

namespace DaedalusStudio.Shell.Views;

public sealed partial class ExternalToolsView : UserControl, IPaneView
{
    public ExternalToolsView() => InitializeComponent();

    public void FocusPrimary() => AddButton.Focus();
}
