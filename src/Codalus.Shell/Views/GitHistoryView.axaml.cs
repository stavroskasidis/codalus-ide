using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// A history: a double-click or Enter on a commit opens its details (a tab), as View Commit Details on its menu does;
/// a section's chevron collapses and expands it; scrolling near the end loads more;
/// Enter in Filter History filters at once.
/// </summary>
public sealed partial class GitHistoryView : UserControl, IPaneView
{
    public GitHistoryView()
    {
        InitializeComponent();
        List.DoubleTapped += (_, e) =>
        {
            if (e.Source is Avalonia.Visual visual && Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Button>(visual, includeSelf: true) is null && ViewModel is { Selected: not null } vm)
            {
                _ = vm.OpenSelectedAsync();
                e.Handled = true;
            }
        };
        List.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        FilterBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && ViewModel is { } vm)
            {
                _ = vm.ReloadAsync();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        List.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) =>
        {
            if (e.Source is ScrollViewer viewer && viewer.Offset.Y + viewer.Viewport.Height >= viewer.Extent.Height - 400 && ViewModel is { } vm)
            {
                _ = vm.LoadMoreAsync();
            }
        });
        CommitMenu.Opening += (_, e) => e.Cancel = ViewModel?.Selected is null;
    }

    private GitHistoryViewModel? ViewModel => DataContext as GitHistoryViewModel;

    public void FocusPrimary() => List.Focus();

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when vm.Selected is not null:
                _ = vm.OpenSelectedAsync();
                e.Handled = true;
                break;
        }
    }

    private void OnRefresh(object? sender, RoutedEventArgs e) => _ = ViewModel?.ReloadAsync();

    private void OnToggleSection(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: GitHistorySection section } && ViewModel is { } vm)
        {
            vm.Toggle(section);
            e.Handled = true;
        }
    }

    private void OnLink(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: GitHistoryLink link } && ViewModel is { } vm)
        {
            _ = vm.RunLinkAsync(link);
            e.Handled = true;
        }
    }

    private void OnCommitAction(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string action } && ViewModel is { } vm)
        {
            _ = vm.RunAsync(action);
        }
    }
}
