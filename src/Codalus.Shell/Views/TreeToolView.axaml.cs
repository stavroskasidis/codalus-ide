using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>A plugin tree view. Clicking an item (or Enter) runs its command; expanding loads children.</summary>
public sealed partial class TreeToolView : UserControl, IPaneView
{
    public TreeToolView()
    {
        InitializeComponent();
        Tree.Tapped += OnTapped;
        Tree.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        Tree.GotFocus += (_, _) => (DataContext as TreeToolViewModel)?.RefreshActions();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is TreeToolViewModel vm)
            {
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(TreeToolViewModel.Actions))
                    {
                        FillActions(vm.Actions);
                    }
                };
                FillActions(vm.Actions);
            }
        };
    }

    /// <summary>The title commands as toolbar buttons (the panel overflows what doesn't fit into its menu).</summary>
    private void FillActions(IReadOnlyList<ToolbarActionViewModel> actions)
    {
        foreach (var old in ActionBar.Children.OfType<Button>().Where(b => !b.Classes.Contains("overflow")).ToList())
        {
            ActionBar.Children.Remove(old);
        }

        foreach (var action in actions)
        {
            // A command with an icon is an icon button, its title the tooltip and the name (M34h); a plugin's own command
            // without one keeps its title.
            var button = new Button { Content = action.Title, Command = action.Command, FontSize = 12 };
            button.Classes.Add("toolbar");
            if (action.Icon is { } icon)
            {
                button.Classes.Add("icon");
                Controls.ButtonIcon.SetKind(button, icon);
            }

            ToolTip.SetTip(button, action.Tooltip);
            Avalonia.Automation.AutomationProperties.SetName(button, action.Title);
            ActionBar.Children.Add(button);
        }
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
