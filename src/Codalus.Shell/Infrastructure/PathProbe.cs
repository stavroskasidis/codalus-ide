namespace Codalus.Shell.Infrastructure;

/// <summary>What a path is, as far as a <see cref="PathProbe"/> could tell in time.</summary>
public enum PathState
{
    /// <summary>The probe didn't answer in time (a share that is offline): shown as not found, and kept.</summary>
    Unknown,

    File,

    Folder,

    Missing,
}

/// <summary>
/// Whether a recent solution, project or folder is there (M56), off the UI thread and within <see cref="Timeout"/>. On a
/// share that is offline (a VPN down, a laptop away from the office) or a dead NFS mount, a file-system call waits for the
/// network's timeout, tens of seconds, or for good: the start window, File → Open Recent and the last-solution startup
/// held the UI thread that long for each such entry. A probe that doesn't answer in time says <see cref="PathState.Unknown"/>.
/// Each probe has a thread of its own, so probes stuck on a dead mount don't take the thread pool's (the hosts' readers run
/// there), and a path has one probe at a time: asking again while one runs waits for it.
/// </summary>
public sealed class PathProbe(Func<string, PathState>? probe = null)
{
    /// <summary>How long a probe may take before its path counts as <see cref="PathState.Unknown"/>.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    private readonly Func<string, PathState> _probe = probe ?? Kind;
    private readonly Dictionary<string, Task<PathState>> _running = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>What <paramref name="path"/> is, from the disk.</summary>
    public static PathState Kind(string path) =>
        Directory.Exists(path) ? PathState.Folder : File.Exists(path) ? PathState.File : PathState.Missing;

    /// <summary>
    /// What a recent entry most likely is, without the disk: a solution or a project file, or else a folder (the recent
    /// list holds nothing else). The icon a row shows until its probe answers.
    /// </summary>
    public static PathState Guess(string path) =>
        Codalus.Workbench.Projects.SolutionTree.IsSolutionOrProjectFile(path) ? PathState.File : PathState.Folder;

    /// <summary>What <paramref name="path"/> is, or <see cref="PathState.Unknown"/> when the disk doesn't say within <see cref="Timeout"/>.</summary>
    public async Task<PathState> KindAsync(string path, CancellationToken cancellationToken = default)
    {
        Task<PathState>? running;
        lock (_gate)
        {
            if (!_running.TryGetValue(path, out running))
            {
                running = Task.Factory.StartNew(() => _probe(path), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                _running[path] = running;
                _ = running.ContinueWith(_ => Forget(path), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        try
        {
            return await running.WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return PathState.Unknown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PathState.Unknown;
        }
    }

    private void Forget(string path)
    {
        lock (_gate)
        {
            _running.Remove(path);
        }
    }
}
