using Avalonia.Controls;
using Avalonia.Input;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>Service references (M15c): a project's OpenAPI and gRPC references, and the form that adds one.</summary>
public sealed partial class ServiceReferencesView : UserControl, IPaneView
{
    public ServiceReferencesView()
    {
        InitializeComponent();

        // Running Services (M34h, Discover before): opening it searches once; picking a service fills the document's address.
        if (RunningServices.Flyout is { } flyout)
        {
            flyout.Opened += (_, _) =>
            {
                if (DataContext is ServiceReferencesViewModel { HasDiscovered: false, IsBusy: false } vm)
                {
                    vm.DiscoverCommand.Execute(null);
                }
            };
        }

        DiscoveredList.Tapped += (_, _) => RunningServices.Flyout?.Hide();
        DiscoveredList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                RunningServices.Flyout?.Hide();
                e.Handled = true;
            }
        };
    }

    public void FocusPrimary() => KindBox.Focus();
}
