using Avalonia.Controls;
using Avalonia.Input;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

public sealed partial class BatchBuildView : LoadingView
{
    public BatchBuildView() => InitializeComponent();

    public override void FocusPrimary() => BuildButton.Focus();
}

public sealed partial class BuildTimingsView : LoadingView
{
    public BuildTimingsView() => InitializeComponent();

    public override void FocusPrimary() => RefreshButton.Focus();
}

public sealed partial class BinaryLogView : LoadingView
{
    public BinaryLogView()
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

    public override void FocusPrimary() => SearchBox.Focus();

    /// <summary>A search result's node, once revealed, is scrolled into view.</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is BinaryLogViewModel viewModel)
        {
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BinaryLogViewModel.Selected) && viewModel.Selected is { } node)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => Tree.TreeContainerFromItem(node)?.BringIntoView(), Avalonia.Threading.DispatcherPriority.Background);
                }
            };
        }
    }

    private void Open()
    {
        if (DataContext is BinaryLogViewModel viewModel)
        {
            _ = viewModel.OpenSelectedAsync();
        }
    }
}
