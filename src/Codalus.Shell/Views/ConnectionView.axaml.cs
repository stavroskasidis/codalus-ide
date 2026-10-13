using Avalonia.Controls;
using Codalus.Shell.Controls;

namespace Codalus.Shell.Views;

/// <summary>Add or edit a database connection (ADR 0037).</summary>
public sealed partial class ConnectionView : UserControl, IPaneView
{
    public ConnectionView() => InitializeComponent();

    public void FocusPrimary() => EngineBox.Focus();
}
