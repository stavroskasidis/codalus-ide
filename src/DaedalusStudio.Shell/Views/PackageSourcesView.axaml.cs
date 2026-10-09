using Avalonia;
using Avalonia.Controls;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>Package Sources: loads the sources the first time it shows.</summary>
public sealed partial class PackageSourcesView : UserControl, IPaneView
{
    private bool _loaded;

    public PackageSourcesView() => InitializeComponent();

    public void FocusPrimary() => Focus();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_loaded && DataContext is PackageSourcesViewModel vm)
        {
            _loaded = true;
            _ = vm.LoadAsync();
        }
    }
}
