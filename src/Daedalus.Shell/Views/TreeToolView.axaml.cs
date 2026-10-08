using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Daedalus.Shell.Controls;
using Daedalus.Shell.ViewModels;

namespace Daedalus.Shell.Views;

/// <summary>A plugin tree view. Clicking an item (or Enter) runs its command; expanding loads children.</summary>
public sealed partial class TreeToolView : UserControl, IPaneView
{
    public TreeToolView()
    {
        InitializeComponent();
        Tree.Tapped += OnTapped;
        Tree.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        Tree.GotFocus += (_, _) => (DataContext as TreeToolViewModel)?.RefreshActions();
    }

    public void FocusPrimary()
    {
        Tree.SelectedItem ??= (DataContext as TreeToolViewModel)?.Roots.FirstOrDefault();
        if (Tree.SelectedItem is not { } item || Tree.ContainerFromItem(item)?.Focus() != true)
        {
            Tree.Focus();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is TreeToolViewModel vm)
        {
            vm.RefreshActions();
            vm.EnsureLoaded();
        }
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        // The expander toggles; it doesn't run the item's command.
        if (e.Source is not Visual visual || visual.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null)
        {
            return;
        }

        if (visual.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is TreeNodeViewModel node)
        {
            (DataContext as TreeToolViewModel)?.Activate(node);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Tree.SelectedItem is TreeNodeViewModel node && DataContext is TreeToolViewModel vm)
        {
            vm.Activate(node);
            e.Handled = true;
        }
    }
}
