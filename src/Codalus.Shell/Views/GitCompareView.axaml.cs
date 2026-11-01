using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// The Compare document (D38's C1-C3): Enter or double-click opens a file's diff; Refresh compares again. The tree's rows
/// expand and collapse as Git Changes' (<see cref="TreeRows"/>, M57).
/// </summary>
public sealed partial class GitCompareView : UserControl
{
    public GitCompareView()
    {
        InitializeComponent();
        // A double-click off the rows opens the selected file's diff, as Compare's list does (and as before M57).
        TreeRows.Attach(Tree, () => (DataContext as GitCompareViewModel)?.Changes, row => Open((row as GitChangeRow)?.Node.Row), offRowsActivatesSelected: true);
        List.DoubleTapped += (_, e) =>
        {
            if (Open(List.SelectedItem as GitFileRow))
            {
                e.Handled = true;
            }
        };

        // In the tunnel: a list takes Enter itself, to select the item, before a handler added to it hears it.
        List.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && Open(List.SelectedItem as GitFileRow))
            {
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        RefreshButton.Click += (_, _) => _ = (DataContext as GitCompareViewModel)?.LoadAsync();
        HistoryButton.Click += (_, _) => _ = (DataContext as GitCompareViewModel)?.ShowHistory?.Invoke();
    }

    private bool Open(GitFileRow? row)
    {
        if (row is null || DataContext is not GitCompareViewModel vm)
        {
            return false;
        }

        _ = vm.OpenAsync(row);
        return true;
    }
}
