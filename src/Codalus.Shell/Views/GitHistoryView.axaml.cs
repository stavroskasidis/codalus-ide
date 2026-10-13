using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>The history window: Enter or double-click on a changed file opens its diff; scrolling near the end loads more.</summary>
public sealed partial class GitHistoryView : UserControl, IPaneView
{
    public GitHistoryView()
    {
        InitializeComponent();
        FileList.DoubleTapped += (_, _) => OpenFile();
        FileList.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                OpenFile();
                e.Handled = true;
            }
        }, RoutingStrategies.Bubble);
        AuthorBox.AddHandler(KeyDownEvent, OnFilterKey, RoutingStrategies.Tunnel);
        MessageBox.AddHandler(KeyDownEvent, OnFilterKey, RoutingStrategies.Tunnel);
        List.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) =>
        {
            if (e.Source is ScrollViewer viewer && viewer.Offset.Y + viewer.Viewport.Height >= viewer.Extent.Height - 400 && ViewModel is { } vm)
            {
                _ = vm.LoadMoreAsync();
            }
        });
    }

    private GitHistoryViewModel? ViewModel => DataContext as GitHistoryViewModel;

    public void FocusPrimary() => List.Focus();

    private void OnFilterKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel is { } vm)
        {
            _ = vm.ReloadAsync();
            e.Handled = true;
        }
    }

    private void OnFilter(object? sender, RoutedEventArgs e) => _ = ViewModel?.ReloadAsync();

    private void OnCommitAction(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string action } && ViewModel is { } vm)
        {
            _ = vm.RunAsync(action);
        }
    }

    private void OpenFile()
    {
        if (FileList.SelectedItem is GitCommitFileRow file && ViewModel is { } vm)
        {
            _ = vm.OpenFileAsync(file);
        }
    }
}
