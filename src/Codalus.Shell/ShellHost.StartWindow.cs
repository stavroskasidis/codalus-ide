using Avalonia.Controls;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// The start window (M38): "What would you like to do?" when the IDE starts without a solution, and from File → Start
/// Window. Recent solutions, projects and folders (search, pinned, grouped by date) and the ways to start: create a
/// project, open a solution or a folder, clone a repository. <see cref="StartupSetting"/> chooses it, an empty window, or
/// the last solution.
/// </summary>
public sealed partial class ShellHost
{
    public const string StartupSetting = "workbench.startup";

    private StartWindow? _startWindow;

    /// <summary>Whether recent entries are there, off the UI thread (M56; tests replace what it asks the disk).</summary>
    internal PathProbe RecentProbe { get; set; } = new();

    /// <summary>Whether the start window is open.</summary>
    public bool IsStartWindowOpen => _startWindow is not null;

    /// <summary>The open start window's model (tests).</summary>
    public StartWindowViewModel? StartWindowModel => _startWindow?.DataContext as StartWindowViewModel;

    /// <summary>At startup with nothing to open: the start window, nothing, or the last solution (<see cref="StartupSetting"/>).</summary>
    private void OnStartupWithoutSolution()
    {
        if (!Options.StartWindow || Options.Path is not null || Options.Remote is not null || Options.Diff is not null || Options.Merge is not null
            || Options.WaitFile is not null || Options.SmokeTest is not null || Options.Bench is not null || Options.OpenFile is not null || Options.Screenshot is not null)
        {
            return;
        }

        ContinueStartup();
    }

    /// <summary>
    /// What a start without a solution shows (<see cref="StartupSetting"/>): the start window, nothing, or the last
    /// solution. After the first-run setup (M46) too, which opens before it.
    /// </summary>
    private void ContinueStartup()
    {
        if (!Options.StartWindow)
        {
            return;
        }

        switch (Workbench.Settings.GetString(StartupSetting, "startWindow"))
        {
            case "lastSolution":
                _ = OpenLastSolutionAsync();
                break;
            case "startWindow":
                Infrastructure.Ui.Post(ShowStartWindow);
                break;
        }
    }

    /// <summary>
    /// The last-solution startup: the first recent entry that is there, in the list's order, each checked off the UI thread
    /// (M56: an entry on a share that is offline counts as not there once its probe's time is up). Nothing opens when the
    /// user opened something meanwhile.
    /// </summary>
    private async Task OpenLastSolutionAsync()
    {
        var opens = _openRequests;
        if (await FirstRecentThereAsync().ConfigureAwait(true) is not { } last || opens != _openRequests || Solutions.IsOpen)
        {
            return;
        }

        await (last.Kind == PathState.Folder ? OpenFolderAsync(last.Path) : OpenSolutionAsync(last.Path)).ConfigureAwait(true);
    }

    /// <summary>The first recent entry that is there, and what it is; null when none is.</summary>
    internal async Task<(string Path, PathState Kind)?> FirstRecentThereAsync()
    {
        foreach (var path in Recent.Items)
        {
            if (await RecentProbe.KindAsync(path).ConfigureAwait(true) is var kind and (PathState.File or PathState.Folder))
            {
                return (path, kind);
            }
        }

        return null;
    }

    public void ShowStartWindow()
    {
        if (_startWindow is not null)
        {
            _startWindow.Activate();
            return;
        }

        var model = new StartWindowViewModel(this);
        _startWindow = new StartWindow { DataContext = model };
        model.CloseRequested += (_, _) => _startWindow?.Close();
        _startWindow.Closed += (_, _) => _startWindow = null;
        if (Window is Window owner && owner.IsVisible)
        {
            _startWindow.Show(owner);
        }
        else
        {
            _startWindow.Show();
        }
    }

    /// <summary>Opens a recent entry: a folder, or a solution or project; a missing one offers to leave the list.</summary>
    internal async Task OpenRecentEntryAsync(string path)
    {
        // Off the UI thread (M56): on a share that is offline, the check waits for the network's timeout.
        var kind = await Task.Run(() => PathProbe.Kind(path)).ConfigureAwait(true);
        if (kind == PathState.Folder)
        {
            await OpenFolderAsync(path).ConfigureAwait(true);
        }
        else if (kind == PathState.File)
        {
            await OpenSolutionAsync(path).ConfigureAwait(true);
        }
        else if (await Dialogs.ConfirmAsync(Localizer.Get("msg.startWindow.notFound", "Not Found"), Localizer.Format("msg.startWindow.removeMissing", "{0} isn't there anymore. Remove it from the recent list?", path), Localizer.Get("msg.startWindow.remove", "Remove")).ConfigureAwait(true))
        {
            Recent.Remove(path);
        }
    }

    /// <summary>Starts another <c>codalus</c> on a recent entry (Open in New Window).</summary>
    internal void OpenRecentInNewWindow(string path) => StartNewWindow(path);
}
