using Avalonia.Controls;
using Avalonia.Input;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>Tools → .NET SDKs (ADR 0113): the lists, and the selection's details.</summary>
public sealed partial class DotnetSdksView : UserControl
{
    public DotnetSdksView()
    {
        InitializeComponent();
        List.AddHandler(KeyDownEvent, OnListKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>Right opens a folder's runtimes and Left folds them, as a tree's node; Enter does either.</summary>
    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not DotnetSdksViewModel model || model.Selected is not { IsGroup: true } group)
        {
            return;
        }

        if (e.Key == Key.Enter || e.Key == Key.Right && !group.IsExpanded || e.Key == Key.Left && group.IsExpanded)
        {
            model.ToggleGroup(group);
            e.Handled = true;
        }
    }
}
