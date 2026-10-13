using Avalonia.Controls;
using Codalus.Shell.Controls;

namespace Codalus.Shell.Views;

/// <summary>Help → Check Environment (M37).</summary>
public sealed partial class EnvironmentView : UserControl, IPaneView
{
    public EnvironmentView() => InitializeComponent();

    public void FocusPrimary() => CheckButton.Focus();
}
