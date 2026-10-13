using Avalonia.Controls;
using DaedalusStudio.Shell.Controls;

namespace DaedalusStudio.Shell.Views;

/// <summary>Tools → Customize (M23b): menus, context menus and toolbars.</summary>
public sealed partial class CustomizeView : UserControl, IPaneView
{
    public CustomizeView() => InitializeComponent();

    public void FocusPrimary() => TargetBox.Focus();
}
