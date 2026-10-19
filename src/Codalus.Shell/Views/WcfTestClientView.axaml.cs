using Avalonia.Controls;
using Avalonia.Input;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Views;

/// <summary>
/// The WCF Test Client (M42; the control set's design since M34h): a service's operations from its metadata, a request
/// form, and the response. Running Services lists the CoreWCF services found running; Esc cancels a call (F5, which
/// invokes, is a key binding: <c>wcf.invoke</c>).
/// </summary>
public sealed partial class WcfTestClientView : UserControl, IPaneView
{
    public WcfTestClientView()
    {
        InitializeComponent();
        AddressBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is WcfTestClientViewModel { IsBusy: false } vm)
            {
                _ = vm.LoadAsync();
                e.Handled = true;
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is WcfTestClientViewModel { IsBusy: true } vm)
            {
                vm.CancelCommand.Execute(null);
                e.Handled = true;
            }
        };
        CopyResponse.Click += async (_, _) =>
        {
            if (DataContext is WcfTestClientViewModel { HasResponse: true } vm && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, vm.ResponseText).ConfigureAwait(true);
            }
        };
        if (RunningServices.Flyout is MenuFlyout flyout)
        {
            flyout.Opening += async (_, _) => await FillRunningServicesAsync(flyout).ConfigureAwait(true);
        }
    }

    /// <summary>Running Services: looks for the CoreWCF services that run and lists them; choosing one loads its metadata.</summary>
    private async Task FillRunningServicesAsync(MenuFlyout flyout)
    {
        if (DataContext is not WcfTestClientViewModel vm)
        {
            return;
        }

        flyout.Items.Clear();
        flyout.Items.Add(new MenuItem { Header = Localizer.Get("msg.wcf.discovering", "Looking for running CoreWCF services…"), IsEnabled = false });
        await vm.DiscoverAsync(selectFirst: false).ConfigureAwait(true); // only list them: the service shown and its form stay
        flyout.Items.Clear();
        if (vm.Discovered.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = vm.Status ?? Localizer.Get("msg.wcf.noneFound", "No running CoreWCF service was found."), IsEnabled = false });
            return;
        }

        foreach (var service in vm.Discovered)
        {
            var item = new MenuItem { Header = service.Label, Icon = new Icon { Kind = "radar" } };
            item.Click += (_, _) => _ = vm.LoadDiscoveredAsync(service);
            flyout.Items.Add(item);
        }
    }

    public void FocusPrimary() => AddressBox.Focus();
}
