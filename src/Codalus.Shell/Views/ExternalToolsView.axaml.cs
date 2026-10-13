using Avalonia.Controls;
using Codalus.Shell.Controls;

namespace Codalus.Shell.Views;

public sealed partial class ExternalToolsView : UserControl, IPaneView
{
    public ExternalToolsView() => InitializeComponent();

    public void FocusPrimary() => AddButton.Focus();
}
