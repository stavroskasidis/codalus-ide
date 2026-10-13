using Codalus.Shell.Controls;
using Avalonia.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>The Work Items window (ADR 0031): double-click opens one in the browser.</summary>
public sealed partial class WorkItemsView : UserControl, IPaneView
{
    public WorkItemsView()
    {
        InitializeComponent();
        List.DoubleTapped += (_, _) => (DataContext as WorkItemsViewModel)?.OpenCommand.Execute(null);
    }

    public void FocusPrimary() => List.Focus();
}
