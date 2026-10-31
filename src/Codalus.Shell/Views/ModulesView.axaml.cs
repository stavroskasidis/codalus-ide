using Avalonia.Controls;
using Codalus.Shell.Controls;

namespace Codalus.Shell.Views;

/// <summary>The Modules window: the loaded modules with their symbol status, searchable by name or path.</summary>
public sealed partial class ModulesView : UserControl, IPaneView
{
    public ModulesView()
    {
        InitializeComponent();
        ShownTracker.Track(this); // refreshed only while shown (M56)
    }

    public void FocusPrimary() => Search.Focus();
}
