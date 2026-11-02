using Avalonia.Controls;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>The Plugins page: the installed plugins and the Plugin Gallery, a tab each.</summary>
public sealed partial class PluginsPageView : UserControl, IPaneView
{
    public PluginsPageView() => InitializeComponent();

    /// <summary>The Browse tab's view: the gallery's declarative view.</summary>
    public Control? GalleryView
    {
        get => Gallery.Content as Control;
        init => Gallery.Content = value;
    }

    public void FocusPrimary()
    {
        if (Tabs.SelectedIndex == PluginsPageViewModel.InstalledTab && Tabs.SelectedContent is PluginsView installed)
        {
            installed.FocusPrimary();
        }
        else
        {
            Tabs.Focus();
        }
    }
}
