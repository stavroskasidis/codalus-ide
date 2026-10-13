using Avalonia;
using Avalonia.Controls;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>A solution-management view: loads its view model the first time it shows.</summary>
public abstract class LoadingView : UserControl, IPaneView
{
    private bool _loaded;

    public virtual void FocusPrimary() => Focus();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_loaded && DataContext is ILoadable loadable)
        {
            _loaded = true;
            _ = loadable.LoadAsync();
        }
    }
}

public sealed partial class ReferenceManagerView : LoadingView
{
    public ReferenceManagerView() => InitializeComponent();
}

public sealed partial class ConfigurationManagerView : LoadingView
{
    public ConfigurationManagerView() => InitializeComponent();
}

public sealed partial class ProjectDependenciesView : LoadingView
{
    public ProjectDependenciesView() => InitializeComponent();
}

public sealed partial class SdkView : LoadingView
{
    public SdkView() => InitializeComponent();
}

public sealed partial class StartupProjectsView : LoadingView
{
    public StartupProjectsView() => InitializeComponent();
}

public sealed partial class PublishView : LoadingView
{
    public PublishView() => InitializeComponent();

    public override void FocusPrimary() => ProfileBox.Focus();
}
