using Avalonia.Controls;
using Avalonia.Input;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>The Endpoints Explorer: double-click or Enter opens the handler; the context menu generates a request.</summary>
public sealed partial class EndpointsExplorerView : UserControl, IPaneView
{
    public EndpointsExplorerView()
    {
        InitializeComponent();
        EndpointsExplorerViewModel? Vm() => DataContext as EndpointsExplorerViewModel;
        Refresh.Click += (_, _) => _ = Vm()?.RefreshAsync();
        List.DoubleTapped += (_, _) => Open();
        List.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Open();
                e.Handled = true;
            }
        };
        List.ContextRequested += (_, e) =>
        {
            if (Vm() is not { } vm || List.SelectedItem is not EndpointRow { IsHeader: false } row)
            {
                return;
            }

            new ContextMenu
            {
                ItemsSource = new[]
                {
                    Controls.MenuEntry.Create("Go to Handler", "go-to", click: () => _ = vm.OpenAsync(row)),
                    Controls.MenuEntry.Create("Generate Request", "send", click: () => _ = vm.GenerateRequestAsync(row)),
                },
            }.Open(List);
            e.Handled = true;
        };
    }

    private void Open()
    {
        if (DataContext is EndpointsExplorerViewModel vm && List.SelectedItem is EndpointRow row)
        {
            _ = vm.OpenAsync(row);
        }
    }

    public void FocusPrimary() => Search.Focus();
}
