using DaedalusStudio.Shell.Controls;
using Avalonia.Controls;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>The Worktrees window (ADR 0031): double-click opens a worktree's solution.</summary>
public sealed partial class WorktreesView : UserControl, IPaneView
{
    public WorktreesView()
    {
        InitializeComponent();
        List.DoubleTapped += (_, _) => (DataContext as WorktreesViewModel)?.OpenCommand.Execute(null);
    }

    public void FocusPrimary() => List.Focus();
}
