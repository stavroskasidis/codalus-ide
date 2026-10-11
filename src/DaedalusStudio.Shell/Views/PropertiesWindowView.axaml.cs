using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>The Properties window (F4): the row that has the focus (or was clicked) shows its description.</summary>
public sealed partial class PropertiesWindowView : UserControl, IPaneView
{
    public PropertiesWindowView()
    {
        InitializeComponent();
        Grid.AddHandler(GotFocusEvent, (_, e) => Select(e.Source), RoutingStrategies.Bubble);
        Grid.AddHandler(PointerPressedEvent, (_, e) => Select(e.Source), RoutingStrategies.Tunnel);
    }

    private void Select(object? source)
    {
        if (DataContext is PropertiesWindowViewModel vm && (source as Avalonia.Visual)?.FindAncestorOfType<Border>(includeSelf: true) is { DataContext: PropertyRowViewModel row })
        {
            vm.SelectedRow = row;
        }
    }

    public void FocusPrimary()
    {
        if (Grid.GetVisualDescendants().OfType<InputElement>().FirstOrDefault(e => e.Focusable && e.IsEffectivelyVisible) is { } first)
        {
            first.Focus();
        }
        else
        {
            Focus();
        }
    }
}
