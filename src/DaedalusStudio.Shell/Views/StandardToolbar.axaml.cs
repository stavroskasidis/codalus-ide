using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>The standard toolbar (M29): see <see cref="ViewModels.ToolbarViewModel"/>.</summary>
public sealed partial class StandardToolbar : UserControl
{
    public StandardToolbar()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => UpdateTips();
    }

    /// <summary>
    /// Tooltips name the button and its shortcut on this OS (M34a, D25): each button's <c>Tag</c> is its command, whose
    /// key binding the view model reports (Undo and Redo use the editor's own keys).
    /// </summary>
    private void UpdateTips()
    {
        if (DataContext is not ToolbarViewModel { KeyLabel: { } keyLabel })
        {
            return;
        }

        foreach (var button in this.GetLogicalDescendants().OfType<Button>())
        {
            if (button.Tag is string command && AutomationProperties.GetName(button) is { Length: > 0 } name)
            {
                ToolTip.SetTip(button, keyLabel(command) is { } keys ? $"{name} ({keys})" : name);
            }
        }
    }
}
