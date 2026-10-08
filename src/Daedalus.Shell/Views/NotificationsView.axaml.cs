using Avalonia.Controls;
using Daedalus.Shell.Controls;

namespace Daedalus.Shell.Views;

public sealed partial class NotificationsView : UserControl, IPaneView
{
    public NotificationsView() => InitializeComponent();

    public void FocusPrimary() => List.Focus();
}
