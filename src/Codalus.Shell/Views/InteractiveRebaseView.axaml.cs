using Avalonia.Controls;
using Codalus.Shell.Controls;

namespace Codalus.Shell.Views;

/// <summary>The interactive rebase editor: see <see cref="ViewModels.InteractiveRebaseViewModel"/>.</summary>
public sealed partial class InteractiveRebaseView : UserControl, IPaneView
{
    public InteractiveRebaseView() => InitializeComponent();

    public void FocusPrimary() => Focus();
}
