using Avalonia;
using Avalonia.Controls;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>.NET Tools: lists the tools the first time it shows.</summary>
public sealed partial class DotnetToolsView : UserControl, IPaneView
{
    private bool _loaded;

    public DotnetToolsView() => InitializeComponent();

    public void FocusPrimary() => Focus();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_loaded && DataContext is DotnetToolsViewModel vm)
        {
            _loaded = true;
            _ = vm.LoadAsync();
        }
    }
}
