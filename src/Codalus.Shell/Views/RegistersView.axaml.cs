using Avalonia.Controls;
using Codalus.Shell.Controls;

namespace Codalus.Shell.Views;

/// <summary>The Registers window (M58): the selected frame's registers, those that changed since the last stop marked.</summary>
public sealed partial class RegistersView : UserControl, IPaneView
{
    public RegistersView()
    {
        InitializeComponent();
        ShownTracker.Track(this); // read only while shown, as the other debugger windows (M56)
    }

    public void FocusPrimary() => List.Focus();
}
