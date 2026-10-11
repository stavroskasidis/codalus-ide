using Avalonia.Controls;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

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
