using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>The Settings UI. Text values are written when the field loses focus, on Enter, or when a path is browsed for.</summary>
public sealed partial class SettingsView : UserControl, IPaneView
{
    public SettingsView()
    {
        InitializeComponent();
        AddHandler(LostFocusEvent, (_, e) => Commit(e.Source), RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        AddHandler(PathBox.BrowsedEvent, (_, e) => Commit((e.Source as PathBox)?.TextBox), RoutingStrategies.Bubble);
    }

    public void FocusPrimary() => SearchBox.Focus();

    private static void Commit(object? source)
    {
        if (source is TextBox { DataContext: TextSettingViewModel setting })
        {
            setting.Commit();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.Source is TextBox { DataContext: TextSettingViewModel { IsMultiline: false } } box)
        {
            Commit(box);
            e.Handled = true;
        }
    }
}
