using Avalonia.Controls;
using DaedalusStudio.Shell.Controls;

namespace DaedalusStudio.Shell.Views;

public sealed partial class NotificationsView : UserControl, IPaneView
{
    public NotificationsView() => InitializeComponent();

    public void FocusPrimary() => List.Focus();
}
