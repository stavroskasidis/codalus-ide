using Avalonia.Controls;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>The Entity Framework window (M27a, ADR 0036): double-click a migration to update the database to it.</summary>
public sealed partial class EntityFrameworkView : UserControl, IPaneView
{
    public EntityFrameworkView()
    {
        InitializeComponent();
        List.DoubleTapped += (_, _) => (DataContext as EntityFrameworkViewModel)?.UpdateToCommand.Execute(null);
    }

    public void FocusPrimary() => ContextBox.Focus();
}
