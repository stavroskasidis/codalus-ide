using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Codalus.Platform;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Setup;

namespace Codalus.Shell;

/// <summary>
/// Opt-in OS integration (M51, ADR 0084): "Open with Codalus IDE" on folders and the solution and project file types,
/// offered in the first-run setup and in Tools → Options → Environment → System Integration, off until chosen. The
/// registration is the source of truth (no setting): the toggles read it back and change it. What the OS starts arrives
/// here as <c>codalus &lt;path&gt;</c> (handed to this window when it has nothing open, <see cref="TakeOpenPath"/>) or, on
/// macOS, as an open-documents event (<see cref="OnActivated"/>).
/// </summary>
public sealed partial class ShellHost
{
    private OsIntegration? _osIntegration;

    /// <summary>This OS's integration for the copy that runs (tests set one with temporary roots: nothing is registered on the machine).</summary>
    public OsIntegration OsIntegration
    {
        get => _osIntegration ??= OsIntegration.ForCurrentPlatform(
            OsLauncher.Current(Options.UserDataDir ?? Environment.GetEnvironmentVariable(AppPaths.UserDataDirVariable)));
        set => _osIntegration = value;
    }

    /// <summary>
    /// Registers or removes <paramref name="feature"/> off the UI thread; how it stands after, and the error to show when
    /// it failed (the status is read again then).
    /// </summary>
    public async Task<(OsIntegrationStatus Status, string? Error)> SetOsIntegrationAsync(OsIntegrationFeature feature, bool on)
    {
        var integration = OsIntegration;
        try
        {
            var status = await Task.Run(() => integration.SetAsync(feature, on, CancellationToken.None)).ConfigureAwait(true);
            Workbench.Log.AppendLine($"[os] {feature} {(on ? "registered" : "removed")} for {integration.Launcher.Program}: {status.State}.");
            return (status, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Workbench.Log.AppendLine($"[os] {feature} {(on ? "registering" : "removing")} failed: {ex.Message}");
            var status = await Task.Run(() => integration.GetStatus(feature)).ConfigureAwait(true);
            return (status, Localizer.Format("msg.osIntegration.failed", "Couldn't change the system's settings: {0}", ex.Message));
        }
    }

    /// <summary>How <paramref name="feature"/> stands, read off the UI thread.</summary>
    public Task<OsIntegrationStatus> OsIntegrationStatusAsync(OsIntegrationFeature feature)
    {
        var integration = OsIntegration;
        return Task.Run(() => integration.GetStatus(feature));
    }

    /// <summary>
    /// <c>codalus &lt;folder or solution&gt;</c> from another process (Explorer's, the Finder's and the file managers'
    /// Open with…, or a terminal): this window opens it when it has nothing open, and says so; with a solution or folder
    /// open it declines (unless it's the same one), and the other process opens a window of its own. Runs on the UI thread.
    /// </summary>
    internal bool TakeOpenPath(string path)
    {
        if (IsClosing)
        {
            return false; // the window is closing (M56): the other process opens its own
        }

        if (Solutions.IsOpen)
        {
            if (!IsOpenNow(path))
            {
                return false;
            }

            Window?.BringToFront();
            return true;
        }

        Window?.BringToFront();
        _startWindow?.Close(); // it asked what to open: this
        _ = Directory.Exists(path) ? OpenFolderAsync(path) : OpenSolutionAsync(path);
        return true;
    }

    /// <summary>Whether <paramref name="path"/> is the open solution, its filter, or the folder it's in.</summary>
    private bool IsOpenNow(string path)
    {
        if (Solutions.Solution is not { } solution)
        {
            return false;
        }

        var wanted = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return new[] { solution.Path, solution.FilterPath, Directory.Exists(solution.Path) ? null : Path.GetDirectoryName(solution.Path) }
            .Where(p => p is not null)
            .Any(p => SolutionService.PathComparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p!)), wanted));
    }

    /// <summary>macOS: the Finder opens solutions, projects and folders by sending them to the running app.</summary>
    private void ListenForOpenedFiles(Avalonia.Application app)
    {
        if (app.TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime lifetime)
        {
            lifetime.Activated += OnActivated;
        }
    }

    private void OnActivated(object? sender, ActivatedEventArgs e)
    {
        if (e is not FileActivatedEventArgs { Files: { } files })
        {
            return;
        }

        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        Ui.Post(() => OpenFromOs(paths));
    }

    /// <summary>
    /// Paths the OS handed over: solutions, projects and folders (here, or in a new window when one is open), and files in
    /// the editor, together: the last one is the active tab (M56).
    /// </summary>
    internal void OpenFromOs(IReadOnlyList<string> paths)
    {
        if (IsClosing)
        {
            // The Finder's open-documents event reaches the app while it shuts down (M56): its window is gone and its
            // services are being disposed, so nothing would show what opened. The lifetime's Exit dropped these too.
            Workbench.Log.AppendLine($"[os] Not opened, the IDE is closing: {string.Join(", ", paths)}");
            return;
        }

        var files = new List<(string, LanguageServices.Protocol.Position?)>();
        foreach (var path in paths)
        {
            Workbench.Log.AppendLine($"[os] Opened from the system: {path}");
            if (Directory.Exists(path) || SolutionTree.IsSolutionOrProjectFile(path))
            {
                if (!TakeOpenPath(path))
                {
                    StartNewWindow(path);
                }

                continue;
            }

            files.Add((path, null));
        }

        if (files.Count > 0)
        {
            _ = OpenFilesAsync(files);
        }
    }

    /// <summary>How long files other <c>codalus</c> processes ask to open wait for the others of their batch (<see cref="OpenTogetherAsync"/>).</summary>
    internal static readonly TimeSpan OpenBatchWindow = TimeSpan.FromMilliseconds(150);

    private List<(string Path, LanguageServices.Protocol.Position? At, TaskCompletionSource<bool> Opened)>? _openBatch;

    /// <summary>
    /// A file another <c>codalus</c> process asked to open (on the UI thread). Explorer's and the file managers' Open with on
    /// several files start one process per file: the requests that come within <see cref="OpenBatchWindow"/> of the first
    /// open together, the last one active (M56), instead of each becoming the active tab in turn. Completes with whether
    /// the file opened in an editor.
    /// </summary>
    internal Task<bool> OpenTogetherAsync(string path, LanguageServices.Protocol.Position? at)
    {
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_openBatch is null)
        {
            _openBatch = [];
            Avalonia.Threading.DispatcherTimer.RunOnce(() => _ = OpenBatchAsync(), OpenBatchWindow);
        }

        _openBatch.Add((path, at, opened));
        return opened.Task;
    }

    private async Task OpenBatchAsync()
    {
        var batch = _openBatch ?? [];
        _openBatch = null;
        try
        {
            var opened = await OpenFilesAsync([.. batch.Select(b => (b.Path, b.At))]).ConfigureAwait(true);
            for (var i = 0; i < batch.Count; i++)
            {
                batch[i].Opened.TrySetResult(opened[i]);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error(ex.Message);
            foreach (var (_, _, opened) in batch)
            {
                opened.TrySetResult(false);
            }
        }
    }
}
