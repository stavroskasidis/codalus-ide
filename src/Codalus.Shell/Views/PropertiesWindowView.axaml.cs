using Avalonia.Controls;
using Codalus.Shell.Controls;

namespace Codalus.Shell.Views;

/// <summary>The Properties window (F4): the component selector or the title, its toolbar, and the property grid (#68).</summary>
public sealed partial class PropertiesWindowView : UserControl, IPaneView
{
    public PropertiesWindowView()
    {
        InitializeComponent();
    }

    /// <summary>The property grid's view (tests).</summary>
    public PropertyGridView Grid => GridView;

    public void FocusPrimary() => GridView.FocusPrimary();
}
