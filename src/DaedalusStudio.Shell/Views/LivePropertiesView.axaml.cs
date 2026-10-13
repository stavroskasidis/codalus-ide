using Avalonia.Controls;
using DaedalusStudio.Shell.Controls;

namespace DaedalusStudio.Shell.Views;

/// <summary>The Live Property Explorer (M19b): edit a value and leave the cell to set it in the running app.</summary>
public sealed partial class LivePropertiesView : UserControl, IPaneView
{
    public LivePropertiesView() => InitializeComponent();

    public void FocusPrimary() => FilterBox.Focus();
}
