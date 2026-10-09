using Avalonia;
using Avalonia.Controls;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>Project properties: loads the first page the first time it shows.</summary>
public sealed partial class ProjectPropertiesView : UserControl, IPaneView
{
    private bool _loaded;

    public ProjectPropertiesView() => InitializeComponent();

    public void FocusPrimary() => PageList.Focus();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_loaded && DataContext is ProjectPropertiesViewModel vm)
        {
            _loaded = true;
            _ = vm.LoadAsync();
        }
    }
}
