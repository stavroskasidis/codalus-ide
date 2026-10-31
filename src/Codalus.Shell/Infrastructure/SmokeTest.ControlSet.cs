using Avalonia.Controls;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// M34h: the control set in the gallery: its page (<see cref="ControlGallery"/>) in all sixteen themes, whatever
/// <c>CODALUS_GALLERY_THEMES</c> says, since it is one window; and the checklist dialog in macOS's button order (the
/// other dialogs are photographed in the OS's own).
/// </summary>
internal sealed partial class SmokeTest
{
    /// <summary>
    /// The control set page's window: the whole page fits it, down to the dialog's footer (ControlSetTests checks it,
    /// so a new row in <see cref="ControlGallery"/> that makes the page taller raises this).
    /// </summary>
    internal const double ControlSetWidth = 1280, ControlSetHeight = 1650;

    /// <summary>The control set's page in every theme, as <c>controls-set-{theme}.png</c>.</summary>
    private async Task GalleryControlSetAsync()
    {
        var page = new Window
        {
            Title = "Control set",
            Width = ControlSetWidth,
            Height = ControlSetHeight,
            Content = new ScrollViewer { Content = ControlGallery.Build() },
            ShowInTaskbar = false,
        };
        var before = shell.Theme.Setting;
        page.Show();
        try
        {
            foreach (var (theme, _) in ThemeService.Themes)
            {
                await shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, theme, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                await WaitAsync(() => shell.Theme.ActiveTheme == theme, () => ThemeWait(theme)).ConfigureAwait(true);
                await Task.Delay(100).ConfigureAwait(true);
                await RenderedAsync(page).ConfigureAwait(true);
                GalleryShot("controls", "set", theme, null, page);
            }
        }
        finally
        {
            page.Close();
            await shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, before, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>The checklist dialog with its buttons in macOS's order (Cancel, then the primary), on any OS.</summary>
    private async Task GalleryDialogOnMacAsync(DialogRequest request)
    {
        var order = shell.Dialogs.PrimaryLast;
        shell.Dialogs.PrimaryLast = true;
        try
        {
            var shown = shell.Dialogs.ShowAsync(request);
            await GalleryThemesAsync(async theme =>
            {
                await SettleAsync().ConfigureAwait(true);
                GalleryShot("dialog-checklist-macos", "data", theme, null);
            }).ConfigureAwait(true);
            shell.Dialogs.Cancel();
            await shown.ConfigureAwait(true);
        }
        finally
        {
            shell.Dialogs.PrimaryLast = order;
        }
    }
}
