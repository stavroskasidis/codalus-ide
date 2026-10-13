using Avalonia.Controls;
using Codalus.Shell.Controls;

namespace Codalus.Shell.Views;

/// <summary>Tools → Customize (M23b): menus, context menus and toolbars.</summary>
public sealed partial class CustomizeView : UserControl, IPaneView
{
    public CustomizeView() => InitializeComponent();

    public void FocusPrimary() => TargetBox.Focus();
}
