using Avalonia.Controls;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Feedback;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// Help → About Codalus IDE (on macOS also the application menu's first entry): a modal window with the version, what
/// the IDE runs on, the license and links (<see cref="AboutWindow"/>). Choosing it again while it's open activates it.
/// </summary>
public sealed partial class ShellHost
{
    private Window? _aboutWindow;

    /// <summary>The open About window (tests, the smoke test), or null.</summary>
    public Window? AboutWindowView => _aboutWindow;

    /// <summary>The About window for a model (<see cref="Views.AboutWindow"/>); tests may replace it.</summary>
    internal Func<AboutViewModel, Window> AboutWindowFactory { get; set; } = model => new Views.AboutWindow { DataContext = model };

    public void ShowAbout()
    {
        if (_aboutWindow is { } open)
        {
            open.Activate();
            return;
        }

        var model = new AboutViewModel(IssueEnvironment.Current(ShellInfo.Version), CopyAboutAsync, OpenAboutLink);
        var window = AboutWindowFactory(model);
        _aboutWindow = window;
        window.Closed += (_, _) =>
        {
            if (_aboutWindow == window)
            {
                _aboutWindow = null;
            }
        };

        // Modal over the main window, as the classic IDE's; on its own while there is none to own it (the smoke test's stand-in).
        if (Window is Window owner && owner.IsVisible)
        {
            _ = window.ShowDialog(owner);
        }
        else
        {
            window.Show();
        }
    }

    private async Task CopyAboutAsync(string text)
    {
        if (Window?.Clipboard is { } clipboard)
        {
            await SafeClipboard.TrySetTextAsync(clipboard, text).ConfigureAwait(true);
        }
    }

    private void OpenAboutLink(Uri url)
    {
        try
        {
            OpenExternalUrl(url);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.about.couldNotOpen", "Could not open {0}: {1}", url, ex.Message));
        }
    }
}
