using Avalonia.Controls;
using Avalonia.Input;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>The Exception Settings window: Enter in the box adds a type; Delete removes the selected one from the list.</summary>
public sealed partial class ExceptionSettingsView : UserControl, IPaneView
{
    public ExceptionSettingsView()
    {
        InitializeComponent();
        ExceptionSettingsViewModel? Vm() => DataContext as ExceptionSettingsViewModel;
        CheckAll.Click += (_, _) => Vm()?.SetAll(true);
        UncheckAll.Click += (_, _) => Vm()?.SetAll(false);
        Reset.Click += (_, _) => Vm()?.Reset();
        Add.Click += (_, _) => Vm()?.Add();
        NewType.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Vm()?.Add();
                e.Handled = true;
            }
        };
        List.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && List.SelectedItem is ExceptionTypeRow row)
            {
                Vm()?.Remove(row);
                e.Handled = true;
            }
        };
    }

    public void FocusPrimary() => Search.Focus();
}
