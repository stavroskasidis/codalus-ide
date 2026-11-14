using Avalonia.Controls;
using Codalus.Shell.Controls;

namespace Codalus.Shell.Views;

/// <summary>The Modules window: the loaded modules with their symbol status, searchable by name or path.</summary>
public sealed partial class ModulesView : UserControl, IPaneView
{
    public ModulesView()
    {
        InitializeComponent();
        ShownTracker.Track(this); // refreshed only while shown (M56)

        // Load Symbols (M58): for a module without symbols, under a debugger that looks them up.
        ModuleMenu.Opening += (_, e) =>
        {
            var row = List.SelectedItem as ViewModels.ModuleRow;
            LoadSymbolsItem.IsEnabled = row is { CanLoadSymbols: true } && DataContext is ViewModels.ModulesViewModel { CanLoadSymbols: true };
            e.Cancel = row is null;
        };
    }

    private void OnLoadSymbols(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.ModulesViewModel model && List.SelectedItem is ViewModels.ModuleRow row)
        {
            _ = model.LoadSymbolsAsync(row);
        }
    }

    public void FocusPrimary() => Search.Focus();
}
