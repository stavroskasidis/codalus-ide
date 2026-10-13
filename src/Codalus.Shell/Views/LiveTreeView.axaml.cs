using Avalonia.Controls;
using Avalonia.Input;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>The Live Visual Tree (M19b): double-click or Enter goes to the element's XAML.</summary>
public sealed partial class LiveTreeView : UserControl, IPaneView
{
    public LiveTreeView()
    {
        InitializeComponent();
        Tree.DoubleTapped += (_, _) => GoToXaml();
        Tree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                GoToXaml();
                e.Handled = true;
            }
        };
    }

    private void GoToXaml()
    {
        if (DataContext is LiveTreeViewModel vm && vm.GoToXamlCommand.CanExecute(null))
        {
            vm.GoToXamlCommand.Execute(null);
        }
    }

    public void FocusPrimary() => Tree.Focus();
}
