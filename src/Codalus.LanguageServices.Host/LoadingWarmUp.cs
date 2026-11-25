using Microsoft.CodeAnalysis;

namespace Codalus.LanguageServices.Host;

/// <summary>
/// Parses the projects' documents while the solution still loads, one at a time on the thread pool: the background
/// pass waits for the whole solution (M34b), and then parsed every file first, on the processors the design-time
/// builds had left idle meanwhile. Parsing only: no binding, compilations or generators, so nothing it does is
/// wrong once more projects arrive. A project's options come from its design-time build, so its trees stand.
/// </summary>
internal sealed class LoadingWarmUp(WorkspaceManager workspace, CancellationToken lifetime, Action<string>? log = null)
{
    private readonly Lock _gate = new();
    private bool _running;
    private bool _again;
    private TaskCompletionSource _idle = CompletedIdle();

    /// <summary>Documents parsed so far (tests, the log).</summary>
    public int Parsed => Volatile.Read(ref _parsed);

    private int _parsed;

    /// <summary>Completes when no walk runs (tests).</summary>
    public Task Idle
    {
        get
        {
            lock (_gate)
            {
                return _idle.Task;
            }
        }
    }

    /// <summary>Projects arrived: parse what isn't parsed yet, while the solution loads.</summary>
    public void ProjectsChanged()
    {
        if (!workspace.Loading || Environment.ProcessorCount <= 2 || lifetime.IsCancellationRequested)
        {
            return;
        }

        lock (_gate)
        {
            _again = true;
            if (_running)
            {
                return;
            }

            _running = true;
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _ = Task.Run(RunAsync, CancellationToken.None);
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                lock (_gate)
                {
                    if (!_again)
                    {
                        _running = false;
                        _idle.TrySetResult();
                        return;
                    }

                    _again = false;
                }

                var before = Parsed;
                if (await WalkAsync(workspace.CurrentSolution).ConfigureAwait(false) && Parsed > before)
                {
                    log?.Invoke($"Loading: {Parsed - before} document(s) parsed ahead of the background pass.");
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Only a head start: the background pass parses what this didn't.
            System.Diagnostics.Debug.WriteLine(ex);
        }

        lock (_gate)
        {
            _running = false;
            _again = false;
            _idle.TrySetResult();
        }
    }

    /// <summary>Parses what isn't parsed; false if the loading ended first.</summary>
    private async Task<bool> WalkAsync(Solution solution)
    {
        foreach (var project in solution.Projects)
        {
            if (project.Id == workspace.MiscellaneousProjectId || !project.SupportsCompilation)
            {
                continue;
            }

            foreach (var document in project.Documents)
            {
                if (!workspace.Loading || lifetime.IsCancellationRequested)
                {
                    return false; // the pass starts now, and parses the rest itself
                }

                if (document.TryGetSyntaxTree(out _))
                {
                    continue;
                }

                await document.GetSyntaxTreeAsync(lifetime).ConfigureAwait(false);
                Interlocked.Increment(ref _parsed);
                await Task.Yield();
            }
        }

        return true;
    }

    private static TaskCompletionSource CompletedIdle()
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        idle.TrySetResult();
        return idle;
    }
}
