using Avalonia.Controls;
using Codalus.Shell.Controls;

namespace Codalus.Shell.Views;

/// <summary>The WCF Test Client (M42): a service's operations from its metadata, a request form, and the response.</summary>
public sealed partial class WcfTestClientView : UserControl, IPaneView
{
    public WcfTestClientView() => InitializeComponent();

    public void FocusPrimary() => AddressBox.Focus();
}
