using Avalonia.Controls;
using DaedalusStudio.Shell.Controls;

namespace DaedalusStudio.Shell.Views;

/// <summary>Service references (M15c): a project's OpenAPI and gRPC references, and the form that adds one.</summary>
public sealed partial class ServiceReferencesView : UserControl, IPaneView
{
    public ServiceReferencesView() => InitializeComponent();

    public void FocusPrimary() => KindBox.Focus();
}
