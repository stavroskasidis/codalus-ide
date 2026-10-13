using System.Globalization;
using Codalus.Projects.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Notifications;

namespace Codalus.Shell;

/// <summary>
/// Running tasks in the status bar (M22a, the maintainer's request of 2026-10-13): the newest task's text where the
/// "Ready" message is ("Loading project 29 of 96: …"), a progress bar (indeterminate when the task can't say how far
/// it is), a cancel button when it can be cancelled, and the list of all running tasks. The IDE's own tasks (loading
/// projects, restoring packages, building) and plugins' (<c>IWindow.WithProgressAsync</c>) share it.
/// </summary>
public sealed partial class ShellHost
{
    public const string ShowTasksCommand = "workbench.showRunningTasks";

    private ProgressTask? _loadTask;
    private ProgressTask? _restoreTask;
    private ProgressTask? _buildTask;

    private void InitializeProgress()
    {
        Workbench.Progress.Changed += (_, _) => Ui.Post(UpdateProgressStatus);
        Workbench.Commands.Register(
            new CommandDescriptor(ShowTasksCommand, "Show Running Tasks", ContributionSource.Shell) { Category = "View" },
            async (_, _) =>
            {
                await Ui.RunAsync(ShowRunningTasksAsync).ConfigureAwait(false);
                return null;
            });
        StatusBar.CancelProgressCommand = new RelayCommand(() => Workbench.Progress.Running.LastOrDefault(t => t.Cancellable)?.Cancel());
        StatusBar.ShowTasksCommand = new RelayCommand(() => _ = ShowRunningTasksAsync());

        // The IDE's own tasks.
        Solutions.ProgressChanged += (_, progress) => Ui.Post(() => OnLoadProgress(progress));
        Solutions.RestoreStateChanged += (_, _) => Ui.Post(OnRestoreProgress);
        Builds.Started += (_, request) => Ui.Post(() =>
        {
            _buildTask?.End();
            var what = request.Project is { } project ? Path.GetFileNameWithoutExtension(project) : Solutions.Solution?.Name ?? "the solution";
            var verb = request.Target switch
            {
                "Rebuild" => "Rebuilding",
                "Clean" => "Cleaning",
                "Publish" => "Publishing",
                _ => "Building",
            };
            _buildTask = Workbench.Progress.Begin($"{verb} {what}", "Build", () => Builds.Cancel());
        });
        Builds.Finished += (_, _) => Ui.Post(() =>
        {
            _buildTask?.End();
            _buildTask = null;
        });
    }

    private void OnLoadProgress(EvaluationProgressParams progress)
    {
        if (progress.Completed || progress.Total == 0)
        {
            _loadTask?.End();
            _loadTask = null;
            return;
        }

        _loadTask ??= Workbench.Progress.Begin("Loading projects", "Solution");
        _loadTask.Report(
            $"Loading project {progress.Evaluated + 1} of {progress.Total}" + (progress.Current is { } current ? ": " + current : string.Empty),
            100.0 * progress.Evaluated / progress.Total);
    }

    private void OnRestoreProgress()
    {
        if (Solutions.RestoreState == "running")
        {
            _restoreTask ??= Workbench.Progress.Begin("Restoring packages", "NuGet");
        }
        else
        {
            _restoreTask?.End();
            _restoreTask = null;
        }
    }

    /// <summary>What a task says in the status bar: its message (which names what it's on), else its title.</summary>
    private static string Describe(ProgressTask task) =>
        (task.Message is { Length: > 0 } message ? message : task.Title + "…")
        + (task.IsCancellationRequested ? " (cancelling)" : string.Empty);

    private void UpdateProgressStatus()
    {
        var running = Workbench.Progress.Running;
        if (running.Count == 0)
        {
            StatusBar.SetProgress(null, null, canCancel: false, tooltip: null);
            return;
        }

        var shown = running[^1];
        var text = Describe(shown) + (running.Count > 1 ? $"  (+{running.Count - 1} more)" : string.Empty);
        var tooltip = string.Join('\n', running.Select(t => $"{t.Source}: {Describe(t)}"
            + (t.Percent is { } percent ? " (" + Math.Round(percent).ToString(CultureInfo.InvariantCulture) + "%)" : string.Empty)));
        StatusBar.SetProgress(text, shown.Percent, running.Any(t => t.Cancellable && !t.IsCancellationRequested), tooltip);
    }

    /// <summary>The running tasks in a quick pick; choosing a cancellable one cancels it.</summary>
    public async Task ShowRunningTasksAsync()
    {
        var running = Workbench.Progress.Running;
        if (running.Count == 0)
        {
            StatusBar.ShowMessage("Nothing is running.", TimeSpan.FromSeconds(3));
            return;
        }

        var items = running.Select(t => new QuickPickItem(Describe(t))
        {
            Detail = t.Source
                + (t.Percent is { } percent ? " · " + Math.Round(percent).ToString(CultureInfo.InvariantCulture) + "%" : string.Empty)
                + (t.Cancellable && !t.IsCancellationRequested ? " · choose to cancel" : string.Empty),
            Tag = t,
        }).ToList();
        if (await Palette.ShowAsync(items, "Running tasks").ConfigureAwait(true) is { Tag: ProgressTask { Cancellable: true } task })
        {
            task.Cancel();
        }
    }
}
