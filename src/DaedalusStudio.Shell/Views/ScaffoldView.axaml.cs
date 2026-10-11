using Avalonia.Controls;
using DaedalusStudio.Shell.Controls;

namespace DaedalusStudio.Shell.Views;

/// <summary>Add New Scaffolded Item (M15c): the scaffolders by category and a form generated from the chosen one's parameters.</summary>
public sealed partial class ScaffoldView : UserControl, IPaneView
{
    public ScaffoldView() => InitializeComponent();

    public void FocusPrimary() => AddButton.Focus();
}
