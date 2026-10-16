using Avalonia.Controls;
using Codalus.Shell.Controls;

namespace Codalus.Shell.Views;

/// <summary>The EditorConfig settings page: see <see cref="EditorConfigDocument"/>.</summary>
public sealed partial class EditorConfigView : UserControl, IPaneView
{
    public EditorConfigView() => InitializeComponent();

    public void FocusPrimary() => List.Focus();
}
