using Avalonia.Controls;
using Avalonia.Input;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>Class View and the Object Browser: see <see cref="SymbolBrowserViewModel"/>.</summary>
public sealed partial class SymbolBrowserView : UserControl, IPaneView
{
    public SymbolBrowserView()
    {
        InitializeComponent();
        Tree.DoubleTapped += (_, _) => Open();
        Tree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Open();
                e.Handled = true;
            }
        };
    }

    private void Open()
    {
        if (DataContext is SymbolBrowserViewModel vm)
        {
            _ = vm.OpenSelectedAsync();
        }
    }

    public void FocusPrimary() => Tree.Focus();
}
