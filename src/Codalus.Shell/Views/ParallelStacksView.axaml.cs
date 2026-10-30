using Avalonia.Controls;

namespace Codalus.Shell.Views;

/// <summary>Parallel Stacks (M14b).</summary>
public sealed partial class ParallelStacksView : UserControl
{
    public ParallelStacksView()
    {
        InitializeComponent();
        ShownTracker.Track(this); // refreshed only while shown (M56)
    }
}
