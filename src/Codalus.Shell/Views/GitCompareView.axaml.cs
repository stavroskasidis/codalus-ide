using Avalonia.Controls;
using Avalonia.Input;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>The Compare document (D38's C1-C3): Enter or double-click opens a file's diff; Refresh compares again.</summary>
public sealed partial class GitCompareView : UserControl
{
    public GitCompareView()
    {
        InitializeComponent();
        Tree.DoubleTapped += (_, _) => Open((Tree.SelectedItem as GitChangeNode)?.Row);
        List.DoubleTapped += (_, _) => Open(List.SelectedItem as GitFileRow);
        Tree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Tree.SelectedItem as GitChangeNode)?.Row is { } row)
            {
                Open(row);
                e.Handled = true;
            }
        };
        List.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && List.SelectedItem is GitFileRow row)
            {
                Open(row);
                e.Handled = true;
            }
        };
        RefreshButton.Click += (_, _) => _ = (DataContext as GitCompareViewModel)?.LoadAsync();
        HistoryButton.Click += (_, _) => _ = (DataContext as GitCompareViewModel)?.ShowHistory?.Invoke();
    }

    private void Open(GitFileRow? row)
    {
        if (row is not null && DataContext is GitCompareViewModel vm)
        {
            _ = vm.OpenAsync(row);
        }
    }
}
