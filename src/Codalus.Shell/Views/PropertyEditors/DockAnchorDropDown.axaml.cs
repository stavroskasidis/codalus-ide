using Avalonia.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views.PropertyEditors;

/// <summary>The Dock and Anchor pickers: a click on an edge commits (Dock) or toggles it (Anchor).</summary>
public sealed partial class DockAnchorDropDown : UserControl, IPropertyDropDown
{
    private Action? _close;

    public DockAnchorDropDown()
    {
        InitializeComponent();
        AddHandler(Button.ClickEvent, (_, _) =>
        {
            if (DataContext is PropertyRowViewModel { IsDock: true })
            {
                _close?.Invoke();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    void IPropertyDropDown.Open(PropertyRowViewModel row, PropertyGridViewModel grid, Action close)
    {
        _close = close;
        DataContext = row;
    }

    void IPropertyDropDown.Closed()
    {
    }

    void IPropertyDropDown.FocusFirst() => Focus();
}
