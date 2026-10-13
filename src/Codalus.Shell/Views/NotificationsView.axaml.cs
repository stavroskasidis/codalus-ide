using Avalonia.Controls;
using Codalus.Shell.Controls;

namespace Codalus.Shell.Views;

public sealed partial class NotificationsView : UserControl, IPaneView
{
    public NotificationsView() => InitializeComponent();

    public void FocusPrimary() => List.Focus();
}
