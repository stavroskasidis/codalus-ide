using Avalonia.Controls;
using Codalus.Shell.Controls;

namespace Codalus.Shell.Views;

/// <summary>Service references (M15c): a project's OpenAPI and gRPC references, and the form that adds one.</summary>
public sealed partial class ServiceReferencesView : UserControl, IPaneView
{
    public ServiceReferencesView() => InitializeComponent();

    public void FocusPrimary() => KindBox.Focus();
}
