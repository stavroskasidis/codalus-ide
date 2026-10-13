using Avalonia;
using Avalonia.Controls;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

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
